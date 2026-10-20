namespace Rafael.Portfolio.Modules.Assistant.Application;

public interface IModelProvider
{
    bool IsAvailable { get; }
    ValueTask<ModelProviderResponse> GenerateAsync(ModelProviderRequest request, CancellationToken cancellationToken = default);
}

public sealed record ModelProviderRequest(
    string SystemPrompt,
    string UserMessage,
    string OutputSchema,
    int MaxOutputTokens = 600,
    string? ReservationId = null);

public sealed record ModelProviderResponse(
    bool Success,
    string? Text,
    int? InputTokens = null,
    int? OutputTokens = null,
    string? ErrorMessage = null);
