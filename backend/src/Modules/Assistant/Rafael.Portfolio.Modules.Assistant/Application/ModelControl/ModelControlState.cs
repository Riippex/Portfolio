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
    /// The outcome is unknown (timeout, disconnect, invalid usage, lost permit). The reserved
    /// charge stays in the counters and is never refunded automatically.
    /// </summary>
    Uncertain
}

/// <summary>The single control document: initialization marker, high-water marks and permit set.</summary>
public sealed record LedgerRoot(
    int SchemaVersion,
    string PolicyVersion,
    string LastDayKey,
    string LastMonthKey,
    IReadOnlyDictionary<string, DateTimeOffset> Permits,
    int LiveReservations)
{
    public const int CurrentSchemaVersion = 1;
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
    DateTimeOffset ExpiresAt);

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

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}\z")]
    private static partial Regex DayPattern();

    [GeneratedRegex(@"^\d{4}-\d{2}\z")]
    private static partial Regex MonthPattern();
}
