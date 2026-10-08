using Microsoft.Extensions.Time.Testing;
using Rafael.Portfolio.Modules.Assistant.Application;
using Rafael.Portfolio.Modules.Assistant.Infrastructure;

namespace Rafael.Portfolio.UnitTests;

public sealed class ModelControlLedgerTests
{
    [Fact]
    public async Task Allows_reservations_within_daily_and_monthly_budgets()
    {
        var ledger = new InMemoryModelControlLedger(dailyBudgetMicroUsd: 100_000, monthlyBudgetMicroUsd: 1_000_000);

        var result = await ledger.TryReserveAsync("res-1", maxEstimatedCostMicroUsd: 10_000);

        Assert.True(result.Success);
        Assert.Equal("res-1", result.ReservationId);
        Assert.Null(result.DenialReason);
    }

    [Fact]
    public async Task Rejects_reservation_when_daily_budget_is_exhausted()
    {
        var ledger = new InMemoryModelControlLedger(dailyBudgetMicroUsd: 20_000, monthlyBudgetMicroUsd: 1_000_000, maxConcurrency: 10);

        var res1 = await ledger.TryReserveAsync("res-1", maxEstimatedCostMicroUsd: 15_000);
        Assert.True(res1.Success);

        var res2 = await ledger.TryReserveAsync("res-2", maxEstimatedCostMicroUsd: 10_000);
        Assert.False(res2.Success);
        Assert.Equal(ModelReservationDenialReason.DailyBudgetExhausted, res2.DenialReason);
        Assert.NotNull(res2.RetryAfter);
    }

    [Fact]
    public async Task Rejects_reservation_when_monthly_budget_is_exhausted()
    {
        var ledger = new InMemoryModelControlLedger(dailyBudgetMicroUsd: 1_000_000, monthlyBudgetMicroUsd: 15_000, maxConcurrency: 10);

        var res1 = await ledger.TryReserveAsync("res-1", maxEstimatedCostMicroUsd: 10_000);
        Assert.True(res1.Success);

        var res2 = await ledger.TryReserveAsync("res-2", maxEstimatedCostMicroUsd: 10_000);
        Assert.False(res2.Success);
        Assert.Equal(ModelReservationDenialReason.MonthlyBudgetExhausted, res2.DenialReason);
    }

    [Fact]
    public async Task Rejects_reservation_when_concurrency_limit_reached()
    {
        var ledger = new InMemoryModelControlLedger(dailyBudgetMicroUsd: 500_000, monthlyBudgetMicroUsd: 5_000_000, maxConcurrency: 2);

        var res1 = await ledger.TryReserveAsync("res-1", maxEstimatedCostMicroUsd: 10_000);
        var res2 = await ledger.TryReserveAsync("res-2", maxEstimatedCostMicroUsd: 10_000);
        Assert.True(res1.Success);
        Assert.True(res2.Success);

        var res3 = await ledger.TryReserveAsync("res-3", maxEstimatedCostMicroUsd: 10_000);
        Assert.False(res3.Success);
        Assert.Equal(ModelReservationDenialReason.ConcurrencyLimitReached, res3.DenialReason);

        // Releasing permit allows 3rd reservation
        await ledger.ReleasePermitAsync("res-1");
        var res3Retry = await ledger.TryReserveAsync("res-3", maxEstimatedCostMicroUsd: 10_000);
        Assert.True(res3Retry.Success);
    }

    [Fact]
    public async Task Rejects_duplicate_reservation_id()
    {
        var ledger = new InMemoryModelControlLedger();

        var res1 = await ledger.TryReserveAsync("res-dup", maxEstimatedCostMicroUsd: 10_000);
        Assert.True(res1.Success);

        var resDup = await ledger.TryReserveAsync("res-dup", maxEstimatedCostMicroUsd: 10_000);
        Assert.False(resDup.Success);
        Assert.Equal(ModelReservationDenialReason.DuplicateReservationId, resDup.DenialReason);
    }

    [Fact]
    public async Task Reconciles_excess_reserved_budget_on_commit()
    {
        var ledger = new InMemoryModelControlLedger(dailyBudgetMicroUsd: 100_000);

        await ledger.TryReserveAsync("res-1", maxEstimatedCostMicroUsd: 50_000);
        Assert.Equal(50_000, ledger.DailySpentMicroUsd);

        // Commit actual 20,000 (30,000 excess returned)
        var committed = await ledger.CommitAsync("res-1", actualCostMicroUsd: 20_000, inputTokensUsed: 1000, outputTokensUsed: 100, callsMade: 1);
        Assert.True(committed);
        Assert.Equal(20_000, ledger.DailySpentMicroUsd);
    }

    [Fact]
    public async Task Rollover_frees_daily_budget_on_new_utc_day()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 8, 23, 50, 0, TimeSpan.Zero));
        var ledger = new InMemoryModelControlLedger(dailyBudgetMicroUsd: 10_000, monthlyBudgetMicroUsd: 100_000, maxConcurrency: 10, timeProvider: time);

        var res1 = await ledger.TryReserveAsync("res-1", maxEstimatedCostMicroUsd: 10_000);
        Assert.True(res1.Success);

        // Daily budget full
        var resBlocked = await ledger.TryReserveAsync("res-2", maxEstimatedCostMicroUsd: 1_000);
        Assert.False(resBlocked.Success);

        // Advance past UTC midnight (15 minutes)
        time.Advance(TimeSpan.FromMinutes(15));

        var resNewDay = await ledger.TryReserveAsync("res-3", maxEstimatedCostMicroUsd: 5_000);
        Assert.True(resNewDay.Success);
    }

    [Fact]
    public async Task Concurrent_reservations_respect_strict_concurrency_and_budget_caps()
    {
        var ledger = new InMemoryModelControlLedger(dailyBudgetMicroUsd: 50_000, maxConcurrency: 2);
        var successCount = 0;

        var tasks = Enumerable.Range(0, 10).Select(i => Task.Run(async () =>
        {
            var res = await ledger.TryReserveAsync($"res-conc-{i}", maxEstimatedCostMicroUsd: 10_000);
            if (res.Success)
            {
                Interlocked.Increment(ref successCount);
            }
        })).ToArray();

        await Task.WhenAll(tasks);

        // Max concurrency is 2, so at most 2 concurrent reservations could succeed before permits are released
        Assert.True(successCount <= 2, $"Granted {successCount} concurrent permits when max was 2.");
    }
}
