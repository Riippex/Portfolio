using Rafael.Portfolio.Modules.JobMatching.Domain;

namespace Rafael.Portfolio.Modules.JobMatching.Application;

public interface IJobDescriptionAnalyzer
{
    JobAnalysisResponse Analyze(JobAnalysisRequest request, IJobMatchingEvidenceAdapter evidenceAdapter);
}
