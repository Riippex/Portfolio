namespace Rafael.Portfolio.Modules.Assistant.Application;

public interface IModelControlLedger
{
    ValueTask<ModelReservationResult> TryReserveAsync(
        string reservationId,
        long maxEstimatedCostMicroUsd,
        int maxInputTokens = 6000,
        int maxOutputTokens = 600,
        int maxCalls = 2,
        CancellationToken cancellationToken = default);

    ValueTask<bool> CommitAsync(
        string reservationId,
        long actualCostMicroUsd,
        int inputTokensUsed,
        int outputTokensUsed,
        int callsMade,
        CancellationToken cancellationToken = default);

    ValueTask<bool> ReleasePermitAsync(
        string reservationId,
        CancellationToken cancellationToken = default);
}
