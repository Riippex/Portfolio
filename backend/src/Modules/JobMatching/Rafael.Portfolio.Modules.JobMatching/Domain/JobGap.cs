namespace Rafael.Portfolio.Modules.JobMatching.Domain;

/// <summary>
/// An undocumented requirement. When <see cref="UnsupportedQualifier"/> is set the
/// requirement's capability may be supported elsewhere, but this specific
/// constraint (for example a years-of-experience figure) is not documented.
/// </summary>
public sealed record JobGap(
    string RequirementId,
    string RequirementText,
    string Notice,
    string? UnsupportedQualifier = null);
