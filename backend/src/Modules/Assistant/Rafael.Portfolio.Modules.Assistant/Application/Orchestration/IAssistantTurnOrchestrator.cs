using Rafael.Portfolio.Modules.Assistant.Domain;

namespace Rafael.Portfolio.Modules.Assistant.Application.Orchestration;

public interface IAssistantTurnOrchestrator
{
    /// <summary>Null selects the existing deterministic answer. No unvalidated text is returned.</summary>
    ValueTask<AssistantChatResponse?> RunAsync(string question, IReadOnlyList<AssistantEvidenceChunk> chunks,
        long turnStart, CancellationToken cancellationToken = default);
}
