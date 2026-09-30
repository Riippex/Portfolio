using Rafael.Portfolio.Modules.Assistant.Application;
using Rafael.Portfolio.Modules.Knowledge.Application;

namespace Rafael.Portfolio.Web.Adapters;

public sealed class KnowledgeAssistantEvidenceAdapter : IAssistantEvidenceAdapter
{
    private readonly IEvidenceRetriever _retriever;

    public KnowledgeAssistantEvidenceAdapter(IEvidenceRetriever retriever)
    {
        _retriever = retriever ?? throw new ArgumentNullException(nameof(retriever));
    }

    public IReadOnlyList<AssistantEvidenceChunk> SearchEvidence(string query, int limit = 5, string? slugFilter = null)
    {
        var retrieved = _retriever.Retrieve(query, limit, slugFilter);
        return retrieved.Select(r => new AssistantEvidenceChunk(
            ChunkId: r.ChunkId,
            DocumentId: r.DocumentId,
            Slug: r.Slug,
            Title: r.Title,
            SectionHeading: r.SectionHeading,
            SectionSlug: r.SectionSlug,
            Content: r.Content,
            Claims: r.Claims.Select(c => c.ClaimId).ToList(),
            SourceUrl: r.SourceUrl,
            EvidenceStatus: r.EvidenceStatus,
            Version: r.Version,
            Visibility: r.Visibility,
            Score: r.Score,
            Citations: r.Citations)).ToList();
    }
}
