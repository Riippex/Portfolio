namespace Rafael.Portfolio.Modules.Assistant.Domain;

public sealed record AssistantCitation(
    string ChunkId,
    string DocumentId,
    string Slug,
    string Title,
    string SectionHeading,
    string? SourceUrl,
    string EvidenceStatus,
    string Version,
    IReadOnlyList<string> Claims);
