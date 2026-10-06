using Rafael.Portfolio.Modules.JobMatching.Domain;

namespace Rafael.Portfolio.Modules.JobMatching.Application;

public sealed class JobMatchingService : IJobMatchingService
{
    private readonly IJobMatchingEvidenceAdapter _evidenceAdapter;
    private readonly IJobDescriptionAnalyzer _analyzer;

    public JobMatchingService(
        IJobMatchingEvidenceAdapter evidenceAdapter,
        IJobDescriptionAnalyzer analyzer)
    {
        _evidenceAdapter = evidenceAdapter ?? throw new ArgumentNullException(nameof(evidenceAdapter));
        _analyzer = analyzer ?? throw new ArgumentNullException(nameof(analyzer));
    }

    public JobAnalysisResponse Analyze(JobAnalysisRequest request)
    {
        if (!JobAnalysisRequest.IsValid(request, out var error))
        {
            throw new ArgumentException(error, nameof(request));
        }

        return _analyzer.Analyze(request!, _evidenceAdapter);
    }
}
