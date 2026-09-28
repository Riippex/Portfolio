namespace Rafael.Portfolio.Modules.Portfolio.Domain;

public sealed record Profile(
    string Name,
    string Headline,
    string Summary,
    IReadOnlyList<string> FocusAreas);
