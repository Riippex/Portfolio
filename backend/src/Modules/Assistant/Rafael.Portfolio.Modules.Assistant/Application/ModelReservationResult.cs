namespace Rafael.Portfolio.Modules.Assistant.Application;

public enum ModelReservationDenialReason
{
    DailyBudgetExhausted,
    MonthlyBudgetExhausted,
    ConcurrencyLimitReached,
    DuplicateReservationId,
    StoreUnavailable,
    InvalidTariffOrPolicy
}

public sealed record ModelReservationResult(
    bool Success,
    string? ReservationId,
    ModelReservationDenialReason? DenialReason = null,
    TimeSpan? RetryAfter = null)
{
    public static ModelReservationResult Granted(string reservationId) =>
        new(true, reservationId);

    public static ModelReservationResult Denied(ModelReservationDenialReason reason, TimeSpan? retryAfter = null) =>
        new(false, null, reason, retryAfter);
}
