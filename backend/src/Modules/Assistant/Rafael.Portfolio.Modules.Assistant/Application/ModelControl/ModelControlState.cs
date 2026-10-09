using System.Globalization;
using System.Text.RegularExpressions;

namespace Rafael.Portfolio.Modules.Assistant.Application;

// Bounded operational accounting metadata only. Nothing here may carry a visitor address or
// country, a prompt, an answer, contact data, a CV, a tool payload or a transcript
// (docs/data-handling.md, "Durable operational record").

public enum ReservationState
{
    /// <summary>Holds a permit; calls may still be started within the per-turn allowances.</summary>
    Active,

    /// <summary>Confirmed usage was recorded; the charge was reconciled within its own periods.</summary>
    Settled,

    /// <summary>Cancelled before any provider call began; the whole reservation was refunded.</summary>
    Cancelled,

    /// <summary>
    /// The lease ended while no provider call was in flight (none began, or every started call
    /// reported its usage). Nothing can still be running for it, so the permit was freed; the
    /// reserved charge stays. This is the only state a lease can move a reservation into.
    /// </summary>
    Lapsed,

    /// <summary>
    /// The outcome is unknown (timeout, disconnect, invalid usage, a call that may still be
    /// running). The reserved charge stays in the counters, the record is kept as evidence and
    /// is never refunded or deleted automatically; only an explicit reconciliation resolves it.
    /// </summary>
    Uncertain
}

public static class ReservationStates
{
    /// <summary>
    /// Resolved obligations need no further action and may expire. Active and Uncertain
    /// reservations are unresolved: their records are evidence and are never given an expiry.
    /// </summary>
    public static bool IsResolved(ReservationState state) =>
        state is ReservationState.Settled or ReservationState.Cancelled or ReservationState.Lapsed;
}

/// <summary>What an operator concluded after checking the provider side of an unresolved reservation.</summary>
public enum ReconciliationResolution
{
    /// <summary>
    /// The PENDING call (started and not completed, or a turn that never started a call) is
    /// confirmed never to have run. Usage already confirmed by earlier completed calls is kept
    /// and priced; only the remainder is refunded, and the whole charge only when no call
    /// completed.
    /// </summary>
    NotDispatched,

    /// <summary>The provider confirmed the pending call completed with these usage figures: added to earlier usage, the charge becomes its tariff cost.</summary>
    Completed,

    /// <summary>The usage cannot be established: the reserved charge is accepted as final.</summary>
    ChargeAsReserved
}

/// <summary>An unresolved reservation, with accounting metadata only.</summary>
public sealed record UnresolvedReservation(
    string Id,
    ReservationState State,
    string DayKey,
    string MonthKey,
    int CallsStarted,
    bool CallInFlight,
    long ReservedMicroUsd,
    long ChargedMicroUsd,
    DateTimeOffset UpdatedAt);

/// <summary>The single control document: initialization marker, high-water marks and permit set.</summary>
public sealed record LedgerRoot(
    int SchemaVersion,
    string PolicyVersion,
    string LastDayKey,
    string LastMonthKey,
    IReadOnlyDictionary<string, DateTimeOffset> Permits,
    int LiveReservations,
    long Epoch = 0)
{
    public const int CurrentSchemaVersion = 1;

    /// <summary>The only schema version this code interprets. Anything else is rejected, never coerced.</summary>
    public const long SupportedSchemaVersion = 1;
}

/// <summary>Charged micro-USD for one UTC day or month, tied to that period's key.</summary>
public sealed record PeriodCounter(string Key, long ChargedMicroUsd, DateTimeOffset ExpiresAt);

public sealed record ReservationRecord(
    string Id,
    ReservationState State,
    string PolicyVersion,
    string TariffVersion,
    string ModelId,
    string DayKey,
    string MonthKey,
    long ReservedMicroUsd,
    long ChargedMicroUsd,
    int MaxCalls,
    int MaxInputTokens,
    int MaxOutputTokens,
    int CallsStarted,
    long InputTokensUsed,
    long OutputTokensUsed,
    bool CallInFlight,
    int PendingInputTokens,
    int PendingOutputTokens,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ExpiresAt);

/// <summary>Usage a provider reported for one finished call. Output is every billable output token, reasoning included.</summary>
public readonly record struct ModelUsage(long InputTokens, long OutputTokens);

/// <summary>UTC accounting periods and their physical-retention boundaries.</summary>
public static partial class ModelControlPeriods
{
    /// <summary>Counters and reservation metadata are kept 40 days after their period ends.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(40);

    public static string DayKey(DateTimeOffset instant) =>
        instant.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string MonthKey(DateTimeOffset instant) =>
        instant.UtcDateTime.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    public static bool IsDayKey(string? key) => key is not null && DayPattern().IsMatch(key) &&
        DateOnly.TryParseExact(key, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    public static bool IsMonthKey(string? key) => key is not null && MonthPattern().IsMatch(key) &&
        DateOnly.TryParseExact(key + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    public static DateTimeOffset DayEnd(string dayKey) =>
        new DateTimeOffset(DateOnly.ParseExact(dayKey, "yyyy-MM-dd", CultureInfo.InvariantCulture).AddDays(1)
            .ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

    public static DateTimeOffset MonthEnd(string monthKey) =>
        new DateTimeOffset(DateOnly.ParseExact(monthKey + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture).AddMonths(1)
            .ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

    public static DateTimeOffset DayExpiry(string dayKey) => DayEnd(dayKey) + Retention;

    public static DateTimeOffset MonthExpiry(string monthKey) => MonthEnd(monthKey) + Retention;

    /// <summary>
    /// When a RESOLVED reservation may expire: 40 days after the end of its month, or 40 days
    /// after it was resolved if that is later, so the evidence of a late reconciliation is kept.
    /// Unresolved reservations have no expiry at all.
    /// </summary>
    public static DateTimeOffset ResolvedExpiry(string monthKey, DateTimeOffset resolvedAt)
    {
        var byPeriod = MonthExpiry(monthKey);
        var byResolution = resolvedAt + Retention;
        return byPeriod > byResolution ? byPeriod : byResolution;
    }

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}\z")]
    private static partial Regex DayPattern();

    [GeneratedRegex(@"^\d{4}-\d{2}\z")]
    private static partial Regex MonthPattern();
}
