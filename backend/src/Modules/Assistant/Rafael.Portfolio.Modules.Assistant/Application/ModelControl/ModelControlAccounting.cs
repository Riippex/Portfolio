using System.Text.RegularExpressions;

namespace Rafael.Portfolio.Modules.Assistant.Application;

/// <summary>The documents a decision wants written. Null means unchanged.</summary>
internal sealed record LedgerWrites(
    LedgerRoot? Root = null,
    PeriodCounter? Day = null,
    PeriodCounter? Month = null,
    ReservationRecord? Reservation = null,
    IReadOnlyList<ReservationRecord>? Reclaimed = null);

internal sealed record ReserveDecision(
    ModelReservationDenialReason? Denial,
    TimeSpan? RetryAfter,
    LedgerWrites? Writes)
{
    public bool Granted => Denial is null;
}

internal sealed record CallDecision(ModelCallDenialReason? Denial, ReservationRecord? Updated)
{
    public bool Allowed => Denial is null;
}

internal sealed record AccountingDecision(ModelControlOutcome Outcome, LedgerWrites? Writes);

/// <summary>
/// The pure accounting rules, shared by the Firestore adapter (inside a transaction) and the
/// in-memory test adapter (under a lock). It never reads a clock or a store: callers pass the
/// documents they read and write exactly what the decision returns, so a transaction that
/// retries recomputes from fresh reads.
/// </summary>
/// <remarks>
/// Safety rules: arithmetic never wraps (a corrupt or extreme value denies and writes nothing);
/// every refund is applied to the reservation's own day and month counters, never the current
/// ones; nothing is refunded unless usage is confirmed or no call began; an unknown outcome
/// leaves the charge in place and the permit held until its lease ends.
/// </remarks>
internal static partial class ModelControlAccounting
{
    private const long MaxSaneCharge = long.MaxValue / 4;
    private const long MaxReportedTokens = 100_000_000;

    public static bool IsValidReservationId(string? id) => id is not null && ReservationIdPattern().IsMatch(id);

    // ---------------------------------------------------------------------------------- reserve

    public static ReserveDecision Reserve(
        ModelControlPolicy policy,
        DateTimeOffset now,
        string reservationId,
        LedgerRoot? root,
        PeriodCounter? day,
        PeriodCounter? month,
        ReservationRecord? existing,
        IReadOnlyDictionary<string, ReservationRecord?> permitHolders)
    {
        if (!policy.TryValidate(now, out _) || !policy.TryWorstCaseCost(out var worstCase))
        {
            return Deny(ModelReservationDenialReason.InvalidTariffOrPolicy);
        }

        if (!IsValidReservationId(reservationId))
        {
            return Deny(ModelReservationDenialReason.InvalidRequest);
        }

        if (root is null)
        {
            return Deny(ModelReservationDenialReason.StoreNotInitialized);
        }

        if (!IsSane(root))
        {
            return Deny(ModelReservationDenialReason.StateInvalid);
        }

        if (!string.Equals(root.PolicyVersion, policy.Version, StringComparison.Ordinal))
        {
            return Deny(ModelReservationDenialReason.InvalidTariffOrPolicy);
        }

        if (existing is not null)
        {
            return Deny(ModelReservationDenialReason.DuplicateReservationId);
        }

        var dayKey = ModelControlPeriods.DayKey(now);
        var monthKey = ModelControlPeriods.MonthKey(now);

        // A clock behind the recorded high-water mark would re-open a closed period.
        if (string.CompareOrdinal(dayKey, root.LastDayKey) < 0 || string.CompareOrdinal(monthKey, root.LastMonthKey) < 0)
        {
            return Deny(ModelReservationDenialReason.StateInvalid);
        }

        // Permits whose lease ended are reclaimed, and their reservation becomes Uncertain:
        // the permit is freed, the charge is not.
        var permits = new Dictionary<string, DateTimeOffset>(root.Permits, StringComparer.Ordinal);
        var reclaimed = new List<ReservationRecord>();
        foreach (var (holderId, leaseEnd) in root.Permits)
        {
            if (leaseEnd > now)
            {
                continue;
            }

            permits.Remove(holderId);
            if (permitHolders.TryGetValue(holderId, out var holder) && holder is { State: ReservationState.Active })
            {
                reclaimed.Add(holder with { State = ReservationState.Uncertain, UpdatedAt = now });
            }
        }

        if (permits.Count >= policy.MaxActivePermits)
        {
            var soonest = permits.Values.Min() - now;
            return Deny(ModelReservationDenialReason.ConcurrencyLimitReached, Clamp(soonest, TimeSpan.FromSeconds(1)));
        }

        if (root.LiveReservations >= policy.MaxLiveReservations)
        {
            return Deny(ModelReservationDenialReason.CapacityExhausted);
        }

        if (!TryCharged(day, dayKey, root.LastDayKey, out var dayCharged) ||
            !TryCharged(month, monthKey, root.LastMonthKey, out var monthCharged))
        {
            return Deny(ModelReservationDenialReason.StateInvalid);
        }

        if (dayCharged > policy.DailyBudgetMicroUsd || worstCase > policy.DailyBudgetMicroUsd - dayCharged)
        {
            return Deny(
                ModelReservationDenialReason.DailyBudgetExhausted,
                Clamp(ModelControlPeriods.DayEnd(dayKey) - now, TimeSpan.FromSeconds(60)));
        }

        if (monthCharged > policy.MonthlyBudgetMicroUsd || worstCase > policy.MonthlyBudgetMicroUsd - monthCharged)
        {
            return Deny(
                ModelReservationDenialReason.MonthlyBudgetExhausted,
                Clamp(ModelControlPeriods.MonthEnd(monthKey) - now, TimeSpan.FromHours(1)));
        }

        permits[reservationId] = now + policy.PermitLease;
        var tariff = policy.Tariff!;
        var record = new ReservationRecord(
            reservationId,
            ReservationState.Active,
            policy.Version,
            tariff.Version,
            tariff.ModelId,
            dayKey,
            monthKey,
            worstCase,
            worstCase,
            policy.MaxCalls,
            policy.MaxInputTokens,
            policy.MaxOutputTokens,
            CallsStarted: 0,
            InputTokensUsed: 0,
            OutputTokensUsed: 0,
            CallInFlight: false,
            PendingInputTokens: 0,
            PendingOutputTokens: 0,
            now,
            now,
            ModelControlPeriods.MonthExpiry(monthKey));

        return new ReserveDecision(
            null,
            null,
            new LedgerWrites(
                root with
                {
                    LastDayKey = dayKey,
                    LastMonthKey = monthKey,
                    Permits = permits,
                    LiveReservations = root.LiveReservations + 1
                },
                new PeriodCounter(dayKey, dayCharged + worstCase, ModelControlPeriods.DayExpiry(dayKey)),
                new PeriodCounter(monthKey, monthCharged + worstCase, ModelControlPeriods.MonthExpiry(monthKey)),
                record,
                reclaimed));
    }

    // -------------------------------------------------------------------------------- begin call

    public static CallDecision BeginCall(
        ModelControlPolicy policy,
        DateTimeOffset now,
        string reservationId,
        LedgerRoot? root,
        ReservationRecord? record,
        int estimatedInputTokens,
        int maxOutputTokens)
    {
        if (!IsValidReservationId(reservationId) || estimatedInputTokens < 1 || maxOutputTokens < 1)
        {
            return new CallDecision(ModelCallDenialReason.InvalidRequest, null);
        }

        if (!policy.TryValidate(now, out _))
        {
            return new CallDecision(ModelCallDenialReason.InvalidTariffOrPolicy, null);
        }

        if (record is null)
        {
            return new CallDecision(ModelCallDenialReason.NotFound, null);
        }

        if (root is null || !IsSane(root) || !IsSane(record))
        {
            return new CallDecision(ModelCallDenialReason.StateInvalid, null);
        }

        if (!string.Equals(record.PolicyVersion, policy.Version, StringComparison.Ordinal) ||
            !string.Equals(record.TariffVersion, policy.Tariff!.Version, StringComparison.Ordinal))
        {
            return new CallDecision(ModelCallDenialReason.InvalidTariffOrPolicy, null);
        }

        if (record.State != ReservationState.Active)
        {
            return new CallDecision(ModelCallDenialReason.NotActive, null);
        }

        if (!root.Permits.TryGetValue(record.Id, out var leaseEnd) || leaseEnd <= now)
        {
            return new CallDecision(ModelCallDenialReason.PermitLost, null);
        }

        if (record.CallInFlight)
        {
            return new CallDecision(ModelCallDenialReason.CallInFlight, null);
        }

        if (record.CallsStarted >= record.MaxCalls)
        {
            return new CallDecision(ModelCallDenialReason.CallLimitReached, null);
        }

        // Cumulative across the whole turn, so a second call cannot restore the allowance.
        if (record.InputTokensUsed + estimatedInputTokens > record.MaxInputTokens)
        {
            return new CallDecision(ModelCallDenialReason.InputTokenLimitReached, null);
        }

        if (record.OutputTokensUsed + maxOutputTokens > record.MaxOutputTokens)
        {
            return new CallDecision(ModelCallDenialReason.OutputTokenLimitReached, null);
        }

        return new CallDecision(
            null,
            record with
            {
                CallsStarted = record.CallsStarted + 1,
                CallInFlight = true,
                PendingInputTokens = estimatedInputTokens,
                PendingOutputTokens = maxOutputTokens,
                UpdatedAt = now
            });
    }

    // ------------------------------------------------------------------------------ complete call

    public static AccountingDecision CompleteCall(
        ModelControlPolicy policy,
        DateTimeOffset now,
        ReservationRecord? record,
        PeriodCounter? day,
        PeriodCounter? month,
        ModelUsage usage)
    {
        if (record is null)
        {
            return Outcome(ModelControlOutcome.NotFound);
        }

        if (!IsSane(record))
        {
            return Outcome(ModelControlOutcome.StateInvalid);
        }

        if (record.State != ReservationState.Active || !record.CallInFlight)
        {
            return Outcome(ModelControlOutcome.InvalidState);
        }

        // Malformed usage is never repaired into a default or a refund: the call's outcome is
        // unknown, so the reservation stays fully charged and cannot be settled.
        if (usage.InputTokens < 1 || usage.OutputTokens < 0 ||
            usage.InputTokens > MaxReportedTokens || usage.OutputTokens > MaxReportedTokens)
        {
            return Uncertain(record, now, ModelControlOutcome.InvalidUsage);
        }

        var inputUsed = record.InputTokensUsed + usage.InputTokens;
        var outputUsed = record.OutputTokensUsed + usage.OutputTokens;
        var completed = record with
        {
            InputTokensUsed = inputUsed,
            OutputTokensUsed = outputUsed,
            CallInFlight = false,
            PendingInputTokens = 0,
            PendingOutputTokens = 0,
            UpdatedAt = now
        };

        if (inputUsed <= record.MaxInputTokens && outputUsed <= record.MaxOutputTokens)
        {
            return new AccountingDecision(ModelControlOutcome.Applied, new LedgerWrites(Reservation: completed));
        }

        // The provider spent more than the turn allowed. Charge what was actually spent (never
        // less than reserved), and freeze the reservation so nothing can be refunded.
        var exceeded = completed with { State = ReservationState.Uncertain };
        var tariff = policy.Tariff;
        if (!policy.TryValidate(now, out _) ||
            tariff is null ||
            !string.Equals(tariff.Version, record.TariffVersion, StringComparison.Ordinal) ||
            !tariff.TryCost(inputUsed, outputUsed, out var actual))
        {
            return new AccountingDecision(ModelControlOutcome.AllowanceExceeded, new LedgerWrites(Reservation: exceeded));
        }

        if (actual <= record.ChargedMicroUsd)
        {
            return new AccountingDecision(ModelControlOutcome.AllowanceExceeded, new LedgerWrites(Reservation: exceeded));
        }

        var extra = actual - record.ChargedMicroUsd;
        if (!IsSane(day) || !IsSane(month) ||
            day!.Key != record.DayKey || month!.Key != record.MonthKey)
        {
            return new AccountingDecision(ModelControlOutcome.AllowanceExceeded, new LedgerWrites(Reservation: exceeded));
        }

        return new AccountingDecision(
            ModelControlOutcome.AllowanceExceeded,
            new LedgerWrites(
                Day: day with { ChargedMicroUsd = SaturatingAdd(day.ChargedMicroUsd, extra) },
                Month: month with { ChargedMicroUsd = SaturatingAdd(month.ChargedMicroUsd, extra) },
                Reservation: exceeded with { ChargedMicroUsd = actual }));
    }

    // --------------------------------------------------------------------------------------- settle

    public static AccountingDecision Settle(
        ModelControlPolicy policy,
        DateTimeOffset now,
        LedgerRoot? root,
        ReservationRecord? record,
        PeriodCounter? day,
        PeriodCounter? month)
    {
        if (record is null)
        {
            return Outcome(ModelControlOutcome.NotFound);
        }

        if (root is null || !IsSane(root) || !IsSane(record))
        {
            return Outcome(ModelControlOutcome.StateInvalid);
        }

        if (record.State != ReservationState.Active || record.CallInFlight || record.CallsStarted < 1 ||
            !root.Permits.TryGetValue(record.Id, out var leaseEnd) || leaseEnd <= now)
        {
            return Outcome(ModelControlOutcome.InvalidState);
        }

        var permits = WithoutPermit(root, record.Id);
        var tariff = policy.Tariff;
        if (!policy.TryValidate(now, out _) ||
            tariff is null ||
            !string.Equals(tariff.Version, record.TariffVersion, StringComparison.Ordinal) ||
            !tariff.TryCost(record.InputTokensUsed, record.OutputTokensUsed, out var actual))
        {
            // The turn is over but cannot be priced: keep the reserved charge, free the permit.
            return new AccountingDecision(
                ModelControlOutcome.InvalidTariffOrPolicy,
                new LedgerWrites(
                    Root: root with { Permits = permits },
                    Reservation: record with { State = ReservationState.Settled, UpdatedAt = now }));
        }

        var settled = record with { State = ReservationState.Settled, ChargedMicroUsd = actual, UpdatedAt = now };
        if (!TryReconcile(record, actual, day, month, out var newDay, out var newMonth))
        {
            return Outcome(ModelControlOutcome.StateInvalid);
        }

        return new AccountingDecision(
            ModelControlOutcome.Applied,
            new LedgerWrites(root with { Permits = permits }, newDay, newMonth, settled));
    }

    // ------------------------------------------------------------------------------ cancel / abandon

    public static AccountingDecision CancelUndispatched(
        DateTimeOffset now,
        LedgerRoot? root,
        ReservationRecord? record,
        PeriodCounter? day,
        PeriodCounter? month)
    {
        if (record is null)
        {
            return Outcome(ModelControlOutcome.NotFound);
        }

        if (root is null || !IsSane(root) || !IsSane(record))
        {
            return Outcome(ModelControlOutcome.StateInvalid);
        }

        if (record.State != ReservationState.Active || record.CallsStarted != 0 || record.CallInFlight)
        {
            return Outcome(ModelControlOutcome.InvalidState);
        }

        if (!TryReconcile(record, 0, day, month, out var newDay, out var newMonth))
        {
            return Outcome(ModelControlOutcome.StateInvalid);
        }

        return new AccountingDecision(
            ModelControlOutcome.Applied,
            new LedgerWrites(
                root with { Permits = WithoutPermit(root, record.Id) },
                newDay,
                newMonth,
                record with { State = ReservationState.Cancelled, ChargedMicroUsd = 0, UpdatedAt = now }));
    }

    public static AccountingDecision Abandon(DateTimeOffset now, ReservationRecord? record)
    {
        if (record is null)
        {
            return Outcome(ModelControlOutcome.NotFound);
        }

        if (!IsSane(record))
        {
            return Outcome(ModelControlOutcome.StateInvalid);
        }

        return record.State switch
        {
            ReservationState.Active => Uncertain(record, now, ModelControlOutcome.Applied),
            ReservationState.Uncertain => Outcome(ModelControlOutcome.Applied),
            _ => Outcome(ModelControlOutcome.InvalidState)
        };
    }

    // -------------------------------------------------------------------------------------- sanity

    public static bool IsSane(LedgerRoot? root) =>
        root is not null &&
        root.SchemaVersion == LedgerRoot.CurrentSchemaVersion &&
        !string.IsNullOrEmpty(root.PolicyVersion) &&
        (root.LastDayKey.Length == 0 || ModelControlPeriods.IsDayKey(root.LastDayKey)) &&
        (root.LastMonthKey.Length == 0 || ModelControlPeriods.IsMonthKey(root.LastMonthKey)) &&
        root.LiveReservations >= 0 &&
        root.Permits.Count <= ModelControlPolicy.ApprovedMaxActivePermits &&
        root.Permits.Keys.All(IsValidReservationId);

    public static bool IsSane(PeriodCounter? counter) =>
        counter is not null && counter.ChargedMicroUsd is >= 0 and <= MaxSaneCharge;

    public static bool IsSane(ReservationRecord? record) =>
        record is not null &&
        IsValidReservationId(record.Id) &&
        ModelControlPeriods.IsDayKey(record.DayKey) &&
        ModelControlPeriods.IsMonthKey(record.MonthKey) &&
        record.DayKey.StartsWith(record.MonthKey, StringComparison.Ordinal) &&
        record.ReservedMicroUsd is > 0 and <= MaxSaneCharge &&
        record.ChargedMicroUsd is >= 0 and <= MaxSaneCharge &&
        record.MaxCalls is >= 1 and <= ModelControlPolicy.ApprovedMaxCalls &&
        record.MaxInputTokens is >= 1 and <= ModelControlPolicy.ApprovedMaxInputTokens &&
        record.MaxOutputTokens is >= 1 and <= ModelControlPolicy.ApprovedMaxOutputTokens &&
        record.CallsStarted >= 0 && record.CallsStarted <= record.MaxCalls &&
        record.InputTokensUsed is >= 0 and <= MaxReportedTokens * 4 &&
        record.OutputTokensUsed is >= 0 and <= MaxReportedTokens * 4 &&
        record.PendingInputTokens >= 0 && record.PendingOutputTokens >= 0 &&
        (record.CallInFlight || (record.PendingInputTokens == 0 && record.PendingOutputTokens == 0));

    // --------------------------------------------------------------------------------------- helpers

    private static ReserveDecision Deny(ModelReservationDenialReason reason, TimeSpan? retryAfter = null) =>
        new(reason, retryAfter, null);

    private static AccountingDecision Outcome(ModelControlOutcome outcome) => new(outcome, null);

    private static AccountingDecision Uncertain(ReservationRecord record, DateTimeOffset now, ModelControlOutcome outcome) =>
        new(outcome, new LedgerWrites(Reservation: record with { State = ReservationState.Uncertain, UpdatedAt = now }));

    private static IReadOnlyDictionary<string, DateTimeOffset> WithoutPermit(LedgerRoot root, string id)
    {
        var permits = new Dictionary<string, DateTimeOffset>(root.Permits, StringComparer.Ordinal);
        permits.Remove(id);
        return permits;
    }

    // A missing counter is legitimate only for a period newer than the recorded high-water mark.
    // A missing counter for the period the root last reserved in means state was lost: deny.
    private static bool TryCharged(PeriodCounter? counter, string key, string lastKey, out long charged)
    {
        charged = 0;
        if (counter is null)
        {
            return lastKey.Length == 0 || string.CompareOrdinal(key, lastKey) > 0;
        }

        if (!IsSane(counter) || !string.Equals(counter.Key, key, StringComparison.Ordinal))
        {
            return false;
        }

        charged = counter.ChargedMicroUsd;
        return true;
    }

    // Moves a reservation's charge to newCharge inside the counters of its OWN day and month.
    // Lowering a counter below zero means the stored state is wrong, so nothing is changed.
    private static bool TryReconcile(
        ReservationRecord record,
        long newCharge,
        PeriodCounter? day,
        PeriodCounter? month,
        out PeriodCounter? newDay,
        out PeriodCounter? newMonth)
    {
        newDay = newMonth = null;
        if (!IsSane(day) || !IsSane(month) ||
            !string.Equals(day!.Key, record.DayKey, StringComparison.Ordinal) ||
            !string.Equals(month!.Key, record.MonthKey, StringComparison.Ordinal))
        {
            return false;
        }

        if (newCharge < record.ChargedMicroUsd)
        {
            var refund = record.ChargedMicroUsd - newCharge;
            if (day.ChargedMicroUsd < refund || month.ChargedMicroUsd < refund)
            {
                return false;
            }

            newDay = day with { ChargedMicroUsd = day.ChargedMicroUsd - refund };
            newMonth = month with { ChargedMicroUsd = month.ChargedMicroUsd - refund };
        }
        else if (newCharge > record.ChargedMicroUsd)
        {
            var extra = newCharge - record.ChargedMicroUsd;
            newDay = day with { ChargedMicroUsd = SaturatingAdd(day.ChargedMicroUsd, extra) };
            newMonth = month with { ChargedMicroUsd = SaturatingAdd(month.ChargedMicroUsd, extra) };
        }

        return true;
    }

    private static long SaturatingAdd(long a, long b) => a > long.MaxValue - b ? long.MaxValue : a + b;

    private static TimeSpan Clamp(TimeSpan value, TimeSpan minimum) =>
        value < minimum ? minimum : value > TimeSpan.FromDays(32) ? TimeSpan.FromDays(32) : value;

    [GeneratedRegex(@"^[A-Za-z0-9_-]{8,64}\z")]
    private static partial Regex ReservationIdPattern();
}
