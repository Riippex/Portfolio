using Microsoft.Extensions.Time.Testing;
using Rafael.Portfolio.Modules.Assistant.Application;
using Rafael.Portfolio.Modules.Assistant.Application.Orchestration;
using Rafael.Portfolio.Modules.Assistant.Domain;

namespace Rafael.Portfolio.UnitTests.Orchestration;

internal sealed class OrchestrationHarness
{
    internal const string Valid = """{"v":1,"outcome":"answer","claims":["C1"]}""";
    internal const string Statement = "The owner-maintained profile announces an AI Engineer role at Expinn; it does not confirm a current role.";
    internal FakeTimeProvider Clock { get; } = new(ModelControlFixtures.Noon);
    internal InMemoryModelControlLedger Real { get; }
    internal RecordingLedger Ledger { get; }
    internal RecordingProvider Provider { get; } = new();
    internal RecordingCounter Counter { get; } = new();
    internal AssistantTurnOrchestrator Orchestrator { get; }
    internal AssistantEvidenceChunk[] Chunks { get; set; } =
    [
        new("chunk-1", "profile", "profile", "Rafael", "Experience", "experience", "Unselected prose",
            ["claim-1", "claim-2"], "https://example.test/profile", "verified", "v1", "public", 1, [],
            [new("claim-1", Statement, "verified", "profile#experience"),
             new("claim-2", "The profile lists StaffHub in its experience section.", "verified", "profile#experience")]),
        new("chunk-2", "profile", "profile", "Rafael", "Studies", "studies", "Unselected studies prose",
            ["claim-3"], "https://example.test/profile", "verified", "v1", "public", 1, [],
            [new("claim-3", "The profile lists studies in progress.", "verified", "profile#studies")])
    ];

    internal OrchestrationHarness()
    {
        Real = new(ModelControlFixtures.Policy(), Clock);
        Ledger = new(Real);
        Orchestrator = new(Provider, Counter, Ledger, Clock);
    }

    internal Task<AssistantChatResponse?> Run(CancellationToken ct = default, string question = "Tell me about Expinn") =>
        Orchestrator.RunAsync(question, Chunks, Clock.GetTimestamp(), ct).AsTask();

    internal ReservationRecord Record => Real.Reservation(Ledger.Id!)!;

    internal async Task<T> Drive<T>(Task<T> task)
    {
        for (var i = 0; i < 64 && !task.IsCompleted; i++)
        {
            Clock.Advance(TimeSpan.FromMilliseconds(250));
            await Task.Delay(2);
        }
        return await task.WaitAsync(TimeSpan.FromSeconds(5));
    }
}

internal sealed class RecordingProvider : IModelProvider
{
    public bool IsAvailable { get; set; } = true;
    internal int Calls { get; private set; }
    internal ModelProviderRequest? Request { get; private set; }
    internal Func<CancellationToken, ValueTask<ModelProviderResponse>> Generate { get; set; } = _ =>
        ValueTask.FromResult(new ModelProviderResponse(true, OrchestrationHarness.Valid, 100, 10));
    public ValueTask<ModelProviderResponse> GenerateAsync(ModelProviderRequest request, CancellationToken cancellationToken = default)
    {
        Calls++;
        Request = request;
        return Generate(cancellationToken);
    }
}

internal sealed class RecordingCounter : IModelRequestTokenCounter
{
    internal int Calls { get; private set; }
    internal ModelProviderRequest? Request { get; private set; }
    internal Func<ModelProviderRequest, CancellationToken, ValueTask<ModelRequestTokenCount>> Count { get; set; } =
        (_, _) => ValueTask.FromResult(new ModelRequestTokenCount(500, ModelTokenCountBasis.ProviderVerifiedUpperBound));
    public ValueTask<ModelRequestTokenCount> CountAsync(ModelProviderRequest request, CancellationToken cancellationToken = default)
    {
        Calls++;
        Request = request;
        return Count(request, cancellationToken);
    }
}

// Decorators operate on the existing real accounting rules; no mock ledger state machine.
internal sealed class RecordingLedger(IModelControlLedger inner) : IModelControlLedger
{
    internal List<string> Calls { get; } = [];
    internal List<ModelUsage> Usage { get; } = [];
    internal string? Id { get; private set; }
    internal Func<Func<ValueTask<ModelReservationResult>>, ValueTask<ModelReservationResult>>? Reserve { get; set; }
    internal Func<Func<ValueTask<ModelCallResult>>, ValueTask<ModelCallResult>>? Begin { get; set; }
    internal Func<string, Func<ValueTask<ModelControlOutcome>>, ValueTask<ModelControlOutcome>>? Outcome { get; set; }

    public ValueTask<ModelReservationResult> TryReserveAsync(string reservationId, CancellationToken cancellationToken = default)
    {
        Calls.Add("reserve");
        Id = reservationId;
        return Reserve is null ? inner.TryReserveAsync(reservationId, cancellationToken)
            : Reserve(() => inner.TryReserveAsync(reservationId, cancellationToken));
    }
    public ValueTask<ModelCallResult> TryBeginCallAsync(string reservationId, int estimatedInputTokens, int maxOutputTokens, CancellationToken cancellationToken = default)
    {
        Calls.Add("begin");
        return Begin is null ? inner.TryBeginCallAsync(reservationId, estimatedInputTokens, maxOutputTokens, cancellationToken)
            : Begin(() => inner.TryBeginCallAsync(reservationId, estimatedInputTokens, maxOutputTokens, cancellationToken));
    }
    private ValueTask<ModelControlOutcome> Apply(string name, Func<ValueTask<ModelControlOutcome>> forward)
    {
        Calls.Add(name);
        return Outcome is null ? forward() : Outcome(name, forward);
    }
    public ValueTask<ModelControlOutcome> CompleteCallAsync(string reservationId, ModelUsage usage, CancellationToken cancellationToken = default)
    {
        Usage.Add(usage);
        return Apply("complete", () => inner.CompleteCallAsync(reservationId, usage, cancellationToken));
    }
    public ValueTask<ModelControlOutcome> SettleAsync(string reservationId, CancellationToken cancellationToken = default) =>
        Apply("settle", () => inner.SettleAsync(reservationId, cancellationToken));
    public ValueTask<ModelControlOutcome> CancelUndispatchedAsync(string reservationId, CancellationToken cancellationToken = default) =>
        Apply("cancel", () => inner.CancelUndispatchedAsync(reservationId, cancellationToken));
    public ValueTask<ModelControlOutcome> AbandonAsync(string reservationId, CancellationToken cancellationToken = default) =>
        Apply("abandon", () => inner.AbandonAsync(reservationId, cancellationToken));
}
