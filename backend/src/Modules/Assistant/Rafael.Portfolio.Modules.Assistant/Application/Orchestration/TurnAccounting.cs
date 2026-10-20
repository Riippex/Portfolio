namespace Rafael.Portfolio.Modules.Assistant.Application.Orchestration;

internal sealed class TurnAccounting(IModelControlLedger ledger, TurnBudgets budgets, string id, TimeSpan end)
{
    internal ValueTask<PortResult<ModelControlOutcome>> CancelAsync() =>
        budgets.AwaitAsync(ct => ledger.CancelUndispatchedAsync(id, ct), end);

    internal ValueTask<PortResult<ModelControlOutcome>> AbandonAsync() =>
        budgets.AwaitAsync(ct => ledger.AbandonAsync(id, ct), end);

    internal async ValueTask<bool> CompleteAsync(ModelUsage usage)
    {
        var first = await CompleteAttemptAsync(usage);
        if (Is(first, ModelControlOutcome.Applied)) return await SettleAsync();
        if (!Ambiguous(first)) return false;

        var second = await CompleteAttemptAsync(usage);
        if (Is(second, ModelControlOutcome.Applied) || Is(second, ModelControlOutcome.InvalidState) || Ambiguous(second))
            return await SettleAsync();
        return false;
    }

    private ValueTask<PortResult<ModelControlOutcome>> CompleteAttemptAsync(ModelUsage usage) =>
        budgets.AwaitAsync(ct => ledger.CompleteCallAsync(id, usage, ct), end);

    private async ValueTask<bool> SettleAsync()
    {
        var result = await budgets.AwaitAsync(ct => ledger.SettleAsync(id, ct), end);
        return Is(result, ModelControlOutcome.Applied) || Is(result, ModelControlOutcome.InvalidTariffOrPolicy);
    }

    private static bool Ambiguous(PortResult<ModelControlOutcome> result) =>
        !result.Known || result.Value == ModelControlOutcome.StoreUnavailable;

    private static bool Is(PortResult<ModelControlOutcome> result, ModelControlOutcome outcome) =>
        result.Known && result.Value == outcome;
}
