using System.Text.RegularExpressions;
using Rafael.Portfolio.Modules.JobMatching.Application;
using Rafael.Portfolio.Modules.JobMatching.Domain;

namespace Rafael.Portfolio.Modules.JobMatching.Infrastructure;

public sealed class DeterministicJobDescriptionAnalyzer : IJobDescriptionAnalyzer
{
    private const int MaxRequirements = 10;
    private const int EvidenceLimit = 5;
    private const string VerifiedStatus = "verified";
    private const string GenericDegree = "generic";
    private const int CertificationWindow = 3;

    // A chunk or claim supports a requirement only when it covers at least this
    // share of the requirement's capability terms.
    private const double RelevanceThreshold = 0.6;

    private static readonly Regex ListItemRegex = new(
        @"^[ \t]*(?:[-*•]|\d{1,2}[.)])[ \t]+([^\r\n]+?)[ \t]*\r?$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex TermRegex = new(@"[\p{L}\p{N}#+]+", RegexOptions.Compiled);

    private static readonly Regex RoleTitleRegex = new(
        @"^(?:[\p{L}\p{N}.#+/&'-]+\s+){0,4}(?:engineer|developer|architect|manager|scientist|analyst|specialist|consultant|designer|programmer|administrator|lead|intern)s?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly HashSet<string> SectionHeadings = new(StringComparer.OrdinalIgnoreCase)
    {
        "requirements", "responsibilities", "qualifications", "about the role", "about us", "about the team",
        "benefits", "nice to have", "must have", "what you'll do", "what you will do", "what we offer",
        "what we're looking for", "who you are", "skills", "key skills", "job description", "overview", "perks",
        "about the company", "why join us", "perks and benefits", "benefits and perks", "compensation",
        "our culture", "how to apply", "role overview", "duties", "key responsibilities", "your responsibilities"
    };

    // Sections that describe the employer or what it offers rather than what the
    // role requires; anything listed under them is not a requirement.
    private static readonly string[] ExcludedSectionMarkers =
    [
        "benefit", "perk", "about us", "about the company", "about the team", "about our",
        "what we offer", "we offer", "why join", "compensation", "salary",
        "culture", "equal opportunity", "how to apply", "who we are", "our mission", "our values"
    ];

    private static readonly Dictionary<string, int> NumberWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["one"] = 1,
        ["two"] = 2,
        ["three"] = 3,
        ["four"] = 4,
        ["five"] = 5,
        ["six"] = 6,
        ["seven"] = 7,
        ["eight"] = 8,
        ["nine"] = 9,
        ["ten"] = 10
    };

    private static readonly string YearNumber = @"(?:\d{1,2}|one|two|three|four|five|six|seven|eight|nine|ten)";

    // A period only ends a sentence before whitespace or the end, so "Node.js" and
    // "ASP.NET" stay whole.
    private static readonly Regex SentenceSeparatorRegex = new(
        @"(?:[;\r\n]|\.(?=\s|$))+",
        RegexOptions.Compiled);

    // Words that start an independent or subordinate statement. A duration never
    // reaches across them, so "Operating GCP while bringing seven years of Java"
    // gives the seven years to Java only.
    private static readonly Regex ScopeBoundaryRegex = new(
        @"\s*,?\s*\b(?:while|whilst|whereas|but|after|before|since|then|when|although|though|however|which|that|where|until)\b\s*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Comma and coordination join the items of one list.
    private static readonly Regex ListSeparatorRegex = new(
        @"\s*(?:,|\b(?:and|plus)\b)\s*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Words around a duration that carry no capability ("over seven years of
    // experience"), used to tell where a duration sits inside its clause.
    private static readonly HashSet<string> DurationFillerWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "of", "experience", "in", "with", "over", "about", "around", "nearly", "almost", "more", "than",
        "total", "overall", "professional", "professionally", "for", "the", "a", "an", "combined",
        "approximately", "roughly", "at", "least", "across", "spanning"
    };

    private static readonly Regex YearsRegex = new(
        $@"(?<![\p{{L}}\p{{N}}])(?<lo>{YearNumber})(?:\s*(?:-|–|to)\s*(?<hi>{YearNumber}))?\s*\+?\s*(?:or\s+more\s+)?(?:years?|yrs?)(?![\p{{L}}\p{{N}}])(?:\s+of)?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex DegreeRegex = new(
        @"(?<![\p{L}\p{N}])(?:(?:bachelor|master)'?s?|ph\.?\s?d\.?|doctorate|b\.?sc\.?|m\.?sc\.?|(?:university|college)\s+degree|degree)(?:\s+degree)?(?![\p{L}\p{N}])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CertificationRegex = new(
        @"(?<![\p{L}\p{N}])(?:certified|certifications?|certificates?)(?![\p{L}\p{N}])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Words that frame a requirement without naming a capability.
    private static readonly HashSet<string> Stopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "and", "or", "the", "of", "in", "on", "for", "with", "to", "using", "use", "as", "at", "by",
        "is", "are", "be", "will", "you", "we", "our", "your", "from", "that", "this", "such", "including",
        "strong", "solid", "proven", "hands", "experience", "experienced", "knowledge", "understanding",
        "familiarity", "ability", "skill", "proficiency", "proficient", "expertise", "expert",
        "advanced", "working", "work", "year", "plus", "good", "excellent", "preferred", "required", "nice",
        "have", "has", "etc", "least", "minimum", "more", "than", "demonstrated", "deep", "senior"
    };

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

    private enum QualifierKind
    {
        Years,
        Degree,
        Certification
    }

    // Detail is the degree level for degrees ("bachelor", "master", "doctorate", or
    // "generic" when no level is named) and unused otherwise.
    private sealed record Qualifier(QualifierKind Kind, string Text, int MinYears, string Detail = "");

    private sealed record ParsedRequirement(
        JobMatchRequirement Requirement,
        string CapabilityLabel,
        string SearchText,
        IReadOnlyList<string> CapabilityTerms,
        IReadOnlyList<Qualifier> Qualifiers);

    private sealed record ChunkAssessment(
        JobMatchingEvidenceChunk Chunk,
        double Coverage,
        JobMatchingEvidenceClaim? DirectClaim,
        JobMatchingEvidenceClaim? VerifiedClaim,
        JobMatchingEvidenceClaim? BestClaim);

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

        foreach (var parsed in requirements)
        {
            Classify(parsed, evidenceAdapter, directMatches, inferences, gaps);
        }

        var assessment = BuildOverallAssessment(
            requirements.Count, directMatches.Count, inferences.Count, gaps.Count);

        return new JobAnalysisResponse(
            RoleSummary: roleSummary,
            ExtractedRequirements: requirements.Select(r => r.Requirement).ToList(),
            DirectMatches: directMatches,
            Inferences: inferences,
            Gaps: gaps,
            OverallAssessment: assessment);
    }

    private static void Classify(
        ParsedRequirement parsed,
        IJobMatchingEvidenceAdapter evidenceAdapter,
        List<JobEvidenceMatch> directMatches,
        List<JobInferenceMatch> inferences,
        List<JobGap> gaps)
    {
        var requirement = parsed.Requirement;

        if (parsed.CapabilityTerms.Count == 0)
        {
            // Nothing but a constraint (for example "Bachelor's degree"): there is
            // no capability to ground, so each qualifier stands as a gap.
            if (parsed.Qualifiers.Count == 0)
            {
                gaps.Add(NoEvidenceGap(requirement));
            }

            foreach (var qualifier in parsed.Qualifiers)
            {
                gaps.Add(QualifierGap(requirement, qualifier));
            }

            return;
        }

        var assessments = evidenceAdapter
            .SearchEvidence(parsed.SearchText, EvidenceLimit)
            .Where(c => c.Score > 0)
            .Select(c => Assess(c, parsed.CapabilityTerms, parsed.Qualifiers))
            .Where(a => a.Coverage >= RelevanceThreshold)
            .OrderByDescending(a => a.DirectClaim is not null)
            .ThenByDescending(a => a.VerifiedClaim is not null)
            .ThenByDescending(a => a.Coverage)
            .ThenByDescending(a => a.Chunk.Score)
            .ToList();

        if (assessments.Count == 0)
        {
            gaps.Add(NoEvidenceGap(requirement));
            return;
        }

        // A direct match is traced to exactly one cited claim that supports the
        // capability and every qualifier on its own. Qualifiers are never
        // satisfied by some other claim in the same chunk.
        var direct = assessments.FirstOrDefault(a => a.DirectClaim is not null);
        if (direct is not null)
        {
            var claim = direct.DirectClaim!;
            directMatches.Add(new JobEvidenceMatch(
                RequirementId: requirement.RequirementId,
                RequirementText: requirement.RequirementText,
                DocumentSlug: direct.Chunk.Slug,
                DocumentKind: direct.Chunk.Kind,
                DocumentTitle: direct.Chunk.Title,
                SectionHeading: direct.Chunk.SectionHeading,
                SectionSlug: direct.Chunk.SectionSlug,
                ClaimId: claim.ClaimId,
                Citation: claim.Citation,
                CitationUrl: direct.Chunk.SourceUrl,
                EvidenceStatus: direct.Chunk.EvidenceStatus,
                GroundingSummary: $"Backed by verified public claim {claim.ClaimId} in {direct.Chunk.Title} ({direct.Chunk.SectionHeading})."));
            return;
        }

        // Otherwise the capability may still be inferred (from a verified claim
        // that lacks the qualifier, or from weaker evidence); every qualifier the
        // cited claim does not itself state remains a gap.
        var capable = assessments.FirstOrDefault(a => a.VerifiedClaim is not null);
        var top = capable ?? assessments[0];
        var topClaim = top.VerifiedClaim ?? top.BestClaim;
        var unsupported = capable is null
            ? parsed.Qualifiers
            : parsed.Qualifiers.Where(q => !ClaimStatesQualifier(capable.VerifiedClaim!, q, parsed.CapabilityTerms)).ToList();
        inferences.Add(new JobInferenceMatch(
            RequirementId: requirement.RequirementId,
            RequirementText: requirement.RequirementText,
            InferredCapability: $"Familiarity with {parsed.CapabilityLabel}",
            SupportingDocumentSlug: top.Chunk.Slug,
            SupportingDocumentKind: top.Chunk.Kind,
            SupportingTitle: top.Chunk.Title,
            SupportingSectionSlug: top.Chunk.SectionSlug,
            SupportingCitation: topClaim?.Citation is { Length: > 0 } claimCitation
                ? claimCitation
                : top.Chunk.Citations.FirstOrDefault() ?? $"{top.Chunk.DocumentId}#{top.Chunk.SectionSlug}",
            SupportingClaimId: topClaim?.ClaimId,
            SupportingEvidenceStatus: top.Chunk.EvidenceStatus,
            Rationale: BuildInferenceRationale(top, capable is not null, unsupported)));

        foreach (var qualifier in unsupported)
        {
            gaps.Add(QualifierGap(requirement, qualifier));
        }
    }

    private static string BuildInferenceRationale(
        ChunkAssessment top,
        bool capabilityVerified,
        IReadOnlyList<Qualifier> unsupported)
    {
        var chunk = top.Chunk;
        var rationale = capabilityVerified
            ? $"The capability is documented by verified claim {top.VerifiedClaim!.ClaimId} in {chunk.Title} ({chunk.SectionHeading})."
            : chunk.EvidenceStatus.Equals(VerifiedStatus, StringComparison.OrdinalIgnoreCase)
                ? $"{chunk.Title} ({chunk.SectionHeading}) is verified, but no verified claim covers this requirement directly."
                : $"Related to {chunk.Title} ({chunk.SectionHeading}); public evidence is currently {chunk.EvidenceStatus}, so this is an inference rather than a verified match.";

        return unsupported.Count == 0
            ? rationale
            : $"{rationale} The qualifier {string.Join(", ", unsupported.Select(q => $"\"{q.Text}\""))} is not documented, so it remains a gap.";
    }

    private static ChunkAssessment Assess(
        JobMatchingEvidenceChunk chunk,
        IReadOnlyList<string> capabilityTerms,
        IReadOnlyList<Qualifier> qualifiers)
    {
        var chunkTerms = Tokenize($"{chunk.SectionHeading} {chunk.Title} {chunk.Content} " +
                                  string.Join(' ', chunk.Claims.Select(c => c.Statement)));
        var coverage = Coverage(capabilityTerms, chunkTerms);

        JobMatchingEvidenceClaim? bestClaim = null;
        var bestClaimCoverage = 0.0;
        JobMatchingEvidenceClaim? verifiedClaim = null;
        var verifiedClaimCoverage = 0.0;
        JobMatchingEvidenceClaim? directClaim = null;
        var directClaimCoverage = 0.0;
        var documentVerified = chunk.EvidenceStatus.Equals(VerifiedStatus, StringComparison.OrdinalIgnoreCase);

        foreach (var claim in chunk.Claims)
        {
            if (string.IsNullOrWhiteSpace(claim.ClaimId))
            {
                continue;
            }

            // The section heading is context for inferences only. Direct evidence is
            // attributed to the claim, so the claim statement itself must state the
            // capability; a claim that merely sits under a matching heading cannot.
            var statementCoverage = Coverage(capabilityTerms, Tokenize(claim.Statement));
            var contextCoverage = Coverage(capabilityTerms, Tokenize($"{claim.Statement} {chunk.SectionHeading}"));
            if (contextCoverage < RelevanceThreshold)
            {
                continue;
            }

            if (contextCoverage > bestClaimCoverage)
            {
                bestClaim = claim;
                bestClaimCoverage = contextCoverage;
            }

            if (statementCoverage < RelevanceThreshold)
            {
                continue;
            }

            // A direct match needs a verified document, a verified claim, and a
            // resolved citation; anything less is at most an inference.
            if (!documentVerified
                || !claim.Status.Equals(VerifiedStatus, StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(claim.Citation))
            {
                continue;
            }

            if (statementCoverage > verifiedClaimCoverage)
            {
                verifiedClaim = claim;
                verifiedClaimCoverage = statementCoverage;
            }

            // The same claim must also state every qualifier to stand alone.
            if (statementCoverage > directClaimCoverage
                && qualifiers.All(q => ClaimStatesQualifier(claim, q, capabilityTerms)))
            {
                directClaim = claim;
                directClaimCoverage = statementCoverage;
            }
        }

        return new ChunkAssessment(chunk, coverage, directClaim, verifiedClaim, bestClaim);
    }

    private static bool ClaimStatesQualifier(
        JobMatchingEvidenceClaim claim,
        Qualifier qualifier,
        IReadOnlyList<string> subjectTerms)
    {
        var statement = claim.Statement;
        switch (qualifier.Kind)
        {
            case QualifierKind.Years:
                return ClaimStatesYears(statement, qualifier.MinYears, subjectTerms);

            case QualifierKind.Degree:
                // A named level must be stated exactly (a bachelor's degree never
                // satisfies a master's requirement); a generic "degree" accepts any.
                var levels = DegreeRegex.Matches(statement).Select(m => DegreeLevel(m.Value)).ToList();
                return qualifier.Detail == GenericDegree
                    ? levels.Count > 0
                    : levels.Contains(qualifier.Detail);

            default:
                // A certification must be tied to the requested subject: every
                // subject term has to sit right beside the certification mention, so
                // "AWS experience and PMP certification" does not certify AWS.
                return CertificationRegex.Matches(statement).Any(m =>
                {
                    var before = TermRegex.Matches(statement[..m.Index].ToLowerInvariant()).Select(x => x.Value).TakeLast(CertificationWindow);
                    var after = TermRegex.Matches(statement[(m.Index + m.Length)..].ToLowerInvariant()).Select(x => x.Value).Take(CertificationWindow);
                    var window = Tokenize(string.Join(' ', before.Concat(after)));
                    return subjectTerms.Count > 0 && subjectTerms.All(window.Contains);
                });
        }
    }

    // A duration only counts for the capability it governs. A claim is split into
    // sentences, then at scope boundaries (while, after, but, which, ...), then into
    // list items at commas and coordinating words. A duration clause may absorb its
    // neighbouring list items only when the construction says so:
    //   - prefix duration ("Seven years building Java, Python and GCP"): the items
    //     after it are covered by it;
    //   - suffix duration ("Java, Python and GCP for seven years"): the items
    //     before it are covered by it;
    //   - a duration in the middle of its own clause ("bringing seven years of
    //     Java experience") covers only that clause.
    // The requested capability must be stated inside the duration's group, so an
    // unrelated duration elsewhere in a compound claim never satisfies it.
    private static bool ClaimStatesYears(string statement, int minYears, IReadOnlyList<string> subjectTerms)
    {
        if (subjectTerms.Count == 0)
        {
            return false;
        }

        foreach (var sentence in SentenceSeparatorRegex.Split(statement))
        {
            foreach (var segment in ScopeBoundaryRegex.Split(sentence))
            {
                foreach (var (text, minimum) in DurationGroups(segment))
                {
                    if (minimum >= minYears && Coverage(subjectTerms, Tokenize(text)) >= RelevanceThreshold)
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private enum DurationPosition
    {
        Prefix,
        Suffix,
        Embedded
    }

    private static List<(string Text, int Minimum)> DurationGroups(string segment)
    {
        var pieces = ListSeparatorRegex.Split(segment)
            .Select(piece => piece.Trim())
            .Where(piece => piece.Length > 0)
            .ToList();

        var groups = new List<(List<string> Parts, int Minimum, DurationPosition Position)>();
        var leading = new List<string>();
        foreach (var piece in pieces)
        {
            var durations = YearsRegex.Matches(piece);
            if (durations.Count > 0)
            {
                var position = ClassifyDuration(piece, durations[0]);
                var parts = new List<string>();

                // Items listed before a suffix duration share it; before any other
                // construction they are unrelated and are dropped.
                if (position == DurationPosition.Suffix)
                {
                    parts.AddRange(leading);
                }

                leading.Clear();
                parts.Add(piece);

                // Several durations in one clause cannot be told apart, so only
                // the smallest is trusted.
                groups.Add((parts, durations.Select(m => ParseYears(m).Min).Min(), position));
            }
            else if (groups.Count > 0)
            {
                // Items after a prefix duration share it; after any other
                // construction they are unrelated and are dropped.
                if (groups[^1].Position == DurationPosition.Prefix)
                {
                    groups[^1].Parts.Add(piece);
                }
            }
            else
            {
                leading.Add(piece);
            }
        }

        return groups.Select(group => (string.Join(' ', group.Parts), group.Minimum)).ToList();
    }

    private static DurationPosition ClassifyDuration(string piece, Match duration)
    {
        var before = ContentWords(piece[..duration.Index]);
        var after = ContentWords(piece[(duration.Index + duration.Length)..]);

        if (before == 0 && after > 0)
        {
            return DurationPosition.Prefix;
        }

        return before > 0 && after == 0 ? DurationPosition.Suffix : DurationPosition.Embedded;
    }

    private static int ContentWords(string text) =>
        TermRegex.Matches(text.ToLowerInvariant())
            .Count(m => !DurationFillerWords.Contains(m.Value) && !int.TryParse(m.Value, out _));

    private static string DegreeLevel(string text)
    {
        var lower = text.ToLowerInvariant();
        if (lower.Contains("bachelor", StringComparison.Ordinal) || Regex.IsMatch(lower, @"^b\.?sc"))
        {
            return "bachelor";
        }

        if (lower.Contains("master", StringComparison.Ordinal) || Regex.IsMatch(lower, @"^m\.?sc"))
        {
            return "master";
        }

        return lower.StartsWith("ph", StringComparison.Ordinal) || lower.Contains("doctorate", StringComparison.Ordinal)
            ? "doctorate"
            : GenericDegree;
    }

    private static JobGap NoEvidenceGap(JobMatchRequirement requirement) =>
        new(
            RequirementId: requirement.RequirementId,
            RequirementText: requirement.RequirementText,
            Notice: "No documented evidence or claims found for this requirement in Rafael's public portfolio.");

    private static JobGap QualifierGap(JobMatchRequirement requirement, Qualifier qualifier) =>
        new(
            RequirementId: requirement.RequirementId,
            RequirementText: requirement.RequirementText,
            Notice: $"The qualifier \"{qualifier.Text}\" is not documented in Rafael's public verified evidence; no claim supports it.",
            UnsupportedQualifier: qualifier.Text);

    private static double Coverage(IReadOnlyList<string> requirementTerms, HashSet<string> evidenceTerms)
    {
        if (requirementTerms.Count == 0)
        {
            return 0;
        }

        return requirementTerms.Count(evidenceTerms.Contains) / (double)requirementTerms.Count;
    }

    private static HashSet<string> Tokenize(string text)
    {
        var terms = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in TermRegex.Matches(text.ToLowerInvariant()))
        {
            terms.Add(Normalize(match.Value));
        }

        return terms;
    }

    private static string Normalize(string term) =>
        term.Length > 3 && term.EndsWith('s') && !term.EndsWith("ss", StringComparison.Ordinal)
            ? term[..^1]
            : term;

    private static string ExtractRoleSummary(string text)
    {
        var firstLine = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()?.Trim().TrimStart('#').Trim();

        if (string.IsNullOrWhiteSpace(firstLine))
        {
            return "Role Vacancy Analysis";
        }

        return firstLine.Length > 100 ? firstLine[..97] + "..." : firstLine;
    }

    private static IReadOnlyList<ParsedRequirement> ExtractRequirements(string text)
    {
        var requirements = new List<ParsedRequirement>();
        var seenTexts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string requirementText, string? category = null)
        {
            if (requirements.Count >= MaxRequirements || !seenTexts.Add(requirementText))
            {
                return;
            }

            requirements.Add(Parse(
                $"req-{requirements.Count + 1:D2}",
                requirementText,
                category ?? CategorizeRequirement(requirementText)));
        }

        // Walk the vacancy section by section: a section label (for example
        // "Benefits:") switches the active section, and lines under a section that
        // does not describe the role's requirements are dropped entirely. Role
        // titles and labels themselves never become requirements.
        var bodyLines = new List<string>();
        var inExcludedSection = false;
        foreach (var rawLine in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (IsSectionLabel(line))
            {
                inExcludedSection = IsExcludedSection(line);
                continue;
            }

            if (inExcludedSection || IsHeadingLine(line))
            {
                continue;
            }

            bodyLines.Add(line);
        }

        // 1. Bulleted or numbered items.
        foreach (var line in bodyLines)
        {
            var match = ListItemRegex.Match(line);
            if (!match.Success)
            {
                continue;
            }

            var content = match.Groups[1].Value.Trim();
            if (content.Length is >= 5 and <= 200 && !IsHeadingLine(content))
            {
                Add(content);
            }
        }

        var body = string.Join('\n', bodyLines);

        // 2. Known competency domains mentioned anywhere in the body.
        if (requirements.Count == 0)
        {
            foreach (var (name, category, keywords) in KnownCompetencyDomains)
            {
                if (keywords.Any(keyword => ContainsKeyword(body, keyword)))
                {
                    Add(name, category);
                }
            }
        }

        // 3. Sentences of the body.
        if (requirements.Count == 0)
        {
            foreach (var sentence in body.Split(['.', ';', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = sentence.Trim();
                if (trimmed.Length is >= 10 and <= 150)
                {
                    Add(trimmed, "General");
                    if (requirements.Count >= 5)
                    {
                        break;
                    }
                }
            }
        }

        return requirements;
    }

    // A label that opens a new section: a markdown heading, a line ending in a
    // colon, or a bare well-known section name. Role titles are not labels, so
    // they leave the active section unchanged.
    private static bool IsSectionLabel(string line)
    {
        var trimmed = line.Trim();
        return trimmed.StartsWith('#')
            || trimmed.EndsWith(':')
            || SectionHeadings.Contains(trimmed);
    }

    private static bool IsExcludedSection(string label)
    {
        var name = label.Trim().TrimStart('#').TrimEnd(':').Trim().ToLowerInvariant();
        return ExcludedSectionMarkers.Any(marker => name.Contains(marker, StringComparison.Ordinal));
    }

    private static bool IsHeadingLine(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.StartsWith('#') || trimmed.EndsWith(':'))
        {
            return true;
        }

        if (SectionHeadings.Contains(trimmed))
        {
            return true;
        }

        // "Backend Engineer", "Senior AI Engineer (Remote)": a short title that
        // ends with a role noun and carries no sentence punctuation.
        var title = Regex.Replace(trimmed, @"\s*\([^)]*\)\s*$", string.Empty);
        return title.Length <= 60 && RoleTitleRegex.IsMatch(title);
    }

    private static ParsedRequirement Parse(string requirementId, string requirementText, string category)
    {
        var qualifiers = new List<Qualifier>();
        var capabilityText = requirementText;

        foreach (Match match in YearsRegex.Matches(requirementText))
        {
            var (min, text) = ParseYears(match);
            qualifiers.Add(new Qualifier(QualifierKind.Years, text, min));
        }

        capabilityText = YearsRegex.Replace(capabilityText, " ");

        foreach (Match match in DegreeRegex.Matches(capabilityText))
        {
            qualifiers.Add(new Qualifier(QualifierKind.Degree, match.Value.Trim(), 0, DegreeLevel(match.Value)));
        }

        capabilityText = DegreeRegex.Replace(capabilityText, " ");

        foreach (Match match in CertificationRegex.Matches(capabilityText))
        {
            qualifiers.Add(new Qualifier(QualifierKind.Certification, match.Value.Trim(), 0));
        }

        capabilityText = CertificationRegex.Replace(capabilityText, " ");

        // Retrieval sees only the capability words, so a qualifier such as "five
        // years" can neither drive nor inflate a match.
        var words = TermRegex.Matches(capabilityText.ToLowerInvariant())
            .Select(m => m.Value)
            .Where(t => t.Length > 1 && !Stopwords.Contains(t) && !int.TryParse(t, out _))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var terms = words.Select(Normalize).Distinct(StringComparer.Ordinal).ToList();

        var label = Regex.Replace(capabilityText, @"\s+", " ").Trim(' ', ',', '.', ';', ':', '-');
        label = Regex.Replace(label, @"^(?:of\s+)+", string.Empty, RegexOptions.IgnoreCase);
        label = Regex.Replace(label, @"\s+experience$", string.Empty, RegexOptions.IgnoreCase).Trim();

        return new ParsedRequirement(
            new JobMatchRequirement(requirementId, requirementText, category),
            label.Length == 0 ? requirementText : label,
            string.Join(' ', words),
            terms,
            qualifiers);
    }

    private static (int Min, string Text) ParseYears(Match match)
    {
        var lo = ParseNumber(match.Groups["lo"].Value);
        var text = Regex.Replace(match.Value, @"\s+of$", string.Empty, RegexOptions.IgnoreCase).Trim();
        return (lo, text);
    }

    private static int ParseNumber(string value) =>
        int.TryParse(value, out var number) ? number : NumberWords.GetValueOrDefault(value);

    private static bool ContainsKeyword(string text, string keyword) =>
        Regex.IsMatch(
            text,
            $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(keyword)}(?![\p{{L}}\p{{N}}])",
            RegexOptions.IgnoreCase);

    private static string CategorizeRequirement(string reqText)
    {
        foreach (var (_, category, keywords) in KnownCompetencyDomains)
        {
            if (keywords.Any(keyword => ContainsKeyword(reqText, keyword)))
            {
                return category;
            }
        }

        return "Core Competency";
    }

    private static string BuildOverallAssessment(int total, int direct, int inferences, int gaps)
    {
        return $"Analysis of {total} extracted requirements identified {direct} direct verified evidence matches, " +
               $"{inferences} supported inferences (capabilities grounded in public evidence that is pending or lacks a verified claim), and {gaps} documented gaps. " +
               "In accordance with portfolio policy, artificial numerical percentage scores are omitted to preserve grounding and prevent unsupported precision.";
    }
}
