using System.Buffers.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Rafael.Portfolio.Web.Security;

namespace Rafael.Portfolio.UnitTests;

// Builds signed identity headers the way the Worker does, for tests that drive the backend.
internal static class TestIdentity
{
    internal const string Secret = "unit-proxy-secret";

    internal static string Encode(object claims) =>
        Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(claims));

    internal static (string Identity, string Proof) Sign(object claims, string secret = Secret)
    {
        var identity = Encode(claims);
        return (identity, SignedIdentity.ComputeProof(secret, identity));
    }

    internal static (string Identity, string Proof) Visitor(
        string ip,
        string method,
        string path,
        string stage = "local",
        string tier = "ordinary",
        string country = "CO",
        string secret = Secret) =>
        Sign(new { v = 2, kind = "visitor", ip, country, tier, stage, method, path }, secret);

    internal static (string Identity, string Proof) ServiceRead(
        string path,
        string stage,
        DateTimeOffset issuedAt,
        string secret = Secret) =>
        Sign(new { v = 2, kind = "service-read", stage, method = "GET", path, iat = issuedAt.ToUnixTimeSeconds() }, secret);

    internal static void Apply(HttpRequest request, (string Identity, string Proof) signed)
    {
        request.Headers[SignedIdentity.IdentityHeader] = signed.Identity;
        request.Headers[SignedIdentity.ProofHeader] = signed.Proof;
    }

    internal static void Apply(HttpRequestMessage request, (string Identity, string Proof) signed)
    {
        request.Headers.TryAddWithoutValidation(SignedIdentity.IdentityHeader, signed.Identity);
        request.Headers.TryAddWithoutValidation(SignedIdentity.ProofHeader, signed.Proof);
    }
}
