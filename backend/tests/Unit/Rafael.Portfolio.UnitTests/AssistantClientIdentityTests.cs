using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Rafael.Portfolio.Web.Endpoints;
using Rafael.Portfolio.Web.Security;

namespace Rafael.Portfolio.UnitTests;

public sealed class AssistantClientIdentityTests
{
    private const string Secret = TestIdentity.Secret;
    private const string VisitorIp = "203.0.113.9";
    private const string ConnectionIp = "192.0.2.44";
    private const string ChatPath = "/v1/assistant/chat/stream";

    private sealed class StubHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "UnitTests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static IConfiguration CreateConfiguration(string? secret = Secret, string? stage = null, params string[] trustedProxies)
    {
        var values = new Dictionary<string, string?>
        {
            ["AssistantSecurity:ProxyIdentitySecret"] = secret,
            [PortfolioStages.ConfigurationKey] = stage
        };

        for (var i = 0; i < trustedProxies.Length; i++)
        {
            values[$"AssistantSecurity:TrustedProxies:{i}"] = trustedProxies[i];
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static DefaultHttpContext CreateContext(string remoteIp = ConnectionIp, string method = "POST", string path = ChatPath)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);
        context.Request.Method = method;
        context.Request.Path = path;
        return context;
    }

    private static string? Validate(
        DefaultHttpContext context,
        string environment,
        string? stage,
        out AssistantEndpoints.ClientIdentity? identity,
        string? secret = Secret,
        params string[] trustedProxies) =>
        AssistantEndpoints.ValidateIdentity(
            context, CreateConfiguration(secret, stage, trustedProxies), new StubHostEnvironment(environment), out identity);

    [Fact]
    public void Valid_signed_identity_yields_an_opaque_stable_key()
    {
        var context = CreateContext();
        TestIdentity.Apply(context.Request, TestIdentity.Visitor(VisitorIp, "POST", ChatPath));

        var error = Validate(context, "Development", stage: null, out var identity);

        Assert.Null(error);
        Assert.NotNull(identity);
        Assert.Equal(AssistantEndpoints.ClientIdentityStatus.ValidSigned, identity.Status);
        Assert.Equal(VisitorIp, identity.TurnstileRemoteIp);
        Assert.StartsWith("k:", identity.RateLimitKey);
        Assert.DoesNotContain(VisitorIp, identity.RateLimitKey);
        Assert.Equal(AssistantEndpoints.DeriveOpaqueKey(Secret, $"ratelimit:{VisitorIp}"), identity.RateLimitKey);
        Assert.False(identity.IsTeamTier);
        Assert.Equal("CO", identity.CountryCode);
        Assert.Equal("local", identity.Stage);
    }

    [Fact]
    public void Tier_country_and_stage_come_from_the_signed_identity()
    {
        var context = CreateContext();
        TestIdentity.Apply(context.Request, TestIdentity.Visitor("2001:db8::1", "POST", ChatPath, stage: "prod", tier: "team", country: "US"));

        var error = Validate(context, "Production", "prod", out var identity);

        Assert.Null(error);
        Assert.True(identity!.IsTeamTier);
        Assert.Equal("US", identity.CountryCode);
        Assert.Equal("prod", identity.Stage);
        Assert.Equal("2001:db8::1", identity.TurnstileRemoteIp);
    }

    [Fact]
    public void One_visitor_has_one_key_across_chat_jobs_and_contact()
    {
        var keys = new[] { ChatPath, "/v1/assistant/chat", "/v1/jobs/analyze", "/v1/contact" }
            .Select(path =>
            {
                var context = CreateContext(path: path);
                TestIdentity.Apply(context.Request, TestIdentity.Visitor("2001:db8::1", "POST", path, stage: "prod"));
                Assert.Null(Validate(context, "Production", "prod", out var identity));
                return identity!.RateLimitKey;
            })
            .Distinct()
            .ToList();

        Assert.Single(keys);
    }

    [Fact]
    public void Different_visitors_get_different_keys_and_ipv4_and_ipv6_do_not_collide()
    {
        var keys = new[] { VisitorIp, "203.0.113.10", "2001:db8::1", "2001:db8::2" }
            .Select(ip =>
            {
                var context = CreateContext();
                TestIdentity.Apply(context.Request, TestIdentity.Visitor(ip, "POST", ChatPath, stage: "prod"));
                Validate(context, "Production", "prod", out var identity);
                return identity!.RateLimitKey;
            })
            .ToList();

        Assert.Equal(4, keys.Distinct().Count());
    }

    [Fact]
    public void Wrong_secret_is_rejected_instead_of_falling_back()
    {
        var context = CreateContext();
        TestIdentity.Apply(context.Request, TestIdentity.Visitor(VisitorIp, "POST", ChatPath, secret: "a-different-secret"));

        var error = Validate(context, "Production", "prod", out var identity);

        Assert.NotNull(error);
        Assert.Null(identity);
    }

    [Fact]
    public void Missing_one_signature_header_is_rejected()
    {
        var context = CreateContext();
        context.Request.Headers[SignedIdentity.IdentityHeader] = TestIdentity.Visitor(VisitorIp, "POST", ChatPath, stage: "prod").Identity;

        var error = Validate(context, "Production", "prod", out var identity);

        Assert.NotNull(error);
        Assert.Null(identity);
    }

    [Fact]
    public void Oversized_signed_identity_is_rejected()
    {
        var context = CreateContext();
        var oversized = new string('a', SignedIdentity.MaxIdentityLength + 1);
        context.Request.Headers[SignedIdentity.IdentityHeader] = oversized;
        context.Request.Headers[SignedIdentity.ProofHeader] = SignedIdentity.ComputeProof(Secret, oversized);

        var error = Validate(context, "Production", "prod", out var identity);

        Assert.NotNull(error);
        Assert.Null(identity);
    }

    [Fact]
    public void Signed_headers_without_backend_secret_are_rejected()
    {
        var context = CreateContext();
        TestIdentity.Apply(context.Request, TestIdentity.Visitor(VisitorIp, "POST", ChatPath));

        var error = Validate(context, "Development", stage: null, out var identity, secret: null);

        Assert.NotNull(error);
        Assert.Null(identity);
    }

    [Fact]
    public void Identity_for_another_stage_method_or_path_is_rejected()
    {
        var wrongStage = CreateContext();
        TestIdentity.Apply(wrongStage.Request, TestIdentity.Visitor(VisitorIp, "POST", ChatPath, stage: "dev", tier: "team"));
        Assert.NotNull(Validate(wrongStage, "Production", "prod", out _));

        var wrongPath = CreateContext(path: "/v1/jobs/analyze");
        TestIdentity.Apply(wrongPath.Request, TestIdentity.Visitor(VisitorIp, "POST", ChatPath, stage: "prod"));
        Assert.NotNull(Validate(wrongPath, "Production", "prod", out _));

        var wrongMethod = CreateContext(method: "GET");
        TestIdentity.Apply(wrongMethod.Request, TestIdentity.Visitor(VisitorIp, "POST", ChatPath, stage: "prod"));
        Assert.NotNull(Validate(wrongMethod, "Production", "prod", out _));
    }

    [Fact]
    public void A_service_read_identity_cannot_stand_in_for_a_visitor()
    {
        var context = CreateContext();
        TestIdentity.Apply(context.Request, TestIdentity.Sign(new
        {
            v = 2,
            kind = "service-read",
            stage = "prod",
            method = "POST",
            path = ChatPath,
            iat = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        }));

        var error = Validate(context, "Production", "prod", out var identity);

        Assert.NotNull(error);
        Assert.Null(identity);
    }

    [Fact]
    public void Malformed_claims_are_rejected_even_when_genuinely_signed()
    {
        var claims = new object[]
        {
            new { v = 2, kind = "visitor", ip = "203.0.113.9:443", country = "CO", tier = "ordinary", stage = "prod", method = "POST", path = ChatPath },
            new { v = 2, kind = "visitor", ip = "2001:DB8::1", country = "CO", tier = "ordinary", stage = "prod", method = "POST", path = ChatPath },
            new { v = 2, kind = "visitor", ip = VisitorIp, country = "co", tier = "ordinary", stage = "prod", method = "POST", path = ChatPath },
            new { v = 2, kind = "visitor", ip = VisitorIp, country = "CO", tier = "admin", stage = "prod", method = "POST", path = ChatPath },
            new { v = 1, kind = "visitor", ip = VisitorIp, country = "CO", tier = "ordinary", stage = "prod", method = "POST", path = ChatPath },
            new { v = 2, kind = "visitor", ip = VisitorIp, country = "CO", tier = "ordinary", stage = "prod", method = "POST", path = ChatPath, extra = 1 }
        };

        foreach (var candidate in claims)
        {
            var context = CreateContext();
            TestIdentity.Apply(context.Request, TestIdentity.Sign(candidate));
            Assert.NotNull(Validate(context, "Production", "prod", out var identity));
            Assert.Null(identity);
        }
    }

    [Fact]
    public void Legacy_colon_delimited_identity_is_no_longer_accepted()
    {
        var context = CreateContext();
        var legacy = $"v1:{VisitorIp}:CO:team:prod";
        context.Request.Headers["X-Client-Key"] = legacy;
        context.Request.Headers["X-Client-Key-Proof"] = SignedIdentity.ComputeProof(Secret, legacy);

        var error = Validate(context, "Production", "prod", out var identity);

        Assert.NotNull(error);
        Assert.Null(identity);
    }

    [Fact]
    public void Production_requires_a_signed_identity_and_ignores_forwarding_headers()
    {
        var context = CreateContext(remoteIp: "10.0.0.1");
        context.Request.Headers["CF-Connecting-IP"] = VisitorIp;
        context.Request.Headers["X-Forwarded-For"] = VisitorIp;

        var error = Validate(context, "Production", "prod", out var identity, Secret, "10.0.0.1");

        Assert.NotNull(error);
        Assert.Null(identity);
    }

    [Fact]
    public void A_deployed_stage_never_uses_the_development_fallback_even_in_a_development_host()
    {
        foreach (var stage in new[] { "dev", "prod" })
        {
            var context = CreateContext();

            var error = Validate(context, "Development", stage, out var identity);

            Assert.NotNull(error);
            Assert.Null(identity);
        }
    }

    [Fact]
    public void A_missing_or_invalid_stage_fails_closed_outside_development()
    {
        foreach (var stage in new string?[] { null, "", "local", "staging", "PROD" })
        {
            var context = CreateContext();
            TestIdentity.Apply(context.Request, TestIdentity.Visitor(VisitorIp, "POST", ChatPath, stage: "prod"));

            var error = Validate(context, "Production", stage, out var identity);

            Assert.Equal("Deployment stage is not configured.", error);
            Assert.Null(identity);
        }
    }

    [Fact]
    public void Development_fallback_uses_an_opaque_connection_key_only_in_the_local_stage()
    {
        var context = CreateContext();

        var error = Validate(context, "Development", stage: "local", out var identity, secret: null);

        Assert.Null(error);
        Assert.NotNull(identity);
        Assert.Equal(AssistantEndpoints.ClientIdentityStatus.Absent, identity.Status);
        Assert.Equal(ConnectionIp, identity.TurnstileRemoteIp);
        Assert.StartsWith("k:", identity.RateLimitKey);
        Assert.DoesNotContain(ConnectionIp, identity.RateLimitKey);
    }

    [Fact]
    public void Trusted_proxy_fallback_pseudonymizes_the_forwarded_ip_in_local_development_only()
    {
        var context = CreateContext(remoteIp: "10.0.0.1");
        context.Request.Headers["X-Forwarded-For"] = VisitorIp;

        var error = Validate(context, "Development", stage: null, out var identity, secret: null, "10.0.0.1");

        Assert.Null(error);
        Assert.NotNull(identity);
        Assert.Equal(VisitorIp, identity.TurnstileRemoteIp);
        Assert.DoesNotContain(VisitorIp, identity.RateLimitKey);
        Assert.Equal(AssistantEndpoints.DeriveOpaqueKey(null, $"connection:{VisitorIp}"), identity.RateLimitKey);
    }

    [Fact]
    public void Opaque_keys_are_stable_and_subject_scoped()
    {
        var first = AssistantEndpoints.DeriveOpaqueKey(Secret, "connection:203.0.113.1");
        var second = AssistantEndpoints.DeriveOpaqueKey(Secret, "connection:203.0.113.1");
        var other = AssistantEndpoints.DeriveOpaqueKey(Secret, "connection:203.0.113.2");

        Assert.Equal(first, second);
        Assert.NotEqual(first, other);
        Assert.DoesNotContain("203.0.113.1", first);
        Assert.DoesNotContain("203.0.113.2", other);
    }
}
