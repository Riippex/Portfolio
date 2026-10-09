namespace Rafael.Portfolio.Modules.Assistant.Application;

public enum ModelReservationDenialReason
{
    DailyBudgetExhausted,
    MonthlyBudgetExhausted,
    ConcurrencyLimitReached,
    DuplicateReservationId,
    StoreUnavailable,
    InvalidTariffOrPolicy,
    InvalidRequest,
    StoreNotInitialized,
    StateInvalid,
    CapacityExhausted
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

public enum ModelCallDenialReason
{
    NotFound,
    NotActive,
    PermitLost,
    CallInFlight,
    CallLimitReached,
    InputTokenLimitReached,
    OutputTokenLimitReached,
    InvalidRequest,
    InvalidTariffOrPolicy,
    StateInvalid,
    StoreUnavailable
}

public sealed record ModelCallResult(bool Allowed, ModelCallDenialReason? DenialReason = null)
{
    public static ModelCallResult Granted() => new(true);

    public static ModelCallResult Denied(ModelCallDenialReason reason) => new(false, reason);
}

public enum ModelControlOutcome
{
    /// <summary>The change was recorded as requested.</summary>
    Applied,

    NotFound,

    /// <summary>The reservation is not in a state that allows this change.</summary>
    InvalidState,

    /// <summary>Usage was malformed; the reservation became Uncertain and stays fully charged.</summary>
    InvalidUsage,

    /// <summary>Usage exceeded the turn allowance; the larger actual cost was charged and no refund is possible.</summary>
    AllowanceExceeded,

    /// <summary>The tariff or policy could not price the turn; the reserved charge stays and nothing is refunded.</summary>
    InvalidTariffOrPolicy,

    /// <summary>Stored state failed validation; nothing was changed.</summary>
    StateInvalid,

    StoreUnavailable
}
