using Rafael.Portfolio.Modules.Assistant.Domain;

namespace Rafael.Portfolio.Modules.Assistant.Application;

public interface IAssistantService
{
    AssistantChatResponse Chat(AssistantChatRequest request);
    IAsyncEnumerable<AssistantStreamEvent> StreamChatAsync(AssistantChatRequest request, CancellationToken cancellationToken = default);
}
