using Microsoft.Extensions.Time.Testing;
using Rafael.Portfolio.Modules.Assistant.Application;

namespace Rafael.Portfolio.UnitTests;

internal static class ModelControlFixtures
{
    // 6,000 input at 100,000 and 600 output at 400,000 micro-USD per million tokens: 600 + 240.
    public const long WorstCase = 840;

    public static readonly DateTimeOffset Noon = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    public static ModelTariff Tariff(DateOnly? validThrough = null, string version = "tariff-1") =>
        new(version, "gemini-3.1-flash-lite", 100_000, 400_000, validThrough ?? new DateOnly(2027, 1, 1));

    public static ModelControlPolicy Policy(
        long? daily = null,
        long? monthly = null,
        ModelTariff? tariff = null,
        string version = "policy-1") =>
        ModelControlPolicy.Approved(version, tariff ?? Tariff()) with
        {
            DailyBudgetMicroUsd = daily ?? ModelControlPolicy.ApprovedDailyBudgetMicroUsd,
            MonthlyBudgetMicroUsd = monthly ?? ModelControlPolicy.ApprovedMonthlyBudgetMicroUsd
        };

    public static string Id(int n) => $"res_{n:D8}";
}

public sealed class ModelControlLedgerTests
{
    private static readonly DateTimeOffset Noon = ModelControlFixtures.Noon;
    private const long Worst = ModelControlFixtures.WorstCase;

    private static (InMemoryModelControlLedger Ledger, FakeTimeProvider Time) Create(ModelControlPolicy? policy = null, DateTimeOffset? start = null)
    {
        var time = new FakeTimeProvider(start ?? Noon);
        return (new InMemoryModelControlLedger(policy ?? ModelControlFixtures.Policy(), time), time);
    }

    private static string Id(int n) => ModelControlFixtures.Id(n);

    // ------------------------------------------------------------------------------- reservation

    [Fact]
    public async Task Reserves_the_full_worst_case_turn_and_one_permit_atomically()
    {
        var (ledger, _) = Create();

        var result = await ledger.TryReserveAsync(Id(1));

        Assert.True(result.Success);
        Assert.Equal(Worst, ledger.DayCharged(Noon));
        Assert.Equal(Worst, ledger.MonthCharged(Noon));
        Assert.Equal(1, ledger.ActivePermits);
        var record = ledger.Reservation(Id(1))!;
        Assert.Equal(ReservationState.Active, record.State);
        Assert.Equal(Worst, record.ReservedMicroUsd);
        Assert.Equal((2, 6000, 600), (record.MaxCalls, record.MaxInputTokens, record.MaxOutputTokens));
    }

    [Fact]
    public async Task Never_grants_more_than_two_global_permits()
    {
        var (ledger, _) = Create();

        Assert.True((await ledger.TryReserveAsync(Id(1))).Success);
        Assert.True((await ledger.TryReserveAsync(Id(2))).Success);
        var third = await ledger.TryReserveAsync(Id(3));

        Assert.False(third.Success);
        Assert.Equal(ModelReservationDenialReason.ConcurrencyLimitReached, third.DenialReason);
        Assert.Equal(2, ledger.ActivePermits);
        Assert.Equal(2 * Worst, ledger.DayCharged(Noon));
    }

    [Fact]
    public async Task Daily_budget_denial_reports_the_remaining_time_in_the_day()
    {
        var (ledger, _) = Create(ModelControlFixtures.Policy(daily: Worst, monthly: 100 * Worst));
        Assert.True((await ledger.TryReserveAsync(Id(1))).Success);
        await ledger.CancelUndispatchedAsync(Id(1));
        Assert.Equal(0, ledger.DayCharged(Noon));

        Assert.True((await ledger.TryReserveAsync(Id(2))).Success);
        await ledger.AbandonAsync(Id(2));
        var denied = await ledger.TryReserveAsync(Id(3));

        Assert.False(denied.Success);
        Assert.Equal(ModelReservationDenialReason.DailyBudgetExhausted, denied.DenialReason);
        Assert.Equal(TimeSpan.FromHours(12), denied.RetryAfter);
    }

    [Fact]
    public async Task Monthly_budget_exhaustion_denies_and_day_rollover_does_not_restore_monthly_credit()
    {
        var (ledger, time) = Create(ModelControlFixtures.Policy(daily: 2 * Worst, monthly: 3 * Worst));
        Assert.True((await ledger.TryReserveAsync(Id(1))).Success);
        Assert.True((await ledger.TryReserveAsync(Id(2))).Success);
        await ledger.AbandonAsync(Id(1));
        await ledger.AbandonAsync(Id(2));

        time.Advance(TimeSpan.FromDays(1)); // a new UTC day; the first two leases have ended
        Assert.True((await ledger.TryReserveAsync(Id(3))).Success); // the third of the month
        await ledger.AbandonAsync(Id(3));
        time.Advance(TimeSpan.FromMinutes(6));

        var denied = await ledger.TryReserveAsync(Id(4));
        Assert.False(denied.Success);
        Assert.Equal(ModelReservationDenialReason.MonthlyBudgetExhausted, denied.DenialReason);

        time.Advance(TimeSpan.FromDays(1)); // yet another day, daily counter fresh, month still spent
        var nextDay = await ledger.TryReserveAsync(Id(5));
        Assert.Equal(ModelReservationDenialReason.MonthlyBudgetExhausted, nextDay.DenialReason);
        Assert.Equal(0, ledger.DayCharged(time.GetUtcNow()));
        Assert.Equal(3 * Worst, ledger.MonthCharged(time.GetUtcNow()));
    }

    [Fact]
    public async Task A_new_utc_day_restores_daily_credit_but_a_new_month_restores_monthly_credit()
    {
        var (ledger, time) = Create(ModelControlFixtures.Policy(daily: Worst, monthly: Worst), new DateTimeOffset(2026, 10, 31, 23, 50, 0, TimeSpan.Zero));
        Assert.True((await ledger.TryReserveAsync(Id(1))).Success);
        await ledger.AbandonAsync(Id(1));

        time.Advance(TimeSpan.FromMinutes(6)); // leases end, still Oct 31 23:56
        Assert.False((await ledger.TryReserveAsync(Id(2))).Success);

        time.Advance(TimeSpan.FromMinutes(10)); // Nov 1 00:06
        Assert.True((await ledger.TryReserveAsync(Id(3))).Success);
    }

    [Fact]
    public async Task Counters_expire_forty_days_after_their_period_and_resolved_reservations_never_before()
    {
        var (ledger, time) = Create();
        await ledger.TryReserveAsync(Id(1));

        // Reservation on 2026-10-08: the day ends 10-09 and the month ends 11-01.
        Assert.Equal(new DateTimeOffset(2026, 11, 18, 0, 0, 0, TimeSpan.Zero), ledger.Counter("day_2026-10-08")!.ExpiresAt);
        Assert.Equal(new DateTimeOffset(2026, 12, 11, 0, 0, 0, TimeSpan.Zero), ledger.Counter("month_2026-10")!.ExpiresAt);

        // An unresolved reservation is evidence and has no expiry at all.
        Assert.Null(ledger.Reservation(Id(1))!.ExpiresAt);

        // Once resolved it expires 40 days after its month ended (not after its creation).
        await ledger.CancelUndispatchedAsync(Id(1));
        Assert.Equal(new DateTimeOffset(2026, 12, 11, 0, 0, 0, TimeSpan.Zero), ledger.Reservation(Id(1))!.ExpiresAt);
        Assert.NotEqual(Noon.AddDays(40), ledger.Reservation(Id(1))!.ExpiresAt);

        // A reservation resolved long after its period keeps its evidence 40 days after resolution.
        await ledger.TryReserveAsync(Id(2));
        time.Advance(TimeSpan.FromSeconds(1));
        var late = new DateTimeOffset(2027, 2, 1, 0, 0, 0, TimeSpan.Zero);
        await ledger.AbandonAsync(Id(2));
        var clock = new SettableClock(late);
        var lateLedger = new InMemoryModelControlLedger(ModelControlFixtures.Policy(), clock);
        lateLedger.Root = ledger.Root;
        lateLedger.SetReservation(ledger.Reservation(Id(2))!);
        lateLedger.SetCounter("day_2026-10-08", ledger.Counter("day_2026-10-08"));
        lateLedger.SetCounter("month_2026-10", ledger.Counter("month_2026-10"));
        Assert.Equal(ModelControlOutcome.Applied, await lateLedger.ReconcileAsync(Id(2), ReconciliationResolution.ChargeAsReserved));
        Assert.Equal(late.AddDays(40), lateLedger.Reservation(Id(2))!.ExpiresAt);
    }

    // ----------------------------------------------------------------------- settlement and periods

    [Fact]
    public async Task Late_settlement_refunds_only_the_original_day_and_never_erases_todays_spending()
    {
        // Reviewed defect: settling yesterday's reservation reduced the CURRENT day's counter.
        var (ledger, time) = Create(start: new DateTimeOffset(2026, 10, 8, 23, 59, 0, TimeSpan.Zero));
        Assert.True((await ledger.TryReserveAsync(Id(1))).Success);
        Assert.True((await ledger.TryBeginCallAsync(Id(1), 1000, 100)).Allowed);
        time.Advance(TimeSpan.FromMinutes(2)); // 2026-10-09 00:01, inside the permit lease
        Assert.True((await ledger.TryReserveAsync(Id(2))).Success);

        Assert.Equal(ModelControlOutcome.Applied, await ledger.CompleteCallAsync(Id(1), new ModelUsage(1000, 100)));
        Assert.Equal(ModelControlOutcome.Applied, await ledger.SettleAsync(Id(1)));

        var today = time.GetUtcNow();
        var yesterday = today.AddDays(-1);
        Assert.Equal(Worst, ledger.DayCharged(today));
        Assert.Equal(100 + 40, ledger.DayCharged(yesterday));
        Assert.Equal(100 + 40 + Worst, ledger.MonthCharged(today));
    }

    [Fact]
    public async Task Late_settlement_across_a_month_boundary_refunds_only_the_original_month()
    {
        var (ledger, time) = Create(start: new DateTimeOffset(2026, 10, 31, 23, 59, 30, TimeSpan.Zero));
        Assert.True((await ledger.TryReserveAsync(Id(1))).Success);
        Assert.True((await ledger.TryBeginCallAsync(Id(1), 1000, 100)).Allowed);
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.True((await ledger.TryReserveAsync(Id(2))).Success);

        await ledger.CompleteCallAsync(Id(1), new ModelUsage(1000, 100));
        await ledger.SettleAsync(Id(1));

        Assert.Equal(Worst, ledger.MonthCharged(time.GetUtcNow())); // November untouched
        Assert.Equal(140, ledger.MonthCharged(time.GetUtcNow().AddDays(-1))); // October reduced
    }

    [Fact]
    public async Task Settlement_reconciles_to_the_tariff_cost_of_the_reported_usage_rounded_up()
    {
        var (ledger, _) = Create();
        await ledger.TryReserveAsync(Id(1));
        await ledger.TryBeginCallAsync(Id(1), 5, 5);

        await ledger.CompleteCallAsync(Id(1), new ModelUsage(5, 3)); // 0.5 -> 1 and 1.2 -> 2
        await ledger.SettleAsync(Id(1));

        Assert.Equal(3, ledger.DayCharged(Noon));
        Assert.Equal(ReservationState.Settled, ledger.Reservation(Id(1))!.State);
        Assert.Equal(0, ledger.ActivePermits);
    }

    [Fact]
    public async Task A_reservation_settles_once()
    {
        var (ledger, _) = Create();
        await ledger.TryReserveAsync(Id(1));
        await ledger.TryBeginCallAsync(Id(1), 1000, 100);
        await ledger.CompleteCallAsync(Id(1), new ModelUsage(1000, 100));

        Assert.Equal(ModelControlOutcome.Applied, await ledger.SettleAsync(Id(1)));
        var charged = ledger.DayCharged(Noon);

        Assert.Equal(ModelControlOutcome.InvalidState, await ledger.SettleAsync(Id(1)));
        Assert.Equal(charged, ledger.DayCharged(Noon));
    }

    [Fact]
    public async Task Settlement_that_would_push_a_counter_below_zero_changes_nothing()
    {
        var (ledger, _) = Create();
        await ledger.TryReserveAsync(Id(1));
        await ledger.TryBeginCallAsync(Id(1), 1000, 100);
        await ledger.CompleteCallAsync(Id(1), new ModelUsage(1000, 100));
        ledger.SetCounter("day_2026-10-08", new PeriodCounter("2026-10-08", 5, Noon.AddDays(40)));

        Assert.Equal(ModelControlOutcome.StateInvalid, await ledger.SettleAsync(Id(1)));

        Assert.Equal(5, ledger.DayCharged(Noon));
        Assert.Equal(ReservationState.Active, ledger.Reservation(Id(1))!.State);
        Assert.Equal(1, ledger.ActivePermits);
    }

    [Fact]
    public async Task Cancelling_before_any_call_refunds_the_whole_reservation_and_frees_the_permit()
    {
        var (ledger, _) = Create();
        await ledger.TryReserveAsync(Id(1));

        Assert.Equal(ModelControlOutcome.Applied, await ledger.CancelUndispatchedAsync(Id(1)));

        Assert.Equal(0, ledger.DayCharged(Noon));
        Assert.Equal(0, ledger.MonthCharged(Noon));
        Assert.Equal(0, ledger.ActivePermits);
        Assert.Equal(ReservationState.Cancelled, ledger.Reservation(Id(1))!.State);
    }

    [Fact]
    public async Task Cancelling_after_a_call_began_is_refused_and_refunds_nothing()
    {
        var (ledger, _) = Create();
        await ledger.TryReserveAsync(Id(1));
        await ledger.TryBeginCallAsync(Id(1), 1000, 100);

        Assert.Equal(ModelControlOutcome.InvalidState, await ledger.CancelUndispatchedAsync(Id(1)));

        Assert.Equal(Worst, ledger.DayCharged(Noon));
        Assert.Equal(1, ledger.ActivePermits);
    }

    // ----------------------------------------------------------------------- per-turn allowances

    [Fact]
    public async Task Allows_at_most_two_calls_per_turn()
    {
        var (ledger, _) = Create();
        await ledger.TryReserveAsync(Id(1));

        Assert.True((await ledger.TryBeginCallAsync(Id(1), 100, 50)).Allowed);
        await ledger.CompleteCallAsync(Id(1), new ModelUsage(100, 40));
        Assert.True((await ledger.TryBeginCallAsync(Id(1), 100, 50)).Allowed);
        await ledger.CompleteCallAsync(Id(1), new ModelUsage(100, 40));

        var third = await ledger.TryBeginCallAsync(Id(1), 100, 50);

        Assert.False(third.Allowed);
        Assert.Equal(ModelCallDenialReason.CallLimitReached, third.DenialReason);
    }

    [Fact]
    public async Task Does_not_allow_a_second_call_while_one_is_in_flight()
    {
        var (ledger, _) = Create();
        await ledger.TryReserveAsync(Id(1));
        await ledger.TryBeginCallAsync(Id(1), 100, 50);

        var second = await ledger.TryBeginCallAsync(Id(1), 100, 50);

        Assert.Equal(ModelCallDenialReason.CallInFlight, second.DenialReason);
    }

    [Fact]
    public async Task Input_tokens_are_cumulative_across_calls_and_include_every_prompt_part()
    {
        var (ledger, _) = Create();
        await ledger.TryReserveAsync(Id(1));
        await ledger.TryBeginCallAsync(Id(1), 4000, 100);
        await ledger.CompleteCallAsync(Id(1), new ModelUsage(4000, 100));

        var over = await ledger.TryBeginCallAsync(Id(1), 2001, 100);
        var exact = await ledger.TryBeginCallAsync(Id(1), 2000, 100);

        Assert.Equal(ModelCallDenialReason.InputTokenLimitReached, over.DenialReason);
        Assert.True(exact.Allowed);
    }

    [Fact]
    public async Task Billable_output_tokens_are_cumulative_across_calls()
    {
        var (ledger, _) = Create();
        await ledger.TryReserveAsync(Id(1));
        await ledger.TryBeginCallAsync(Id(1), 100, 400);
        await ledger.CompleteCallAsync(Id(1), new ModelUsage(100, 400));

        var over = await ledger.TryBeginCallAsync(Id(1), 100, 201);
        var exact = await ledger.TryBeginCallAsync(Id(1), 100, 200);

        Assert.Equal(ModelCallDenialReason.OutputTokenLimitReached, over.DenialReason);
        Assert.True(exact.Allowed);
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(-1, 100)]
    [InlineData(100, 0)]
    [InlineData(100, -5)]
    [InlineData(6001, 100)]
    [InlineData(100, 601)]
    public async Task Rejects_malformed_or_oversized_call_requests_before_dispatch(int input, int output)
    {
        var (ledger, _) = Create();
        await ledger.TryReserveAsync(Id(1));

        var result = await ledger.TryBeginCallAsync(Id(1), input, output);

        Assert.False(result.Allowed);
        Assert.False(ledger.Reservation(Id(1))!.CallInFlight);
        Assert.Equal(0, ledger.Reservation(Id(1))!.CallsStarted);
    }

    [Fact]
    public async Task A_call_cannot_start_for_an_unknown_or_inactive_reservation()
    {
        var (ledger, _) = Create();
        Assert.Equal(ModelCallDenialReason.NotFound, (await ledger.TryBeginCallAsync(Id(9), 10, 10)).DenialReason);

        await ledger.TryReserveAsync(Id(1));
        await ledger.CancelUndispatchedAsync(Id(1));
        Assert.Equal(ModelCallDenialReason.NotActive, (await ledger.TryBeginCallAsync(Id(1), 10, 10)).DenialReason);
    }

    // ------------------------------------------------------------------------------- usage

    [Theory]
    [InlineData(-1, 10)]
    [InlineData(10, -1)]
    [InlineData(0, 10)]
    [InlineData(long.MaxValue, 10)]
    [InlineData(10, long.MaxValue)]
    [InlineData(long.MinValue, long.MinValue)]
    public async Task Invalid_usage_marks_the_outcome_unknown_and_never_refunds(long input, long output)
    {
        var (ledger, _) = Create();
        await ledger.TryReserveAsync(Id(1));
        await ledger.TryBeginCallAsync(Id(1), 1000, 100);

        var outcome = await ledger.CompleteCallAsync(Id(1), new ModelUsage(input, output));

        Assert.Equal(ModelControlOutcome.InvalidUsage, outcome);
        Assert.Equal(ReservationState.Uncertain, ledger.Reservation(Id(1))!.State);
        Assert.Equal(Worst, ledger.DayCharged(Noon));
        Assert.Equal(Worst, ledger.MonthCharged(Noon));
        Assert.Equal(ModelControlOutcome.InvalidState, await ledger.SettleAsync(Id(1)));
        Assert.Equal(Worst, ledger.DayCharged(Noon));
    }

    [Fact]
    public async Task Usage_beyond_the_turn_allowance_is_charged_at_the_larger_actual_cost_and_never_refunded()
    {
        var (ledger, _) = Create();
        await ledger.TryReserveAsync(Id(1));
        await ledger.TryBeginCallAsync(Id(1), 6000, 600);

        // The provider reports far more than the permitted turn: 90,000 input and 9,000 output.
        var outcome = await ledger.CompleteCallAsync(Id(1), new ModelUsage(90_000, 9_000));

        Assert.Equal(ModelControlOutcome.AllowanceExceeded, outcome);
        var actual = 9_000 + 3_600; // ceil(90000*0.1) + ceil(9000*0.4)
        Assert.Equal(actual, ledger.DayCharged(Noon));
        Assert.Equal(actual, ledger.MonthCharged(Noon));
        Assert.Equal(ReservationState.Settled, ledger.Reservation(Id(1))!.State);
        Assert.Equal(actual, ledger.Reservation(Id(1))!.ChargedMicroUsd);
        Assert.Equal(ModelControlOutcome.InvalidState, await ledger.SettleAsync(Id(1)));
        Assert.Equal(ModelCallDenialReason.NotActive, (await ledger.TryBeginCallAsync(Id(1), 10, 10)).DenialReason);
    }

    [Fact]
    public async Task Cumulative_usage_over_the_cap_across_two_calls_is_also_conservative()
    {
        var (ledger, _) = Create();
        await ledger.TryReserveAsync(Id(1));
        await ledger.TryBeginCallAsync(Id(1), 3000, 300);
        await ledger.CompleteCallAsync(Id(1), new ModelUsage(3000, 300));
        await ledger.TryBeginCallAsync(Id(1), 3000, 300);

        var outcome = await ledger.CompleteCallAsync(Id(1), new ModelUsage(3500, 300));

        Assert.Equal(ModelControlOutcome.AllowanceExceeded, outcome);
        Assert.True(ledger.DayCharged(Noon) >= Worst);
        Assert.Equal(ReservationState.Settled, ledger.Reservation(Id(1))!.State);
    }

    [Fact]
    public async Task Completing_without_a_call_in_flight_is_refused()
    {
        var (ledger, _) = Create();
        await ledger.TryReserveAsync(Id(1));

        Assert.Equal(ModelControlOutcome.InvalidState, await ledger.CompleteCallAsync(Id(1), new ModelUsage(10, 10)));
        Assert.Equal(ModelControlOutcome.NotFound, await ledger.CompleteCallAsync(Id(9), new ModelUsage(10, 10)));
    }

    // ------------------------------------------------------------- uncertain outcomes and permits

    [Fact]
    public async Task Abandoning_keeps_the_charge_and_holds_the_permit_regardless_of_time()
    {
        var (ledger, time) = Create();
        await ledger.TryReserveAsync(Id(1));
        await ledger.TryBeginCallAsync(Id(1), 1000, 100);

        Assert.Equal(ModelControlOutcome.Applied, await ledger.AbandonAsync(Id(1)));

        Assert.Equal(Worst, ledger.DayCharged(Noon));
        Assert.Equal(1, ledger.ActivePermits);
        Assert.Equal(ReservationState.Uncertain, ledger.Reservation(Id(1))!.State);
        Assert.True(ledger.Reservation(Id(1))!.CallInFlight);

        // A caller timeout or disconnect is not a lease end, and a lease end is not completion.
        await ledger.TryReserveAsync(Id(2));
        await ledger.AbandonAsync(Id(2));
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(ModelReservationDenialReason.ConcurrencyLimitReached, (await ledger.TryReserveAsync(Id(3))).DenialReason);
    }

    [Fact]
    public async Task Two_started_calls_keep_their_permits_after_the_lease_and_no_third_turn_is_admitted()
    {
        // Reviewed defect: after six minutes a third reservation and call were allowed while two
        // dispatched calls were still marked in flight.
        var (ledger, time) = Create();
        await ledger.TryReserveAsync(Id(1));
        await ledger.TryReserveAsync(Id(2));
        Assert.True((await ledger.TryBeginCallAsync(Id(1), 1000, 100)).Allowed);
        Assert.True((await ledger.TryBeginCallAsync(Id(2), 1000, 100)).Allowed);

        time.Advance(TimeSpan.FromMinutes(6));
        var third = await ledger.TryReserveAsync(Id(3));

        Assert.False(third.Success);
        Assert.Equal(ModelReservationDenialReason.ConcurrencyLimitReached, third.DenialReason);
        Assert.Equal(2, ledger.ActivePermits);
        Assert.True(ledger.Reservation(Id(1))!.CallInFlight);
        Assert.True(ledger.Reservation(Id(2))!.CallInFlight);
        Assert.Equal(2 * Worst, ledger.DayCharged(Noon));
        Assert.Equal(0, ledger.ReservationCount - 2);

        // Waiting longer changes nothing: only an explicit reconciliation releases them.
        time.Advance(TimeSpan.FromHours(6));
        Assert.Equal(ModelReservationDenialReason.ConcurrencyLimitReached, (await ledger.TryReserveAsync(Id(3))).DenialReason);
        Assert.Equal(2, ledger.ActivePermits);
    }

    [Fact]
    public async Task A_next_reservation_marks_unresolved_calls_uncertain_without_freeing_or_refunding()
    {
        var (ledger, time) = Create();
        await ledger.TryReserveAsync(Id(1));
        await ledger.TryBeginCallAsync(Id(1), 1000, 100);
        await ledger.TryReserveAsync(Id(2));
        await ledger.TryBeginCallAsync(Id(2), 1000, 100);
        await ledger.CompleteCallAsync(Id(2), new ModelUsage(1000, 100)); // call finished, turn not settled

        time.Advance(TimeSpan.FromMinutes(6));
        var next = await ledger.TryReserveAsync(Id(3));

        // Id(2) had no call in flight, so nothing can still be running for it: its permit is freed.
        Assert.True(next.Success);
        Assert.Equal(ReservationState.Uncertain, ledger.Reservation(Id(1))!.State);
        Assert.True(ledger.Reservation(Id(1))!.CallInFlight);
        Assert.Equal(ReservationState.Lapsed, ledger.Reservation(Id(2))!.State);
        Assert.Equal(Worst, ledger.Reservation(Id(2))!.ChargedMicroUsd); // never refunded
        Assert.Equal(3 * Worst, ledger.DayCharged(Noon));
        Assert.Equal(2, ledger.ActivePermits); // Id(1) stays held, plus the new turn
        Assert.Contains(Id(1), ledger.Root!.Permits.Keys);
        Assert.Equal(ModelControlOutcome.InvalidState, await ledger.SettleAsync(Id(1)));
    }

    [Fact]
    public async Task A_reservation_that_never_dispatched_lapses_and_frees_its_permit_with_the_charge_kept()
    {
        var (ledger, time) = Create();
        await ledger.TryReserveAsync(Id(1));
        await ledger.TryReserveAsync(Id(2));

        time.Advance(ModelControlPolicy.ApprovedPermitLease + TimeSpan.FromSeconds(1));
        var next = await ledger.TryReserveAsync(Id(3));

        Assert.True(next.Success);
        Assert.Equal(ReservationState.Lapsed, ledger.Reservation(Id(1))!.State);
        Assert.Equal(ReservationState.Lapsed, ledger.Reservation(Id(2))!.State);
        Assert.NotNull(ledger.Reservation(Id(1))!.ExpiresAt);
        Assert.Equal(1, ledger.ActivePermits);
        Assert.Equal(3 * Worst, ledger.DayCharged(Noon));
        Assert.Equal(ModelControlOutcome.InvalidState, await ledger.SettleAsync(Id(1)));
        Assert.Equal(ModelCallDenialReason.NotActive, (await ledger.TryBeginCallAsync(Id(1), 10, 10)).DenialReason);
    }

    [Fact]
    public async Task A_permit_whose_reservation_is_missing_or_unreadable_stays_held()
    {
        var (ledger, time) = Create();
        await ledger.TryReserveAsync(Id(1));
        await ledger.TryReserveAsync(Id(2));
        ledger.SetReservation(ledger.Reservation(Id(1))! with { ReservedMicroUsd = -1 }); // corrupt
        ledger.RemoveReservation(Id(2)); // lost

        time.Advance(TimeSpan.FromHours(1));

        Assert.Equal(ModelReservationDenialReason.ConcurrencyLimitReached, (await ledger.TryReserveAsync(Id(3))).DenialReason);
        Assert.Equal(2, ledger.ActivePermits);
    }

    [Fact]
    public async Task A_late_call_after_the_lease_ended_is_refused()
    {
        var (ledger, time) = Create();
        await ledger.TryReserveAsync(Id(1));

        time.Advance(ModelControlPolicy.ApprovedPermitLease + TimeSpan.FromSeconds(1));

        Assert.Equal(ModelCallDenialReason.PermitLost, (await ledger.TryBeginCallAsync(Id(1), 10, 10)).DenialReason);
        Assert.Equal(ModelControlOutcome.InvalidState, await ledger.SettleAsync(Id(1)));
        Assert.Equal(Worst, ledger.DayCharged(Noon));
    }

    // ------------------------------------------------------------------ explicit reconciliation

    private static async Task<(InMemoryModelControlLedger Ledger, FakeTimeProvider Time)> TwoStuckCalls()
    {
        var (ledger, time) = Create();
        await ledger.TryReserveAsync(Id(1));
        await ledger.TryReserveAsync(Id(2));
        await ledger.TryBeginCallAsync(Id(1), 1000, 100);
        await ledger.TryBeginCallAsync(Id(2), 1000, 100);
        time.Advance(TimeSpan.FromMinutes(6));
        return (ledger, time);
    }

    [Fact]
    public async Task Confirming_a_call_never_ran_refunds_it_and_releases_its_permit()
    {
        var (ledger, _) = await TwoStuckCalls();

        Assert.Equal(ModelControlOutcome.Applied, await ledger.ReconcileAsync(Id(1), ReconciliationResolution.NotDispatched));

        Assert.Equal(ReservationState.Cancelled, ledger.Reservation(Id(1))!.State);
        Assert.False(ledger.Reservation(Id(1))!.CallInFlight);
        Assert.Equal(0, ledger.Reservation(Id(1))!.ChargedMicroUsd);
        Assert.Equal(Worst, ledger.DayCharged(Noon)); // only Id(2) remains charged
        Assert.Equal(1, ledger.ActivePermits);
        Assert.True((await ledger.TryReserveAsync(Id(3))).Success); // now, and only now, a permit is free
    }

    [Fact]
    public async Task Confirming_completion_charges_the_confirmed_usage_in_the_original_period()
    {
        var (ledger, time) = Create(start: new DateTimeOffset(2026, 10, 8, 23, 58, 0, TimeSpan.Zero));
        await ledger.TryReserveAsync(Id(1));
        await ledger.TryBeginCallAsync(Id(1), 1000, 100);
        time.Advance(TimeSpan.FromHours(3)); // the next UTC day
        await ledger.TryReserveAsync(Id(2));
        await ledger.AbandonAsync(Id(1));

        Assert.Equal(ModelControlOutcome.Applied,
            await ledger.ReconcileAsync(Id(1), ReconciliationResolution.Completed, new ModelUsage(1000, 100)));

        Assert.Equal(140, ledger.DayCharged(time.GetUtcNow().AddDays(-1)));
        Assert.Equal(Worst, ledger.DayCharged(time.GetUtcNow())); // today untouched
        Assert.Equal(ReservationState.Settled, ledger.Reservation(Id(1))!.State);
        Assert.Equal(140, ledger.Reservation(Id(1))!.ChargedMicroUsd);
        Assert.DoesNotContain(Id(1), ledger.Root!.Permits.Keys);
    }

    [Fact]
    public async Task Accepting_the_charge_as_reserved_releases_the_permit_without_changing_any_counter()
    {
        var (ledger, _) = await TwoStuckCalls();

        Assert.Equal(ModelControlOutcome.Applied, await ledger.ReconcileAsync(Id(2), ReconciliationResolution.ChargeAsReserved));

        Assert.Equal(2 * Worst, ledger.DayCharged(Noon));
        Assert.Equal(ReservationState.Settled, ledger.Reservation(Id(2))!.State);
        Assert.Equal(1, ledger.ActivePermits);
    }

    [Fact]
    public async Task Reconciliation_refuses_invalid_input_and_resolved_reservations()
    {
        var (ledger, _) = await TwoStuckCalls();

        Assert.Equal(ModelControlOutcome.InvalidUsage, await ledger.ReconcileAsync(Id(1), ReconciliationResolution.Completed, null));
        Assert.Equal(ModelControlOutcome.InvalidUsage, await ledger.ReconcileAsync(Id(1), ReconciliationResolution.Completed, new ModelUsage(-1, 5)));
        Assert.Equal(ModelControlOutcome.InvalidUsage, await ledger.ReconcileAsync(Id(1), ReconciliationResolution.Completed, new ModelUsage(0, 5)));
        Assert.Equal(ModelControlOutcome.NotFound, await ledger.ReconcileAsync(Id(9), ReconciliationResolution.ChargeAsReserved));
        Assert.Equal(ModelControlOutcome.InvalidState, await ledger.ReconcileAsync(Id(1), (ReconciliationResolution)99));
        Assert.Equal(2 * Worst, ledger.DayCharged(Noon));
        Assert.Equal(2, ledger.ActivePermits);

        Assert.Equal(ModelControlOutcome.Applied, await ledger.ReconcileAsync(Id(1), ReconciliationResolution.ChargeAsReserved));
        Assert.Equal(ModelControlOutcome.InvalidState, await ledger.ReconcileAsync(Id(1), ReconciliationResolution.NotDispatched));
        Assert.Equal(Worst + Worst, ledger.DayCharged(Noon)); // the second call cannot refund a settled one
    }

    [Fact]
    public async Task Confirmed_completion_needs_a_call_in_flight_and_a_matching_tariff()
    {
        var (ledger, _) = Create();
        await ledger.TryReserveAsync(Id(1));
        await ledger.AbandonAsync(Id(1)); // no call ever began

        Assert.Equal(ModelControlOutcome.InvalidState,
            await ledger.ReconcileAsync(Id(1), ReconciliationResolution.Completed, new ModelUsage(10, 10)));

        await ledger.TryReserveAsync(Id(2));
        await ledger.TryBeginCallAsync(Id(2), 100, 10);
        ledger.Policy = ModelControlFixtures.Policy(tariff: ModelControlFixtures.Tariff(version: "tariff-2"));
        Assert.Equal(ModelControlOutcome.InvalidTariffOrPolicy,
            await ledger.ReconcileAsync(Id(2), ReconciliationResolution.Completed, new ModelUsage(10, 10)));
        Assert.Equal(2 * Worst, ledger.DayCharged(Noon));
    }

    [Fact]
    public async Task Reconciliation_of_a_closed_period_whose_counters_expired_keeps_the_evidence_and_frees_the_permit()
    {
        var clock = new SettableClock(Noon);
        var ledger = new InMemoryModelControlLedger(ModelControlFixtures.Policy(), clock);
        await ledger.TryReserveAsync(Id(1));
        await ledger.TryBeginCallAsync(Id(1), 100, 10);

        clock.Now = new DateTimeOffset(2027, 3, 1, 0, 0, 0, TimeSpan.Zero); // both periods long expired
        ledger.SetCounter("day_2026-10-08", null);
        ledger.SetCounter("month_2026-10", null);

        Assert.Equal(ModelControlOutcome.Applied, await ledger.ReconcileAsync(Id(1), ReconciliationResolution.NotDispatched));
        Assert.Equal(ReservationState.Cancelled, ledger.Reservation(Id(1))!.State);
        Assert.Equal(0, ledger.ActivePermits);
        Assert.Null(ledger.Counter("day_2026-10-08"));
    }

    [Fact]
    public async Task Reconciliation_with_a_missing_counter_in_a_live_period_changes_nothing()
    {
        var (ledger, _) = await TwoStuckCalls();
        ledger.SetCounter("month_2026-10", null);

        Assert.Equal(ModelControlOutcome.StateInvalid, await ledger.ReconcileAsync(Id(1), ReconciliationResolution.NotDispatched));

        Assert.Equal(ReservationState.Active, ledger.Reservation(Id(1))!.State);
        Assert.Equal(2, ledger.ActivePermits);
    }

    [Fact]
    public async Task Unresolved_reservations_can_be_listed_without_content()
    {
        var (ledger, _) = await TwoStuckCalls();
        await ledger.ReconcileAsync(Id(1), ReconciliationResolution.ChargeAsReserved);

        var unresolved = await ledger.ListUnresolvedAsync();

        var only = Assert.Single(unresolved);
        Assert.Equal(Id(2), only.Id);
        Assert.True(only.CallInFlight);
        Assert.Equal(Worst, only.ChargedMicroUsd);
    }

    [Fact]
    public async Task Every_admission_advances_the_epoch_and_a_saturated_epoch_denies()
    {
        var (ledger, _) = Create();
        await ledger.TryReserveAsync(Id(1));
        await ledger.TryReserveAsync(Id(2));
        Assert.Equal(2, ledger.Root!.Epoch);

        ledger.Root = ledger.Root with { Epoch = long.MaxValue };
        Assert.Equal(ModelReservationDenialReason.StateInvalid, (await ledger.TryReserveAsync(Id(3))).DenialReason);
        ledger.Root = ledger.Root with { Epoch = -1 };
        Assert.Equal(ModelReservationDenialReason.StateInvalid, (await ledger.TryReserveAsync(Id(3))).DenialReason);
    }

    [Fact]
    public async Task Resolved_and_unresolved_reservations_both_count_toward_capacity()
    {
        var (ledger, _) = await TwoStuckCalls();
        await ledger.ReconcileAsync(Id(1), ReconciliationResolution.ChargeAsReserved);

        Assert.Equal(2, ledger.Root!.LiveReservations);
    }

    // ------------------------------------------------------------------------- duplicate IDs

    [Fact]
    public async Task A_duplicate_reservation_id_cannot_dispatch_paid_work_twice()
    {
        var (ledger, _) = Create();
        Assert.True((await ledger.TryReserveAsync(Id(1))).Success);
        Assert.True((await ledger.TryBeginCallAsync(Id(1), 1000, 100)).Allowed);

        var duplicate = await ledger.TryReserveAsync(Id(1));

        Assert.False(duplicate.Success);
        Assert.Equal(ModelReservationDenialReason.DuplicateReservationId, duplicate.DenialReason);
        Assert.Equal(1, ledger.ActivePermits);
        Assert.Equal(Worst, ledger.DayCharged(Noon));
        // The one in-flight call is still the only one that can run.
        Assert.Equal(ModelCallDenialReason.CallInFlight, (await ledger.TryBeginCallAsync(Id(1), 10, 10)).DenialReason);
    }

    [Fact]
    public async Task A_settled_or_cancelled_id_cannot_be_reused()
    {
        var (ledger, _) = Create();
        await ledger.TryReserveAsync(Id(1));
        await ledger.CancelUndispatchedAsync(Id(1));
        await ledger.TryReserveAsync(Id(2));
        await ledger.TryBeginCallAsync(Id(2), 100, 10);
        await ledger.CompleteCallAsync(Id(2), new ModelUsage(100, 10));
        await ledger.SettleAsync(Id(2));

        Assert.Equal(ModelReservationDenialReason.DuplicateReservationId, (await ledger.TryReserveAsync(Id(1))).DenialReason);
        Assert.Equal(ModelReservationDenialReason.DuplicateReservationId, (await ledger.TryReserveAsync(Id(2))).DenialReason);
    }

    [Fact]
    public async Task Concurrent_attempts_with_one_id_grant_exactly_one_reservation()
    {
        var (ledger, _) = Create();

        var results = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => ledger.TryReserveAsync(Id(1)).AsTask())));

        Assert.Equal(1, results.Count(r => r.Success));
        Assert.Equal(Worst, ledger.DayCharged(Noon));
        Assert.Equal(1, ledger.ActivePermits);
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("has space in it")]
    [InlineData("slash/not/allowed")]
    [InlineData("0123456789012345678901234567890123456789012345678901234567890123456789")]
    public async Task Rejects_ids_that_are_not_bounded_opaque_tokens(string id)
    {
        var (ledger, _) = Create();

        var result = await ledger.TryReserveAsync(id);

        Assert.False(result.Success);
        Assert.Equal(ModelReservationDenialReason.InvalidRequest, result.DenialReason);
        Assert.Equal(0, ledger.ActivePermits);
    }

    [Fact]
    public async Task Concurrent_reservations_cannot_oversubscribe_money_or_permits()
    {
        var (ledger, _) = Create(ModelControlFixtures.Policy(daily: 5 * Worst, monthly: 5 * Worst));

        var results = await Task.WhenAll(Enumerable.Range(1, 64).Select(i => Task.Run(() => ledger.TryReserveAsync(Id(i)).AsTask())));

        Assert.Equal(2, results.Count(r => r.Success));
        Assert.True(ledger.DayCharged(Noon) <= 5 * Worst);
        Assert.Equal(2, ledger.ActivePermits);
    }

    // --------------------------------------------------------------- overflow and corrupt balances

    [Theory]
    [InlineData(long.MaxValue)]
    [InlineData(long.MaxValue - 1)]
    [InlineData(long.MaxValue / 2)]
    [InlineData(-1L)]
    [InlineData(long.MinValue)]
    public async Task A_corrupt_or_extreme_balance_denies_admission_without_changing_state(long corrupt)
    {
        var (ledger, _) = Create();
        await ledger.TryReserveAsync(Id(1));
        await ledger.AbandonAsync(Id(1));
        ledger.SetCounter("day_2026-10-08", new PeriodCounter("2026-10-08", corrupt, Noon.AddDays(40)));
        var rootBefore = ledger.Root;
        var recordsBefore = ledger.ReservationCount;

        var result = await ledger.TryReserveAsync(Id(2));

        Assert.False(result.Success);
        Assert.Equal(corrupt, ledger.DayCharged(Noon));
        Assert.Equal(rootBefore, ledger.Root);
        Assert.Equal(recordsBefore, ledger.ReservationCount);
    }

    [Fact]
    public async Task A_one_unit_balance_followed_by_an_extreme_request_cannot_overflow_into_admission()
    {
        // Reviewed defect: after a one-unit reservation, long.MaxValue was admitted by wraparound.
        var tariff = new ModelTariff("tariff-x", "model-x", ModelTariff.MaxRateMicroUsdPerMillionTokens, ModelTariff.MaxRateMicroUsdPerMillionTokens, new DateOnly(2027, 1, 1));
        var absurd = ModelControlFixtures.Policy(tariff: tariff);

        Assert.False(absurd.TryValidate(Noon, out _)); // worst case does not fit the daily budget

        var (ledger, _) = Create(absurd);
        var denied = await ledger.TryReserveAsync(Id(1));
        Assert.False(denied.Success);
        Assert.Equal(ModelReservationDenialReason.InvalidTariffOrPolicy, denied.DenialReason);
        Assert.Equal(0, ledger.DayCharged(Noon));
        Assert.False(tariff.TryCost(long.MaxValue, long.MaxValue, out _));
    }

    [Fact]
    public async Task A_balance_that_leaves_less_than_a_turn_denies_without_overflow()
    {
        var (ledger, _) = Create();
        await ledger.TryReserveAsync(Id(1));
        await ledger.CancelUndispatchedAsync(Id(1));
        ledger.SetCounter("day_2026-10-08", new PeriodCounter("2026-10-08", ModelControlPolicy.ApprovedDailyBudgetMicroUsd - 1, Noon.AddDays(40)));

        var result = await ledger.TryReserveAsync(Id(2));

        Assert.Equal(ModelReservationDenialReason.DailyBudgetExhausted, result.DenialReason);
        Assert.Equal(ModelControlPolicy.ApprovedDailyBudgetMicroUsd - 1, ledger.DayCharged(Noon));
    }

    // --------------------------------------------------------------------- lost or corrupt state

    [Fact]
    public async Task A_store_that_was_never_initialized_denies_instead_of_starting_from_zero()
    {
        var ledger = new InMemoryModelControlLedger(ModelControlFixtures.Policy(), new FakeTimeProvider(Noon), initialize: false);

        var result = await ledger.TryReserveAsync(Id(1));

        Assert.Equal(ModelReservationDenialReason.StoreNotInitialized, result.DenialReason);
        Assert.Null(ledger.Root);
        Assert.Equal(0, ledger.ReservationCount);
    }

    [Fact]
    public async Task Lost_current_period_state_denies_instead_of_granting_a_fresh_allowance()
    {
        var (ledger, _) = Create();
        await ledger.TryReserveAsync(Id(1));
        await ledger.AbandonAsync(Id(1));

        ledger.SetCounter("day_2026-10-08", null);
        Assert.Equal(ModelReservationDenialReason.StateInvalid, (await ledger.TryReserveAsync(Id(2))).DenialReason);

        ledger.SetCounter("day_2026-10-08", new PeriodCounter("2026-10-08", Worst, Noon.AddDays(40)));
        ledger.SetCounter("month_2026-10", null);
        Assert.Equal(ModelReservationDenialReason.StateInvalid, (await ledger.TryReserveAsync(Id(2))).DenialReason);
        Assert.Equal(1, ledger.ReservationCount);
    }

    [Fact]
    public async Task Corrupt_stored_documents_deny()
    {
        var (ledger, _) = Create();
        await ledger.TryReserveAsync(Id(1));
        await ledger.AbandonAsync(Id(1));
        var root = ledger.Root!;

        ledger.SetCounter("day_2026-10-08", new PeriodCounter("2026-10-07", 0, Noon.AddDays(40)));
        Assert.Equal(ModelReservationDenialReason.StateInvalid, (await ledger.TryReserveAsync(Id(2))).DenialReason);
        ledger.SetCounter("day_2026-10-08", new PeriodCounter("2026-10-08", Worst, Noon.AddDays(40)));

        ledger.Root = root with { SchemaVersion = 99 };
        Assert.Equal(ModelReservationDenialReason.StateInvalid, (await ledger.TryReserveAsync(Id(2))).DenialReason);

        ledger.Root = root with { LiveReservations = -3 };
        Assert.Equal(ModelReservationDenialReason.StateInvalid, (await ledger.TryReserveAsync(Id(2))).DenialReason);

        ledger.Root = root with { LastDayKey = "not-a-day" };
        Assert.Equal(ModelReservationDenialReason.StateInvalid, (await ledger.TryReserveAsync(Id(2))).DenialReason);

        ledger.Root = root with { Permits = new Dictionary<string, DateTimeOffset> { ["a_b_c_d_1"] = Noon.AddMinutes(1), ["a_b_c_d_2"] = Noon.AddMinutes(1), ["a_b_c_d_3"] = Noon.AddMinutes(1) } };
        Assert.Equal(ModelReservationDenialReason.StateInvalid, (await ledger.TryReserveAsync(Id(2))).DenialReason);
    }

    [Fact]
    public async Task A_corrupt_reservation_record_blocks_calls_and_settlement()
    {
        var (ledger, _) = Create();
        await ledger.TryReserveAsync(Id(1));
        var good = ledger.Reservation(Id(1))!;

        ledger.SetReservation(good with { ReservedMicroUsd = -5 });
        Assert.Equal(ModelCallDenialReason.StateInvalid, (await ledger.TryBeginCallAsync(Id(1), 10, 10)).DenialReason);
        Assert.Equal(ModelControlOutcome.StateInvalid, await ledger.CancelUndispatchedAsync(Id(1)));

        ledger.SetReservation(good with { InputTokensUsed = -1 });
        Assert.Equal(ModelControlOutcome.StateInvalid, await ledger.AbandonAsync(Id(1)));
        ledger.SetReservation(good with { CallsStarted = 9 });
        Assert.Equal(ModelCallDenialReason.StateInvalid, (await ledger.TryBeginCallAsync(Id(1), 10, 10)).DenialReason);
        Assert.Equal(Worst, ledger.DayCharged(Noon));
    }

    [Fact]
    public async Task A_clock_behind_the_recorded_high_water_mark_denies()
    {
        var clock = new SettableClock(Noon);
        var ledger = new InMemoryModelControlLedger(ModelControlFixtures.Policy(), clock);
        await ledger.TryReserveAsync(Id(1));
        await ledger.AbandonAsync(Id(1));

        clock.Now = Noon.AddDays(-2);

        Assert.Equal(ModelReservationDenialReason.StateInvalid, (await ledger.TryReserveAsync(Id(2))).DenialReason);
        Assert.Equal(Worst, ledger.DayCharged(Noon));
    }

    private sealed class SettableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task A_policy_version_that_does_not_match_the_stored_control_document_denies()
    {
        var (ledger, _) = Create();
        ledger.Policy = ModelControlFixtures.Policy(version: "policy-2");

        Assert.Equal(ModelReservationDenialReason.InvalidTariffOrPolicy, (await ledger.TryReserveAsync(Id(1))).DenialReason);
        Assert.Equal(0, ledger.DayCharged(Noon));
    }

    [Fact]
    public async Task Capacity_is_bounded_and_denies_before_any_state_changes()
    {
        var (ledger, _) = Create();
        ledger.Root = ledger.Root! with { LiveReservations = ModelControlPolicy.ApprovedMaxLiveReservations };

        var result = await ledger.TryReserveAsync(Id(1));

        Assert.Equal(ModelReservationDenialReason.CapacityExhausted, result.DenialReason);
        Assert.Equal(0, ledger.ReservationCount);
        Assert.Equal(0, ledger.DayCharged(Noon));
    }

    [Fact]
    public async Task Each_reservation_counts_toward_the_live_capacity()
    {
        var (ledger, _) = Create();
        await ledger.TryReserveAsync(Id(1));
        await ledger.TryReserveAsync(Id(2));

        Assert.Equal(2, ledger.Root!.LiveReservations);
    }

    // ----------------------------------------------------------------------- tariffs and policy

    [Fact]
    public async Task An_expired_tariff_disables_new_paid_admission()
    {
        var (ledger, time) = Create(ModelControlFixtures.Policy(tariff: ModelControlFixtures.Tariff(new DateOnly(2026, 10, 8))));
        Assert.True((await ledger.TryReserveAsync(Id(1))).Success);
        await ledger.CancelUndispatchedAsync(Id(1));

        time.Advance(TimeSpan.FromDays(1));

        Assert.Equal(ModelReservationDenialReason.InvalidTariffOrPolicy, (await ledger.TryReserveAsync(Id(2))).DenialReason);
    }

    [Fact]
    public async Task A_tariff_change_mid_turn_blocks_new_calls_and_keeps_the_charge_on_settlement()
    {
        var (ledger, _) = Create();
        await ledger.TryReserveAsync(Id(1));
        await ledger.TryBeginCallAsync(Id(1), 1000, 100);
        await ledger.CompleteCallAsync(Id(1), new ModelUsage(1000, 100));

        ledger.Policy = ModelControlFixtures.Policy(tariff: ModelControlFixtures.Tariff(version: "tariff-2"));

        Assert.Equal(ModelCallDenialReason.InvalidTariffOrPolicy, (await ledger.TryBeginCallAsync(Id(1), 10, 10)).DenialReason);
        Assert.Equal(ModelControlOutcome.InvalidTariffOrPolicy, await ledger.SettleAsync(Id(1)));
        Assert.Equal(Worst, ledger.DayCharged(Noon)); // unpriced, therefore not refunded
        Assert.Equal(0, ledger.ActivePermits);
    }

    [Fact]
    public async Task A_missing_tariff_denies_all_paid_admission()
    {
        var policy = ModelControlPolicy.Approved("policy-1", tariff: null);
        var (ledger, _) = Create(policy);

        Assert.Equal(ModelReservationDenialReason.InvalidTariffOrPolicy, (await ledger.TryReserveAsync(Id(1))).DenialReason);
        Assert.Equal(ModelCallDenialReason.InvalidTariffOrPolicy, (await ledger.TryBeginCallAsync(Id(1), 10, 10)).DenialReason);
    }

    public static TheoryData<string> InvalidPolicies() => new()
    {
        "negative-daily", "zero-daily", "daily-above-ceiling", "negative-monthly", "monthly-above-ceiling", "month-below-day",
        "permits-three", "permits-zero", "calls-three", "calls-zero", "input-above", "input-zero", "output-above", "output-zero",
        "capacity-above", "capacity-zero", "lease-too-long", "lease-too-short", "bad-version", "empty-version",
        "no-tariff", "expired-tariff", "zero-rate", "negative-rate", "rate-above-ceiling", "bad-tariff-version", "bad-model-id",
        "turn-larger-than-day"
    };

    [Theory]
    [MemberData(nameof(InvalidPolicies))]
    public async Task Malformed_policies_and_tariffs_deny_instead_of_becoming_defaults(string kind)
    {
        var good = ModelControlFixtures.Policy();
        var policy = kind switch
        {
            "negative-daily" => good with { DailyBudgetMicroUsd = -1 },
            "zero-daily" => good with { DailyBudgetMicroUsd = 0 },
            "daily-above-ceiling" => good with { DailyBudgetMicroUsd = ModelControlPolicy.ApprovedDailyBudgetMicroUsd + 1 },
            "negative-monthly" => good with { MonthlyBudgetMicroUsd = -1 },
            "monthly-above-ceiling" => good with { MonthlyBudgetMicroUsd = ModelControlPolicy.ApprovedMonthlyBudgetMicroUsd + 1 },
            "month-below-day" => good with { DailyBudgetMicroUsd = 50_000, MonthlyBudgetMicroUsd = 40_000 },
            "permits-three" => good with { MaxActivePermits = 3 },
            "permits-zero" => good with { MaxActivePermits = 0 },
            "calls-three" => good with { MaxCalls = 3 },
            "calls-zero" => good with { MaxCalls = 0 },
            "input-above" => good with { MaxInputTokens = 6001 },
            "input-zero" => good with { MaxInputTokens = 0 },
            "output-above" => good with { MaxOutputTokens = 601 },
            "output-zero" => good with { MaxOutputTokens = 0 },
            "capacity-above" => good with { MaxLiveReservations = ModelControlPolicy.ApprovedMaxLiveReservations + 1 },
            "capacity-zero" => good with { MaxLiveReservations = 0 },
            "lease-too-long" => good with { PermitLease = TimeSpan.FromHours(1) },
            "lease-too-short" => good with { PermitLease = TimeSpan.FromSeconds(1) },
            "bad-version" => good with { Version = "has space" },
            "empty-version" => good with { Version = "" },
            "no-tariff" => good with { Tariff = null },
            "expired-tariff" => good with { Tariff = ModelControlFixtures.Tariff(new DateOnly(2026, 10, 7)) },
            "zero-rate" => good with { Tariff = good.Tariff! with { InputMicroUsdPerMillionTokens = 0 } },
            "negative-rate" => good with { Tariff = good.Tariff! with { OutputMicroUsdPerMillionTokens = -5 } },
            "rate-above-ceiling" => good with { Tariff = good.Tariff! with { InputMicroUsdPerMillionTokens = ModelTariff.MaxRateMicroUsdPerMillionTokens + 1 } },
            "bad-tariff-version" => good with { Tariff = good.Tariff! with { Version = "" } },
            "bad-model-id" => good with { Tariff = good.Tariff! with { ModelId = "bad id!" } },
            "turn-larger-than-day" => good with { DailyBudgetMicroUsd = Worst - 1, MonthlyBudgetMicroUsd = Worst - 1 },
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        Assert.False(policy.TryValidate(Noon, out var error), kind);
        Assert.NotEmpty(error);

        var ledger = new InMemoryModelControlLedger(policy, new FakeTimeProvider(Noon));
        var result = await ledger.TryReserveAsync(Id(1));

        Assert.False(result.Success);
        Assert.Equal(ModelReservationDenialReason.InvalidTariffOrPolicy, result.DenialReason);
        Assert.Equal(0, ledger.ReservationCount);
        Assert.Equal(0, ledger.DayCharged(Noon));
    }

    [Fact]
    public void The_approved_policy_is_valid_and_matches_the_owner_approved_numbers()
    {
        var policy = ModelControlFixtures.Policy();

        Assert.True(policy.TryValidate(Noon, out _));
        Assert.Equal(100_000, policy.DailyBudgetMicroUsd);
        Assert.Equal(1_000_000, policy.MonthlyBudgetMicroUsd);
        Assert.Equal(2, policy.MaxActivePermits);
        Assert.Equal((2, 6000, 600), (policy.MaxCalls, policy.MaxInputTokens, policy.MaxOutputTokens));
        Assert.True(policy.TryWorstCaseCost(out var worst));
        Assert.Equal(Worst, worst);
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(1, 0, 1)]
    [InlineData(1_000_000, 0, 100_000)]
    [InlineData(1_000_001, 0, 100_001)]
    [InlineData(0, 1, 1)]
    [InlineData(0, 2_500_001, 1_000_001)]
    [InlineData(5, 3, 3)]
    public void Tariff_costs_are_integer_and_round_up(long input, long output, long expected)
    {
        Assert.True(ModelControlFixtures.Tariff().TryCost(input, output, out var cost));
        Assert.Equal(expected, cost);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    [InlineData(long.MinValue, 0)]
    public void Tariff_cost_rejects_negative_counts(long input, long output)
    {
        Assert.False(ModelControlFixtures.Tariff().TryCost(input, output, out _));
    }

    [Fact]
    public void Tariff_cost_rejects_results_that_do_not_fit_instead_of_wrapping()
    {
        var steepest = new ModelTariff("tariff-x", "model-x", ModelTariff.MaxRateMicroUsdPerMillionTokens, ModelTariff.MaxRateMicroUsdPerMillionTokens, new DateOnly(2027, 1, 1));

        Assert.False(steepest.TryCost(long.MaxValue, long.MaxValue, out var cost));
        Assert.Equal(0, cost);
        Assert.False(steepest.TryCost(long.MaxValue, 0, out _));
    }

    // ---------------------------------------------------------------------------- store failures

    [Fact]
    public async Task A_store_outage_denies_every_operation()
    {
        var (ledger, _) = Create();
        await ledger.TryReserveAsync(Id(1));
        ledger.StoreDown = true;

        Assert.Equal(ModelReservationDenialReason.StoreUnavailable, (await ledger.TryReserveAsync(Id(2))).DenialReason);
        Assert.Equal(ModelCallDenialReason.StoreUnavailable, (await ledger.TryBeginCallAsync(Id(1), 10, 10)).DenialReason);
        Assert.Equal(ModelControlOutcome.StoreUnavailable, await ledger.CompleteCallAsync(Id(1), new ModelUsage(10, 10)));
        Assert.Equal(ModelControlOutcome.StoreUnavailable, await ledger.SettleAsync(Id(1)));
        Assert.Equal(ModelControlOutcome.StoreUnavailable, await ledger.CancelUndispatchedAsync(Id(1)));
        Assert.Equal(ModelControlOutcome.StoreUnavailable, await ledger.AbandonAsync(Id(1)));
        Assert.Equal(Worst, ledger.DayCharged(Noon));
    }
}
