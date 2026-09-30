using Rafael.Portfolio.Modules.Assistant.Application;
using Rafael.Portfolio.Modules.Assistant.Domain;
using Rafael.Portfolio.Modules.Assistant.Infrastructure;

namespace Rafael.Portfolio.UnitTests;

public sealed class AssistantStreamTests
{
    private sealed class MockEvidenceAdapter : IAssistantEvidenceAdapter
    {
        public List<AssistantEvidenceChunk> Chunks { get; set; } = [];

        public IReadOnlyList<AssistantEvidenceChunk> SearchEvidence(string query, int limit = 5, string? slugFilter = null) =>
            Chunks;
    }

    private static AssistantEvidenceChunk CreateChunk(
        string chunkId = "chunk-test-1",
        string slug = "vextis",
        string title = "Vextis Architecture",
        double score = 4.0)
    {
        return new AssistantEvidenceChunk(
            ChunkId: chunkId,
            DocumentId: "doc-1",
            Slug: slug,
            Title: title,
            SectionHeading: "Overview",
            SectionSlug: "overview",
            Content: "Autonomous multi-agent workflow orchestration with state machines.",
            Claims: ["claim-1"],
            SourceUrl: "https://example.com/source",
            EvidenceStatus: "pending",
            Version: "2026.09",
            Visibility: "public",
            Score: score,
            Citations: ["docs/evidence/vextis.md#overview"]);
    }

    [Fact]
    public async Task StreamChatAsync_yields_status_citations_and_tokens_for_grounded_query()
    {
        var adapter = new MockEvidenceAdapter
        {
            Chunks = [CreateChunk()]
        };
        var synthesizer = new DeterministicGroundedSynthesizer();
        var service = new AssistantService(adapter, synthesizer);

        var events = new List<AssistantStreamEvent>();
        await foreach (var evt in service.StreamChatAsync(new AssistantChatRequest("Explain Vextis")))
        {
            events.Add(evt);
        }

        Assert.NotEmpty(events);

        // First event is status
        Assert.Equal("status", events[0].Type);
        Assert.Equal(AssistantGroundingStatus.Grounded, events[0].GroundingStatus);

        // Citation events follow
        var citationEvents = events.Where(e => e.Type == "citation").ToList();
        Assert.Single(citationEvents);
        Assert.Equal("chunk-test-1", citationEvents[0].Citation?.ChunkId);
        Assert.Equal("vextis", citationEvents[0].Citation?.Slug);

        // Token events follow
        var tokenEvents = events.Where(e => e.Type == "token").ToList();
        Assert.NotEmpty(tokenEvents);
        var combinedText = string.Join(" ", tokenEvents.Select(t => t.Text));
        Assert.Contains("Autonomous multi-agent workflow orchestration", combinedText);

        // Last event is done
        Assert.Equal("done", events[^1].Type);
        Assert.True(events[^1].Done);
    }

    [Fact]
    public async Task StreamChatAsync_yields_not_documented_for_empty_matches()
    {
        var adapter = new MockEvidenceAdapter { Chunks = [] };
        var synthesizer = new DeterministicGroundedSynthesizer();
        var service = new AssistantService(adapter, synthesizer);

        var events = new List<AssistantStreamEvent>();
        await foreach (var evt in service.StreamChatAsync(new AssistantChatRequest("Unmatched query")))
        {
            events.Add(evt);
        }

        Assert.NotEmpty(events);
        Assert.Equal("status", events[0].Type);
        Assert.Equal(AssistantGroundingStatus.NotDocumented, events[0].GroundingStatus);

        Assert.DoesNotContain(events, e => e.Type == "citation");

        var tokenEvents = events.Where(e => e.Type == "token").ToList();
        Assert.NotEmpty(tokenEvents);
        Assert.Contains("not have documented evidence", tokenEvents[0].Text);

        Assert.Equal("done", events[^1].Type);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task StreamChatAsync_throws_on_empty_message(string empty)
    {
        var adapter = new MockEvidenceAdapter();
        var synthesizer = new DeterministicGroundedSynthesizer();
        var service = new AssistantService(adapter, synthesizer);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await foreach (var _ in service.StreamChatAsync(new AssistantChatRequest(empty)))
            {
            }
        });
    }

    [Fact]
    public async Task StreamChatAsync_throws_on_oversized_message()
    {
        var adapter = new MockEvidenceAdapter();
        var synthesizer = new DeterministicGroundedSynthesizer();
        var service = new AssistantService(adapter, synthesizer);

        var oversized = new string('x', AssistantChatRequest.MaxMessageLength + 1);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await foreach (var _ in service.StreamChatAsync(new AssistantChatRequest(oversized)))
            {
            }
        });
    }
}
