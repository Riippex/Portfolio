namespace Rafael.Portfolio.Modules.JobMatching.Domain;

public sealed record JobEvidenceMatch(
    string RequirementId,
    string RequirementText,
    string DocumentSlug,
    string DocumentTitle,
    string SectionHeading,
    string ClaimId,
    string? CitationUrl,
    string EvidenceStatus,
    string GroundingSummary);
