namespace Rafael.Portfolio.Modules.Knowledge.Domain;

public sealed record PublicEvidenceItem(
    string Id,
    string Slug,
    string Kind,
    string Title,
    string Summary,
    string Version,
    string EvidenceStatus,
    string? SourceUrl,
    string DocumentPath,
    DateOnly LastReviewed,
    IReadOnlyList<EvidenceClaim> Claims);
