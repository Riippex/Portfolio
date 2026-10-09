using Google.Cloud.Firestore;
using Rafael.Portfolio.Modules.Assistant.Application;
using static Rafael.Portfolio.Modules.Assistant.Infrastructure.FirestoreDocuments;

namespace Rafael.Portfolio.Modules.Assistant.Infrastructure;

/// <summary>
/// The backend-owned control ledger on Cloud Firestore. Every operation is one bounded
/// transaction: it reads the control document, the reservation and the period counters it
/// needs, applies the pure rules in <c>ModelControlAccounting</c>, and writes the result, so
/// concurrent replicas and restarts see one consistent allowance. The database is shared by
/// all stages (see docs/runbooks/model-control.md); this class never creates it.
/// </summary>
/// <remarks>
/// Stored data is operational accounting metadata only: opaque reservation ids, UTC period
/// keys, integer micro-USD and token counts, policy/tariff/model versions, states and
/// timestamps. It holds no visitor address or country, prompt, answer, contact data, CV, tool
/// payload or transcript. Any failure (outage, contention beyond the retry bound, missing or
/// corrupt state) denies; nothing here falls back to a fresh allowance.
/// </remarks>
public sealed class FirestoreModelControlLedger : IModelControlLedger, IModelControlRecovery
{
    public const string ControlCollection = "model_control";
    public const string ControlDocument = "state";
    public const string PeriodsCollection = "model_control_periods";
    public const string ReservationsCollection = "model_control_reservations";
    public const int MaxTransactionAttempts = 5;

    /// <summary>Upper bound for one ledger operation, retries included. A store that does not answer in time denies.</summary>
    public static readonly TimeSpan DefaultOperationTimeout = TimeSpan.FromSeconds(10);

    private readonly Lazy<FirestoreDb> _lazyDb;
    private readonly ModelControlPolicy _policy;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _operationTimeout;
    private readonly Func<CancellationToken, Task>? _afterCapacityCount;
    private readonly Lock _denialLock = new();
    private (ModelReservationDenialReason Reason, TimeSpan? RetryAfter, DateTimeOffset Until)? _recentDenial;

    public FirestoreModelControlLedger(
        FirestoreDb db,
        ModelControlPolicy policy,
        TimeProvider? timeProvider = null,
        TimeSpan? operationTimeout = null,
        Func<CancellationToken, Task>? afterCapacityCount = null)
        : this(new Lazy<FirestoreDb>(db ?? throw new ArgumentNullException(nameof(db))), policy, timeProvider, operationTimeout, afterCapacityCount)
    {
    }

    /// <summary>
    /// The client is created on first use, so a host without usable credentials still starts and
    /// every call fails closed as StoreUnavailable instead of preventing startup.
    /// </summary>
    public FirestoreModelControlLedger(
        Lazy<FirestoreDb> db,
        ModelControlPolicy policy,
        TimeProvider? timeProvider = null,
        TimeSpan? operationTimeout = null,
        Func<CancellationToken, Task>? afterCapacityCount = null)
    {
        // The hook runs between counting and the guarded write of ReconcileCapacityAsync. It exists
        // so a test can interleave an admission at exactly that point; production passes null.
        _afterCapacityCount = afterCapacityCount;
        _operationTimeout = operationTimeout is { } timeout && timeout > TimeSpan.Zero ? timeout : DefaultOperationTimeout;
        _lazyDb = db ?? throw new ArgumentNullException(nameof(db));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    private FirestoreDb _db => _lazyDb.Value;

    private CancellationTokenSource Bounded(CancellationToken callerToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        source.CancelAfter(_operationTimeout);
        return source;
    }

    private DocumentReference RootRef => _db.Collection(ControlCollection).Document(ControlDocument);

    private DocumentReference DayRef(string key) => _db.Collection(PeriodsCollection).Document("day_" + key);

    private DocumentReference MonthRef(string key) => _db.Collection(PeriodsCollection).Document("month_" + key);

    private DocumentReference ReservationRef(string id) => _db.Collection(ReservationsCollection).Document(id);

    private static TransactionOptions Options => TransactionOptions.ForMaxAttempts(MaxTransactionAttempts);

    // ------------------------------------------------------------------------------ initialization

    /// <summary>
    /// Controlled one-time initialization of the control document. It never overwrites an
    /// existing document and is never part of the request path: a missing document makes every
    /// reservation fail with StoreNotInitialized instead of silently starting from zero.
    /// </summary>
    public async Task<bool> InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (!_policy.TryValidate(_timeProvider.GetUtcNow(), out _))
        {
            throw new InvalidOperationException("The model control policy is invalid; the store was not initialized.");
        }

        return await _db.RunTransactionAsync(async tx =>
        {
            var snapshot = await tx.GetSnapshotAsync(RootRef, cancellationToken);
            if (snapshot.Exists)
            {
                return false;
            }

            tx.Create(
                RootRef,
                ToFields(new LedgerRoot(
                    LedgerRoot.CurrentSchemaVersion,
                    _policy.Version,
                    string.Empty,
                    string.Empty,
                    new Dictionary<string, DateTimeOffset>(),
                    0)));
            return true;
        }, Options, cancellationToken);
    }

    // ---------------------------------------------------------------------------------------- reserve

    public async ValueTask<ModelReservationResult> TryReserveAsync(string reservationId, CancellationToken cancellationToken = default)
    {
        var result = await ReserveOnceAsync(reservationId, cancellationToken);
        if (result.DenialReason == ModelReservationDenialReason.CapacityExhausted)
        {
            // Physical TTL deletion is asynchronous, so reclaim logically expired metadata once.
            await CleanupExpiredAsync(20, cancellationToken);
            ForgetDenial(); // the store changed under it: ask the store again, once
            result = await ReserveOnceAsync(reservationId, cancellationToken);
        }

        return result;
    }

    // A reservation denied for budget, permits, capacity or an unavailable store is denied again
    // from memory for a few seconds. This only ever refuses: it never grants, extends or records
    // spending, and it keeps a burst of doomed requests from queueing read-write transactions on
    // the one control document (which, with Firestore's locking, stalls everyone).
    private static TimeSpan? DenialMemory(ModelReservationDenialReason reason) => reason switch
    {
        ModelReservationDenialReason.ConcurrencyLimitReached => TimeSpan.FromSeconds(1),
        ModelReservationDenialReason.StoreUnavailable => TimeSpan.FromSeconds(1),
        ModelReservationDenialReason.DailyBudgetExhausted => TimeSpan.FromSeconds(5),
        ModelReservationDenialReason.MonthlyBudgetExhausted => TimeSpan.FromSeconds(5),
        ModelReservationDenialReason.CapacityExhausted => TimeSpan.FromSeconds(5),
        _ => null
    };

    private ModelReservationResult? RecentDenial()
    {
        lock (_denialLock)
        {
            return _recentDenial is { } denial && denial.Until > _timeProvider.GetUtcNow()
                ? ModelReservationResult.Denied(denial.Reason, denial.RetryAfter)
                : null;
        }
    }

    private void ForgetDenial()
    {
        lock (_denialLock)
        {
            _recentDenial = null;
        }
    }

    private ModelReservationResult Remember(ModelReservationResult result)
    {
        if (!result.Success && result.DenialReason is { } reason && DenialMemory(reason) is { } memory)
        {
            lock (_denialLock)
            {
                _recentDenial = (reason, result.RetryAfter, _timeProvider.GetUtcNow() + memory);
            }
        }

        return result;
    }

    private async Task<ModelReservationResult> ReserveOnceAsync(string reservationId, CancellationToken callerToken)
    {
        if (RecentDenial() is { } remembered && ModelControlAccounting.IsValidReservationId(reservationId))
        {
            return remembered;
        }

        using var bounded = Bounded(callerToken);
        var cancellationToken = bounded.Token;
        if (!ModelControlAccounting.IsValidReservationId(reservationId))
        {
            return ModelReservationResult.Denied(ModelReservationDenialReason.InvalidRequest);
        }

        // An unusable policy or tariff is decided before the store is touched.
        if (!_policy.TryValidate(_timeProvider.GetUtcNow(), out _))
        {
            return ModelReservationResult.Denied(ModelReservationDenialReason.InvalidTariffOrPolicy);
        }

        try
        {
            return Remember(await _db.RunTransactionAsync(async tx =>
            {
                var now = _timeProvider.GetUtcNow();
                var dayKey = ModelControlPeriods.DayKey(now);
                var monthKey = ModelControlPeriods.MonthKey(now);

                var root = await ReadRootAsync(tx, cancellationToken);
                var day = await ReadCounterAsync(tx, DayRef(dayKey), "day", cancellationToken);
                var month = await ReadCounterAsync(tx, MonthRef(monthKey), "month", cancellationToken);
                var existing = await ReadReservationAsync(tx, reservationId, cancellationToken);

                var holders = new Dictionary<string, ReservationRecord?>(StringComparer.Ordinal);
                if (root is not null && ModelControlAccounting.IsSane(root))
                {
                    foreach (var (holderId, leaseEnd) in root.Permits)
                    {
                        if (leaseEnd <= now)
                        {
                            holders[holderId] = await ReadReservationAsync(tx, holderId, cancellationToken);
                        }
                    }
                }

                var decision = ModelControlAccounting.Reserve(_policy, now, reservationId, root, day, month, existing, holders);
                if (!decision.Granted)
                {
                    return ModelReservationResult.Denied(decision.Denial!.Value, decision.RetryAfter);
                }

                Write(tx, decision.Writes!, createReservation: true);
                return ModelReservationResult.Granted(reservationId);
            }, Options, cancellationToken));
        }
        catch (StoredStateInvalidException)
        {
            return ModelReservationResult.Denied(ModelReservationDenialReason.StateInvalid);
        }
        catch (Exception) when (!callerToken.IsCancellationRequested)
        {
            return Remember(ModelReservationResult.Denied(ModelReservationDenialReason.StoreUnavailable));
        }
    }

    // ------------------------------------------------------------------------------------- begin call

    public async ValueTask<ModelCallResult> TryBeginCallAsync(
        string reservationId,
        int estimatedInputTokens,
        int maxOutputTokens,
        CancellationToken callerToken = default)
    {
        using var bounded = Bounded(callerToken);
        var cancellationToken = bounded.Token;
        if (!ModelControlAccounting.IsValidReservationId(reservationId))
        {
            return ModelCallResult.Denied(ModelCallDenialReason.InvalidRequest);
        }

        if (!_policy.TryValidate(_timeProvider.GetUtcNow(), out _))
        {
            return ModelCallResult.Denied(ModelCallDenialReason.InvalidTariffOrPolicy);
        }

        try
        {
            return await _db.RunTransactionAsync(async tx =>
            {
                var now = _timeProvider.GetUtcNow();
                var root = await ReadRootAsync(tx, cancellationToken);
                var record = await ReadReservationAsync(tx, reservationId, cancellationToken);

                var decision = ModelControlAccounting.BeginCall(
                    _policy, now, reservationId, root, record, estimatedInputTokens, maxOutputTokens);
                if (!decision.Allowed)
                {
                    return ModelCallResult.Denied(decision.Denial!.Value);
                }

                tx.Set(ReservationRef(reservationId), ToFields(decision.Updated!));
                return ModelCallResult.Granted();
            }, Options, cancellationToken);
        }
        catch (StoredStateInvalidException)
        {
            return ModelCallResult.Denied(ModelCallDenialReason.StateInvalid);
        }
        catch (Exception) when (!callerToken.IsCancellationRequested)
        {
            return ModelCallResult.Denied(ModelCallDenialReason.StoreUnavailable);
        }
    }

    // ------------------------------------------------------------------------- complete / settle / etc.

    public ValueTask<ModelControlOutcome> CompleteCallAsync(string reservationId, ModelUsage usage, CancellationToken cancellationToken = default) =>
        MutateAsync(reservationId, needsRoot: false, cancellationToken, (now, root, record, day, month) =>
            ModelControlAccounting.CompleteCall(_policy, now, record, day, month, usage));

    public ValueTask<ModelControlOutcome> SettleAsync(string reservationId, CancellationToken cancellationToken = default) =>
        MutateAsync(reservationId, needsRoot: true, cancellationToken, (now, root, record, day, month) =>
            ModelControlAccounting.Settle(_policy, now, root, record, day, month));

    public ValueTask<ModelControlOutcome> CancelUndispatchedAsync(string reservationId, CancellationToken cancellationToken = default) =>
        MutateAsync(reservationId, needsRoot: true, cancellationToken, (now, root, record, day, month) =>
            ModelControlAccounting.CancelUndispatched(now, root, record, day, month));

    public ValueTask<ModelControlOutcome> AbandonAsync(string reservationId, CancellationToken cancellationToken = default) =>
        MutateAsync(reservationId, needsRoot: false, cancellationToken, (now, root, record, day, month) =>
            ModelControlAccounting.Abandon(now, record));

    private async ValueTask<ModelControlOutcome> MutateAsync(
        string reservationId,
        bool needsRoot,
        CancellationToken callerToken,
        Func<DateTimeOffset, LedgerRoot?, ReservationRecord?, PeriodCounter?, PeriodCounter?, AccountingDecision> decide)
    {
        using var bounded = Bounded(callerToken);
        var cancellationToken = bounded.Token;
        if (!ModelControlAccounting.IsValidReservationId(reservationId))
        {
            return ModelControlOutcome.NotFound;
        }

        try
        {
            return await _db.RunTransactionAsync(async tx =>
            {
                var now = _timeProvider.GetUtcNow();
                var root = needsRoot ? await ReadRootAsync(tx, cancellationToken) : null;
                var record = await ReadReservationAsync(tx, reservationId, cancellationToken);

                // The reservation's OWN periods, never the current ones: a late settlement
                // must not touch another day's or month's counters.
                PeriodCounter? day = null;
                PeriodCounter? month = null;
                if (record is not null)
                {
                    day = await ReadCounterAsync(tx, DayRef(record.DayKey), "day", cancellationToken);
                    month = await ReadCounterAsync(tx, MonthRef(record.MonthKey), "month", cancellationToken);
                }

                var decision = decide(now, root, record, day, month);
                if (decision.Writes is not null)
                {
                    Write(tx, decision.Writes, createReservation: false);
                }

                return decision.Outcome;
            }, Options, cancellationToken);
        }
        catch (StoredStateInvalidException)
        {
            return ModelControlOutcome.StateInvalid;
        }
        catch (Exception) when (!callerToken.IsCancellationRequested)
        {
            return ModelControlOutcome.StoreUnavailable;
        }
    }

    // ------------------------------------------------------------------------------- cleanup

    /// <summary>
    /// Deletes RESOLVED reservation metadata and counters whose retention has passed, and lowers
    /// the live count to match. A reservation that is Active or Uncertain, or that does not
    /// validate, is evidence of an unresolved obligation and is never deleted here, even if it
    /// somehow carries an expiry. Bounded by <paramref name="maxDocuments"/>.
    /// </summary>
    public async Task<int> CleanupExpiredAsync(int maxDocuments = 50, CancellationToken callerToken = default)
    {
        using var bounded = Bounded(callerToken);
        var cancellationToken = bounded.Token;
        var limit = Math.Clamp(maxDocuments, 1, 100);
        try
        {
            var deletedReservations = await _db.RunTransactionAsync(async tx =>
            {
                var now = _timeProvider.GetUtcNow();
                var cutoff = Timestamp.FromDateTimeOffset(now);
                var expired = await tx.GetSnapshotAsync(
                    _db.Collection(ReservationsCollection).WhereLessThanOrEqualTo("expiresAt", cutoff).Limit(limit),
                    cancellationToken);
                var root = await ReadRootAsync(tx, cancellationToken);

                var removable = new List<DocumentReference>();
                foreach (var document in expired.Documents)
                {
                    if (TryParseReservation(document) is { } record &&
                        ReservationStates.IsResolved(record.State) &&
                        record.ExpiresAt is { } expiry && expiry <= now)
                    {
                        removable.Add(document.Reference);
                    }
                }

                foreach (var reference in removable)
                {
                    tx.Delete(reference);
                }

                if (removable.Count > 0 && root is not null && ModelControlAccounting.IsSane(root) && root.Epoch < long.MaxValue)
                {
                    tx.Set(RootRef, ToFields(root with
                    {
                        LiveReservations = Math.Max(0, root.LiveReservations - removable.Count),
                        Epoch = root.Epoch + 1
                    }));
                }

                return removable.Count;
            }, Options, cancellationToken);

            var deletedCounters = await _db.RunTransactionAsync(async tx =>
            {
                var cutoff = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow());
                var expired = await tx.GetSnapshotAsync(
                    _db.Collection(PeriodsCollection).WhereLessThanOrEqualTo("expiresAt", cutoff).Limit(limit),
                    cancellationToken);
                foreach (var document in expired.Documents)
                {
                    tx.Delete(document.Reference);
                }

                return expired.Count;
            }, Options, cancellationToken);

            return deletedReservations + deletedCounters;
        }
        catch (Exception) when (!callerToken.IsCancellationRequested)
        {
            return 0;
        }
    }

    // ------------------------------------------------------------------------------- recovery

    public async ValueTask<IReadOnlyList<UnresolvedReservation>> ListUnresolvedAsync(
        int maxItems = 50,
        CancellationToken callerToken = default)
    {
        using var bounded = Bounded(callerToken);
        var snapshot = await _db.Collection(ReservationsCollection)
            .WhereIn("state", new object[] { nameof(ReservationState.Active), nameof(ReservationState.Uncertain) })
            .Limit(Math.Clamp(maxItems, 1, 200))
            .GetSnapshotAsync(bounded.Token);

        return snapshot.Documents
            .Select(TryParseReservation)
            .OfType<ReservationRecord>()
            .Select(record => new UnresolvedReservation(
                record.Id, record.State, record.DayKey, record.MonthKey, record.CallsStarted, record.CallInFlight,
                record.ReservedMicroUsd, record.ChargedMicroUsd, record.UpdatedAt))
            .ToList();
    }

    public ValueTask<ModelControlOutcome> ReconcileAsync(
        string reservationId,
        ReconciliationResolution resolution,
        ModelUsage? confirmedUsage = null,
        CancellationToken cancellationToken = default) =>
        MutateAsync(reservationId, needsRoot: true, cancellationToken, (now, root, record, day, month) =>
            ModelControlAccounting.Reconcile(_policy, now, root, record, day, month, resolution, confirmedUsage));

    /// <summary>
    /// Sets the live-reservation count from an observed total after asynchronous TTL deletions.
    /// The count is taken outside a transaction, so it can be stale by the time it is written; the
    /// control document therefore carries an epoch that every admission and every cleanup
    /// advances, and the write happens only if the epoch and count are exactly what they were
    /// before counting. Otherwise nothing is written (ConcurrentChange) and the caller retries.
    /// A deletion that happens while counting can only make the observed total too high, which
    /// is the safe direction.
    /// </summary>
    public async ValueTask<CapacityReconciliation> ReconcileCapacityAsync(CancellationToken callerToken = default)
    {
        using var bounded = Bounded(callerToken);
        var cancellationToken = bounded.Token;
        try
        {
            var snapshot = await RootRef.GetSnapshotAsync(cancellationToken);
            if (!snapshot.Exists)
            {
                return CapacityReconciliation.StateInvalid;
            }

            var before = ParseRoot(snapshot.ToDictionary());
            if (!ModelControlAccounting.IsSane(before) || before.Epoch == long.MaxValue)
            {
                return CapacityReconciliation.StateInvalid;
            }

            var counted = await _db.Collection(ReservationsCollection).Count().GetSnapshotAsync(cancellationToken);
            var observed = (int)Math.Min(counted.Count ?? int.MaxValue, int.MaxValue);

            if (_afterCapacityCount is not null)
            {
                await _afterCapacityCount(cancellationToken);
            }

            return await _db.RunTransactionAsync(async tx =>
            {
                var root = await ReadRootAsync(tx, cancellationToken);
                if (root is null || !ModelControlAccounting.IsSane(root))
                {
                    return CapacityReconciliation.StateInvalid;
                }

                if (root.Epoch != before.Epoch || root.LiveReservations != before.LiveReservations)
                {
                    return CapacityReconciliation.ConcurrentChange;
                }

                tx.Set(RootRef, ToFields(root with { LiveReservations = observed, Epoch = root.Epoch + 1 }));
                return CapacityReconciliation.Applied;
            }, Options, cancellationToken);
        }
        catch (StoredStateInvalidException)
        {
            return CapacityReconciliation.StateInvalid;
        }
        catch (Exception) when (!callerToken.IsCancellationRequested)
        {
            return CapacityReconciliation.StoreUnavailable;
        }
    }

    // ------------------------------------------------------------------------------------- storage I/O

    // All reads of the transaction have happened before this is called.
    private void Write(Transaction tx, LedgerWrites writes, bool createReservation)
    {
        if (writes.Root is not null)
        {
            tx.Set(RootRef, ToFields(writes.Root));
        }

        if (writes.Day is not null)
        {
            tx.Set(DayRef(writes.Day.Key), ToFields(writes.Day, "day"));
        }

        if (writes.Month is not null)
        {
            tx.Set(MonthRef(writes.Month.Key), ToFields(writes.Month, "month"));
        }

        if (writes.Reservation is not null)
        {
            var reference = ReservationRef(writes.Reservation.Id);
            if (createReservation)
            {
                // Create fails if the document exists, a second guard behind the duplicate check.
                tx.Create(reference, ToFields(writes.Reservation));
            }
            else
            {
                tx.Set(reference, ToFields(writes.Reservation));
            }
        }

        foreach (var reclaimed in writes.Reclaimed ?? [])
        {
            tx.Set(ReservationRef(reclaimed.Id), ToFields(reclaimed));
        }
    }

    private async Task<LedgerRoot?> ReadRootAsync(Transaction tx, CancellationToken cancellationToken)
    {
        var snapshot = await tx.GetSnapshotAsync(RootRef, cancellationToken);
        return snapshot.Exists ? ParseRoot(snapshot.ToDictionary()) : null;
    }

    private async Task<PeriodCounter?> ReadCounterAsync(Transaction tx, DocumentReference reference, string kind, CancellationToken cancellationToken)
    {
        var snapshot = await tx.GetSnapshotAsync(reference, cancellationToken);
        return snapshot.Exists ? ParseCounter(snapshot.ToDictionary(), kind) : null;
    }

    private async Task<ReservationRecord?> ReadReservationAsync(Transaction tx, string id, CancellationToken cancellationToken)
    {
        var snapshot = await tx.GetSnapshotAsync(ReservationRef(id), cancellationToken);
        return snapshot.Exists ? ParseReservation(snapshot.Id, snapshot.ToDictionary()) : null;
    }

    private static ReservationRecord? TryParseReservation(DocumentSnapshot snapshot)
    {
        try
        {
            return ParseReservation(snapshot.Id, snapshot.ToDictionary());
        }
        catch (StoredStateInvalidException)
        {
            return null;
        }
    }
}
