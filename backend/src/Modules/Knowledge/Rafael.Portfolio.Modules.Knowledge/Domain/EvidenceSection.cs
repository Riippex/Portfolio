namespace Rafael.Portfolio.Modules.Knowledge.Domain;

public sealed record EvidenceSection(
    string Heading,
    string Slug,
    string Content,
    IReadOnlyList<EvidenceClaim> Claims);
