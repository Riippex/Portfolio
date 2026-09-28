namespace Rafael.Portfolio.Modules.Portfolio.Domain;

public sealed record ProjectDetail(
    string Slug,
    string Name,
    string Summary,
    string EvidenceStatus,
    string? SourceUrl,
    DateOnly LastReviewed,
    IReadOnlyList<ProjectClaim> Claims);
