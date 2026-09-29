using Rafael.Portfolio.Modules.Knowledge.Domain;

namespace Rafael.Portfolio.Modules.Knowledge.Application;

public interface IEvidenceSource
{
    PublicEvidenceInventory GetInventory();
    IReadOnlyList<IngestedEvidenceDocument> GetAllEvidence();
    IngestedEvidenceDocument? GetEvidenceBySlug(string slug);
    IngestedEvidenceDocument? GetEvidenceById(string id);
}
