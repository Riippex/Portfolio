namespace Rafael.Portfolio.Modules.JobMatching.Domain;

public sealed record JobGap(
    string RequirementId,
    string RequirementText,
    string Notice);
