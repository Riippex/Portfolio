namespace Rafael.Portfolio.Modules.Portfolio.Domain;

public sealed record ProjectSummary(
    string Slug,
    string Name,
    string Summary,
    string EvidenceStatus);
