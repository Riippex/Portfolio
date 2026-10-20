using Rafael.Portfolio.Modules.Assistant.Application;

namespace Rafael.Portfolio.UnitTests.Orchestration;

public sealed class OrchestrationAccountingTests
{
    [Theory]
    [InlineData("reserve", false)]
    [InlineData("reserve", true)]
    [InlineData("begin", false)]
    [InlineData("begin", true)]
    public async Task Ambiguous_admission_never_dispatches_and_only_confirmed_cancel_refunds(string stage, bool committed)
    {
        var h = new OrchestrationHarness();
        if (stage == "reserve") h.Ledger.Reserve = async forward =>
        {
            if (committed) await forward();
            return ModelReservationResult.Denied(ModelReservationDenialReason.StoreUnavailable);
        };
        else h.Ledger.Begin = async forward =>
        {
            if (committed) await forward();
            return ModelCallResult.Denied(ModelCallDenialReason.StoreUnavailable);
        };
        Assert.Null(await h.Run());
        Assert.Equal(0, h.Provider.Calls);
        Assert.Equal(1, h.Ledger.Calls.Count(c => c == "cancel"));
        if (stage == "reserve" && !committed) Assert.Equal(0, h.Real.ReservationCount);
        else if (stage == "begin" && committed)
        {
            Assert.True(h.Record.CallInFlight);
            Assert.Equal(ModelControlFixtures.WorstCase, h.Record.ChargedMicroUsd);
            h.Clock.Advance(TimeSpan.FromMinutes(6));
            await h.Real.TryReserveAsync("res_reclaim_001");
            Assert.Equal(ReservationState.Uncertain, h.Record.State);
            Assert.Contains(h.Ledger.Id!, h.Real.Root!.Permits.Keys);
        }
        else
        {
            Assert.Equal(ReservationState.Cancelled, h.Record.State);
            Assert.Equal(0, h.Real.ActivePermits);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Lost_complete_response_retries_identical_usage_without_double_accounting(bool committed)
    {
        var h = new OrchestrationHarness();
        var first = true;
        h.Ledger.Outcome = async (name, forward) =>
        {
            if (name == "complete" && first)
            {
                first = false;
                if (committed) await forward();
                return ModelControlOutcome.StoreUnavailable;
            }
            return await forward();
        };
        Assert.NotNull(await h.Run());
        Assert.Equal(2, h.Ledger.Usage.Count);
        Assert.Equal(h.Ledger.Usage[0], h.Ledger.Usage[1]);
        Assert.Equal(100, h.Record.InputTokensUsed);
        Assert.Equal(10, h.Record.OutputTokensUsed);
        Assert.Equal(14, h.Record.ChargedMicroUsd);
        Assert.Equal(ReservationState.Settled, h.Record.State);
        Assert.Equal(1, h.Ledger.Calls.Count(c => c == "settle"));
        Assert.DoesNotContain("abandon", h.Ledger.Calls);
    }

    [Theory]
    [InlineData("complete", false)]
    [InlineData("complete", true)]
    [InlineData("settle", false)]
    [InlineData("settle", true)]
    public async Task P2_never_emits_when_settlement_cannot_be_confirmed(string stage, bool committed)
    {
        var h = new OrchestrationHarness();
        h.Ledger.Outcome = async (name, forward) =>
        {
            if (name == stage || stage == "complete" && name == "settle")
            {
                if (committed) await forward();
                return ModelControlOutcome.StoreUnavailable;
            }
            return await forward();
        };
        Assert.Null(await h.Run());
        Assert.True(h.Ledger.Usage.Count <= 2);
        Assert.Equal(1, h.Ledger.Calls.Count(c => c == "settle"));
        Assert.DoesNotContain("abandon", h.Ledger.Calls);
        Assert.Equal(committed ? ReservationState.Settled : ReservationState.Active, h.Record.State);
    }

    [Fact]
    public async Task Settle_invalid_tariff_resolves_at_retained_charge_and_releases_immediately()
    {
        var h = new OrchestrationHarness();
        h.Ledger.Outcome = (name, forward) =>
        {
            if (name == "settle") h.Real.Policy = h.Real.Policy with { Tariff = null };
            return forward();
        };
        Assert.NotNull(await h.Run());
        Assert.Equal(ReservationState.Settled, h.Record.State);
        Assert.Equal(ModelControlFixtures.WorstCase, h.Record.ChargedMicroUsd);
        Assert.Equal(0, h.Real.ActivePermits);
    }

    [Theory]
    [InlineData("unpriced")]
    [InlineData("retained")]
    [InlineData("invalid-counter")]
    [InlineData("larger")]
    public async Task Allowance_exceeded_preserves_each_real_ledger_outcome(string mode)
    {
        var h = new OrchestrationHarness();
        var input = mode == "retained" ? 6001 : 20000;
        h.Provider.Generate = _ => ValueTask.FromResult(new ModelProviderResponse(true, OrchestrationHarness.Valid, input, 0));
        h.Ledger.Outcome = (name, forward) =>
        {
            if (name == "complete" && mode == "unpriced") h.Real.Policy = h.Real.Policy with { Tariff = null };
            if (name == "complete" && mode == "invalid-counter") h.Real.SetCounter("day_2026-10-08", null);
            return forward();
        };
        Assert.Null(await h.Run());
        Assert.DoesNotContain("settle", h.Ledger.Calls);
        Assert.DoesNotContain("abandon", h.Ledger.Calls);
        Assert.False(h.Record.CallInFlight);
        Assert.Equal(mode is "unpriced" or "invalid-counter" ? ReservationState.Uncertain : ReservationState.Settled, h.Record.State);
        Assert.Equal(mode == "larger" ? 2000 : ModelControlFixtures.WorstCase, h.Record.ChargedMicroUsd);
        Assert.Equal(1, h.Real.ActivePermits);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6001)]
    [InlineData(20000)]
    public async Task Lost_invalid_or_excessive_usage_never_assumes_active_state_or_reserve_charge(int input)
    {
        var h = new OrchestrationHarness();
        h.Provider.Generate = _ => ValueTask.FromResult(new ModelProviderResponse(true, OrchestrationHarness.Valid, input, 0));
        var first = true;
        h.Ledger.Outcome = async (name, forward) =>
        {
            var result = await forward();
            if (name == "complete" && first) { first = false; return ModelControlOutcome.StoreUnavailable; }
            return result;
        };
        Assert.Null(await h.Run());
        Assert.Equal(2, h.Ledger.Usage.Count);
        Assert.DoesNotContain("abandon", h.Ledger.Calls);
        Assert.Equal(input == 0 ? ReservationState.Uncertain : ReservationState.Settled, h.Record.State);
        Assert.Equal(input == 0, h.Record.CallInFlight);
        Assert.Equal(input == 20000 ? 2000 : ModelControlFixtures.WorstCase, h.Record.ChargedMicroUsd);
    }

    [Fact]
    public async Task Invalid_state_from_cancel_does_not_prove_begin_committed()
    {
        var h = new OrchestrationHarness();
        h.Ledger.Begin = async forward =>
        {
            await forward();
            await h.Real.ReconcileAsync(h.Ledger.Id!, ReconciliationResolution.NotDispatched);
            return ModelCallResult.Denied(ModelCallDenialReason.StoreUnavailable);
        };
        Assert.Null(await h.Run());
        Assert.Equal(ReservationState.Cancelled, h.Record.State);
        Assert.Equal(0, h.Record.ChargedMicroUsd);
        Assert.Equal(0, h.Real.ActivePermits);
        Assert.Equal(0, h.Provider.Calls);
    }

    [Theory]
    [InlineData(ModelControlOutcome.InvalidState)]
    [InlineData(ModelControlOutcome.NotFound)]
    [InlineData(ModelControlOutcome.StateInvalid)]
    public async Task Authoritative_complete_failure_has_no_retry_or_settle(ModelControlOutcome outcome)
    {
        var h = new OrchestrationHarness();
        h.Ledger.Outcome = (name, forward) => name == "complete" ? ValueTask.FromResult(outcome) : forward();
        Assert.Null(await h.Run());
        Assert.Single(h.Ledger.Usage);
        Assert.DoesNotContain("settle", h.Ledger.Calls);
        Assert.True(h.Record.CallInFlight);
    }
}
