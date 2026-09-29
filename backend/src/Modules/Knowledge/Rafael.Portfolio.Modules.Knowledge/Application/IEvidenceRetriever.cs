using Rafael.Portfolio.Modules.Knowledge.Domain;

namespace Rafael.Portfolio.Modules.Knowledge.Application;

public interface IEvidenceRetriever
{
    IReadOnlyList<RetrievedChunk> Retrieve(RetrievalQuery query);
    IReadOnlyList<RetrievedChunk> Retrieve(string queryText, int limit = 5, string? slugFilter = null);
}
