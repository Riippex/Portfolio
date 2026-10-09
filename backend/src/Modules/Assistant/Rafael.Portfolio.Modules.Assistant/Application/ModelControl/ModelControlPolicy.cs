using System.Text.RegularExpressions;

namespace Rafael.Portfolio.Modules.Assistant.Application;

/// <summary>
/// The owner-approved limits for paid model work, shared by every stage and replica. A policy
/// may tighten but never loosen the approved ceilings; anything out of range makes the policy
/// invalid and paid admission is denied rather than defaulted.
/// </summary>
public sealed partial record ModelControlPolicy(
    string Version,
    long DailyBudgetMicroUsd,
    long MonthlyBudgetMicroUsd,
    int MaxActivePermits,
    TimeSpan PermitLease,
    int MaxCalls,
    int MaxInputTokens,
    int MaxOutputTokens,
    int MaxLiveReservations,
    ModelTariff? Tariff)
{
    public const long ApprovedDailyBudgetMicroUsd = 100_000; // USD 0.10 per UTC day
    public const long ApprovedMonthlyBudgetMicroUsd = 1_000_000; // USD 1.00 per UTC month
    public const int ApprovedMaxActivePermits = 2;
    public const int ApprovedMaxCalls = 2;
    public const int ApprovedMaxInputTokens = 6_000;
    public const int ApprovedMaxOutputTokens = 600;
    public const int ApprovedMaxLiveReservations = 2_000;
    public static readonly TimeSpan ApprovedPermitLease = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan MinimumPermitLease = TimeSpan.FromSeconds(30);

    /// <summary>The approved policy with the given tariff (which may be absent, denying paid work).</summary>
    public static ModelControlPolicy Approved(string version, ModelTariff? tariff) =>
        new(
            version,
            ApprovedDailyBudgetMicroUsd,
            ApprovedMonthlyBudgetMicroUsd,
            ApprovedMaxActivePermits,
            ApprovedPermitLease,
            ApprovedMaxCalls,
            ApprovedMaxInputTokens,
            ApprovedMaxOutputTokens,
            ApprovedMaxLiveReservations,
            tariff);

    public bool TryValidate(DateTimeOffset now, out string error)
    {
        if (!Identifier().IsMatch(Version ?? string.Empty))
        {
            error = "Policy version is missing or malformed.";
        }
        else if (DailyBudgetMicroUsd is <= 0 or > ApprovedDailyBudgetMicroUsd ||
                 MonthlyBudgetMicroUsd is <= 0 or > ApprovedMonthlyBudgetMicroUsd ||
                 MonthlyBudgetMicroUsd < DailyBudgetMicroUsd)
        {
            error = "Budgets must be positive, within the approved ceilings, and the month must cover a day.";
        }
        else if (MaxActivePermits is < 1 or > ApprovedMaxActivePermits ||
                 MaxCalls is < 1 or > ApprovedMaxCalls ||
                 MaxInputTokens is < 1 or > ApprovedMaxInputTokens ||
                 MaxOutputTokens is < 1 or > ApprovedMaxOutputTokens ||
                 MaxLiveReservations is < 1 or > ApprovedMaxLiveReservations)
        {
            error = "Permit, call, token or capacity limits are outside the approved ceilings.";
        }
        else if (PermitLease < MinimumPermitLease || PermitLease > ApprovedPermitLease)
        {
            error = "Permit lease is outside the approved range.";
        }
        else if (Tariff is null)
        {
            error = "No model tariff is configured.";
        }
        else if (!Tariff.TryValidate(now, out error))
        {
            // error is set by the tariff
        }
        else if (!TryWorstCaseCost(out var worstCase) || worstCase > DailyBudgetMicroUsd)
        {
            error = "A worst-case turn does not fit in the daily budget.";
        }
        else
        {
            error = string.Empty;
            return true;
        }

        return false;
    }

    /// <summary>
    /// The cost of the largest permitted turn: every allowed input and billable output token
    /// (system, tool and reasoning tokens included) at the tariff, rounded up. Two calls share
    /// these token caps, so this is also the worst case for a two-call turn.
    /// </summary>
    public bool TryWorstCaseCost(out long costMicroUsd)
    {
        costMicroUsd = 0;
        return Tariff is not null && Tariff.TryCost(MaxInputTokens, MaxOutputTokens, out costMicroUsd) && costMicroUsd > 0;
    }

    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,64}\z")]
    private static partial Regex Identifier();
}
