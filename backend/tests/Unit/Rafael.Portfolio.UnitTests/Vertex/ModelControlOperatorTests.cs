using Microsoft.Extensions.Time.Testing;
using Rafael.Portfolio.Modules.Assistant.Application;
using Rafael.Portfolio.Web.Operations;

namespace Rafael.Portfolio.UnitTests.Vertex;

public sealed class ModelControlOperatorTests
{
    private const string Id = "res_operator_001";

    [Theory]
    [InlineData("not-dispatched", 0)]
    [InlineData("completed", 9)]
    [InlineData("charge-as-reserved", 840)]
    public async Task Explicit_operator_recovery_uses_real_accounting_and_releases_only_the_chosen_reservation(string resolution, long charge)
    {
        var ledger = new InMemoryModelControlLedger(ModelControlFixtures.Policy(), new FakeTimeProvider(ModelControlFixtures.Noon));
        await ledger.TryReserveAsync(Id);
        await ledger.TryBeginCallAsync(Id, 100, 100);
        await ledger.AbandonAsync(Id);
        var args = new List<string> { "reconcile", Id, resolution, "--apply", "--confirm", Id, "--provider-state-checked" };
        if (resolution == "completed") args.AddRange(["50", "10"]);
        using var output = new StringWriter();
        Assert.Equal(0, await ModelControlOperator.RunAsync(args.ToArray(), ledger, output));
        Assert.Contains("Applied", output.ToString());
        Assert.Equal(charge, ledger.Reservation(Id)!.ChargedMicroUsd);
        Assert.Equal(0, ledger.ActivePermits);
        Assert.NotNull(ledger.Reservation(Id)!.ExpiresAt);
    }

    public static IEnumerable<object[]> InvalidArguments()
    {
        var valid = new[] { "reconcile", Id, "completed", "--apply", "--confirm", Id, "--provider-state-checked", "50", "10" };
        yield return [Array.Empty<string>()];
        yield return [new[] { "list", "extra" }];
        yield return [new[] { "initialize", "--apply" }];
        yield return [new[] { "reconcile", Id, "not-dispatched" }];
        foreach (var index in new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8 })
        {
            var bad = (string[])valid.Clone();
            bad[index] = "invalid";
            yield return [bad];
        }
        yield return [valid.Concat(new[] { "extra" }).ToArray()];
    }

    private sealed class UnreachableRecovery : IModelControlRecovery
    {
        public ValueTask<IReadOnlyList<UnresolvedReservation>> ListUnresolvedAsync(int maxItems = 50, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("No credentials or store may be touched.");
        public ValueTask<ModelControlOutcome> ReconcileAsync(string reservationId, ReconciliationResolution resolution,
            ModelUsage? confirmedUsage = null, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public ValueTask<CapacityReconciliation> ReconcileCapacityAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    }

    [Theory]
    [MemberData(nameof(InvalidArguments))]
    public async Task Missing_confirmation_bad_usage_or_unrecognized_commands_touch_no_store(string[] args)
    {
        using var output = new StringWriter();
        Assert.Equal(2, await ModelControlOperator.RunAsync(args, new UnreachableRecovery(), output));
        Assert.Contains("No operation performed", output.ToString());
    }

    [Fact]
    public async Task Listing_is_read_only_and_contains_only_existing_accounting_metadata()
    {
        var ledger = new InMemoryModelControlLedger(ModelControlFixtures.Policy(), new FakeTimeProvider(ModelControlFixtures.Noon));
        await ledger.TryReserveAsync(Id);
        using var output = new StringWriter();
        Assert.Equal(0, await ModelControlOperator.RunAsync(["list"], ledger, output));
        Assert.Contains(Id, output.ToString());
        Assert.DoesNotContain("prompt", output.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ReservationState.Active, ledger.Reservation(Id)!.State);
        Assert.Equal(1, ledger.ActivePermits);
    }

    [Fact]
    public async Task A_store_exception_never_claims_a_refund_or_echoes_the_exception()
    {
        using var output = new StringWriter();
        Assert.Equal(3, await ModelControlOperator.RunAsync(["list"], new UnreachableRecovery(), output));
        Assert.Contains("unconfirmed", output.ToString());
        Assert.DoesNotContain("credentials", output.ToString());
    }
}
