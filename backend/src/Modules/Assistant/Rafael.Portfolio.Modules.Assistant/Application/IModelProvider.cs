namespace Rafael.Portfolio.Modules.Assistant.Application;

public interface IModelProvider
{
    bool IsAvailable { get; }
    ValueTask<ModelProviderResponse> GenerateAsync(ModelProviderRequest request, CancellationToken cancellationToken = default);
}

public sealed record ModelProviderRequest(
    string SystemPrompt,
    string UserMessage,
    IReadOnlyList<ModelToolDefinition>? Tools = null,
    int MaxOutputTokens = 600);

public sealed record ModelProviderResponse(
    bool Success,
    string? Text,
    IReadOnlyList<ModelToolCall>? ToolCalls = null,
    int InputTokens = 0,
    int OutputTokens = 0,
    string? ErrorMessage = null);

public sealed record ModelToolDefinition(
    string Name,
    string Description,
    object ParametersSchema);

public sealed record ModelToolCall(
    string ToolName,
    string ArgumentsJson);
