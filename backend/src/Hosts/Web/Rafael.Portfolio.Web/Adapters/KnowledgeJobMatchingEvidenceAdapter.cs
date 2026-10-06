using Rafael.Portfolio.Modules.JobMatching.Application;
using Rafael.Portfolio.Modules.Knowledge.Application;

namespace Rafael.Portfolio.Web.Adapters;

public sealed class KnowledgeJobMatchingEvidenceAdapter : IJobMatchingEvidenceAdapter
{
    private readonly IEvidenceRetriever _retriever;

    public KnowledgeJobMatchingEvidenceAdapter(IEvidenceRetriever retriever)
    {
        _retriever = retriever ?? throw new ArgumentNullException(nameof(retriever));
    }

    public IReadOnlyList<JobMatchingEvidenceChunk> SearchEvidence(string query, int limit = 5, string? slugFilter = null)
    {
        var retrieved = _retriever.Retrieve(query, limit, slugFilter);
        return retrieved.Select(r => new JobMatchingEvidenceChunk(
            ChunkId: r.ChunkId,
            DocumentId: r.DocumentId,
            Slug: r.Slug,
            Kind: r.Kind,
            Title: r.Title,
            SectionHeading: r.SectionHeading,
            SectionSlug: r.SectionSlug,
            Content: r.Content,
            Claims: r.Claims
                .Select(c => new JobMatchingEvidenceClaim(c.ClaimId, c.Statement, c.Status, c.Citation))
                .ToList(),
            SourceUrl: r.SourceUrl,
            EvidenceStatus: r.EvidenceStatus,
            Version: r.Version,
            Visibility: r.Visibility,
            Score: r.Score,
            Citations: r.Citations)).ToList();
    }
}
