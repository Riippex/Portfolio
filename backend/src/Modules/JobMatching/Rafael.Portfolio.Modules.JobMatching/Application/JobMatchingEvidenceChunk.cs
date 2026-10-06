namespace Rafael.Portfolio.Modules.JobMatching.Application;

public sealed record JobMatchingEvidenceChunk(
    string ChunkId,
    string DocumentId,
    string Slug,
    string Title,
    string SectionHeading,
    string SectionSlug,
    string Content,
    IReadOnlyList<string> Claims,
    string? SourceUrl,
    string EvidenceStatus,
    string Version,
    string Visibility,
    double Score,
    IReadOnlyList<string> Citations);
