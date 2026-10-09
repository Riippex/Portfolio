using System.Text.RegularExpressions;

namespace Rafael.Portfolio.Modules.Assistant.Application;

/// <summary>
/// A versioned price list for one model, in integer micro-USD per million tokens. Costs are
/// computed with integer arithmetic and rounded up, so an estimate can only err upward.
/// An unknown, malformed or expired tariff is never replaced by a default: it disables paid
/// admission.
/// </summary>
public sealed partial record ModelTariff(
    string Version,
    string ModelId,
    long InputMicroUsdPerMillionTokens,
    long OutputMicroUsdPerMillionTokens,
    DateOnly ValidThroughUtc)
{
    /// <summary>Ceiling on a per-million-token rate (USD 1,000 per million tokens).</summary>
    public const long MaxRateMicroUsdPerMillionTokens = 1_000_000_000;

    private const long TokensPerUnit = 1_000_000;

    public bool TryValidate(DateTimeOffset now, out string error)
    {
        if (!Identifier().IsMatch(Version ?? string.Empty))
        {
            error = "Tariff version is missing or malformed.";
        }
        else if (!Identifier().IsMatch(ModelId ?? string.Empty))
        {
            error = "Tariff model id is missing or malformed.";
        }
        else if (InputMicroUsdPerMillionTokens is <= 0 or > MaxRateMicroUsdPerMillionTokens ||
                 OutputMicroUsdPerMillionTokens is <= 0 or > MaxRateMicroUsdPerMillionTokens)
        {
            error = "Tariff rates must be positive and within the approved ceiling.";
        }
        else if (DateOnly.FromDateTime(now.UtcDateTime) > ValidThroughUtc)
        {
            error = "Tariff has expired.";
        }
        else
        {
            error = string.Empty;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Cost in micro-USD of the given token counts, rounded up per component. Returns false for
    /// negative counts or any result that does not fit in a long, never a wrapped value.
    /// </summary>
    public bool TryCost(long inputTokens, long outputTokens, out long costMicroUsd)
    {
        costMicroUsd = 0;
        if (inputTokens < 0 || outputTokens < 0 || InputMicroUsdPerMillionTokens <= 0 || OutputMicroUsdPerMillionTokens <= 0)
        {
            return false;
        }

        var input = CeilingDivide((UInt128)(ulong)inputTokens * (UInt128)(ulong)InputMicroUsdPerMillionTokens);
        var output = CeilingDivide((UInt128)(ulong)outputTokens * (UInt128)(ulong)OutputMicroUsdPerMillionTokens);
        var total = input + output;
        if (total > (UInt128)long.MaxValue)
        {
            return false;
        }

        costMicroUsd = (long)total;
        return true;
    }

    private static UInt128 CeilingDivide(UInt128 numerator) =>
        (numerator + (UInt128)(TokensPerUnit - 1)) / (UInt128)TokensPerUnit;

    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,64}\z")]
    private static partial Regex Identifier();
}
