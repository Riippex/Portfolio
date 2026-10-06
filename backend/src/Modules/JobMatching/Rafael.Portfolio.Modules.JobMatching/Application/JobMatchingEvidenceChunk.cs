namespace Rafael.Portfolio.Modules.JobMatching.Application;

public sealed record JobMatchingEvidenceClaim(
    string ClaimId,
    string Statement,
    string Status,
    string Citation);

public sealed record JobMatchingEvidenceChunk(
    string ChunkId,
    string DocumentId,
    string Slug,
    string Kind,
    string Title,
    string SectionHeading,
    string SectionSlug,
    string Content,
    IReadOnlyList<JobMatchingEvidenceClaim> Claims,
    string? SourceUrl,
    string EvidenceStatus,
    string Version,
    string Visibility,
    double Score,
    IReadOnlyList<string> Citations);
