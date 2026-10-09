using Rafael.Portfolio.Modules.Assistant.Application;

namespace Rafael.Portfolio.UnitTests;

/// <summary>
/// Test-only ledger. It drives the same pure accounting rules as the Firestore adapter, under a
/// lock instead of a transaction, and exposes its documents so tests can seed corrupt or lost
/// state. It is not part of the production assembly and proves nothing about Firestore
/// concurrency; the emulator tests do that.
/// </summary>
internal sealed class InMemoryModelControlLedger : IModelControlLedger, IModelControlRecovery
{
    private readonly Lock _lock = new();
    private readonly TimeProvider _time;
    private readonly Dictionary<string, PeriodCounter> _periods = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ReservationRecord> _reservations = new(StringComparer.Ordinal);

    public InMemoryModelControlLedger(ModelControlPolicy policy, TimeProvider? time = null, bool initialize = true)
    {
        Policy = policy;
        _time = time ?? TimeProvider.System;
        if (initialize)
        {
            Root = NewRoot(policy.Version);
        }
    }

    public ModelControlPolicy Policy { get; set; }

    public LedgerRoot? Root { get; set; }

    /// <summary>When true every operation reports an outage.</summary>
    public bool StoreDown { get; set; }

    public static LedgerRoot NewRoot(string policyVersion) =>
        new(LedgerRoot.CurrentSchemaVersion, policyVersion, string.Empty, string.Empty, new Dictionary<string, DateTimeOffset>(), 0);

    public long DayCharged(DateTimeOffset instant) => Counter("day_" + ModelControlPeriods.DayKey(instant))?.ChargedMicroUsd ?? 0;

    public long MonthCharged(DateTimeOffset instant) => Counter("month_" + ModelControlPeriods.MonthKey(instant))?.ChargedMicroUsd ?? 0;

    public PeriodCounter? Counter(string documentName) =>
        _periods.TryGetValue(documentName, out var counter) ? counter : null;

    public void SetCounter(string documentName, PeriodCounter? counter)
    {
        lock (_lock)
        {
            if (counter is null)
            {
                _periods.Remove(documentName);
            }
            else
            {
                _periods[documentName] = counter;
            }
        }
    }

    public ReservationRecord? Reservation(string id) =>
        _reservations.TryGetValue(id, out var record) ? record : null;

    public void SetReservation(ReservationRecord record)
    {
        lock (_lock)
        {
            _reservations[record.Id] = record;
        }
    }

    public void RemoveReservation(string id)
    {
        lock (_lock)
        {
            _reservations.Remove(id);
        }
    }

    public int ReservationCount => _reservations.Count;

    public int ActivePermits => Root?.Permits.Count ?? 0;

    public ValueTask<ModelReservationResult> TryReserveAsync(string reservationId, CancellationToken cancellationToken = default)
    {
        if (StoreDown)
        {
            return ValueTask.FromResult(ModelReservationResult.Denied(ModelReservationDenialReason.StoreUnavailable));
        }

        lock (_lock)
        {
            var now = _time.GetUtcNow();
            var dayKey = ModelControlPeriods.DayKey(now);
            var monthKey = ModelControlPeriods.MonthKey(now);
            _reservations.TryGetValue(reservationId ?? string.Empty, out var existing);

            var holders = new Dictionary<string, ReservationRecord?>(StringComparer.Ordinal);
            if (Root is not null)
            {
                foreach (var (holderId, leaseEnd) in Root.Permits)
                {
                    if (leaseEnd <= now)
                    {
                        holders[holderId] = Reservation(holderId);
                    }
                }
            }

            var decision = ModelControlAccounting.Reserve(
                Policy, now, reservationId!, Root, Counter("day_" + dayKey), Counter("month_" + monthKey), existing, holders);
            if (!decision.Granted)
            {
                return ValueTask.FromResult(ModelReservationResult.Denied(decision.Denial!.Value, decision.RetryAfter));
            }

            Apply(decision.Writes!);
            return ValueTask.FromResult(ModelReservationResult.Granted(reservationId!));
        }
    }

    public ValueTask<ModelCallResult> TryBeginCallAsync(
        string reservationId,
        int estimatedInputTokens,
        int maxOutputTokens,
        CancellationToken cancellationToken = default)
    {
        if (StoreDown)
        {
            return ValueTask.FromResult(ModelCallResult.Denied(ModelCallDenialReason.StoreUnavailable));
        }

        lock (_lock)
        {
            var decision = ModelControlAccounting.BeginCall(
                Policy, _time.GetUtcNow(), reservationId, Root, Reservation(reservationId), estimatedInputTokens, maxOutputTokens);
            if (!decision.Allowed)
            {
                return ValueTask.FromResult(ModelCallResult.Denied(decision.Denial!.Value));
            }

            _reservations[reservationId] = decision.Updated!;
            return ValueTask.FromResult(ModelCallResult.Granted());
        }
    }

    public ValueTask<ModelControlOutcome> CompleteCallAsync(string reservationId, ModelUsage usage, CancellationToken cancellationToken = default) =>
        Mutate(reservationId, (now, record, day, month) => ModelControlAccounting.CompleteCall(Policy, now, record, day, month, usage));

    public ValueTask<ModelControlOutcome> SettleAsync(string reservationId, CancellationToken cancellationToken = default) =>
        Mutate(reservationId, (now, record, day, month) => ModelControlAccounting.Settle(Policy, now, Root, record, day, month));

    public ValueTask<ModelControlOutcome> CancelUndispatchedAsync(string reservationId, CancellationToken cancellationToken = default) =>
        Mutate(reservationId, (now, record, day, month) => ModelControlAccounting.CancelUndispatched(now, Root, record, day, month));

    public ValueTask<ModelControlOutcome> AbandonAsync(string reservationId, CancellationToken cancellationToken = default) =>
        Mutate(reservationId, (now, record, _, _) => ModelControlAccounting.Abandon(now, record));

    public ValueTask<ModelControlOutcome> ReconcileAsync(
        string reservationId,
        ReconciliationResolution resolution,
        ModelUsage? confirmedUsage = null,
        CancellationToken cancellationToken = default) =>
        Mutate(reservationId, (now, record, day, month) =>
            ModelControlAccounting.Reconcile(Policy, now, Root, record, day, month, resolution, confirmedUsage));

    public ValueTask<IReadOnlyList<UnresolvedReservation>> ListUnresolvedAsync(int maxItems = 50, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            IReadOnlyList<UnresolvedReservation> list = _reservations.Values
                .Where(record => !ReservationStates.IsResolved(record.State))
                .Take(maxItems)
                .Select(record => new UnresolvedReservation(
                    record.Id, record.State, record.DayKey, record.MonthKey, record.CallsStarted, record.CallInFlight,
                    record.ReservedMicroUsd, record.ChargedMicroUsd, record.UpdatedAt))
                .ToList();
            return ValueTask.FromResult(list);
        }
    }

    public ValueTask<CapacityReconciliation> ReconcileCapacityAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (Root is null || !ModelControlAccounting.IsSane(Root))
            {
                return ValueTask.FromResult(CapacityReconciliation.StateInvalid);
            }

            Root = Root with { LiveReservations = _reservations.Count, Epoch = Root.Epoch + 1 };
            return ValueTask.FromResult(CapacityReconciliation.Applied);
        }
    }

    private ValueTask<ModelControlOutcome> Mutate(
        string reservationId,
        Func<DateTimeOffset, ReservationRecord?, PeriodCounter?, PeriodCounter?, AccountingDecision> decide)
    {
        if (StoreDown)
        {
            return ValueTask.FromResult(ModelControlOutcome.StoreUnavailable);
        }

        lock (_lock)
        {
            var record = Reservation(reservationId);
            var day = record is null ? null : Counter("day_" + record.DayKey);
            var month = record is null ? null : Counter("month_" + record.MonthKey);
            var decision = decide(_time.GetUtcNow(), record, day, month);
            if (decision.Writes is not null)
            {
                Apply(decision.Writes);
            }

            return ValueTask.FromResult(decision.Outcome);
        }
    }

    private void Apply(LedgerWrites writes)
    {
        if (writes.Root is not null)
        {
            Root = writes.Root;
        }

        if (writes.Day is not null)
        {
            _periods["day_" + writes.Day.Key] = writes.Day;
        }

        if (writes.Month is not null)
        {
            _periods["month_" + writes.Month.Key] = writes.Month;
        }

        if (writes.Reservation is not null)
        {
            _reservations[writes.Reservation.Id] = writes.Reservation;
        }

        foreach (var reclaimed in writes.Reclaimed ?? [])
        {
            _reservations[reclaimed.Id] = reclaimed;
        }
    }
}
