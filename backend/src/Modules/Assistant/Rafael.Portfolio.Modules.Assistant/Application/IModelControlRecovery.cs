namespace Rafael.Portfolio.Modules.Assistant.Application;

public enum CapacityReconciliation
{
    /// <summary>The live-reservation count now equals the observed total.</summary>
    Applied,

    /// <summary>An admission or cleanup happened while counting, so nothing was changed. Try again.</summary>
    ConcurrentChange,

    /// <summary>The control document failed validation; nothing was changed.</summary>
    StateInvalid,

    StoreUnavailable
}

/// <summary>
/// Explicit, operator-driven recovery for the model control ledger. Nothing on a request path
/// calls it, no timer calls it, and it is not exposed over HTTP. A timeout, a disconnect or a
/// lease that ended is never a reason to reuse a permit whose provider call may still be running;
/// only <see cref="ReconcileAsync"/> after checking the provider side releases it.
/// </summary>
public interface IModelControlRecovery
{
    /// <summary>Lists reservations that are still unresolved (Active or Uncertain), accounting metadata only.</summary>
    ValueTask<IReadOnlyList<UnresolvedReservation>> ListUnresolvedAsync(
        int maxItems = 50,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves one unresolved reservation and releases its permit. For
    /// <see cref="ReconciliationResolution.Completed"/>, <paramref name="confirmedUsage"/> is the
    /// provider-confirmed usage of the call that was in flight.
    /// </summary>
    ValueTask<ModelControlOutcome> ReconcileAsync(
        string reservationId,
        ReconciliationResolution resolution,
        ModelUsage? confirmedUsage = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the live-reservation count from an observed total, only if no admission or cleanup
    /// changed the control document while counting. A stale count is never written.
    /// </summary>
    ValueTask<CapacityReconciliation> ReconcileCapacityAsync(CancellationToken cancellationToken = default);
}
