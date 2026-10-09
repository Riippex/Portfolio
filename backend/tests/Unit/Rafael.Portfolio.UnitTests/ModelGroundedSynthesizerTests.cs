using Microsoft.Extensions.Time.Testing;
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

    private static InMemoryModelControlLedger NewLedger(ModelControlPolicy? policy = null) =>
        new(policy ?? ModelControlFixtures.Policy(), new FakeTimeProvider(ModelControlFixtures.Noon));

    [Fact]
    public void Falls_back_to_deterministic_synthesizer_when_model_provider_is_disabled()
    {
        var ledger = NewLedger();
        var synthesizer = new ModelGroundedSynthesizer(new DisabledModelProvider(), ledger, new DeterministicGroundedSynthesizer());

        var response = synthesizer.Synthesize("Tell me about Rafael's agent experience", SampleChunks);

        Assert.NotEmpty(response.Answer);
        Assert.NotEmpty(response.Citations);
        Assert.Equal(0, ledger.ReservationCount);
    }

    [Fact]
    public void Reserves_calls_once_and_settles_to_the_reported_usage()
    {
        var provider = new TestModelProvider(isAvailable: true, response: new ModelProviderResponse(
            Success: true,
            Text: "Rafael has extensive experience designing autonomous AI agents.",
            InputTokens: 500,
            OutputTokens: 100));
        var ledger = NewLedger();
        var synthesizer = new ModelGroundedSynthesizer(provider, ledger, new DeterministicGroundedSynthesizer());

        var response = synthesizer.Synthesize("Tell me about agents", SampleChunks);

        Assert.Equal("Rafael has extensive experience designing autonomous AI agents.", response.Answer);
        Assert.Equal(1, provider.CallCount);
        Assert.Equal(1, ledger.ReservationCount);
        Assert.Equal(50 + 40, ledger.DayCharged(ModelControlFixtures.Noon));
        Assert.Equal(0, ledger.ActivePermits);
    }

    [Fact]
    public void Falls_back_without_calling_the_provider_when_the_reservation_is_denied()
    {
        var provider = new TestModelProvider(isAvailable: true, response: new ModelProviderResponse(Success: true, Text: "AI response"));
        var ledger = NewLedger(ModelControlFixtures.Policy(daily: 1, monthly: 1)); // a turn does not fit
        var synthesizer = new ModelGroundedSynthesizer(provider, ledger, new DeterministicGroundedSynthesizer());

        var response = synthesizer.Synthesize("Tell me about agents", SampleChunks);

        Assert.NotEmpty(response.Answer);
        Assert.Equal(0, provider.CallCount);
        Assert.Equal(0, ledger.DayCharged(ModelControlFixtures.Noon));
    }

    [Fact]
    public void Falls_back_without_spending_when_no_control_store_is_available()
    {
        var provider = new TestModelProvider(isAvailable: true, response: new ModelProviderResponse(Success: true, Text: "AI response"));
        var synthesizer = new ModelGroundedSynthesizer(provider, new UnavailableModelControlLedger(), new DeterministicGroundedSynthesizer());

        var response = synthesizer.Synthesize("Tell me about agents", SampleChunks);

        Assert.NotEmpty(response.Answer);
        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public void Refunds_the_reservation_when_the_prompt_exceeds_the_input_allowance_before_any_call()
    {
        var provider = new TestModelProvider(isAvailable: true, response: new ModelProviderResponse(Success: true, Text: "AI response"));
        var ledger = NewLedger();
        var synthesizer = new ModelGroundedSynthesizer(provider, ledger, new DeterministicGroundedSynthesizer());
        var huge = SampleChunks.Select(c => c with { Content = new string('x', 7_000) }).ToList();

        var response = synthesizer.Synthesize("Tell me about agents", huge);

        Assert.NotEmpty(response.Answer);
        Assert.Equal(0, provider.CallCount);
        Assert.Equal(0, ledger.DayCharged(ModelControlFixtures.Noon));
        Assert.Equal(0, ledger.ActivePermits);
    }

    [Fact]
    public void Keeps_the_charge_and_the_permit_when_the_provider_fails_after_dispatch()
    {
        var provider = new TestModelProvider(isAvailable: true, response: new ModelProviderResponse(Success: false, Text: null, ErrorMessage: "timeout"));
        var ledger = NewLedger();
        var synthesizer = new ModelGroundedSynthesizer(provider, ledger, new DeterministicGroundedSynthesizer());

        var response = synthesizer.Synthesize("Tell me about agents", SampleChunks);

        Assert.NotEmpty(response.Answer);
        Assert.Equal(1, provider.CallCount);
        Assert.Equal(ModelControlFixtures.WorstCase, ledger.DayCharged(ModelControlFixtures.Noon));
        Assert.Equal(1, ledger.ActivePermits);
    }

    [Fact]
    public void Keeps_the_charge_when_the_provider_throws_after_dispatch()
    {
        var provider = new TestModelProvider(isAvailable: true, response: new ModelProviderResponse(Success: true, Text: "x"), throwOnCall: true);
        var ledger = NewLedger();
        var synthesizer = new ModelGroundedSynthesizer(provider, ledger, new DeterministicGroundedSynthesizer());

        var response = synthesizer.Synthesize("Tell me about agents", SampleChunks);

        Assert.NotEmpty(response.Answer);
        Assert.Equal(ModelControlFixtures.WorstCase, ledger.DayCharged(ModelControlFixtures.Noon));
        Assert.Equal(ReservationState.Uncertain, ledger.Reservation(ledger.Root!.Permits.Keys.Single())!.State);
    }

    [Fact]
    public void Does_not_use_an_answer_whose_usage_is_invalid()
    {
        var provider = new TestModelProvider(isAvailable: true, response: new ModelProviderResponse(
            Success: true, Text: "unaccounted answer", InputTokens: 0, OutputTokens: 0));
        var ledger = NewLedger();
        var synthesizer = new ModelGroundedSynthesizer(provider, ledger, new DeterministicGroundedSynthesizer());

        var response = synthesizer.Synthesize("Tell me about agents", SampleChunks);

        Assert.NotEqual("unaccounted answer", response.Answer);
        Assert.Equal(ModelControlFixtures.WorstCase, ledger.DayCharged(ModelControlFixtures.Noon));
    }

    private sealed class TestModelProvider : IModelProvider
    {
        public bool IsAvailable { get; }
        private readonly ModelProviderResponse _response;
        public int CallCount { get; private set; }

        private readonly bool _throwOnCall;

        public TestModelProvider(bool isAvailable, ModelProviderResponse response, bool throwOnCall = false)
        {
            IsAvailable = isAvailable;
            _response = response;
            _throwOnCall = throwOnCall;
        }

        public ValueTask<ModelProviderResponse> GenerateAsync(ModelProviderRequest request, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return _throwOnCall ? throw new InvalidOperationException("provider failure") : ValueTask.FromResult(_response);
        }
    }
}
