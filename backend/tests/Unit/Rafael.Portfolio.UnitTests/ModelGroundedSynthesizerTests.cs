using Rafael.Portfolio.Modules.Assistant.Application;
using Rafael.Portfolio.Modules.Assistant.Domain;
using Rafael.Portfolio.Modules.Assistant.Infrastructure;

namespace Rafael.Portfolio.UnitTests;

public sealed class ModelGroundedSynthesizerTests
{
    private static readonly List<AssistantEvidenceChunk> SampleChunks = new()
    {
        new AssistantEvidenceChunk(
            ChunkId: "chunk-1",
            DocumentId: "doc-1",
            Slug: "autonomous-agents",
            Title: "Autonomous Agents",
            SectionHeading: "Overview",
            SectionSlug: "overview",
            Content: "Rafael built autonomous agent pipelines.",
            Claims: new[] { "Built autonomous agent pipelines" },
            SourceUrl: "https://example.local/agents",
            EvidenceStatus: "verified",
            Version: "v1",
            Visibility: "public",
            Score: 1.0,
            Citations: new List<string>())
    };

    [Fact]
    public void Falls_back_to_deterministic_synthesizer_when_model_provider_is_disabled()
    {
        var disabledProvider = new DisabledModelProvider();
        var ledger = new InMemoryModelControlLedger();
        var fallback = new DeterministicGroundedSynthesizer();
        var synthesizer = new ModelGroundedSynthesizer(disabledProvider, ledger, fallback);

        var response = synthesizer.Synthesize("Tell me about Rafael's agent experience", SampleChunks);

        Assert.NotNull(response);
        Assert.NotEmpty(response.Answer);
        Assert.NotEmpty(response.Citations);
    }

    [Fact]
    public void Uses_model_provider_and_commits_budget_when_available_and_reservation_granted()
    {
        var mockProvider = new TestModelProvider(isAvailable: true, response: new ModelProviderResponse(
            Success: true,
            Text: "Rafael has extensive experience designing autonomous AI agents.",
            InputTokens: 500,
            OutputTokens: 100));

        var ledger = new InMemoryModelControlLedger();
        var fallback = new DeterministicGroundedSynthesizer();
        var synthesizer = new ModelGroundedSynthesizer(mockProvider, ledger, fallback);

        var response = synthesizer.Synthesize("Tell me about agents", SampleChunks);

        Assert.Equal("Rafael has extensive experience designing autonomous AI agents.", response.Answer);
        Assert.Equal(1, ledger.ReservationCount);
    }

    [Fact]
    public void Falls_back_when_budget_reservation_is_denied()
    {
        var mockProvider = new TestModelProvider(isAvailable: true, response: new ModelProviderResponse(Success: true, Text: "AI response"));
        var ledger = new InMemoryModelControlLedger(dailyBudgetMicroUsd: 1); // Budget 1 micro-USD (insufficient)
        var fallback = new DeterministicGroundedSynthesizer();
        var synthesizer = new ModelGroundedSynthesizer(mockProvider, ledger, fallback);

        var response = synthesizer.Synthesize("Tell me about agents", SampleChunks);

        Assert.NotNull(response);
        // Fallback produced an answer, model provider was not invoked
        Assert.Equal(0, mockProvider.CallCount);
    }

    private sealed class TestModelProvider : IModelProvider
    {
        public bool IsAvailable { get; }
        private readonly ModelProviderResponse _response;
        public int CallCount { get; private set; }

        public TestModelProvider(bool isAvailable, ModelProviderResponse response)
        {
            IsAvailable = isAvailable;
            _response = response;
        }

        public ValueTask<ModelProviderResponse> GenerateAsync(ModelProviderRequest request, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return ValueTask.FromResult(_response);
        }
    }
}
