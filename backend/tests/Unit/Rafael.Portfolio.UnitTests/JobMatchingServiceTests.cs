using Rafael.Portfolio.Modules.JobMatching.Application;
using Rafael.Portfolio.Modules.JobMatching.Domain;
using Rafael.Portfolio.Modules.JobMatching.Infrastructure;

namespace Rafael.Portfolio.UnitTests;

public sealed class JobMatchingServiceTests
{
    private sealed class FakeEvidenceAdapter(IReadOnlyList<JobMatchingEvidenceChunk> chunks) : IJobMatchingEvidenceAdapter
    {
        public IReadOnlyList<JobMatchingEvidenceChunk> SearchEvidence(string query, int limit = 5, string? slugFilter = null)
        {
            var terms = query.Split([' ', ',', '.', '-', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(t => t.Length > 2)
                .ToList();

            return chunks.Where(c =>
                terms.Any(t =>
                    c.Content.Contains(t, StringComparison.OrdinalIgnoreCase)
                    || c.Title.Contains(t, StringComparison.OrdinalIgnoreCase)
                    || c.SectionHeading.Contains(t, StringComparison.OrdinalIgnoreCase)
                    || c.Slug.Contains(t, StringComparison.OrdinalIgnoreCase)
                    || c.Claims.Any(cl => cl.Contains(t, StringComparison.OrdinalIgnoreCase)))).ToList();
        }
    }

    private static JobMatchingEvidenceChunk CreateChunk(
        string slug,
        string title,
        string heading,
        string content,
        string status,
        double score = 1.5,
        string claimId = "claim-01")
    {
        return new JobMatchingEvidenceChunk(
            ChunkId: $"{slug}-chunk-01",
            DocumentId: $"evidence-{slug}",
            Slug: slug,
            Title: title,
            SectionHeading: heading,
            SectionSlug: heading.ToLowerInvariant().Replace(' ', '-'),
            Content: content,
            Claims: [claimId],
            SourceUrl: $"https://github.com/example/{slug}",
            EvidenceStatus: status,
            Version: "2026.09",
            Visibility: "public",
            Score: score,
            Citations: [$"docs/evidence/{slug}.md#{heading.ToLowerInvariant()}"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Analyze_throws_ArgumentException_when_vacancy_is_null_or_whitespace(string? text)
    {
        var analyzer = new DeterministicJobDescriptionAnalyzer();
        var adapter = new FakeEvidenceAdapter([]);
        var service = new JobMatchingService(adapter, analyzer);

        var ex = Assert.Throws<ArgumentException>(() => service.Analyze(new JobAnalysisRequest(text!)));
        Assert.Contains("Vacancy text is required", ex.Message);
    }

    [Fact]
    public void Analyze_throws_ArgumentException_when_vacancy_is_under_min_length()
    {
        var analyzer = new DeterministicJobDescriptionAnalyzer();
        var adapter = new FakeEvidenceAdapter([]);
        var service = new JobMatchingService(adapter, analyzer);

        var ex = Assert.Throws<ArgumentException>(() => service.Analyze(new JobAnalysisRequest("Short")));
        Assert.Contains($"at least {JobAnalysisRequest.MinVacancyLength} characters", ex.Message);
    }

    [Fact]
    public void Analyze_throws_ArgumentException_when_vacancy_exceeds_max_length()
    {
        var analyzer = new DeterministicJobDescriptionAnalyzer();
        var adapter = new FakeEvidenceAdapter([]);
        var service = new JobMatchingService(adapter, analyzer);

        var oversized = new string('x', JobAnalysisRequest.MaxVacancyLength + 1);
        var ex = Assert.Throws<ArgumentException>(() => service.Analyze(new JobAnalysisRequest(oversized)));
        Assert.Contains($"maximum length of {JobAnalysisRequest.MaxVacancyLength}", ex.Message);
    }

    [Fact]
    public void Analyze_classifies_verified_evidence_as_DirectMatches()
    {
        var chunks = new List<JobMatchingEvidenceChunk>
        {
            CreateChunk("vextis", "Vextis", "Agent Architecture", "Autonomous agents and tool use architecture.", "verified", 2.0, "claim-vextis-01")
        };
        var analyzer = new DeterministicJobDescriptionAnalyzer();
        var adapter = new FakeEvidenceAdapter(chunks);
        var service = new JobMatchingService(adapter, analyzer);

        var request = new JobAnalysisRequest(
            "Senior AI Engineer\n- Autonomous Agents architecture and tool use design\n- COBOL mainframe operations");

        var response = service.Analyze(request);

        Assert.NotNull(response);
        Assert.NotEmpty(response.ExtractedRequirements);

        var directMatch = Assert.Single(response.DirectMatches);
        Assert.Equal("vextis", directMatch.DocumentSlug);
        Assert.Equal("verified", directMatch.EvidenceStatus);
        Assert.Equal("claim-vextis-01", directMatch.ClaimId);

        var gap = Assert.Single(response.Gaps);
        Assert.Contains("COBOL", gap.RequirementText);
    }

    [Fact]
    public void Analyze_classifies_pending_evidence_as_Inferences_never_direct_matches()
    {
        var chunks = new List<JobMatchingEvidenceChunk>
        {
            CreateChunk("jobty", "JobTY", "Matching Engine", "Job matching and evaluation engine.", "pending", 2.0, "claim-jobty-01")
        };
        var analyzer = new DeterministicJobDescriptionAnalyzer();
        var adapter = new FakeEvidenceAdapter(chunks);
        var service = new JobMatchingService(adapter, analyzer);

        var request = new JobAnalysisRequest(
            "Evaluation Engine Architect\n- Job matching engine and vacancy evaluation");

        var response = service.Analyze(request);

        Assert.NotNull(response);
        Assert.Empty(response.DirectMatches); // Must NOT be direct match if status is pending!
        var inference = Assert.Single(response.Inferences);
        Assert.Equal("jobty", inference.SupportingDocumentSlug);
        Assert.Equal("pending", inference.SupportingEvidenceStatus);
        Assert.Contains("JobTY", inference.SupportingTitle);
    }

    [Fact]
    public void Analyze_identifies_unmatched_requirements_as_gaps()
    {
        var chunks = new List<JobMatchingEvidenceChunk>
        {
            CreateChunk("kinetiq-v", "Kinetiq V", "Pipeline", "Computer vision edge pipeline.", "verified", 2.0)
        };
        var analyzer = new DeterministicJobDescriptionAnalyzer();
        var adapter = new FakeEvidenceAdapter(chunks);
        var service = new JobMatchingService(adapter, analyzer);

        var request = new JobAnalysisRequest(
            "Staff Engineer\n- Computer vision pipeline\n- Advanced Quantum Cryptography Hardware Design");

        var response = service.Analyze(request);

        Assert.NotEmpty(response.DirectMatches);
        var gap = response.Gaps.FirstOrDefault(g => g.RequirementText.Contains("Quantum"));
        Assert.NotNull(gap);
        Assert.Contains("No documented evidence", gap.Notice);
    }

    [Fact]
    public void OverallAssessment_refuses_false_numerical_percentages()
    {
        var analyzer = new DeterministicJobDescriptionAnalyzer();
        var adapter = new FakeEvidenceAdapter([]);
        var service = new JobMatchingService(adapter, analyzer);

        var request = new JobAnalysisRequest("Looking for a backend engineer with distributed systems experience.");
        var response = service.Analyze(request);

        Assert.NotNull(response.OverallAssessment);
        Assert.Contains("numerical percentage scores are omitted", response.OverallAssessment);
        Assert.DoesNotContain("%", response.OverallAssessment);
    }
}
