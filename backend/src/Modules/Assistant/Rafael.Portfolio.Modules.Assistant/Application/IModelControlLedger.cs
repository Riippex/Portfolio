namespace Rafael.Portfolio.Modules.Assistant.Application;

/// <summary>
/// Backend-owned admission and accounting for paid model work. One portfolio-wide allowance
/// (USD 0.10 per UTC day, USD 1.00 per UTC month) and two global permits are shared by every
/// stage, replica and restart. The ledger caps model cost only; it is not a total invoice cap.
/// </summary>
/// <remarks>
/// Lifecycle of a turn: <see cref="TryReserveAsync"/> reserves the whole worst-case turn and a
/// permit; <see cref="TryBeginCallAsync"/> is required before every provider call (at most two,
/// within the cumulative 6,000 input / 600 billable output token allowance);
/// <see cref="CompleteCallAsync"/> records the reported usage; <see cref="SettleAsync"/> reconciles
/// the charge. <see cref="CancelUndispatchedAsync"/> refunds only when no call began.
/// <see cref="AbandonAsync"/> records an unknown outcome: the charge stays and the permit is held
/// until its lease ends. A denied or failing store never grants spending authority.
/// </remarks>
public interface IModelControlLedger
{
    /// <summary>Reserves the full permitted worst-case turn and one permit, atomically.</summary>
    ValueTask<ModelReservationResult> TryReserveAsync(
        string reservationId,
        CancellationToken cancellationToken = default);

    /// <summary>Authorizes exactly one provider call within the reservation's remaining allowance.</summary>
    ValueTask<ModelCallResult> TryBeginCallAsync(
        string reservationId,
        int estimatedInputTokens,
        int maxOutputTokens,
        CancellationToken cancellationToken = default);

    /// <summary>Records the usage a provider reported for the call in flight.</summary>
    ValueTask<ModelControlOutcome> CompleteCallAsync(
        string reservationId,
        ModelUsage usage,
        CancellationToken cancellationToken = default);

    /// <summary>Finishes a turn whose calls all completed, reconciling within the reservation's own periods.</summary>
    ValueTask<ModelControlOutcome> SettleAsync(
        string reservationId,
        CancellationToken cancellationToken = default);

    /// <summary>Refunds a reservation for which no provider call began, and releases its permit.</summary>
    ValueTask<ModelControlOutcome> CancelUndispatchedAsync(
        string reservationId,
        CancellationToken cancellationToken = default);

    /// <summary>Marks the outcome unknown. The charge stays and the permit is held until its lease ends.</summary>
    ValueTask<ModelControlOutcome> AbandonAsync(
        string reservationId,
        CancellationToken cancellationToken = default);
}
