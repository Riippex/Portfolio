using System.Net;
using System.Text;
using System.Text.Json;

namespace Rafael.Portfolio.IntegrationTests;

// Stage access and signed identity against the published artifact. These assertions depend only on
// status codes, so they hold whatever evidence the portfolio currently publishes.
public sealed partial class PublishedBackendSmokeTests
{
    private static HttpRequestMessage ChatRequestFor((string Identity, string Proof)? signed, string path = "/v1/assistant/chat")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative))
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { message = "Tell me about autonomous agents" }),
                Encoding.UTF8,
                "application/json")
        };

        if (signed is { } identity)
        {
            request.Headers.TryAddWithoutValidation("X-Portfolio-Identity", identity.Identity);
            request.Headers.TryAddWithoutValidation("X-Portfolio-Identity-Proof", identity.Proof);
        }

        return request;
    }

    private static async Task<HttpStatusCode> SendStatusAsync(HttpClient http, HttpRequestMessage request)
    {
        using (request)
        using (var response = await http.SendAsync(request))
        {
            return response.StatusCode;
        }
    }

    [Fact]
    public async Task Published_backend_enforces_stage_access_and_the_signed_identity_contract()
    {
        await RunPublishedAsync(async (run, publishDir) =>
        {
            // A deployed public stage never falls back to a connection-based identity, even when the host
            // environment name is Development, and it accepts only identities bound to its stage and call.
            run.Stage = "public stage host";
            var prodPort = GetFreePort();
            var prodHost = StartHost(run, "public stage host", publishDir, prodPort, new Dictionary<string, string>
            {
                ["ASPNETCORE_ENVIRONMENT"] = "Development",
                ["Portfolio__Stage"] = "prod",
                ["AssistantSecurity__ProxyIdentitySecret"] = IntegrationSecret
            });
            using var prod = run.CreateClient(prodPort);
            await WaitForHealthAsync(run, prod, prodHost);

            run.Stage = "public stage requests";
            Assert.Equal(HttpStatusCode.OK, await SendStatusAsync(prod, new HttpRequestMessage(HttpMethod.Get, "/v1/projects")));
            Assert.Equal(HttpStatusCode.OK, await SendStatusAsync(prod, new HttpRequestMessage(HttpMethod.Get, "/v1/profile")));
            Assert.Equal(HttpStatusCode.Forbidden, await SendStatusAsync(prod, ChatRequestFor(null)));

            var valid = SignVisitor("POST", "/v1/assistant/chat", "203.0.113.50", "prod");
            Assert.Equal(HttpStatusCode.OK, await SendStatusAsync(prod, ChatRequestFor(valid)));

            var ipv6 = SignVisitor("POST", "/v1/assistant/chat", "2001:db8::50", "prod");
            Assert.Equal(HttpStatusCode.OK, await SendStatusAsync(prod, ChatRequestFor(ipv6)));

            var forged = SignVisitor("POST", "/v1/assistant/chat", "203.0.113.51", "prod", secret: "a-different-secret");
            Assert.Equal(HttpStatusCode.Forbidden, await SendStatusAsync(prod, ChatRequestFor(forged)));

            var otherStage = SignVisitor("POST", "/v1/assistant/chat", "203.0.113.52", "dev", tier: "team");
            Assert.Equal(HttpStatusCode.Forbidden, await SendStatusAsync(prod, ChatRequestFor(otherStage)));

            // An identity signed for chat cannot be replayed against job analysis.
            Assert.Equal(HttpStatusCode.Forbidden, await SendStatusAsync(prod, ChatRequestFor(valid, "/v1/jobs/analyze")));

            // The version 1 colon-delimited identity is no longer understood.
            using (var legacy = ChatRequestFor(null))
            {
                var legacyKey = "v1:203.0.113.53:CO:team:prod";
                legacy.Headers.TryAddWithoutValidation("X-Client-Key", legacyKey);
                legacy.Headers.TryAddWithoutValidation("X-Client-Key-Proof", ProofFor(legacyKey));
                Assert.Equal(HttpStatusCode.Forbidden, (await prod.SendAsync(legacy)).StatusCode);
            }

            // The private dev stage keeps every data route closed to direct callers.
            run.Stage = "private dev stage host";
            var devPort = GetFreePort();
            var devHost = StartHost(run, "private dev stage host", publishDir, devPort, new Dictionary<string, string>
            {
                ["ASPNETCORE_ENVIRONMENT"] = "Development",
                ["Portfolio__Stage"] = "dev",
                ["AssistantSecurity__ProxyIdentitySecret"] = IntegrationSecret
            });
            using var dev = run.CreateClient(devPort);
            await WaitForHealthAsync(run, dev, devHost);

            run.Stage = "private dev stage requests";
            foreach (var closedPath in new[] { "/v1/profile", "/v1/projects", "/v1/projects/vextis", "/v1/evidence", "/v1/evidence/search?q=agents", "/openapi/v1.json", "/health/" })
            {
                Assert.Equal(HttpStatusCode.Forbidden, await SendStatusAsync(dev, new HttpRequestMessage(HttpMethod.Get, closedPath)));
            }

            Assert.Equal(HttpStatusCode.OK, await SendStatusAsync(dev, new HttpRequestMessage(HttpMethod.Get, "/health")));

            using (var serviceRead = new HttpRequestMessage(HttpMethod.Get, "/v1/projects"))
            {
                var signedRead = SignServiceRead("/v1/projects", "dev");
                serviceRead.Headers.TryAddWithoutValidation("X-Portfolio-Identity", signedRead.Identity);
                serviceRead.Headers.TryAddWithoutValidation("X-Portfolio-Identity-Proof", signedRead.Proof);
                Assert.Equal(HttpStatusCode.OK, await SendStatusAsync(dev, serviceRead));
            }

            using (var wrongPathRead = new HttpRequestMessage(HttpMethod.Get, "/v1/profile"))
            {
                var signedRead = SignServiceRead("/v1/projects", "dev");
                wrongPathRead.Headers.TryAddWithoutValidation("X-Portfolio-Identity", signedRead.Identity);
                wrongPathRead.Headers.TryAddWithoutValidation("X-Portfolio-Identity-Proof", signedRead.Proof);
                Assert.Equal(HttpStatusCode.Forbidden, await SendStatusAsync(dev, wrongPathRead));
            }

            var ordinary = SignVisitor("POST", "/v1/assistant/chat", "203.0.113.60", "dev");
            Assert.Equal(HttpStatusCode.Forbidden, await SendStatusAsync(dev, ChatRequestFor(ordinary)));
            Assert.Equal(HttpStatusCode.Forbidden, await SendStatusAsync(dev, ChatRequestFor(null)));

            var team = SignVisitor("POST", "/v1/assistant/chat", "203.0.113.61", "dev", tier: "team");
            Assert.Equal(HttpStatusCode.OK, await SendStatusAsync(dev, ChatRequestFor(team)));

            // Startup refuses a missing or invalid stage instead of serving.
            run.Stage = "startup probes for a missing or invalid stage";
            foreach (var invalidStage in new[] { "", "local", "staging", "PROD" })
            {
                var stageProbeError = await ExpectStartupFailureAsync(run, $"stage startup probe '{invalidStage}'", publishDir, new Dictionary<string, string>
                {
                    ["ASPNETCORE_ENVIRONMENT"] = "Production",
                    ["Portfolio__Stage"] = invalidStage,
                    ["Turnstile__SecretKey"] = "0x4AAAAAA_test_secret",
                    ["AssistantSecurity__ProxyIdentitySecret"] = IntegrationSecret
                });
                Assert.Contains("Portfolio:Stage", stageProbeError);
            }

            run.Stage = "startup probe for a deployed stage without the identity secret";
            var secretProbeError = await ExpectStartupFailureAsync(run, "deployed stage secret probe", publishDir, new Dictionary<string, string>
            {
                ["ASPNETCORE_ENVIRONMENT"] = "Development",
                ["Portfolio__Stage"] = "dev",
                ["AssistantSecurity__ProxyIdentitySecret"] = ""
            });
            Assert.Contains("ProxyIdentitySecret", secretProbeError);
        });
    }
}
