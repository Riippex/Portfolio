using Rafael.Portfolio.Modules.Assistant.Application;
using Rafael.Portfolio.Modules.Assistant.Application.Orchestration;

namespace Rafael.Portfolio.EmulatorTests;

public sealed partial class FirestoreModelControlLedgerTests
{
    private sealed class OfflineProvider(int input) : IModelProvider
    {
        public bool IsAvailable => true;
        public ValueTask<ModelProviderResponse> GenerateAsync(ModelProviderRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ModelProviderResponse(true, "{\"v\":1,\"outcome\":\"answer\",\"claims\":[\"C1\"]}", input, 10));
    }

    private sealed class OfflineCounter : IModelRequestTokenCounter
    {
        public ValueTask<ModelRequestTokenCount> CountAsync(ModelProviderRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ModelRequestTokenCount(500, ModelTokenCountBasis.ProviderVerifiedUpperBound));
    }

    private sealed class LostResponseLedger(IModelControlLedger inner, string mode) : IModelControlLedger
    {
        internal string Id { get; private set; } = "";
        internal int Completes { get; private set; }
        internal int Settles { get; private set; }
        public ValueTask<ModelReservationResult> TryReserveAsync(string reservationId, CancellationToken cancellationToken = default)
        {
            Id = reservationId;
            return inner.TryReserveAsync(reservationId, cancellationToken);
        }
        public ValueTask<ModelCallResult> TryBeginCallAsync(string reservationId, int estimatedInputTokens, int maxOutputTokens, CancellationToken cancellationToken = default) =>
            inner.TryBeginCallAsync(reservationId, estimatedInputTokens, maxOutputTokens, cancellationToken);
        public async ValueTask<ModelControlOutcome> CompleteCallAsync(string reservationId, ModelUsage usage, CancellationToken cancellationToken = default)
        {
            Completes++;
            if (mode == "before-commit" && Completes == 1) return ModelControlOutcome.StoreUnavailable;
            var result = await inner.CompleteCallAsync(reservationId, usage, cancellationToken);
            return mode is "lost-complete" or "lost-invalid" or "lost-overspend" && Completes == 1
                ? ModelControlOutcome.StoreUnavailable : result;
        }
        public async ValueTask<ModelControlOutcome> SettleAsync(string reservationId, CancellationToken cancellationToken = default)
        {
            Settles++;
            var result = await inner.SettleAsync(reservationId, cancellationToken);
            return mode == "lost-settle" ? ModelControlOutcome.StoreUnavailable : result;
        }
        public ValueTask<ModelControlOutcome> CancelUndispatchedAsync(string reservationId, CancellationToken cancellationToken = default) =>
            inner.CancelUndispatchedAsync(reservationId, cancellationToken);
        public ValueTask<ModelControlOutcome> AbandonAsync(string reservationId, CancellationToken cancellationToken = default) =>
            inner.AbandonAsync(reservationId, cancellationToken);
    }

    [Theory]
    [InlineData("normal")]
    [InlineData("before-commit")]
    [InlineData("lost-complete")]
    [InlineData("lost-settle")]
    [InlineData("lost-invalid")]
    [InlineData("lost-overspend")]
    public async Task Orchestration_accounts_real_transactions_without_inferring_a_lost_response(string mode)
    {
        var (h, real) = await StartAsync();
        var decorated = new LostResponseLedger(real, mode);
        var input = mode == "lost-invalid" ? 0 : mode == "lost-overspend" ? 20000 : 100;
        var orchestrator = new AssistantTurnOrchestrator(new OfflineProvider(input), new OfflineCounter(), decorated, h.Clock);
        AssistantEvidenceChunk[] chunks =
        [new("chunk", "document", "profile", "Rafael", "Experience", "experience", "prose",
            ["claim"], "https://example.test/profile", "verified", "v1", "public", 1, [],
            [new("claim", "The owner-maintained public profile lists an announced role.", "verified", "profile#experience")])];
        var result = await orchestrator.RunAsync("Tell me about the role", chunks, h.Clock.GetTimestamp());
        if (mode is "lost-settle" or "lost-invalid" or "lost-overspend") Assert.Null(result);
        else Assert.NotNull(result);
        Assert.Equal(mode == "lost-invalid" ? "Uncertain" : "Settled", await h.Raw.State(decorated.Id));
        var fields = (await h.Raw.Fields(h.Raw.Reservation(decorated.Id)))!;
        Assert.Equal(mode == "lost-invalid", (bool)fields["callInFlight"]);
        Assert.Equal(mode == "lost-overspend" ? 2004L : mode == "lost-invalid" ? Worst : 14L, (long)fields["chargedMicroUsd"]);
        Assert.True(decorated.Completes <= 2);
        Assert.True(decorated.Settles <= 1);
        if (mode == "lost-invalid") Assert.DoesNotContain("expiresAt", fields.Keys);
        else Assert.Contains("expiresAt", fields.Keys);
        if (mode == "lost-overspend")
        {
            Assert.Equal(1, await h.Raw.PermitCount());
            h.Clock.Advance(TimeSpan.FromMinutes(6));
            await real.TryReserveAsync("res_after_overspend");
            Assert.Equal(1, await h.Raw.PermitCount()); // old permit reclaimed, new permit admitted
        }
        else Assert.Equal(mode == "lost-invalid" ? 1 : 0, await h.Raw.PermitCount());
    }
}
