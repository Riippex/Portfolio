namespace Rafael.Portfolio.Modules.Assistant.Domain;

public sealed record AssistantStreamEvent(
    string Type,
    string? Text = null,
    string? GroundingStatus = null,
    AssistantCitation? Citation = null,
    string? Error = null,
    bool Done = false)
{
    public static AssistantStreamEvent Status(string status) =>
        new("status", GroundingStatus: status);

    public static AssistantStreamEvent Token(string text) =>
        new("token", Text: text);

    public static AssistantStreamEvent CitationEvent(AssistantCitation citation) =>
        new("citation", Citation: citation);

    public static AssistantStreamEvent ErrorEvent(string error) =>
        new("error", Error: error);

    public static AssistantStreamEvent DoneEvent() =>
        new("done", Done: true);
}
