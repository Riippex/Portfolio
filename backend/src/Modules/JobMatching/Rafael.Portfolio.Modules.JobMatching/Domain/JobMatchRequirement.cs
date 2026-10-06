namespace Rafael.Portfolio.Modules.JobMatching.Domain;

public sealed record JobMatchRequirement(
    string RequirementId,
    string RequirementText,
    string Category);
