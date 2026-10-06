namespace Rafael.Portfolio.Modules.JobMatching.Domain;

public sealed record JobAnalysisResponse(
    string RoleSummary,
    IReadOnlyList<JobMatchRequirement> ExtractedRequirements,
    IReadOnlyList<JobEvidenceMatch> DirectMatches,
    IReadOnlyList<JobInferenceMatch> Inferences,
    IReadOnlyList<JobGap> Gaps,
    string OverallAssessment);
