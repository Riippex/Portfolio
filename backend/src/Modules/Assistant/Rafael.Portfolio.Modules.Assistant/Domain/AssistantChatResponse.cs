namespace Rafael.Portfolio.Modules.Assistant.Domain;

public sealed record AssistantChatResponse(
    string Answer,
    string GroundingStatus,
    IReadOnlyList<AssistantCitation> Citations);
