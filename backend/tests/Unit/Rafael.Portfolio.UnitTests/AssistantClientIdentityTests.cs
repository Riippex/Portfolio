using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Rafael.Portfolio.Web.Endpoints;

namespace Rafael.Portfolio.UnitTests;

public sealed class AssistantClientIdentityTests
{
    private const string Secret = "unit-proxy-secret";
    private const string VisitorIp = "203.0.113.9";
    private const string ConnectionIp = "192.0.2.44";

    private sealed class StubHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "UnitTests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static IConfiguration CreateConfiguration(string? secret = Secret, params string[] trustedProxies)
    {
        var values = new Dictionary<string, string?>
        {
            ["AssistantSecurity:ProxyIdentitySecret"] = secret
        };

        for (var i = 0; i < trustedProxies.Length; i++)
        {
            values[$"AssistantSecurity:TrustedProxies:{i}"] = trustedProxies[i];
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static DefaultHttpContext CreateContext(string remoteIp = ConnectionIp)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);
        return context;
    }

    private static string ComputeProof(string secret, string subject)
    {
        var bytes = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(subject));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static void Sign(DefaultHttpContext context, string visitorId, string proof)
    {
        context.Request.Headers["X-Client-Key"] = visitorId;
        context.Request.Headers["X-Client-Key-Proof"] = proof;
    }

    [Fact]
    public void Valid_signed_identity_yields_an_opaque_stable_key()
    {
        var context = CreateContext();
        Sign(context, VisitorIp, ComputeProof(Secret, VisitorIp));

        var error = AssistantEndpoints.ValidateIdentity(
            context, CreateConfiguration(), new StubHostEnvironment("Development"), out var identity);

        Assert.Null(error);
        Assert.NotNull(identity);
        Assert.Equal(AssistantEndpoints.ClientIdentityStatus.ValidSigned, identity.Status);
        Assert.Equal(VisitorIp, identity.TurnstileRemoteIp);
        Assert.StartsWith("k:", identity.RateLimitKey);
        Assert.DoesNotContain(VisitorIp, identity.RateLimitKey);
        Assert.Equal(
            AssistantEndpoints.DeriveOpaqueKey(Secret, $"ratelimit:{VisitorIp}"),
            identity.RateLimitKey);
    }

    [Fact]
    public void Wrong_secret_is_rejected_instead_of_falling_back()
    {
        var context = CreateContext();
        Sign(context, VisitorIp, ComputeProof("a-different-secret", VisitorIp));

        var error = AssistantEndpoints.ValidateIdentity(
            context, CreateConfiguration(), new StubHostEnvironment("Production"), out var identity);

        Assert.NotNull(error);
        Assert.Null(identity);
    }

    [Fact]
    public void Missing_one_signature_header_is_rejected()
    {
        var context = CreateContext();
        context.Request.Headers["X-Client-Key"] = VisitorIp;

        var error = AssistantEndpoints.ValidateIdentity(
            context, CreateConfiguration(), new StubHostEnvironment("Production"), out var identity);

        Assert.NotNull(error);
        Assert.Null(identity);
    }

    [Fact]
    public void Oversized_signed_identity_is_rejected()
    {
        var context = CreateContext();
        var oversized = new string('a', 129);
        Sign(context, oversized, ComputeProof(Secret, oversized));

        var error = AssistantEndpoints.ValidateIdentity(
            context, CreateConfiguration(), new StubHostEnvironment("Production"), out var identity);

        Assert.NotNull(error);
        Assert.Null(identity);
    }

    [Fact]
    public void Signed_headers_without_backend_secret_are_rejected()
    {
        var context = CreateContext();
        Sign(context, VisitorIp, ComputeProof(Secret, VisitorIp));

        var error = AssistantEndpoints.ValidateIdentity(
            context, CreateConfiguration(secret: null), new StubHostEnvironment("Development"), out var identity);

        Assert.NotNull(error);
        Assert.Null(identity);
    }

    [Fact]
    public void Production_requires_a_signed_identity()
    {
        var context = CreateContext();

        var error = AssistantEndpoints.ValidateIdentity(
            context, CreateConfiguration(), new StubHostEnvironment("Production"), out var identity);

        Assert.NotNull(error);
        Assert.Null(identity);
    }

    [Fact]
    public void Development_fallback_uses_an_opaque_connection_key()
    {
        var context = CreateContext();

        var error = AssistantEndpoints.ValidateIdentity(
            context, CreateConfiguration(secret: null), new StubHostEnvironment("Development"), out var identity);

        Assert.Null(error);
        Assert.NotNull(identity);
        Assert.Equal(AssistantEndpoints.ClientIdentityStatus.Absent, identity.Status);
        Assert.Equal(ConnectionIp, identity.TurnstileRemoteIp);
        Assert.StartsWith("k:", identity.RateLimitKey);
        Assert.DoesNotContain(ConnectionIp, identity.RateLimitKey);
    }

    [Fact]
    public void Trusted_proxy_fallback_pseudonymizes_the_forwarded_ip()
    {
        var context = CreateContext(remoteIp: "10.0.0.1");
        context.Request.Headers["X-Forwarded-For"] = VisitorIp;

        var error = AssistantEndpoints.ValidateIdentity(
            context,
            CreateConfiguration(secret: null, trustedProxies: "10.0.0.1"),
            new StubHostEnvironment("Development"),
            out var identity);

        Assert.Null(error);
        Assert.NotNull(identity);
        Assert.Equal(VisitorIp, identity.TurnstileRemoteIp);
        Assert.DoesNotContain(VisitorIp, identity.RateLimitKey);
        Assert.Equal(
            AssistantEndpoints.DeriveOpaqueKey(null, $"connection:{VisitorIp}"),
            identity.RateLimitKey);
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
