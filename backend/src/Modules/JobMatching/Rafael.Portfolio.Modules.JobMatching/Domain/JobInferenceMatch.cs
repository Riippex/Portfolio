namespace Rafael.Portfolio.Modules.JobMatching.Domain;

public sealed record JobInferenceMatch(
    string RequirementId,
    string RequirementText,
    string InferredCapability,
    string SupportingDocumentSlug,
    string SupportingTitle,
    string SupportingEvidenceStatus,
    string Rationale);
