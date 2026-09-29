namespace Rafael.Portfolio.Modules.Knowledge.Domain;

public sealed record RetrievedChunk(
    string ChunkId,
    string DocumentId,
    string Slug,
    string Title,
    string SectionHeading,
    string SectionSlug,
    string Content,
    IReadOnlyList<EvidenceClaim> Claims,
    string? SourceUrl,
    string EvidenceStatus,
    string Version,
    string Visibility,
    double Score,
    IReadOnlyList<string> Citations);
