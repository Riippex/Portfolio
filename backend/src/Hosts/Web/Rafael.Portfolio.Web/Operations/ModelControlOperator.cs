using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Rafael.Portfolio.Modules.Assistant.Application;
using Rafael.Portfolio.Web.Adapters;

namespace Rafael.Portfolio.Web.Operations;

/// <summary>Explicit local operator entry point. Never mapped over HTTP or called by a timer.</summary>
public static partial class ModelControlOperator
{
    public static async Task<int> FromEnvironmentAsync(string[] args)
    {
        // Validate arguments before building a credentialed client or touching Firestore.
        if (!Parse(args, out _)) return await UsageAsync(Console.Error);
        var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
        var ledger = ModelControlComposition.CreateLedger(configuration, TimeProvider.System);
        if (ledger is not IModelControlRecovery recovery)
        {
            await Console.Error.WriteLineAsync("Control ledger is not configured. No operation performed.");
            return 2;
        }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        return await RunAsync(args, recovery, Console.Out, deadline.Token);
    }

    public static async Task<int> RunAsync(string[] args, IModelControlRecovery recovery, TextWriter output, CancellationToken ct = default)
    {
        if (!Parse(args, out var command)) return await UsageAsync(output);
        try
        {
            if (command!.List)
            {
                var unresolved = await recovery.ListUnresolvedAsync(50, ct);
                // Only the port's allowlisted accounting metadata; no provider or visitor data.
                await output.WriteLineAsync(JsonSerializer.Serialize(unresolved));
                return 0;
            }
            var outcome = await recovery.ReconcileAsync(command.Id!, command.Resolution, command.Usage, ct);
            await output.WriteLineAsync(JsonSerializer.Serialize(new { reservationId = command.Id, outcome = outcome.ToString() }));
            return outcome == ModelControlOutcome.Applied ? 0 : 3;
        }
        catch (Exception)
        {
            await output.WriteLineAsync("Operation unconfirmed. Inspect the ledger before another action; no refund or release is assumed.");
            return 3;
        }
    }

    private sealed record Command(bool List, string? Id = null, ReconciliationResolution Resolution = default, ModelUsage? Usage = null);

    private static bool Parse(string[] args, out Command? command)
    {
        command = null;
        if (args.Length == 1 && args[0] == "list") { command = new(true); return true; }
        // Mutation requires the exact reservation twice and an affirmative operator attestation.
        // This cannot establish provider state automatically; the owner must check it first.
        if (args.Length is not (7 or 9) || args[0] != "reconcile" || !IdPattern().IsMatch(args[1])
            || args[3] != "--apply" || args[4] != "--confirm" || args[5] != args[1]
            || args[6] != "--provider-state-checked") return false;
        var resolution = args[2] switch
        {
            "not-dispatched" => ReconciliationResolution.NotDispatched,
            "completed" => ReconciliationResolution.Completed,
            "charge-as-reserved" => ReconciliationResolution.ChargeAsReserved,
            _ => (ReconciliationResolution?)null
        };
        if (resolution is null) return false;
        ModelUsage? usage = null;
        if (resolution == ReconciliationResolution.Completed)
        {
            if (args.Length != 9 || !long.TryParse(args[7], NumberStyles.None, CultureInfo.InvariantCulture, out var input)
                || !long.TryParse(args[8], NumberStyles.None, CultureInfo.InvariantCulture, out var output)
                || input is < 1 or > 100_000_000 || output is < 0 or > 100_000_000) return false;
            usage = new ModelUsage(input, output);
        }
        else if (args.Length != 7) return false;
        command = new(false, args[1], resolution.Value, usage);
        return true;
    }

    private static async Task<int> UsageAsync(TextWriter output)
    {
        await output.WriteLineAsync("Use --model-control list, or --model-control reconcile ID not-dispatched|completed|charge-as-reserved --apply --confirm ID --provider-state-checked [INPUT OUTPUT for completed]. No operation performed.");
        return 2;
    }

    [GeneratedRegex(@"^[A-Za-z0-9_-]{8,64}\z")]
    private static partial Regex IdPattern();
}
