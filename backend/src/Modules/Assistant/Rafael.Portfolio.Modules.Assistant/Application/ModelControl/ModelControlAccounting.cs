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

        // A saturated epoch could no longer be advanced, so it is treated as corrupt.
        if (!IsSane(root) || root.Epoch == long.MaxValue)
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

        // A lease that ended is NOT proof that the provider finished: a remote call can outlive
        // its caller. So an ended lease frees a permit only when nothing can still be running
        // for it (no call in flight). A permit whose reservation has a call in flight, or whose
        // reservation cannot be found or read, stays held and the reservation becomes
        // Uncertain; only an explicit reconciliation (Reconcile) releases it. The charge is
        // never touched here.
        var permits = new Dictionary<string, DateTimeOffset>(root.Permits, StringComparer.Ordinal);
        var reclaimed = new List<ReservationRecord>();
        foreach (var (holderId, leaseEnd) in root.Permits)
        {
            if (leaseEnd > now)
            {
                continue;
            }

            if (!permitHolders.TryGetValue(holderId, out var holder) || holder is null || !IsSane(holder))
            {
                continue; // unknown or unreadable: fail closed, keep the permit
            }

            if (holder.CallInFlight)
            {
                if (holder.State == ReservationState.Active)
                {
                    reclaimed.Add(holder with { State = ReservationState.Uncertain, UpdatedAt = now });
                }

                continue;
            }

            permits.Remove(holderId);
            if (holder.State == ReservationState.Active)
            {
                reclaimed.Add(Resolve(holder, ReservationState.Lapsed, holder.ChargedMicroUsd, now));
            }
        }

        if (permits.Count >= policy.MaxActivePermits)
        {
            // Only a live lease ends on its own; permits held for unresolved calls wait for reconciliation.
            var live = permits.Values.Where(lease => lease > now).ToList();
            var wait = live.Count > 0 ? live.Min() - now : TimeSpan.FromMinutes(5);
            return Deny(ModelReservationDenialReason.ConcurrencyLimitReached, Clamp(wait, TimeSpan.FromSeconds(1)));
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
            ExpiresAt: null);

        return new ReserveDecision(
            null,
            null,
            new LedgerWrites(
                root with
                {
                    LastDayKey = dayKey,
                    LastMonthKey = monthKey,
                    Permits = permits,
                    LiveReservations = root.LiveReservations + 1,
                    Epoch = root.Epoch + 1
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
        // Without a price or readable counters the charge cannot be finalized: the reservation
        // stays Uncertain and unresolved. With them it is settled at its larger actual cost.
        if (!policy.TryValidate(now, out _) ||
            tariff is null ||
            !string.Equals(tariff.Version, record.TariffVersion, StringComparison.Ordinal) ||
            !tariff.TryCost(inputUsed, outputUsed, out var actual))
        {
            return new AccountingDecision(ModelControlOutcome.AllowanceExceeded, new LedgerWrites(Reservation: exceeded));
        }

        if (actual <= record.ChargedMicroUsd)
        {
            return new AccountingDecision(
                ModelControlOutcome.AllowanceExceeded,
                new LedgerWrites(Reservation: Resolve(completed, ReservationState.Settled, record.ChargedMicroUsd, now)));
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
                Reservation: Resolve(completed, ReservationState.Settled, actual, now)));
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
                    Reservation: Resolve(record, ReservationState.Settled, record.ChargedMicroUsd, now)));
        }

        var settled = Resolve(record, ReservationState.Settled, actual, now);
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
                Resolve(record, ReservationState.Cancelled, 0, now)));
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

    // ----------------------------------------------------------------------------- reconciliation

    /// <summary>
    /// Resolves an unresolved reservation (Active or Uncertain) after an operator checked the
    /// provider side. This is the only way a permit held for an unresolved call is released and
    /// the only way an Uncertain charge changes. The adjustment is applied to the reservation's
    /// OWN day and month counters, each judged independently: a counter that is missing because
    /// its closed period has already expired is skipped (it no longer affects any admission) and
    /// never recreated, while the other counter is still adjusted. A missing counter whose period
    /// has not expired, or a corrupt or mismatched one, changes nothing at all.
    ///
    /// Scope of each resolution:
    /// <list type="bullet">
    /// <item>NotDispatched concerns only the PENDING call (the one started and not completed, or
    /// the reservation's never-started turn). Usage already confirmed by earlier completed calls
    /// is never erased: it is retained and priced with the reservation's tariff, so only the
    /// unused remainder is refunded. With no completed call the whole charge is refunded. It is
    /// refused when there is no pending call to disown while earlier calls completed.</item>
    /// <item>Completed adds the provider-confirmed usage of the pending call to earlier usage.</item>
    /// <item>ChargeAsReserved accepts the reserved charge as final.</item>
    /// </list>
    /// </summary>
    public static AccountingDecision Reconcile(
        ModelControlPolicy policy,
        DateTimeOffset now,
        LedgerRoot? root,
        ReservationRecord? record,
        PeriodCounter? day,
        PeriodCounter? month,
        ReconciliationResolution resolution,
        ModelUsage? confirmedUsage)
    {
        if (record is null)
        {
            return Outcome(ModelControlOutcome.NotFound);
        }

        if (root is null || !IsSane(root) || !IsSane(record))
        {
            return Outcome(ModelControlOutcome.StateInvalid);
        }

        if (ReservationStates.IsResolved(record.State))
        {
            return Outcome(ModelControlOutcome.InvalidState);
        }

        long newCharge;
        switch (resolution)
        {
            case ReconciliationResolution.NotDispatched:
                var scoped = PriceRetainedUsage(policy, now, record, out newCharge);
                if (scoped != ModelControlOutcome.Applied)
                {
                    return Outcome(scoped);
                }

                break;
            case ReconciliationResolution.ChargeAsReserved:
                newCharge = record.ChargedMicroUsd;
                break;
            case ReconciliationResolution.Completed:
                var priced = PriceConfirmedUsage(policy, now, record, confirmedUsage, out newCharge);
                if (priced != ModelControlOutcome.Applied)
                {
                    return Outcome(priced);
                }

                break;

            default:
                return Outcome(ModelControlOutcome.InvalidState);
        }

        // Each original period is judged on its own, even when the charge does not change: a
        // reservation is never resolved on top of counters that are missing, corrupt or for
        // another period. Nothing is written unless both are acceptable.
        PeriodCounter? newDay = null;
        PeriodCounter? newMonth = null;
        var delta = newCharge - record.ChargedMicroUsd;
        if (!TryAdjustCounter(day, record.DayKey, ModelControlPeriods.DayExpiry(record.DayKey), now, delta, out newDay) ||
            !TryAdjustCounter(month, record.MonthKey, ModelControlPeriods.MonthExpiry(record.MonthKey), now, delta, out newMonth))
        {
            return Outcome(ModelControlOutcome.StateInvalid);
        }

        // Nothing was consumed only when the whole charge is refunded.
        var state = newCharge == 0 && resolution == ReconciliationResolution.NotDispatched
            ? ReservationState.Cancelled
            : ReservationState.Settled;
        return new AccountingDecision(
            ModelControlOutcome.Applied,
            new LedgerWrites(
                root with { Permits = WithoutPermit(root, record.Id) },
                newDay,
                newMonth,
                Resolve(record with { CallInFlight = false, PendingInputTokens = 0, PendingOutputTokens = 0 }, state, newCharge, now)));
    }

    // The confirmed figures are the usage of the one call that was in flight; they are added to
    // what earlier calls already reported and priced with the reservation's own tariff.
    private static ModelControlOutcome PriceConfirmedUsage(
        ModelControlPolicy policy,
        DateTimeOffset now,
        ReservationRecord record,
        ModelUsage? confirmedUsage,
        out long charge)
    {
        charge = 0;
        if (!record.CallInFlight)
        {
            return ModelControlOutcome.InvalidState;
        }

        if (confirmedUsage is not { } usage || usage.InputTokens < 1 || usage.OutputTokens < 0 ||
            usage.InputTokens > MaxReportedTokens || usage.OutputTokens > MaxReportedTokens)
        {
            return ModelControlOutcome.InvalidUsage;
        }

        var tariff = policy.Tariff;
        return policy.TryValidate(now, out _) &&
               tariff is not null &&
               string.Equals(tariff.Version, record.TariffVersion, StringComparison.Ordinal) &&
               tariff.TryCost(record.InputTokensUsed + usage.InputTokens, record.OutputTokensUsed + usage.OutputTokens, out charge)
            ? ModelControlOutcome.Applied
            : ModelControlOutcome.InvalidTariffOrPolicy;
    }

    // NotDispatched disowns only the pending call. What earlier completed calls already reported
    // stays charged at the reservation's tariff; with none, the whole charge is refunded.
    private static ModelControlOutcome PriceRetainedUsage(
        ModelControlPolicy policy,
        DateTimeOffset now,
        ReservationRecord record,
        out long charge)
    {
        charge = 0;
        var completedCalls = record.CallsStarted - (record.CallInFlight ? 1 : 0);
        var hasUsage = record.InputTokensUsed > 0 || record.OutputTokensUsed > 0;
        if (completedCalls == 0)
        {
            // Usage without a completed call (or a call that cannot be told apart) is contradictory.
            return hasUsage ? ModelControlOutcome.StateInvalid : ModelControlOutcome.Applied;
        }

        // Earlier calls completed, so there must be a pending call to disown and usage to keep.
        if (!record.CallInFlight)
        {
            return ModelControlOutcome.InvalidState;
        }

        if (!hasUsage)
        {
            return ModelControlOutcome.StateInvalid;
        }

        var tariff = policy.Tariff;
        return policy.TryValidate(now, out _) &&
               tariff is not null &&
               string.Equals(tariff.Version, record.TariffVersion, StringComparison.Ordinal) &&
               tariff.TryCost(record.InputTokensUsed, record.OutputTokensUsed, out charge)
            ? ModelControlOutcome.Applied
            : ModelControlOutcome.InvalidTariffOrPolicy;
    }

    // A resolved reservation keeps its evidence until its (state-aware) expiry; an unresolved
    // one has no expiry.
    private static ReservationRecord Resolve(ReservationRecord record, ReservationState state, long charged, DateTimeOffset now) =>
        record with
        {
            State = state,
            ChargedMicroUsd = charged,
            UpdatedAt = now,
            ExpiresAt = ModelControlPeriods.ResolvedExpiry(record.MonthKey, now)
        };

    // -------------------------------------------------------------------------------------- sanity

    public static bool IsSane(LedgerRoot? root) =>
        root is not null &&
        root.SchemaVersion == LedgerRoot.CurrentSchemaVersion &&
        !string.IsNullOrEmpty(root.PolicyVersion) &&
        (root.LastDayKey.Length == 0 || ModelControlPeriods.IsDayKey(root.LastDayKey)) &&
        (root.LastMonthKey.Length == 0 || ModelControlPeriods.IsMonthKey(root.LastMonthKey)) &&
        root.LiveReservations >= 0 &&
        root.Epoch >= 0 &&
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
        (record.CallInFlight || (record.PendingInputTokens == 0 && record.PendingOutputTokens == 0)) &&
        ReservationStates.IsResolved(record.State) == record.ExpiresAt.HasValue;

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

    // Validates ONE of a reservation's own counters and applies a signed charge change to it (a
    // zero change validates only and writes nothing). A counter that is
    // missing is acceptable only when its period has legitimately expired and been deleted: it is
    // skipped and never recreated. A missing counter that should still exist, a corrupt or
    // mismatched one, or a refund larger than the counter holds, is refused.
    private static bool TryAdjustCounter(
        PeriodCounter? counter,
        string key,
        DateTimeOffset expiry,
        DateTimeOffset now,
        long delta,
        out PeriodCounter? updated)
    {
        updated = null;
        if (counter is null)
        {
            return expiry <= now;
        }

        if (!IsSane(counter) || !string.Equals(counter.Key, key, StringComparison.Ordinal))
        {
            return false;
        }

        if (delta < 0)
        {
            if (counter.ChargedMicroUsd < -delta)
            {
                return false;
            }

            updated = counter with { ChargedMicroUsd = counter.ChargedMicroUsd + delta };
        }
        else if (delta > 0)
        {
            updated = counter with { ChargedMicroUsd = SaturatingAdd(counter.ChargedMicroUsd, delta) };
        }

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
