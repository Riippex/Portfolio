namespace Rafael.Portfolio.Modules.Portfolio.Domain;

public sealed record Profile(
    string Name,
    string Headline,
    string Summary,
    string EvidenceStatus,
    IReadOnlyList<string> FocusAreas,
    IReadOnlyList<ProfileLink>? Links = null)
{
    public IReadOnlyList<ProfileLink> Links { get; init; } = Links ?? [];
}
