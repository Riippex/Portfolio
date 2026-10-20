using Rafael.Portfolio.Modules.Assistant.Domain;

namespace Rafael.Portfolio.Modules.Assistant.Application;

public interface IAssistantService
{
    AssistantChatResponse Chat(AssistantChatRequest request);
    ValueTask<AssistantChatResponse> ChatAsync(AssistantChatRequest request, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Chat(request));
    IAsyncEnumerable<AssistantStreamEvent> StreamChatAsync(AssistantChatRequest request, CancellationToken cancellationToken = default);
}
