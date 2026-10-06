namespace Rafael.Portfolio.Modules.JobMatching.Domain;

/// <summary>
/// A requirement fully supported by a verified, relevant public claim. The claim
/// identifier and citation are the real ones from the evidence record; the
/// document kind and section identify the inspectable target.
/// </summary>
public sealed record JobEvidenceMatch(
    string RequirementId,
    string RequirementText,
    string DocumentSlug,
    string DocumentKind,
    string DocumentTitle,
    string SectionHeading,
    string SectionSlug,
    string ClaimId,
    string Citation,
    string? CitationUrl,
    string EvidenceStatus,
    string GroundingSummary);
