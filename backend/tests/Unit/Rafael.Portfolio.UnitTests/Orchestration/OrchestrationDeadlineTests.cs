using Rafael.Portfolio.Modules.Assistant.Application;

namespace Rafael.Portfolio.UnitTests.Orchestration;

public sealed class OrchestrationDeadlineTests
{
    [Theory]
    [InlineData("reserve")]
    [InlineData("count")]
    [InlineData("begin")]
    [InlineData("provider")]
    [InlineData("complete")]
    [InlineData("settle")]
    [InlineData("cancel")]
    [InlineData("abandon")]
    public async Task Every_port_is_bounded_even_if_it_ignores_cancellation(string stage)
    {
        var h = new OrchestrationHarness();
        var lateReserve = new TaskCompletionSource<ModelReservationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lateBegin = new TaskCompletionSource<ModelCallResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lateCount = new TaskCompletionSource<ModelRequestTokenCount>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lateProvider = new TaskCompletionSource<ModelProviderResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lateOutcome = new TaskCompletionSource<ModelControlOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (stage == "reserve") h.Ledger.Reserve = _ => new(lateReserve.Task);
        if (stage == "count") h.Counter.Count = (_, _) => new(lateCount.Task);
        if (stage == "begin") h.Ledger.Begin = _ => new(lateBegin.Task);
        if (stage == "provider") h.Provider.Generate = _ => new(lateProvider.Task);
        if (stage == "cancel") h.Counter.Count = (_, _) => throw new InvalidOperationException();
        if (stage == "abandon") h.Provider.Generate = _ => throw new InvalidOperationException();
        h.Ledger.Outcome = (name, forward) => name == stage ? new(lateOutcome.Task) : forward();
        var start = h.Clock.GetTimestamp();
        Assert.Null(await h.Drive(h.Run()));
        Assert.True(h.Clock.GetElapsedTime(start) <= TimeSpan.FromSeconds(15.5));
        var calls = h.Ledger.Calls.ToArray();
        var providerCalls = h.Provider.Calls;
        lateReserve.TrySetResult(ModelReservationResult.Granted("res_unobserved"));
        lateBegin.TrySetResult(ModelCallResult.Granted());
        lateCount.TrySetResult(new(500, ModelTokenCountBasis.ProviderVerifiedUpperBound));
        lateProvider.TrySetResult(new(true, OrchestrationHarness.Valid, 100, 10));
        lateOutcome.TrySetResult(ModelControlOutcome.Applied);
        await Task.Delay(15);
        Assert.Equal(calls, h.Ledger.Calls);
        Assert.Equal(providerCalls, h.Provider.Calls);
        if (stage == "provider")
        {
            Assert.Equal(ReservationState.Uncertain, h.Record.State);
            Assert.True(h.Record.CallInFlight);
            Assert.DoesNotContain("complete", h.Ledger.Calls);
        }
    }

    [Fact]
    public async Task A_late_reserve_after_cancel_not_found_does_not_chain_work_or_assume_a_refund()
    {
        var h = new OrchestrationHarness();
        Func<ValueTask<ModelReservationResult>>? deferred = null;
        h.Ledger.Reserve = forward =>
        {
            deferred = forward;
            return ValueTask.FromResult(ModelReservationResult.Denied(ModelReservationDenialReason.StoreUnavailable));
        };
        Assert.Null(await h.Run());
        Assert.Equal(["reserve", "cancel"], h.Ledger.Calls);
        Assert.Equal(0, h.Real.ReservationCount);
        await deferred!();
        Assert.Equal(ReservationState.Active, h.Record.State);
        Assert.Equal(ModelControlFixtures.WorstCase, h.Record.ChargedMicroUsd);
        h.Clock.Advance(TimeSpan.FromMinutes(6));
        await h.Real.TryReserveAsync("res_reclaim_late");
        Assert.Equal(ReservationState.Lapsed, h.Record.State);
        Assert.DoesNotContain(h.Ledger.Id!, h.Real.Root!.Permits.Keys);
        Assert.Equal(0, h.Provider.Calls);
        Assert.Equal(0, h.Counter.Calls);
    }

    [Fact]
    public async Task Client_abort_cancels_work_but_not_the_accounting_epilogue()
    {
        var h = new OrchestrationHarness();
        using var abort = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Provider.Generate = async ct =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException();
        };
        var run = h.Run(abort.Token);
        await entered.Task;
        abort.Cancel();
        Assert.Null(await run.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains("abandon", h.Ledger.Calls);
        Assert.Equal(ReservationState.Uncertain, h.Record.State);
        Assert.Equal(1, h.Real.ActivePermits);
    }

    [Fact]
    public async Task Successive_accounting_calls_share_four_seconds_not_four_seconds_each()
    {
        var h = new OrchestrationHarness();
        h.Ledger.Outcome = async (name, forward) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(2.5), h.Clock);
            return await forward();
        };
        var start = h.Clock.GetTimestamp();
        Assert.Null(await h.Drive(h.Run()));
        Assert.True(h.Clock.GetElapsedTime(start) < TimeSpan.FromSeconds(5));
        Assert.Equal(["reserve", "begin", "complete", "settle"], h.Ledger.Calls);
        h.Clock.Advance(TimeSpan.FromSeconds(5));
        await Task.Delay(15);
        Assert.Equal(ReservationState.Settled, h.Record.State); // late commit is real but never emitted
    }

    [Fact]
    public async Task Preparation_shares_three_seconds_and_cannot_dispatch_after_a_late_begin()
    {
        var h = new OrchestrationHarness();
        h.Ledger.Reserve = async forward =>
        {
            await Task.Delay(TimeSpan.FromSeconds(1.5), h.Clock);
            return await forward();
        };
        h.Counter.Count = async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(1.5), h.Clock, ct);
            return new(500, ModelTokenCountBasis.ProviderVerifiedUpperBound);
        };
        Assert.Null(await h.Drive(h.Run()));
        Assert.DoesNotContain("begin", h.Ledger.Calls);
        Assert.Equal(0, h.Provider.Calls);
        Assert.Equal(ReservationState.Cancelled, h.Record.State);
    }

    [Fact]
    public async Task Two_pending_orphans_deny_new_turns_until_they_finish()
    {
        var h = new OrchestrationHarness();
        var never = new TaskCompletionSource<ModelRequestTokenCount>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Counter.Count = (_, _) => new(never.Task);
        Assert.Null(await h.Drive(h.Run()));
        Assert.Null(await h.Drive(h.Run()));
        var calls = h.Ledger.Calls.Count;
        Assert.Null(await h.Run());
        Assert.Equal(calls, h.Ledger.Calls.Count);
        never.SetResult(new(500, ModelTokenCountBasis.ProviderVerifiedUpperBound));
        await Task.Delay(25);
        h.Counter.Count = (_, _) => ValueTask.FromResult(new ModelRequestTokenCount(500, ModelTokenCountBasis.ProviderVerifiedUpperBound));
        Assert.NotNull(await h.Run());
    }

    [Fact]
    public async Task A_late_complete_racing_its_retry_records_usage_only_once()
    {
        var h = new OrchestrationHarness();
        var first = true;
        Func<ValueTask<ModelControlOutcome>>? late = null;
        h.Ledger.Outcome = (name, forward) =>
        {
            if (name == "complete" && first)
            {
                first = false;
                late = forward;
                return ValueTask.FromResult(ModelControlOutcome.StoreUnavailable);
            }
            return forward();
        };
        Assert.NotNull(await h.Run());
        Assert.Equal(ModelControlOutcome.InvalidState, await late!());
        Assert.Equal(100, h.Record.InputTokensUsed);
        Assert.Equal(14, h.Record.ChargedMicroUsd);
    }
}
