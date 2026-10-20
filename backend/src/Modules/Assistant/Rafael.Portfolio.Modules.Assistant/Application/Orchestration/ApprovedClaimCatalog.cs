using System.Text;
using System.Text.Json;
using Rafael.Portfolio.Modules.Assistant.Domain;

namespace Rafael.Portfolio.Modules.Assistant.Application.Orchestration;

internal sealed record OfferedClaim(string Alias, AssistantEvidenceChunk Chunk, AssistantApprovedClaim Claim);

internal static class ApprovedClaimCatalog
{
    internal const string Schema = """
        {"type":"object","additionalProperties":false,"required":["v","outcome","claims"],"properties":{"v":{"type":"integer","const":1},"outcome":{"enum":["answer","insufficient"]},"claims":{"type":"array","maxItems":3,"uniqueItems":true,"items":{"type":"string"}}}}
        """;

    internal static OfferedClaim[] Create(IReadOnlyList<AssistantEvidenceChunk> chunks)
    {
        var result = new List<OfferedClaim>();
        var seen = new HashSet<(string, string, string)>();
        foreach (var chunk in chunks.Where(c => c.EvidenceStatus.Equals("verified", StringComparison.OrdinalIgnoreCase)
                     && c.Visibility.Equals("public", StringComparison.OrdinalIgnoreCase) && c.Score > 0))
        {
            foreach (var claim in chunk.ApprovedClaims ?? [])
            {
                if (result.Count == 6) return result.ToArray();
                if (!claim.Status.Equals("verified", StringComparison.OrdinalIgnoreCase)
                    || string.IsNullOrWhiteSpace(claim.Statement) || claim.Statement.Length > 400
                    || string.IsNullOrWhiteSpace(claim.ClaimId) || string.IsNullOrWhiteSpace(claim.Citation)
                    || !chunk.Claims.Contains(claim.ClaimId)
                    || !seen.Add((chunk.DocumentId, chunk.Version, claim.ClaimId))) continue;
                result.Add(new OfferedClaim($"C{result.Count + 1}", chunk, claim));
            }
        }
        return result.ToArray();
    }

    // The byte bound is checked before allocation/parsing. A UTF-16 length check first bounds
    // the scan even if an untrusted provider returns a huge string. Neither is a token estimate.
    internal static string[]? Validate(string? text, OfferedClaim[] offered)
    {
        if (text is null || text.Length > 256 || Encoding.UTF8.GetByteCount(text) > 256) return null;
        try
        {
            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(text), new JsonReaderOptions { MaxDepth = 3 });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return null;
            var properties = new HashSet<string>(StringComparer.Ordinal);
            string? outcome = null;
            var claims = new List<string>();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName || reader.ValueIsEscaped) return null;
                var property = reader.GetString()!;
                if (!properties.Add(property) || !reader.Read()) return null;
                switch (property)
                {
                    case "v":
                        if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out var version) || version != 1) return null;
                        break;
                    case "outcome":
                        if (reader.TokenType != JsonTokenType.String || reader.ValueIsEscaped) return null;
                        outcome = reader.GetString();
                        if (outcome is not ("answer" or "insufficient")) return null;
                        break;
                    case "claims":
                        if (reader.TokenType != JsonTokenType.StartArray) return null;
                        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                        {
                            if (reader.TokenType != JsonTokenType.String || reader.ValueIsEscaped || claims.Count == 3) return null;
                            var alias = reader.GetString()!;
                            if (claims.Contains(alias) || !offered.Any(c => c.Alias == alias)) return null;
                            claims.Add(alias);
                        }
                        if (reader.TokenType != JsonTokenType.EndArray) return null;
                        break;
                    default: return null;
                }
            }
            if (reader.TokenType != JsonTokenType.EndObject || properties.Count != 3 || reader.Read()) return null;
            return outcome == "answer" && claims.Count > 0 || outcome == "insufficient" && claims.Count == 0
                ? claims.ToArray() : null;
        }
        catch (JsonException) { return null; }
    }

    internal static AssistantChatResponse Render(string[] selected, OfferedClaim[] offered)
    {
        if (selected.Length == 0) return new(AssistantService.NotDocumentedMessage, AssistantGroundingStatus.NotDocumented, []);
        var chosen = selected.Select(alias => offered.Single(c => c.Alias == alias)).ToArray();
        var answer = "Here is what Rafael's public profile documents:\n\n"
            + string.Join("\n", chosen.Select(c => $"- {c.Claim.Statement}"));
        var citations = chosen.GroupBy(c => c.Chunk.ChunkId).Select(group =>
        {
            var chunk = group.First().Chunk;
            return new AssistantCitation(chunk.ChunkId, chunk.DocumentId, chunk.Slug, chunk.Title,
                chunk.SectionHeading, chunk.SourceUrl, chunk.EvidenceStatus, chunk.Version,
                group.Select(c => c.Claim.ClaimId).ToArray());
        }).ToArray();
        return new(answer, AssistantGroundingStatus.Grounded, citations);
    }
}
