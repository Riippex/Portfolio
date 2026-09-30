namespace Rafael.Portfolio.Modules.Assistant.Domain;

public sealed record AssistantSafetyDecision(
    bool IsSafe,
    string? Reason = null);
