using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Rafael.Portfolio.Web.Security;

internal enum SignedIdentityKind
{
    /// <summary>A visitor admitted by the Worker: platform IP, country, tier, stage, call.</summary>
    Visitor,

    /// <summary>A Worker-signed, time-bound read of public data for the private dev stage.</summary>
    ServiceRead
}

internal enum VisitorTier
{
    Ordinary,
    Team
}

internal sealed record VerifiedIdentity(
    SignedIdentityKind Kind,
    PortfolioStage Stage,
    string Method,
    string Path,
    string? Ip = null,
    string? Country = null,
    VisitorTier Tier = VisitorTier.Ordinary,
    long IssuedAt = 0);

internal enum SignedIdentityOutcome
{
    /// <summary>No identity header was sent.</summary>
    Absent,

    /// <summary>The identity is genuine, well formed and bound to this stage and request.</summary>
    Valid,

    /// <summary>An identity was sent but is incomplete, forged, malformed or bound elsewhere.</summary>
    Invalid
}

/// <summary>
/// Verifies the signed visitor identity, version 2. The frontend implements the same contract
/// (<c>src/modules/security/identity.ts</c>); both are tested against
/// <c>docs/contracts/visitor-identity-v2.json</c>.
/// <list type="bullet">
/// <item><c>X-Portfolio-Identity</c>: base64url (no padding) of UTF-8 JSON claims.</item>
/// <item><c>X-Portfolio-Identity-Proof</c>: lowercase hex HMAC-SHA256 over
/// <c>"portfolio-identity-v2\n"</c> plus the exact identity header as transmitted.</item>
/// </list>
/// The proof is checked, in constant time, before the payload is parsed. Claims are then
/// validated strictly (exact claim set, canonical IP, known tier and stage) and must match the
/// backend's configured stage and this request's method and path.
/// </summary>
internal static partial class SignedIdentity
{
    public const string IdentityHeader = "X-Portfolio-Identity";
    public const string ProofHeader = "X-Portfolio-Identity-Proof";
    public const string ProofDomain = "portfolio-identity-v2\n";
    public const int MaxIdentityLength = 1024;

    public static readonly TimeSpan ServiceReadMaxAge = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan ServiceReadMaxClockSkew = TimeSpan.FromMinutes(1);

    private static readonly string[] VisitorClaims = ["v", "kind", "ip", "country", "tier", "stage", "method", "path"];
    private static readonly string[] ServiceReadClaims = ["v", "kind", "stage", "method", "path", "iat"];

    public static string ComputeProof(string secret, string identityHeader)
    {
        var bytes = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret),
            Encoding.UTF8.GetBytes(ProofDomain + identityHeader));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static SignedIdentityOutcome Verify(
        HttpRequest request,
        string? secret,
        PortfolioStage expectedStage,
        TimeProvider timeProvider,
        out VerifiedIdentity? identity)
    {
        identity = null;

        var header = request.Headers[IdentityHeader].FirstOrDefault();
        var proof = request.Headers[ProofHeader].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(header) && string.IsNullOrWhiteSpace(proof))
        {
            return SignedIdentityOutcome.Absent;
        }

        if (request.Headers[IdentityHeader].Count > 1 || request.Headers[ProofHeader].Count > 1 ||
            string.IsNullOrEmpty(header) || string.IsNullOrEmpty(proof) || string.IsNullOrWhiteSpace(secret) ||
            header.Length > MaxIdentityLength)
        {
            return SignedIdentityOutcome.Invalid;
        }

        var expected = Encoding.ASCII.GetBytes(ComputeProof(secret, header));
        var provided = Encoding.ASCII.GetBytes(proof);
        if (expected.Length != provided.Length || !CryptographicOperations.FixedTimeEquals(expected, provided))
        {
            return SignedIdentityOutcome.Invalid;
        }

        if (!TryParseClaims(header, out var claims) ||
            claims.Stage != expectedStage ||
            !string.Equals(claims.Method, request.Method, StringComparison.Ordinal) ||
            !string.Equals(claims.Path, request.Path.Value, StringComparison.Ordinal))
        {
            return SignedIdentityOutcome.Invalid;
        }

        if (claims.Kind == SignedIdentityKind.ServiceRead)
        {
            var age = timeProvider.GetUtcNow() - DateTimeOffset.FromUnixTimeSeconds(claims.IssuedAt);
            if (age > ServiceReadMaxAge || age < -ServiceReadMaxClockSkew)
            {
                return SignedIdentityOutcome.Invalid;
            }
        }

        identity = claims;
        return SignedIdentityOutcome.Valid;
    }

    /// <summary>Strictly decodes and validates the claims of an identity header.</summary>
    internal static bool TryParseClaims(string identity, out VerifiedIdentity claims)
    {
        claims = null!;
        if (identity.Length == 0 || identity.Length > MaxIdentityLength || !Base64UrlText().IsMatch(identity))
        {
            return false;
        }

        byte[] bytes;
        string json;
        try
        {
            bytes = Base64Url.DecodeFromChars(identity);
            json = new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException or DecoderFallbackException)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 4 });
            return TryReadClaims(document.RootElement, out claims);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadClaims(JsonElement root, out VerifiedIdentity claims)
    {
        claims = null!;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!properties.TryAdd(property.Name, property.Value))
            {
                return false;
            }
        }

        if (!properties.TryGetValue("kind", out var kindElement) || kindElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var isVisitor = kindElement.GetString() == "visitor";
        var isServiceRead = kindElement.GetString() == "service-read";
        var expectedClaims = isVisitor ? VisitorClaims : isServiceRead ? ServiceReadClaims : null;
        if (expectedClaims is null ||
            properties.Count != expectedClaims.Length ||
            !expectedClaims.All(properties.ContainsKey) ||
            !TryInteger(properties["v"], out var version) || version != 2 ||
            !TryString(properties, "stage", out var stageText) || !PortfolioStages.TryParse(stageText, out var stage) ||
            !TryString(properties, "method", out var method) || !MethodText().IsMatch(method) ||
            !TryString(properties, "path", out var path) || !PathText().IsMatch(path))
        {
            return false;
        }

        if (isServiceRead)
        {
            if (method != "GET" || !TryInteger(properties["iat"], out var issuedAt) || issuedAt <= 0)
            {
                return false;
            }

            claims = new VerifiedIdentity(SignedIdentityKind.ServiceRead, stage, method, path, IssuedAt: issuedAt);
            return true;
        }

        if (!TryString(properties, "ip", out var ip) || !IpCanonicalizer.TryCanonicalize(ip, out var canonicalIp) || canonicalIp != ip ||
            !TryString(properties, "country", out var country) || !CountryText().IsMatch(country) ||
            !TryString(properties, "tier", out var tierText) || tierText is not ("ordinary" or "team"))
        {
            return false;
        }

        claims = new VerifiedIdentity(
            SignedIdentityKind.Visitor,
            stage,
            method,
            path,
            ip,
            country,
            tierText == "team" ? VisitorTier.Team : VisitorTier.Ordinary);
        return true;
    }

    private static bool TryInteger(JsonElement element, out long value)
    {
        value = 0;
        return element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out value);
    }

    private static bool TryString(Dictionary<string, JsonElement> properties, string name, out string value)
    {
        value = string.Empty;
        if (properties[name].ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = properties[name].GetString() ?? string.Empty;
        return true;
    }

    [GeneratedRegex(@"^[A-Za-z0-9_-]+\z")]
    private static partial Regex Base64UrlText();

    [GeneratedRegex(@"^[A-Z]{3,7}\z")]
    private static partial Regex MethodText();

    [GeneratedRegex(@"^/[!-~]{0,255}\z")]
    private static partial Regex PathText();

    [GeneratedRegex(@"^[A-Z][A-Z0-9]\z")]
    private static partial Regex CountryText();
}
