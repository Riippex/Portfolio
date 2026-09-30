using Rafael.Portfolio.Modules.Assistant.Domain;

namespace Rafael.Portfolio.Modules.Assistant.Application;

public interface IAssistantSynthesizer
{
    AssistantChatResponse Synthesize(string query, IReadOnlyList<AssistantEvidenceChunk> relevantChunks);
}
