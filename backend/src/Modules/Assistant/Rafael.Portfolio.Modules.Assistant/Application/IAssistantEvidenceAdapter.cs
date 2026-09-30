namespace Rafael.Portfolio.Modules.Assistant.Application;

public interface IAssistantEvidenceAdapter
{
    IReadOnlyList<AssistantEvidenceChunk> SearchEvidence(string query, int limit = 5, string? slugFilter = null);
}
