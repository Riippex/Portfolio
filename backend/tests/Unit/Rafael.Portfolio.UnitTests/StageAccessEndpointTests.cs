using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Rafael.Portfolio.Modules.Assistant.Application;
using Rafael.Portfolio.Modules.Assistant.Domain;
using Rafael.Portfolio.Modules.Assistant.Infrastructure;
using Rafael.Portfolio.Modules.Contact.Application;
using Rafael.Portfolio.Modules.Contact.Domain;
using Rafael.Portfolio.Modules.Contact.Infrastructure;
using Rafael.Portfolio.Modules.JobMatching.Application;
using Rafael.Portfolio.Modules.JobMatching.Domain;
using Rafael.Portfolio.Modules.Portfolio.Application;
using Rafael.Portfolio.Modules.Portfolio.Domain;
using Rafael.Portfolio.Web.Endpoints;
using Rafael.Portfolio.Web.Security;

namespace Rafael.Portfolio.UnitTests;

// Drives the real stage guard, identity validation and endpoints over a real Kestrel listener
// (loopback, ephemeral port) with the production environment name. Only the domain services
// behind the endpoints are stubs, so no model, email or network call can happen.
public sealed class StageAccessEndpointTests
{
    private const string Ip = "203.0.113.9";
    private const string OtherIp = "198.51.100.20";

    private sealed class StubCatalog : IPortfolioCatalog
    {
        public Profile GetProfile() => new("Rafael", "headline", "summary", "pending", ["agents"]);

        public IReadOnlyList<ProjectSummary> GetProjects() => [new("vextis", "Vextis", "summary", "pending")];

        public ProjectDetail? GetProjectBySlug(string slug) => null;
    }

    private sealed class StubAssistant : IAssistantService
    {
        public AssistantChatResponse Chat(AssistantChatRequest request) =>
            new("answer", "not_documented", []);

        public async IAsyncEnumerable<AssistantStreamEvent> StreamChatAsync(
            AssistantChatRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return AssistantStreamEvent.DoneEvent();
            await Task.CompletedTask;
        }
    }

    private sealed class StubJobs : IJobMatchingService
    {
        public JobAnalysisResponse Analyze(JobAnalysisRequest request) =>
            new("summary", [], [], [], [], "assessment");
    }

    private sealed class StubContact : IContactService
    {
        public Task<ContactDeliveryResult> SendContactMessageAsync(ContactMessage message, CancellationToken cancellationToken = default) =>
            Task.FromResult(ContactDeliveryResult.Delivered());
    }

    private sealed class ConfigurableTurnstile : ITurnstileValidator
    {
        public bool Result { get; set; } = true;

        public string? LastRemoteIp { get; private set; }

        public Task<bool> ValidateAsync(string? token, string? remoteIp, CancellationToken cancellationToken = default)
        {
            LastRemoteIp = remoteIp;
            return Task.FromResult(Result);
        }
    }

    private sealed class Host : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private Host(WebApplication app, HttpClient client, ConfigurableTurnstile turnstile)
        {
            _app = app;
            Client = client;
            Turnstile = turnstile;
        }

        public HttpClient Client { get; }

        public ConfigurableTurnstile Turnstile { get; }

        public static async Task<Host> StartAsync(string? stage, int countryLimit = 100)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [PortfolioStages.ConfigurationKey] = stage,
                ["AssistantSecurity:ProxyIdentitySecret"] = TestIdentity.Secret
            });

            var turnstile = new ConfigurableTurnstile();
            builder.Services.AddSingleton<IPortfolioCatalog, StubCatalog>();
            builder.Services.AddSingleton<IAssistantService, StubAssistant>();
            builder.Services.AddSingleton<IJobMatchingService, StubJobs>();
            builder.Services.AddSingleton<IContactService, StubContact>();
            builder.Services.AddSingleton<ITurnstileValidator>(turnstile);
            builder.Services.AddSingleton<IAssistantRateLimiter>(
                new InMemorySlidingWindowRateLimiter(limit: 5, teamLimit: 15, countryLimit: countryLimit));
            builder.Services.AddSingleton<IContactRateLimiter>(new InMemoryContactRateLimiter(3, TimeSpan.FromMinutes(10)));
            builder.Services.AddSingleton(TimeProvider.System);
            builder.Services.AddHealthChecks();

            var app = builder.Build();
            app.UseMiddleware<PrivateDevAccessMiddleware>();
            app.MapHealthChecks("/health");
            var v1 = app.MapGroup("/v1");
            v1.MapPortfolioEndpoints();
            v1.MapAssistantEndpoints();
            v1.MapJobMatchingEndpoints();
            v1.MapContactEndpoints();
            await app.StartAsync();

            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            var client = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(20) };
            return new Host(app, client, turnstile);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    private static HttpRequestMessage Get(string path, (string, string)? signed = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (signed is { } identity)
        {
            TestIdentity.Apply(request, identity);
        }

        return request;
    }

    private static HttpRequestMessage Chat(string path, string stage, string ip = Ip, string tier = "ordinary", string country = "CO", bool sign = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent("""{"message":"Tell me about autonomous agents"}""", Encoding.UTF8, "application/json")
        };
        if (sign)
        {
            TestIdentity.Apply(request, TestIdentity.Visitor(ip, "POST", path, stage, tier, country));
        }

        return request;
    }

    private static HttpRequestMessage Job(string stage, string ip = Ip, string tier = "ordinary", string country = "CO")
    {
        const string path = "/v1/jobs/analyze";
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent("""{"vacancyText":"Senior AI Engineer building autonomous agents"}""", Encoding.UTF8, "application/json")
        };
        TestIdentity.Apply(request, TestIdentity.Visitor(ip, "POST", path, stage, tier, country));
        return request;
    }

    private static HttpRequestMessage Contact(string stage, string ip = Ip, string tier = "ordinary", string country = "CO")
    {
        const string path = "/v1/contact";
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(
                """{"name":"Alice Visitor","email":"alice@example.com","message":"Inquiring about systems architecture.","consent":true}""",
                Encoding.UTF8,
                "application/json")
        };
        TestIdentity.Apply(request, TestIdentity.Visitor(ip, "POST", path, stage, tier, country));
        return request;
    }

    private static async Task<HttpStatusCode> StatusAsync(Host host, HttpRequestMessage request)
    {
        using (request)
        using (var response = await host.Client.SendAsync(request))
        {
            return response.StatusCode;
        }
    }

    // ---- private dev stage -------------------------------------------------------------

    [Fact]
    public async Task Dev_keeps_only_the_exact_health_endpoint_public()
    {
        await using var host = await Host.StartAsync("dev");

        Assert.Equal(HttpStatusCode.OK, await StatusAsync(host, Get("/health")));
        using var health = await host.Client.GetAsync("/health");
        Assert.Equal("Healthy", await health.Content.ReadAsStringAsync());

        foreach (var path in new[] { "/health/", "/healthz", "/Health", "/health/x", "/openapi/v1.json", "/v1/profile", "/v1/projects", "/v1/projects/vextis", "/unknown" })
        {
            Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(host, Get(path)));
        }

        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(host, new HttpRequestMessage(HttpMethod.Post, "/health")));
    }

    [Fact]
    public async Task Dev_data_routes_are_closed_to_direct_callers_without_a_valid_identity()
    {
        await using var host = await Host.StartAsync("dev");

        var forgedProof = TestIdentity.ServiceRead("/v1/profile", "dev", DateTimeOffset.UtcNow);
        forgedProof.Item2 = forgedProof.Item2[..^1] + (forgedProof.Item2[^1] == '0' ? '1' : '0');
        var legacy = Get("/v1/profile");
        legacy.Headers.TryAddWithoutValidation("X-Client-Key", $"v1:{Ip}:CO:team:dev");
        legacy.Headers.TryAddWithoutValidation("X-Client-Key-Proof", "anything");
        var spoofedHeaders = Get("/v1/profile");
        spoofedHeaders.Headers.TryAddWithoutValidation("CF-Connecting-IP", Ip);
        spoofedHeaders.Headers.TryAddWithoutValidation("X-Forwarded-For", Ip);
        spoofedHeaders.Headers.TryAddWithoutValidation("X-Client-Tier", "team");

        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(host, Get("/v1/profile", forgedProof)));
        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(host, legacy));
        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(host, spoofedHeaders));
        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(host, Get("/v1/profile", TestIdentity.ServiceRead("/v1/profile", "dev", DateTimeOffset.UtcNow, secret: "another-secret"))));
    }

    [Fact]
    public async Task Dev_admits_a_worker_signed_service_read_only_for_its_own_path_and_a_short_time()
    {
        await using var host = await Host.StartAsync("dev");
        var now = DateTimeOffset.UtcNow;

        Assert.Equal(HttpStatusCode.OK, await StatusAsync(host, Get("/v1/profile", TestIdentity.ServiceRead("/v1/profile", "dev", now))));
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(host, Get("/v1/projects", TestIdentity.ServiceRead("/v1/projects", "dev", now))));

        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(host, Get("/v1/projects", TestIdentity.ServiceRead("/v1/profile", "dev", now))));
        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(host, Get("/v1/profile", TestIdentity.ServiceRead("/v1/profile", "dev", now - TimeSpan.FromMinutes(10)))));
        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(host, Get("/v1/profile", TestIdentity.ServiceRead("/v1/profile", "prod", now))));
    }

    [Fact]
    public async Task Dev_never_lets_a_service_read_or_an_ordinary_visitor_reach_model_routes()
    {
        await using var host = await Host.StartAsync("dev");
        const string path = "/v1/assistant/chat";

        var serviceRead = Chat(path, "dev", sign: false);
        TestIdentity.Apply(serviceRead, TestIdentity.Sign(new
        {
            v = 2,
            kind = "service-read",
            stage = "dev",
            method = "POST",
            path,
            iat = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        }));

        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(host, serviceRead));
        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(host, Chat(path, "dev", tier: "ordinary")));
        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(host, Chat(path, "dev", sign: false)));
        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(host, Chat(path, "prod", tier: "team")));
    }

    [Fact]
    public async Task Dev_admits_team_visitors_to_every_route_with_their_own_signed_identity()
    {
        await using var host = await Host.StartAsync("dev");

        Assert.Equal(HttpStatusCode.OK, await StatusAsync(host, Get("/v1/profile", TestIdentity.Visitor(Ip, "GET", "/v1/profile", "dev", "team"))));
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(host, Chat("/v1/assistant/chat", "dev", tier: "team")));
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(host, Job("dev", tier: "team")));
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(host, Contact("dev", tier: "team")));
    }

    // ---- public prod stage ---------------------------------------------------------------

    [Fact]
    public async Task Prod_serves_public_data_without_an_identity_but_requires_one_for_visitor_routes()
    {
        await using var host = await Host.StartAsync("prod");

        Assert.Equal(HttpStatusCode.OK, await StatusAsync(host, Get("/v1/profile")));
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(host, Get("/v1/projects")));
        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(host, Chat("/v1/assistant/chat", "prod", sign: false)));
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(host, Chat("/v1/assistant/chat", "prod")));
    }

    [Fact]
    public async Task A_missing_or_invalid_stage_in_production_fails_startup_instead_of_serving()
    {
        foreach (var stage in new string?[] { null, "", "local", "staging", "PROD" })
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Host.StartAsync(stage));
            Assert.Contains("Portfolio:Stage", error.Message);
        }
    }

    [Fact]
    public async Task An_identity_cannot_be_replayed_against_another_endpoint_or_stage()
    {
        await using var host = await Host.StartAsync("prod");
        var chatIdentity = TestIdentity.Visitor(Ip, "POST", "/v1/assistant/chat", "prod");

        var onJobs = Job("prod");
        onJobs.Headers.Remove(SignedIdentity.IdentityHeader);
        onJobs.Headers.Remove(SignedIdentity.ProofHeader);
        TestIdentity.Apply(onJobs, chatIdentity);

        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(host, onJobs));
        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(host, Chat("/v1/assistant/chat", "dev", tier: "team")));
    }

    [Fact]
    public async Task Chat_and_job_analysis_share_one_quota_per_visitor()
    {
        await using var host = await Host.StartAsync("prod");

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.OK, await StatusAsync(host, Chat("/v1/assistant/chat", "prod")));
        }

        for (var i = 0; i < 2; i++)
        {
            Assert.Equal(HttpStatusCode.OK, await StatusAsync(host, Job("prod")));
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, await StatusAsync(host, Chat("/v1/assistant/chat", "prod")));
        Assert.Equal(HttpStatusCode.TooManyRequests, await StatusAsync(host, Chat("/v1/assistant/chat/stream", "prod")));
        Assert.Equal(HttpStatusCode.TooManyRequests, await StatusAsync(host, Job("prod")));

        // Another visitor is unaffected.
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(host, Chat("/v1/assistant/chat", "prod", ip: OtherIp)));
    }

    [Fact]
    public async Task The_same_visitor_over_ipv6_has_its_own_quota_and_equivalent_ipv6_is_one_visitor()
    {
        await using var host = await Host.StartAsync("prod");

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(HttpStatusCode.OK, await StatusAsync(host, Chat("/v1/assistant/chat", "prod", ip: "2001:db8::1")));
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, await StatusAsync(host, Chat("/v1/assistant/chat", "prod", ip: "2001:db8::1")));
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(host, Chat("/v1/assistant/chat", "prod", ip: "2001:db8::2")));
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(host, Chat("/v1/assistant/chat", "prod", ip: Ip)));
    }

    [Fact]
    public async Task Team_visitors_get_the_higher_limit_but_still_count_toward_the_country()
    {
        await using var host = await Host.StartAsync("prod", countryLimit: 20);

        for (var i = 0; i < 15; i++)
        {
            Assert.Equal(HttpStatusCode.OK, await StatusAsync(host, Chat("/v1/assistant/chat", "prod", tier: "team")));
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, await StatusAsync(host, Chat("/v1/assistant/chat", "prod", tier: "team")));

        // Five more requests from other visitors in the same country exhaust the country's 20.
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(HttpStatusCode.OK, await StatusAsync(host, Chat("/v1/assistant/chat", "prod", ip: $"198.51.100.{i + 1}")));
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, await StatusAsync(host, Chat("/v1/assistant/chat", "prod", ip: "198.51.100.200")));
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(host, Chat("/v1/assistant/chat", "prod", ip: "198.51.100.200", country: "MX")));
    }

    [Fact]
    public async Task Team_access_does_not_bypass_human_verification()
    {
        await using var host = await Host.StartAsync("prod");
        host.Turnstile.Result = false;

        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(host, Chat("/v1/assistant/chat", "prod", tier: "team")));
        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(host, Contact("prod", tier: "team")));
        Assert.Equal(Ip, host.Turnstile.LastRemoteIp);
    }

    [Fact]
    public async Task Contact_keeps_its_own_three_attempt_quota_even_for_team_visitors()
    {
        await using var host = await Host.StartAsync("prod");

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.OK, await StatusAsync(host, Contact("prod", tier: "team")));
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, await StatusAsync(host, Contact("prod", tier: "team")));

        // The contact quota is separate: chat for the same visitor is still available in full.
        for (var i = 0; i < 15; i++)
        {
            Assert.Equal(HttpStatusCode.OK, await StatusAsync(host, Chat("/v1/assistant/chat", "prod", tier: "team")));
        }
    }

    [Fact]
    public async Task Exhausting_chat_does_not_consume_the_contact_quota()
    {
        await using var host = await Host.StartAsync("prod");

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(HttpStatusCode.OK, await StatusAsync(host, Chat("/v1/assistant/chat", "prod")));
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, await StatusAsync(host, Chat("/v1/assistant/chat", "prod")));
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(host, Contact("prod")));
    }

    [Fact]
    public async Task Responses_never_echo_the_visitor_address()
    {
        await using var host = await Host.StartAsync("prod");

        for (var i = 0; i < 6; i++)
        {
            using var request = Chat("/v1/assistant/chat", "prod");
            using var response = await host.Client.SendAsync(request);
            Assert.DoesNotContain(Ip, await response.Content.ReadAsStringAsync());
        }

        using var forged = Chat("/v1/assistant/chat", "prod", sign: false);
        forged.Headers.TryAddWithoutValidation(SignedIdentity.IdentityHeader, "forged");
        forged.Headers.TryAddWithoutValidation(SignedIdentity.ProofHeader, "forged");
        using var forgedResponse = await host.Client.SendAsync(forged);
        Assert.Equal(HttpStatusCode.Forbidden, forgedResponse.StatusCode);
        using var body = JsonDocument.Parse(await forgedResponse.Content.ReadAsStringAsync());
        Assert.DoesNotContain("forged", body.RootElement.GetProperty("error").GetString());
    }
}
