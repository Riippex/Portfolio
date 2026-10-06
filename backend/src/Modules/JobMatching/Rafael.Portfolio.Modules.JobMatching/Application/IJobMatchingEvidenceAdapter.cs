namespace Rafael.Portfolio.Modules.JobMatching.Application;

public interface IJobMatchingEvidenceAdapter
{
    IReadOnlyList<JobMatchingEvidenceChunk> SearchEvidence(string query, int limit = 5, string? slugFilter = null);
}
