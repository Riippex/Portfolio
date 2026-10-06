namespace Rafael.Portfolio.Modules.JobMatching.Domain;

/// <summary>
/// A capability that public evidence supports only indirectly (pending evidence,
/// no verified relevant claim, or a requirement qualifier that remains a gap).
/// </summary>
public sealed record JobInferenceMatch(
    string RequirementId,
    string RequirementText,
    string InferredCapability,
    string SupportingDocumentSlug,
    string SupportingDocumentKind,
    string SupportingTitle,
    string SupportingSectionSlug,
    string SupportingCitation,
    string? SupportingClaimId,
    string SupportingEvidenceStatus,
    string Rationale);
