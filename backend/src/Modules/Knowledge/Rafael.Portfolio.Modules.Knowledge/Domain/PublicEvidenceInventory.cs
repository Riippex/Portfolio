namespace Rafael.Portfolio.Modules.Knowledge.Domain;

public sealed record PublicEvidenceInventory(
    string Version,
    DateOnly LastUpdated,
    IReadOnlyList<PublicEvidenceItem> Items);
