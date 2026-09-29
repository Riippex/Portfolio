namespace Rafael.Portfolio.Modules.Knowledge.Domain;

public sealed record IngestedEvidenceDocument(
    PublicEvidenceItem Item,
    string Visibility,
    string RawMarkdown,
    IReadOnlyList<EvidenceSection> Sections);
