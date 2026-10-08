using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using Rafael.Portfolio.Web.Security;

namespace Rafael.Portfolio.UnitTests;

// The frontend (src/modules/security) and the backend both check docs/contracts/visitor-identity-v2.json,
// so a value that one side signs or canonicalizes is accepted identically by the other. The vector proofs
// were produced by an independent HMAC implementation.
public sealed class SignedIdentityContractTests
{
    private static readonly JsonElement Vectors = LoadVectors();

    private static JsonElement LoadVectors()
    {
        var path = Path.Combine(TestRepositoryRoot.Find(), "docs", "contracts", "visitor-identity-v2.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.Clone();
    }

    public static TheoryData<string, string> CanonicalIps()
    {
        var data = new TheoryData<string, string>();
        foreach (var item in Vectors.GetProperty("ip").GetProperty("canonical").EnumerateArray())
        {
            data.Add(item.GetProperty("input").GetString()!, item.GetProperty("canonical").GetString()!);
        }

        return data;
    }

    public static TheoryData<string> InvalidIps()
    {
        var data = new TheoryData<string>();
        foreach (var item in Vectors.GetProperty("ip").GetProperty("invalid").EnumerateArray())
        {
            data.Add(item.GetString()!);
        }

        return data;
    }

    public static TheoryData<string> ValidIdentityNames()
    {
        var data = new TheoryData<string>();
        foreach (var item in Vectors.GetProperty("identity").GetProperty("valid").EnumerateArray())
        {
            data.Add(item.GetProperty("name").GetString()!);
        }

        return data;
    }

    public static TheoryData<string> InvalidIdentityNames()
    {
        var data = new TheoryData<string>();
        foreach (var item in Vectors.GetProperty("identity").GetProperty("invalid").EnumerateArray())
        {
            data.Add(item.GetProperty("name").GetString()!);
        }

        return data;
    }

    private static JsonElement Vector(string group, string name) =>
        Vectors.GetProperty("identity").GetProperty(group).EnumerateArray()
            .First(item => item.GetProperty("name").GetString() == name);

    [Fact]
    public void Header_names_and_proof_domain_match_the_contract()
    {
        Assert.Equal(SignedIdentity.IdentityHeader, Vectors.GetProperty("headers").GetProperty("identity").GetString());
        Assert.Equal(SignedIdentity.ProofHeader, Vectors.GetProperty("headers").GetProperty("proof").GetString());
        Assert.Equal(SignedIdentity.ProofDomain, Vectors.GetProperty("proofDomain").GetString());
    }

    [Theory]
    [MemberData(nameof(CanonicalIps))]
    public void Canonicalizes_exact_addresses(string input, string expected)
    {
        Assert.True(IpCanonicalizer.TryCanonicalize(input, out var canonical));
        Assert.Equal(expected, canonical);
        Assert.True(IpCanonicalizer.TryCanonicalize(canonical, out var again));
        Assert.Equal(canonical, again);
    }

    [Theory]
    [MemberData(nameof(InvalidIps))]
    public void Rejects_everything_that_is_not_an_exact_address(string input)
    {
        Assert.False(IpCanonicalizer.TryCanonicalize(input, out _));
    }

    [Fact]
    public void Rejects_null_and_over_long_input()
    {
        Assert.False(IpCanonicalizer.TryCanonicalize(null, out _));
        Assert.False(IpCanonicalizer.TryCanonicalize(new string('1', 300), out _));
    }

    [Theory]
    [MemberData(nameof(ValidIdentityNames))]
    public void Valid_vectors_parse_to_the_expected_claims_and_proof(string name)
    {
        var vector = Vector("valid", name);
        var identity = vector.GetProperty("identity").GetString()!;
        var claims = vector.GetProperty("claims");

        Assert.Equal(vector.GetProperty("proof").GetString(), SignedIdentity.ComputeProof(Vectors.GetProperty("secret").GetString()!, identity));
        Assert.True(SignedIdentity.TryParseClaims(identity, out var parsed));

        Assert.Equal(claims.GetProperty("method").GetString(), parsed.Method);
        Assert.Equal(claims.GetProperty("path").GetString(), parsed.Path);
        Assert.Equal(claims.GetProperty("stage").GetString(), PortfolioStages.ToText(parsed.Stage));

        if (claims.GetProperty("kind").GetString() == "visitor")
        {
            Assert.Equal(SignedIdentityKind.Visitor, parsed.Kind);
            Assert.Equal(claims.GetProperty("ip").GetString(), parsed.Ip);
            Assert.Equal(claims.GetProperty("country").GetString(), parsed.Country);
            Assert.Equal(claims.GetProperty("tier").GetString() == "team" ? VisitorTier.Team : VisitorTier.Ordinary, parsed.Tier);
        }
        else
        {
            Assert.Equal(SignedIdentityKind.ServiceRead, parsed.Kind);
            Assert.Equal(claims.GetProperty("iat").GetInt64(), parsed.IssuedAt);
        }
    }

    [Theory]
    [MemberData(nameof(InvalidIdentityNames))]
    public void Correctly_signed_but_invalid_payloads_are_rejected(string name)
    {
        var vector = Vector("invalid", name);
        var identity = vector.GetProperty("identity").GetString()!;

        Assert.Equal(vector.GetProperty("proof").GetString(), SignedIdentity.ComputeProof(Vectors.GetProperty("secret").GetString()!, identity));
        Assert.False(SignedIdentity.TryParseClaims(identity, out _));
    }

    [Theory]
    [MemberData(nameof(InvalidIdentityNames))]
    public void Verify_rejects_invalid_payloads_even_when_the_proof_is_genuine(string name)
    {
        var vector = Vector("invalid", name);
        var request = new DefaultHttpContext().Request;
        request.Method = "POST";
        request.Path = "/v1/assistant/chat/stream";
        request.Headers[SignedIdentity.IdentityHeader] = vector.GetProperty("identity").GetString();
        request.Headers[SignedIdentity.ProofHeader] = vector.GetProperty("proof").GetString();

        var outcome = SignedIdentity.Verify(
            request, Vectors.GetProperty("secret").GetString(), PortfolioStage.Prod, TimeProvider.System, out var identity);

        Assert.Equal(SignedIdentityOutcome.Invalid, outcome);
        Assert.Null(identity);
    }
}

public sealed class SignedIdentityVerificationTests
{
    private const string Ip = "203.0.113.9";
    private const string Path = "/v1/assistant/chat/stream";
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    private static HttpRequest Request(string method = "POST", string path = Path)
    {
        var request = new DefaultHttpContext().Request;
        request.Method = method;
        request.Path = path;
        return request;
    }

    private static SignedIdentityOutcome Verify(
        HttpRequest request,
        string? secret = TestIdentity.Secret,
        PortfolioStage stage = PortfolioStage.Prod,
        TimeProvider? time = null) =>
        SignedIdentity.Verify(request, secret, stage, time ?? new FakeTimeProvider(Now), out _);

    [Fact]
    public void Accepts_a_genuine_identity_bound_to_this_request()
    {
        var request = Request();
        TestIdentity.Apply(request, TestIdentity.Visitor(Ip, "POST", Path, stage: "prod", tier: "team", country: "US"));

        var outcome = SignedIdentity.Verify(request, TestIdentity.Secret, PortfolioStage.Prod, TimeProvider.System, out var identity);

        Assert.Equal(SignedIdentityOutcome.Valid, outcome);
        Assert.Equal(Ip, identity!.Ip);
        Assert.Equal("US", identity.Country);
        Assert.Equal(VisitorTier.Team, identity.Tier);
    }

    [Fact]
    public void Accepts_canonical_ipv6_visitors()
    {
        var request = Request();
        TestIdentity.Apply(request, TestIdentity.Visitor("2001:db8::1", "POST", Path, stage: "prod"));

        Assert.Equal(SignedIdentityOutcome.Valid, Verify(request));
    }

    [Fact]
    public void No_headers_means_absent_and_legacy_headers_are_ignored()
    {
        var request = Request();
        request.Headers["X-Client-Key"] = $"v1:{Ip}:CO:team:prod";
        request.Headers["X-Client-Key-Proof"] = "anything";
        request.Headers["X-Client-Tier"] = "team";

        Assert.Equal(SignedIdentityOutcome.Absent, Verify(request));
    }

    [Fact]
    public void Rejects_a_tampered_identity_or_proof()
    {
        var (identity, proof) = TestIdentity.Visitor(Ip, "POST", Path, stage: "prod");
        var forgedTeam = TestIdentity.Encode(new { v = 2, kind = "visitor", ip = Ip, country = "CO", tier = "team", stage = "prod", method = "POST", path = Path });

        var cases = new (string Identity, string Proof)[]
        {
            (forgedTeam, proof),
            (identity, proof[..^1] + (proof[^1] == '0' ? '1' : '0')),
            (identity, proof.ToUpperInvariant()),
            (identity + "A", proof),
            (identity, proof + "0"),
            (identity, proof[..^2])
        };

        foreach (var (candidateIdentity, candidateProof) in cases)
        {
            var request = Request();
            request.Headers[SignedIdentity.IdentityHeader] = candidateIdentity;
            request.Headers[SignedIdentity.ProofHeader] = candidateProof;
            Assert.Equal(SignedIdentityOutcome.Invalid, Verify(request));
        }
    }

    [Fact]
    public void Rejects_a_proof_made_with_another_secret_or_without_the_domain()
    {
        var wrongSecret = Request();
        TestIdentity.Apply(wrongSecret, TestIdentity.Visitor(Ip, "POST", Path, stage: "prod", secret: "another-secret"));
        Assert.Equal(SignedIdentityOutcome.Invalid, Verify(wrongSecret));

        var identity = TestIdentity.Encode(new { v = 2, kind = "visitor", ip = Ip, country = "CO", tier = "ordinary", stage = "prod", method = "POST", path = Path });
        var noDomain = Request();
        noDomain.Headers[SignedIdentity.IdentityHeader] = identity;
        noDomain.Headers[SignedIdentity.ProofHeader] = Convert.ToHexString(
            System.Security.Cryptography.HMACSHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(TestIdentity.Secret), System.Text.Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        Assert.Equal(SignedIdentityOutcome.Invalid, Verify(noDomain));
    }

    [Fact]
    public void Rejects_incomplete_oversized_or_unverifiable_identities()
    {
        var onlyIdentity = Request();
        onlyIdentity.Headers[SignedIdentity.IdentityHeader] = TestIdentity.Visitor(Ip, "POST", Path, stage: "prod").Identity;
        Assert.Equal(SignedIdentityOutcome.Invalid, Verify(onlyIdentity));

        var onlyProof = Request();
        onlyProof.Headers[SignedIdentity.ProofHeader] = "0".PadLeft(64, '0');
        Assert.Equal(SignedIdentityOutcome.Invalid, Verify(onlyProof));

        var oversized = new string('a', SignedIdentity.MaxIdentityLength + 1);
        var oversizedRequest = Request();
        oversizedRequest.Headers[SignedIdentity.IdentityHeader] = oversized;
        oversizedRequest.Headers[SignedIdentity.ProofHeader] = SignedIdentity.ComputeProof(TestIdentity.Secret, oversized);
        Assert.Equal(SignedIdentityOutcome.Invalid, Verify(oversizedRequest));

        var signed = Request();
        TestIdentity.Apply(signed, TestIdentity.Visitor(Ip, "POST", Path, stage: "prod"));
        Assert.Equal(SignedIdentityOutcome.Invalid, Verify(signed, secret: null));
        Assert.Equal(SignedIdentityOutcome.Invalid, Verify(signed, secret: " "));
    }

    [Fact]
    public void Rejects_repeated_identity_headers()
    {
        var request = Request();
        var (identity, proof) = TestIdentity.Visitor(Ip, "POST", Path, stage: "prod");
        request.Headers[SignedIdentity.IdentityHeader] = new[] { identity, identity };
        request.Headers[SignedIdentity.ProofHeader] = proof;
        Assert.Equal(SignedIdentityOutcome.Invalid, Verify(request));
    }

    [Fact]
    public void Binds_the_identity_to_the_expected_stage()
    {
        var request = Request();
        TestIdentity.Apply(request, TestIdentity.Visitor(Ip, "POST", Path, stage: "dev", tier: "team"));

        Assert.Equal(SignedIdentityOutcome.Valid, Verify(request, stage: PortfolioStage.Dev));
        Assert.Equal(SignedIdentityOutcome.Invalid, Verify(request, stage: PortfolioStage.Prod));
        Assert.Equal(SignedIdentityOutcome.Invalid, Verify(request, stage: PortfolioStage.Local));
    }

    [Fact]
    public void Binds_the_identity_to_the_method_and_path_of_the_request()
    {
        var signed = TestIdentity.Visitor(Ip, "POST", Path, stage: "prod");

        var otherPath = Request(path: "/v1/jobs/analyze");
        TestIdentity.Apply(otherPath, signed);
        Assert.Equal(SignedIdentityOutcome.Invalid, Verify(otherPath));

        var otherMethod = Request(method: "PUT");
        TestIdentity.Apply(otherMethod, signed);
        Assert.Equal(SignedIdentityOutcome.Invalid, Verify(otherMethod));

        var caseDifference = Request(path: "/V1/assistant/chat/stream");
        TestIdentity.Apply(caseDifference, signed);
        Assert.Equal(SignedIdentityOutcome.Invalid, Verify(caseDifference));
    }

    [Fact]
    public void Service_reads_are_time_bound()
    {
        var time = new FakeTimeProvider(Now);
        var request = Request("GET", "/v1/profile");
        TestIdentity.Apply(request, TestIdentity.ServiceRead("/v1/profile", "dev", Now));

        Assert.Equal(SignedIdentityOutcome.Valid, Verify(request, stage: PortfolioStage.Dev, time: time));

        time.Advance(SignedIdentity.ServiceReadMaxAge - TimeSpan.FromSeconds(1));
        Assert.Equal(SignedIdentityOutcome.Valid, Verify(request, stage: PortfolioStage.Dev, time: time));

        time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(SignedIdentityOutcome.Invalid, Verify(request, stage: PortfolioStage.Dev, time: time));

        var future = Request("GET", "/v1/profile");
        TestIdentity.Apply(future, TestIdentity.ServiceRead("/v1/profile", "dev", Now + SignedIdentity.ServiceReadMaxClockSkew + TimeSpan.FromSeconds(5)));
        Assert.Equal(SignedIdentityOutcome.Invalid, Verify(future, stage: PortfolioStage.Dev, time: new FakeTimeProvider(Now)));
    }

    [Fact]
    public void Service_reads_are_bound_to_their_path_and_to_get()
    {
        var otherPath = Request("GET", "/v1/projects");
        TestIdentity.Apply(otherPath, TestIdentity.ServiceRead("/v1/profile", "dev", Now));
        Assert.Equal(SignedIdentityOutcome.Invalid, Verify(otherPath, stage: PortfolioStage.Dev));

        var post = Request("POST", "/v1/profile");
        TestIdentity.Apply(post, TestIdentity.ServiceRead("/v1/profile", "dev", Now));
        Assert.Equal(SignedIdentityOutcome.Invalid, Verify(post, stage: PortfolioStage.Dev));
    }
}

public sealed class PortfolioStageTests
{
    private sealed class StubHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "UnitTests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static IConfiguration Configuration(string? stage) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [PortfolioStages.ConfigurationKey] = stage })
            .Build();

    [Theory]
    [InlineData("dev", "Production", true, "dev")]
    [InlineData("prod", "Production", true, "prod")]
    [InlineData("dev", "Development", true, "dev")]
    [InlineData("prod", "Test", true, "prod")]
    [InlineData("local", "Development", true, "local")]
    [InlineData("local", "Test", true, "local")]
    [InlineData(null, "Development", true, "local")]
    [InlineData("", "Test", true, "local")]
    [InlineData(null, "Production", false, null)]
    [InlineData("", "Production", false, null)]
    [InlineData("local", "Production", false, null)]
    [InlineData("staging", "Production", false, null)]
    [InlineData("DEV", "Production", false, null)]
    [InlineData("Prod", "Production", false, null)]
    [InlineData(" dev", "Production", false, null)]
    [InlineData("development", "Development", false, null)]
    [InlineData("production", "Production", false, null)]
    public void Resolves_only_an_explicit_valid_stage(string? configured, string environmentName, bool resolved, string? expected)
    {
        var ok = PortfolioStages.TryResolve(Configuration(configured), new StubHostEnvironment(environmentName), out var stage);

        Assert.Equal(resolved, ok);
        if (resolved)
        {
            Assert.Equal(expected, PortfolioStages.ToText(stage));
        }
    }

    [Fact]
    public void Resolve_throws_with_an_actionable_message_for_an_invalid_stage()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => PortfolioStages.Resolve(Configuration(null), new StubHostEnvironment("Production")));

        Assert.Contains("Portfolio:Stage", error.Message);
    }
}
