using System.Text.RegularExpressions;
using Rafael.Portfolio.Modules.Assistant.Application;

namespace Rafael.Portfolio.Modules.Assistant.Infrastructure;

/// <summary>Explicit activation inputs; code installation alone never enables inference.</summary>
public sealed partial record VertexModelOptions(
    bool Enabled = false,
    string ProjectId = "",
    string Location = "",
    bool DataPolicyReviewed = false,
    bool TokenCountingReviewed = false,
    bool RecoveryReady = false)
{
    public const string ModelId = "gemini-3.1-flash-lite";
    public const string PromptVersion = "canonical-claims-v1";

    public bool IsUsable(ModelControlPolicy policy, DateTimeOffset now) =>
        Enabled && DataPolicyReviewed && TokenCountingReviewed && RecoveryReady
        && ProjectPattern().IsMatch(ProjectId ?? "") && Location is "us" or "eu" or "global"
        && policy.TryValidate(now, out _) && policy.Tariff is { } tariff && tariff.ModelId == ModelId
        // No stale low tariff may authorize a more expensive endpoint. These are reviewed
        // standard on-demand text rates, not discounted/batch rates; recheck before activation.
        && tariff.InputMicroUsdPerMillionTokens >= (Location == "global" ? 250_000 : 275_000)
        && tariff.OutputMicroUsdPerMillionTokens >= (Location == "global" ? 1_500_000 : 1_650_000)
        && tariff.ValidThroughUtc <= DateOnly.FromDateTime(now.UtcDateTime).AddDays(31);

    internal Uri Endpoint(string method)
    {
        if (!ProjectPattern().IsMatch(ProjectId ?? "") || Location is not ("us" or "eu" or "global")
            || method is not ("countTokens" or "generateContent")) throw new InvalidOperationException("Invalid provider configuration.");
        var host = Location == "global" ? "aiplatform.googleapis.com" : $"{Location}-aiplatform.googleapis.com";
        return new Uri($"https://{host}/v1/projects/{ProjectId}/locations/{Location}/publishers/google/models/{ModelId}:{method}");
    }

    [GeneratedRegex(@"^[a-z][a-z0-9-]{4,28}[a-z0-9]\z")]
    private static partial Regex ProjectPattern();
}
