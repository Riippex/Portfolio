namespace Rafael.Portfolio.Modules.Portfolio.Domain;

public sealed record Profile(
    string Name,
    string Headline,
    string Summary,
    string EvidenceStatus,
    IReadOnlyList<string> FocusAreas);
