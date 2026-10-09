using Rafael.Portfolio.Modules.Assistant.Application;

namespace Rafael.Portfolio.Modules.Assistant.Infrastructure;

/// <summary>
/// Registered whenever no control store is configured or the configuration is unusable. It
/// grants nothing, so paid work stays disabled and the bounded deterministic fallback is used;
/// it never invents a fresh allowance.
/// </summary>
public sealed class UnavailableModelControlLedger : IModelControlLedger
{
    public ValueTask<ModelReservationResult> TryReserveAsync(string reservationId, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(ModelReservationResult.Denied(ModelReservationDenialReason.StoreUnavailable));

    public ValueTask<ModelCallResult> TryBeginCallAsync(
        string reservationId,
        int estimatedInputTokens,
        int maxOutputTokens,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(ModelCallResult.Denied(ModelCallDenialReason.StoreUnavailable));

    public ValueTask<ModelControlOutcome> CompleteCallAsync(string reservationId, ModelUsage usage, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(ModelControlOutcome.StoreUnavailable);

    public ValueTask<ModelControlOutcome> SettleAsync(string reservationId, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(ModelControlOutcome.StoreUnavailable);

    public ValueTask<ModelControlOutcome> CancelUndispatchedAsync(string reservationId, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(ModelControlOutcome.StoreUnavailable);

    public ValueTask<ModelControlOutcome> AbandonAsync(string reservationId, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(ModelControlOutcome.StoreUnavailable);
}
