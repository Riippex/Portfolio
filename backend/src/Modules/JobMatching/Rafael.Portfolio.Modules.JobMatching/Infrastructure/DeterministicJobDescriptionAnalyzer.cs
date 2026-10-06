using System.Text.RegularExpressions;
using Rafael.Portfolio.Modules.JobMatching.Application;
using Rafael.Portfolio.Modules.JobMatching.Domain;

namespace Rafael.Portfolio.Modules.JobMatching.Infrastructure;

public sealed class DeterministicJobDescriptionAnalyzer : IJobDescriptionAnalyzer
{
    private static readonly Regex BulletRegex = new(
        @"^[\s]*[-*•\d+.]\s+(.+)$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly (string Name, string Category, string[] Keywords)[] KnownCompetencyDomains =
    [
        ("Autonomous Agents", "Artificial Intelligence", ["agent", "agents", "autonomous", "multi-agent", "agentic", "tool use", "rag"]),
        ("Computer Vision", "Machine Learning", ["computer vision", "vision", "opencv", "image processing", "video", "inference", "object detection"]),
        ("Cloud Architecture & Microservices", "Cloud Systems", ["cloud", "gcp", "google cloud", "cloudflare", "workers", "distributed", "modular monolith", "serverless"]),
        (".NET & C# Ecosystem", "Backend Engineering", [".net", "c#", "dotnet", "asp.net", "entity framework", "csharp"]),
        ("TypeScript & Next.js", "Frontend Engineering", ["next.js", "nextjs", "react", "typescript", "javascript", "tailwind", "frontend"]),
        ("Data Handling & Privacy", "Security & Governance", ["privacy", "data handling", "security", "gdpr", "retention", "ephemeral", "sandbox"]),
        ("Observability & Reliability", "DevOps & Systems", ["observability", "telemetry", "logging", "monitoring", "metrics", "tracing", "reliability"]),
        ("CI/CD & Containers", "DevOps & Systems", ["docker", "kubernetes", "k8s", "ci/cd", "github actions", "pipeline", "container"])
    ];

    public JobAnalysisResponse Analyze(JobAnalysisRequest request, IJobMatchingEvidenceAdapter evidenceAdapter)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(evidenceAdapter);

        var vacancyText = request.VacancyText.Trim();
        var roleSummary = ExtractRoleSummary(vacancyText);
        var requirements = ExtractRequirements(vacancyText);

        var directMatches = new List<JobEvidenceMatch>();
        var inferences = new List<JobInferenceMatch>();
        var gaps = new List<JobGap>();

        foreach (var req in requirements)
        {
            var candidates = evidenceAdapter.SearchEvidence(req.RequirementText, limit: 3);
            var bestScored = candidates.Where(c => c.Score > 0).ToList();

            if (bestScored.Count == 0)
            {
                gaps.Add(new JobGap(
                    RequirementId: req.RequirementId,
                    RequirementText: req.RequirementText,
                    Notice: "No documented evidence or claims found for this requirement in Rafael's public portfolio."));
                continue;
            }

            var verifiedChunk = bestScored.FirstOrDefault(c =>
                string.Equals(c.EvidenceStatus, "verified", StringComparison.OrdinalIgnoreCase));

            if (verifiedChunk is not null)
            {
                var claimId = verifiedChunk.Claims.FirstOrDefault() ?? "claim-verified";
                directMatches.Add(new JobEvidenceMatch(
                    RequirementId: req.RequirementId,
                    RequirementText: req.RequirementText,
                    DocumentSlug: verifiedChunk.Slug,
                    DocumentTitle: verifiedChunk.Title,
                    SectionHeading: verifiedChunk.SectionHeading,
                    ClaimId: claimId,
                    CitationUrl: verifiedChunk.SourceUrl,
                    EvidenceStatus: verifiedChunk.EvidenceStatus,
                    GroundingSummary: $"Backed by verified public evidence in {verifiedChunk.Title} ({verifiedChunk.SectionHeading})."));
            }
            else
            {
                var topChunk = bestScored[0];
                inferences.Add(new JobInferenceMatch(
                    RequirementId: req.RequirementId,
                    RequirementText: req.RequirementText,
                    InferredCapability: $"Aligned with {topChunk.Title} competency",
                    SupportingDocumentSlug: topChunk.Slug,
                    SupportingTitle: topChunk.Title,
                    SupportingEvidenceStatus: topChunk.EvidenceStatus,
                    Rationale: $"Documented in project {topChunk.Title} ({topChunk.SectionHeading}); public evidence is currently {topChunk.EvidenceStatus}."));
            }
        }

        var assessment = BuildOverallAssessment(requirements.Count, directMatches.Count, inferences.Count, gaps.Count);

        return new JobAnalysisResponse(
            RoleSummary: roleSummary,
            ExtractedRequirements: requirements,
            DirectMatches: directMatches,
            Inferences: inferences,
            Gaps: gaps,
            OverallAssessment: assessment);
    }

    private static string ExtractRoleSummary(string text)
    {
        var firstLine = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()?.Trim();

        if (string.IsNullOrWhiteSpace(firstLine))
        {
            return "Role Vacancy Analysis";
        }

        return firstLine.Length > 100 ? firstLine[..97] + "..." : firstLine;
    }

    private static IReadOnlyList<JobMatchRequirement> ExtractRequirements(string text)
    {
        var requirements = new List<JobMatchRequirement>();
        var seenTexts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var idCounter = 1;

        // 1. Check bullet points
        var bulletMatches = BulletRegex.Matches(text);
        foreach (Match match in bulletMatches)
        {
            if (requirements.Count >= 10) break;
            var lineContent = match.Groups[1].Value.Trim();
            if (lineContent.Length >= 5 && lineContent.Length <= 200 && seenTexts.Add(lineContent))
            {
                var category = CategorizeRequirement(lineContent);
                requirements.Add(new JobMatchRequirement(
                    RequirementId: $"req-{idCounter++:D2}",
                    RequirementText: lineContent,
                    Category: category));
            }
        }

        // 2. Scan known competency domains if no bullets were found
        if (requirements.Count == 0)
        {
            foreach (var (name, category, keywords) in KnownCompetencyDomains)
            {
                if (requirements.Count >= 10) break;
                var found = keywords.Any(kw => text.Contains(kw, StringComparison.OrdinalIgnoreCase));
                if (found && seenTexts.Add(name))
                {
                    requirements.Add(new JobMatchRequirement(
                        RequirementId: $"req-{idCounter++:D2}",
                        RequirementText: name,
                        Category: category));
                }
            }
        }

        // 3. If still empty, fall back to sentences
        if (requirements.Count == 0)
        {
            var sentences = text.Split(['.', ';', '\n'], StringSplitOptions.RemoveEmptyEntries);
            foreach (var s in sentences)
            {
                var trimmed = s.Trim();
                if (trimmed.Length >= 10 && trimmed.Length <= 150 && seenTexts.Add(trimmed))
                {
                    requirements.Add(new JobMatchRequirement(
                        RequirementId: $"req-{idCounter++:D2}",
                        RequirementText: trimmed,
                        Category: "General"));
                    if (requirements.Count >= 5) break;
                }
            }
        }

        if (requirements.Count == 0)
        {
            var fallback = text.Length > 100 ? text[..97] + "..." : text;
            requirements.Add(new JobMatchRequirement(
                RequirementId: "req-01",
                RequirementText: fallback,
                Category: "General"));
        }

        return requirements;
    }

    private static string CategorizeRequirement(string reqText)
    {
        foreach (var (name, category, keywords) in KnownCompetencyDomains)
        {
            if (keywords.Any(kw => reqText.Contains(kw, StringComparison.OrdinalIgnoreCase)))
            {
                return category;
            }
        }
        return "Core Competency";
    }

    private static string BuildOverallAssessment(int total, int direct, int inferences, int gaps)
    {
        return $"Analysis of {total} extracted requirements identified {direct} direct verified evidence matches, " +
               $"{inferences} supported inferences (grounded in pending project catalog items), and {gaps} documented gaps. " +
               "In accordance with portfolio policy, artificial numerical percentage scores are omitted to preserve grounding and prevent unsupported precision.";
    }
}
