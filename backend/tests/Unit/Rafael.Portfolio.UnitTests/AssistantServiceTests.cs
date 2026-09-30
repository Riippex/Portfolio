using Rafael.Portfolio.Modules.Assistant.Application;
using Rafael.Portfolio.Modules.Assistant.Domain;
using Rafael.Portfolio.Modules.Assistant.Infrastructure;

namespace Rafael.Portfolio.UnitTests;

public sealed class AssistantServiceTests
{
    private sealed class StubEvidenceAdapter : IAssistantEvidenceAdapter
    {
        public List<AssistantEvidenceChunk> ChunksToReturn { get; set; } = [];
        public string? LastCapturedQuery { get; private set; }
        public int LastCapturedLimit { get; private set; }
        public string? LastCapturedSlug { get; private set; }

        public IReadOnlyList<AssistantEvidenceChunk> SearchEvidence(string query, int limit = 5, string? slugFilter = null)
        {
            LastCapturedQuery = query;
            LastCapturedLimit = limit;
            LastCapturedSlug = slugFilter;
            return ChunksToReturn;
        }
    }

    private static AssistantEvidenceChunk CreateChunk(
        string chunkId = "chunk-vextis-arch",
        string slug = "vextis",
        string title = "Vextis Architecture",
        string sectionHeading = "Architecture",
        string content = "Multi-agent autonomous workflow engine with deterministic state machines.",
        double score = 3.5,
        string evidenceStatus = "pending",
        IReadOnlyList<string>? claims = null)
    {
        return new AssistantEvidenceChunk(
            ChunkId: chunkId,
            DocumentId: "doc-vextis",
            Slug: slug,
            Title: title,
            SectionHeading: sectionHeading,
            SectionSlug: "architecture",
            Content: content,
            Claims: claims ?? ["claim-vextis-01"],
            SourceUrl: "https://example.com/vextis",
            EvidenceStatus: evidenceStatus,
            Version: "2026.09",
            Visibility: "public",
            Score: score,
            Citations: ["docs/evidence/projects/vextis.md#architecture"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void Chat_throws_on_empty_or_whitespace_message(string message)
    {
        var adapter = new StubEvidenceAdapter();
        var synthesizer = new DeterministicGroundedSynthesizer();
        var service = new AssistantService(adapter, synthesizer);

        Assert.Throws<ArgumentException>(() => service.Chat(new AssistantChatRequest(message)));
    }

    [Fact]
    public void Chat_throws_on_null_request()
    {
        var adapter = new StubEvidenceAdapter();
        var synthesizer = new DeterministicGroundedSynthesizer();
        var service = new AssistantService(adapter, synthesizer);

        Assert.Throws<ArgumentNullException>(() => service.Chat(null!));
    }

    [Fact]
    public void Chat_throws_on_oversized_message()
    {
        var adapter = new StubEvidenceAdapter();
        var synthesizer = new DeterministicGroundedSynthesizer();
        var service = new AssistantService(adapter, synthesizer);

        var oversized = new string('a', AssistantChatRequest.MaxMessageLength + 1);

        Assert.Throws<ArgumentException>(() => service.Chat(new AssistantChatRequest(oversized)));
    }

    [Fact]
    public void Chat_accepts_max_boundary_message_length()
    {
        var adapter = new StubEvidenceAdapter();
        var synthesizer = new DeterministicGroundedSynthesizer();
        var service = new AssistantService(adapter, synthesizer);

        var boundaryMessage = new string('a', AssistantChatRequest.MaxMessageLength);

        var response = service.Chat(new AssistantChatRequest(boundaryMessage));

        Assert.Equal(AssistantGroundingStatus.NotDocumented, response.GroundingStatus);
    }

    [Theory]
    [InlineData("invalid_slug!")]
    [InlineData("slug with spaces")]
    [InlineData("-starts-with-hyphen")]
    public void Chat_throws_on_invalid_slug_filter(string invalidSlug)
    {
        var adapter = new StubEvidenceAdapter();
        var synthesizer = new DeterministicGroundedSynthesizer();
        var service = new AssistantService(adapter, synthesizer);

        Assert.Throws<ArgumentException>(() => service.Chat(new AssistantChatRequest("hello", invalidSlug)));
    }

    [Fact]
    public void Chat_throws_on_oversized_slug_filter()
    {
        var adapter = new StubEvidenceAdapter();
        var synthesizer = new DeterministicGroundedSynthesizer();
        var service = new AssistantService(adapter, synthesizer);

        var oversizedSlug = new string('a', AssistantChatRequest.MaxSlugLength + 1);

        Assert.Throws<ArgumentException>(() => service.Chat(new AssistantChatRequest("hello", oversizedSlug)));
    }

    [Fact]
    public void Chat_returns_grounded_response_with_citations_when_evidence_matches()
    {
        var adapter = new StubEvidenceAdapter
        {
            ChunksToReturn = [CreateChunk()]
        };
        var synthesizer = new DeterministicGroundedSynthesizer();
        var service = new AssistantService(adapter, synthesizer);

        var response = service.Chat(new AssistantChatRequest("Tell me about Vextis architecture"));

        Assert.Equal(AssistantGroundingStatus.Grounded, response.GroundingStatus);
        Assert.Contains("Multi-agent autonomous workflow engine", response.Answer);
        var citation = Assert.Single(response.Citations);
        Assert.Equal("chunk-vextis-arch", citation.ChunkId);
        Assert.Equal("vextis", citation.Slug);
        Assert.Equal("Vextis Architecture", citation.Title);
        Assert.Equal("Architecture", citation.SectionHeading);
        Assert.Equal("pending", citation.EvidenceStatus);
        Assert.Equal("2026.09", citation.Version);
        Assert.Contains("claim-vextis-01", citation.Claims);
    }

    [Fact]
    public void Chat_returns_not_documented_when_no_evidence_matches()
    {
        var adapter = new StubEvidenceAdapter
        {
            ChunksToReturn = []
        };
        var synthesizer = new DeterministicGroundedSynthesizer();
        var service = new AssistantService(adapter, synthesizer);

        var response = service.Chat(new AssistantChatRequest("Quantum baking recipes"));

        Assert.Equal(AssistantGroundingStatus.NotDocumented, response.GroundingStatus);
        Assert.Empty(response.Citations);
        Assert.Contains("not have documented evidence", response.Answer);
    }

    [Fact]
    public void Chat_ignores_zero_score_chunks_and_reports_not_documented()
    {
        var adapter = new StubEvidenceAdapter
        {
            ChunksToReturn = [CreateChunk(score: 0.0)]
        };
        var synthesizer = new DeterministicGroundedSynthesizer();
        var service = new AssistantService(adapter, synthesizer);

        var response = service.Chat(new AssistantChatRequest("Unmatched query"));

        Assert.Equal(AssistantGroundingStatus.NotDocumented, response.GroundingStatus);
        Assert.Empty(response.Citations);
    }

    [Fact]
    public void Chat_preserves_pending_status_for_unverified_evidence()
    {
        var adapter = new StubEvidenceAdapter
        {
            ChunksToReturn = [CreateChunk(evidenceStatus: "pending")]
        };
        var synthesizer = new DeterministicGroundedSynthesizer();
        var service = new AssistantService(adapter, synthesizer);

        var response = service.Chat(new AssistantChatRequest("Vextis status"));

        Assert.Equal(AssistantGroundingStatus.Grounded, response.GroundingStatus);
        var citation = Assert.Single(response.Citations);
        Assert.Equal("pending", citation.EvidenceStatus);
    }

    [Fact]
    public void Chat_passes_slug_filter_to_adapter()
    {
        var adapter = new StubEvidenceAdapter();
        var synthesizer = new DeterministicGroundedSynthesizer();
        var service = new AssistantService(adapter, synthesizer);

        service.Chat(new AssistantChatRequest("autonomous systems", "vextis"));

        Assert.Equal("autonomous systems", adapter.LastCapturedQuery);
        Assert.Equal("vextis", adapter.LastCapturedSlug);
    }

    [Fact]
    public void Synthesizer_caps_citations_at_top_three_chunks()
    {
        var adapter = new StubEvidenceAdapter
        {
            ChunksToReturn =
            [
                CreateChunk("chunk-1", "s1", "T1", "Sec1", "Content 1", 5.0),
                CreateChunk("chunk-2", "s2", "T2", "Sec2", "Content 2", 4.0),
                CreateChunk("chunk-3", "s3", "T3", "Sec3", "Content 3", 3.0),
                CreateChunk("chunk-4", "s4", "T4", "Sec4", "Content 4", 2.0),
                CreateChunk("chunk-5", "s5", "T5", "Sec5", "Content 5", 1.0)
            ]
        };
        var synthesizer = new DeterministicGroundedSynthesizer();
        var service = new AssistantService(adapter, synthesizer);

        var response = service.Chat(new AssistantChatRequest("multi chunk query"));

        Assert.Equal(AssistantGroundingStatus.Grounded, response.GroundingStatus);
        Assert.Equal(3, response.Citations.Count);
        Assert.Equal("chunk-1", response.Citations[0].ChunkId);
        Assert.Equal("chunk-2", response.Citations[1].ChunkId);
        Assert.Equal("chunk-3", response.Citations[2].ChunkId);
    }
}
