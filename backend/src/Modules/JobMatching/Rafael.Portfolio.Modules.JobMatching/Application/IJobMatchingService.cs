using Rafael.Portfolio.Modules.JobMatching.Domain;

namespace Rafael.Portfolio.Modules.JobMatching.Application;

public interface IJobMatchingService
{
    JobAnalysisResponse Analyze(JobAnalysisRequest request);
}
