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
                    || c.Claims.Any(cl => cl.Statement.Contains(t, StringComparison.OrdinalIgnoreCase)))).ToList();
        }
    }

    private static JobMatchingEvidenceChunk CreateChunk(
        string slug,
        string title,
        string heading,
        string content,
        string status,
        double score = 1.5,
        string claimId = "claim-01",
        string kind = "project",
        string? claimStatement = null,
        string? claimStatus = null,
        string? claimCitation = null,
        bool withClaims = true)
    {
        var sectionSlug = heading.ToLowerInvariant().Replace(' ', '-');
        var citation = claimCitation ?? $"docs/evidence/{slug}.md#{sectionSlug}";
        return new JobMatchingEvidenceChunk(
            ChunkId: $"{slug}-chunk-01",
            DocumentId: $"evidence-{slug}",
            Slug: slug,
            Kind: kind,
            Title: title,
            SectionHeading: heading,
            SectionSlug: sectionSlug,
            Content: content,
            Claims: withClaims
                ? [new JobMatchingEvidenceClaim(claimId, claimStatement ?? content, claimStatus ?? status, citation)]
                : [],
            SourceUrl: $"https://github.com/example/{slug}",
            EvidenceStatus: status,
            Version: "2026.09",
            Visibility: "public",
            Score: score,
            Citations: [$"docs/evidence/{slug}.md#{sectionSlug}"]);
    }

    private static JobAnalysisResponse Analyze(string vacancy, params JobMatchingEvidenceChunk[] chunks)
    {
        var service = new JobMatchingService(new FakeEvidenceAdapter(chunks), new DeterministicJobDescriptionAnalyzer());
        return service.Analyze(new JobAnalysisRequest(vacancy));
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

    [Fact]
    public void Unsupported_qualifier_is_kept_as_a_gap_while_the_capability_is_inferred()
    {
        var response = Analyze(
            "Platform Engineer\n- Five years of autonomous agents experience",
            CreateChunk("vextis", "Vextis", "Architecture", "Autonomous agents with sandboxed memory.", "verified", 2.0, "claim-vextis-01"));

        Assert.Empty(response.DirectMatches);

        var inference = Assert.Single(response.Inferences);
        Assert.Contains("autonomous agents", inference.InferredCapability, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("claim-vextis-01", inference.SupportingClaimId);

        var gap = Assert.Single(response.Gaps);
        Assert.Equal(inference.RequirementId, gap.RequirementId);
        Assert.Equal("Five years", gap.UnsupportedQualifier);
        Assert.Contains("Five years", gap.Notice);
    }

    [Fact]
    public void Qualifier_documented_by_a_verified_claim_allows_a_direct_match()
    {
        var response = Analyze(
            "Platform Engineer\n- Five years of autonomous agents experience",
            CreateChunk(
                "vextis", "Vextis", "Architecture", "Autonomous agents in production.", "verified", 2.0, "claim-vextis-01",
                claimStatement: "Seven years building autonomous agents in production."));

        var match = Assert.Single(response.DirectMatches);
        Assert.Equal("claim-vextis-01", match.ClaimId);
        Assert.Empty(response.Gaps);
        Assert.Empty(response.Inferences);
    }

    [Fact]
    public void Insufficient_years_in_a_claim_do_not_satisfy_the_qualifier()
    {
        var response = Analyze(
            "Platform Engineer\n- 8+ years of autonomous agents",
            CreateChunk(
                "vextis", "Vextis", "Architecture", "Autonomous agents in production.", "verified", 2.0, "claim-vextis-01",
                claimStatement: "Three years building autonomous agents."));

        Assert.Empty(response.DirectMatches);
        Assert.Single(response.Inferences);
        Assert.Equal("8+ years", Assert.Single(response.Gaps).UnsupportedQualifier);
    }

    [Fact]
    public void Constraint_only_requirements_become_qualifier_gaps()
    {
        var response = Analyze(
            "Platform Engineer\n- Master's degree\n- Certification",
            CreateChunk("vextis", "Vextis", "Architecture", "Autonomous agents with sandboxed memory.", "verified", 2.0));

        Assert.Empty(response.DirectMatches);
        Assert.Contains(response.Gaps, g => g.UnsupportedQualifier == "Master's degree");
        Assert.Contains(response.Gaps, g => g.UnsupportedQualifier == "Certification");
    }

    [Fact]
    public void Weakly_related_chunks_are_gaps_not_support()
    {
        var response = Analyze(
            "Platform Engineer\n- Quantum cryptography hardware design",
            CreateChunk("kinetiq-v", "Kinetiq V", "Pipeline", "Hardware accelerated computer vision pipeline.", "verified", 2.0));

        Assert.Empty(response.DirectMatches);
        Assert.Empty(response.Inferences);
        Assert.Single(response.Gaps);
    }

    [Fact]
    public void Role_headings_and_section_labels_are_not_extracted_as_requirements()
    {
        var response = Analyze(
            "Backend Engineer\n\nRequirements:\n1. Computer vision pipeline\n2) Autonomous agents design\n\nBenefits:\n- Remote work",
            CreateChunk("kinetiq-v", "Kinetiq V", "Pipeline", "Computer vision pipeline.", "verified", 2.0));

        Assert.Equal("Backend Engineer", response.RoleSummary);
        Assert.Equal(
            ["Computer vision pipeline", "Autonomous agents design"],
            response.ExtractedRequirements.Select(r => r.RequirementText));
        Assert.DoesNotContain(response.ExtractedRequirements, r => r.RequirementText.Contains("Backend Engineer"));
        Assert.DoesNotContain(response.ExtractedRequirements, r => r.RequirementText.EndsWith(':'));
    }

    [Theory]
    [InlineData("Backend Engineer")]
    [InlineData("## Senior AI Engineer (Remote)")]
    public void A_vacancy_with_only_a_role_heading_extracts_no_requirements(string vacancy)
    {
        var response = Analyze(vacancy);

        Assert.Empty(response.ExtractedRequirements);
        Assert.Empty(response.DirectMatches);
        Assert.Empty(response.Inferences);
        Assert.Empty(response.Gaps);
    }

    [Fact]
    public void Prose_vacancy_skips_the_role_heading_line()
    {
        var response = Analyze(
            "Backend Engineer\nWe want someone who knows computer vision pipelines.",
            CreateChunk("kinetiq-v", "Kinetiq V", "Pipeline", "Computer vision pipeline.", "verified", 2.0));

        Assert.DoesNotContain(response.ExtractedRequirements, r => r.RequirementText == "Backend Engineer");
        Assert.NotEmpty(response.ExtractedRequirements);
    }

    [Fact]
    public void Verified_chunk_without_claims_is_never_direct_evidence()
    {
        var response = Analyze(
            "Platform Engineer\n- Autonomous agents architecture",
            CreateChunk("vextis", "Vextis", "Architecture", "Autonomous agents architecture.", "verified", 2.0, withClaims: false));

        Assert.Empty(response.DirectMatches);
        var inference = Assert.Single(response.Inferences);
        Assert.Null(inference.SupportingClaimId);
        Assert.Equal("docs/evidence/vextis.md#architecture", inference.SupportingCitation);
        Assert.Contains("no verified claim", inference.Rationale);
    }

    [Fact]
    public void Verified_chunk_with_only_a_pending_claim_is_not_direct_evidence()
    {
        var response = Analyze(
            "Platform Engineer\n- Autonomous agents architecture",
            CreateChunk("vextis", "Vextis", "Architecture", "Autonomous agents architecture.", "verified", 2.0, "claim-vextis-01", claimStatus: "pending"));

        Assert.Empty(response.DirectMatches);
        Assert.Equal("claim-vextis-01", Assert.Single(response.Inferences).SupportingClaimId);
    }

    [Fact]
    public void Verified_claim_without_a_citation_is_not_direct_evidence()
    {
        var response = Analyze(
            "Platform Engineer\n- Autonomous agents architecture",
            CreateChunk("vextis", "Vextis", "Architecture", "Autonomous agents architecture.", "verified", 2.0, "claim-vextis-01", claimCitation: " "));

        Assert.Empty(response.DirectMatches);
        Assert.Single(response.Inferences);
    }

    [Fact]
    public void Direct_match_carries_the_real_claim_id_citation_and_target()
    {
        var response = Analyze(
            "Platform Engineer\n- Autonomous agents architecture",
            CreateChunk(
                "vextis", "Vextis", "Architecture", "Autonomous agents architecture.", "verified", 2.0, "claim-vextis-01",
                claimCitation: "docs/evidence/projects/vextis.md#architecture"));

        var match = Assert.Single(response.DirectMatches);
        Assert.Equal("claim-vextis-01", match.ClaimId);
        Assert.Equal("docs/evidence/projects/vextis.md#architecture", match.Citation);
        Assert.Equal("project", match.DocumentKind);
        Assert.Equal("vextis", match.DocumentSlug);
        Assert.Equal("architecture", match.SectionSlug);
    }

    [Fact]
    public void No_response_ever_contains_a_synthetic_claim_identifier()
    {
        var response = Analyze(
            "Platform Engineer\n- Autonomous agents architecture\n- Computer vision pipeline",
            CreateChunk("vextis", "Vextis", "Architecture", "Autonomous agents architecture.", "verified", 2.0, withClaims: false),
            CreateChunk("kinetiq-v", "Kinetiq V", "Pipeline", "Computer vision pipeline.", "pending", 2.0, "claim-kinetiq-v-01"));

        Assert.DoesNotContain(response.DirectMatches, m => m.ClaimId == "claim-verified");
        Assert.All(response.Inferences, i => Assert.NotEqual("claim-verified", i.SupportingClaimId));
    }

    [Fact]
    public void Profile_evidence_is_identified_as_a_profile_not_a_project()
    {
        var response = Analyze(
            "Platform Engineer\n- Autonomous agents\n- Cloud systems",
            CreateChunk(
                "profile", "Rafael Patino", "Focus areas", "Autonomous agents and cloud systems.", "verified", 2.0, "claim-profile-01",
                kind: "profile", claimCitation: "docs/evidence/profile.md#focus-areas"),
            CreateChunk(
                "vextis", "Vextis", "Architecture", "Autonomous agents.", "pending", 1.0, "claim-vextis-01"));

        var direct = response.DirectMatches.First(m => m.DocumentSlug == "profile");
        Assert.Equal("profile", direct.DocumentKind);
        Assert.Equal("docs/evidence/profile.md#focus-areas", direct.Citation);
        Assert.All(response.Inferences, i =>
            Assert.Equal(i.SupportingDocumentSlug == "profile" ? "profile" : "project", i.SupportingDocumentKind));
    }

    private static JobMatchingEvidenceChunk WithClaims(
        JobMatchingEvidenceChunk chunk,
        params JobMatchingEvidenceClaim[] claims) => chunk with { Claims = claims };

    [Fact]
    public void Qualifier_stated_only_by_an_unrelated_claim_in_the_same_chunk_is_not_a_direct_match()
    {
        var chunk = WithClaims(
            CreateChunk("vextis", "Vextis", "Architecture", "Autonomous agents with sandboxed memory.", "verified", 2.0),
            new JobMatchingEvidenceClaim(
                "claim-vextis-01", "Autonomous agents with sandboxed memory.", "verified",
                "docs/evidence/projects/vextis.md#architecture"),
            new JobMatchingEvidenceClaim(
                "claim-vextis-02", "Seven years leading unrelated hardware work.", "verified",
                "docs/evidence/projects/vextis.md#history"));

        var response = Analyze("Platform Engineer\n- Five years of autonomous agents experience", chunk);

        Assert.Empty(response.DirectMatches);

        var inference = Assert.Single(response.Inferences);
        Assert.Equal("claim-vextis-01", inference.SupportingClaimId);
        Assert.Equal("docs/evidence/projects/vextis.md#architecture", inference.SupportingCitation);

        var gap = Assert.Single(response.Gaps);
        Assert.Equal("Five years", gap.UnsupportedQualifier);
    }

    [Fact]
    public void Direct_match_cites_the_single_claim_that_states_the_capability_and_the_qualifier()
    {
        var chunk = WithClaims(
            CreateChunk("vextis", "Vextis", "Architecture", "Autonomous agents with sandboxed memory.", "verified", 2.0),
            new JobMatchingEvidenceClaim(
                "claim-vextis-01", "Autonomous agents with sandboxed memory.", "verified",
                "docs/evidence/projects/vextis.md#architecture"),
            new JobMatchingEvidenceClaim(
                "claim-vextis-02", "Seven years building autonomous agents in production.", "verified",
                "docs/evidence/projects/vextis.md#history"));

        var response = Analyze("Platform Engineer\n- Five years of autonomous agents experience", chunk);

        var match = Assert.Single(response.DirectMatches);
        Assert.Equal("claim-vextis-02", match.ClaimId);
        Assert.Equal("docs/evidence/projects/vextis.md#history", match.Citation);
        Assert.Empty(response.Inferences);
        Assert.Empty(response.Gaps);
    }

    [Fact]
    public void Each_qualifier_must_be_stated_by_the_same_cited_claim()
    {
        var chunk = WithClaims(
            CreateChunk("vextis", "Vextis", "Architecture", "Autonomous agents with sandboxed memory.", "verified", 2.0),
            new JobMatchingEvidenceClaim(
                "claim-vextis-01", "Seven years building autonomous agents.", "verified",
                "docs/evidence/projects/vextis.md#history"),
            new JobMatchingEvidenceClaim(
                "claim-vextis-02", "Autonomous agents certified by an external body.", "verified",
                "docs/evidence/projects/vextis.md#certification"));

        var response = Analyze("Platform Engineer\n- Five years of certified autonomous agents experience", chunk);

        Assert.Empty(response.DirectMatches);
        Assert.Single(response.Inferences);
        Assert.Contains(response.Gaps, g => g.UnsupportedQualifier == "certified");
    }

    [Fact]
    public void Only_requirement_sections_contribute_requirements_not_benefits()
    {
        var response = Analyze(
            "Backend Engineer\n\nAbout us\nWe are a friendly company.\n- Weekly team lunches\n\n" +
            "Requirements:\n- Computer vision pipeline\n- Autonomous agents design\n\n" +
            "Benefits:\n- Private health insurance\n- Unlimited vacation days\n\n" +
            "Perks\n- Gym membership\n\nAbout the team:\n- Quarterly offsites\n\n" +
            "What we offer:\n1. Stock options\n2. Remote budget",
            CreateChunk("kinetiq-v", "Kinetiq V", "Pipeline", "Computer vision pipeline.", "verified", 2.0));

        Assert.Equal(
            ["Computer vision pipeline", "Autonomous agents design"],
            response.ExtractedRequirements.Select(r => r.RequirementText));

        var surfaced = response.DirectMatches.Select(m => m.RequirementText)
            .Concat(response.Inferences.Select(i => i.RequirementText))
            .Concat(response.Gaps.Select(g => g.RequirementText))
            .ToList();
        Assert.All(surfaced, text => Assert.Contains(text, new[] { "Computer vision pipeline", "Autonomous agents design" }));
    }

    [Fact]
    public void Extraction_resumes_after_an_excluded_section_and_covers_equivalent_requirement_sections()
    {
        var response = Analyze(
            "Staff Engineer\nBenefits:\n- Free snacks\n\n" +
            "Responsibilities:\n- Computer vision pipeline ownership\n\n" +
            "Qualifications\n1. Autonomous agents design\n\n" +
            "Must have:\n- Cloud systems architecture\n\n" +
            "Nice to have:\n- Observability tooling\n\n" +
            "Skills:\n- Computer vision evaluation",
            CreateChunk("kinetiq-v", "Kinetiq V", "Pipeline", "Computer vision pipeline.", "verified", 2.0));

        Assert.Equal(
            [
                "Computer vision pipeline ownership",
                "Autonomous agents design",
                "Cloud systems architecture",
                "Observability tooling",
                "Computer vision evaluation"
            ],
            response.ExtractedRequirements.Select(r => r.RequirementText));
    }

    [Fact]
    public void A_vacancy_with_only_benefit_bullets_extracts_no_requirements()
    {
        var response = Analyze("Backend Engineer\nBenefits:\n- Private health insurance\n- Unlimited vacation days");

        Assert.Empty(response.ExtractedRequirements);
        Assert.Empty(response.Gaps);
    }
}
