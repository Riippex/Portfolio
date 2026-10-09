using System.Reflection;
using Microsoft.Extensions.Configuration;
using Rafael.Portfolio.Modules.Assistant.Application;
using Rafael.Portfolio.Modules.Assistant.Infrastructure;
using Rafael.Portfolio.Web.Adapters;

namespace Rafael.Portfolio.UnitTests;

public sealed class ModelControlCompositionTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>($"Assistant:ModelControl:{v.Key}", v.Value)))
            .Build();

    private static readonly (string, string?)[] ValidTariff =
    [
        ("Tariff:Version", "tariff-1"),
        ("Tariff:ModelId", "gemini-3.1-flash-lite"),
        ("Tariff:InputMicroUsdPerMillionTokens", "100000"),
        ("Tariff:OutputMicroUsdPerMillionTokens", "400000"),
        ("Tariff:ValidThroughUtc", "2099-01-01")
    ];

    [Fact]
    public async Task Without_store_configuration_every_paid_request_is_denied()
    {
        var ledger = ModelControlComposition.CreateLedger(Config(), TimeProvider.System);

        Assert.IsType<UnavailableModelControlLedger>(ledger);
        var result = await ledger.TryReserveAsync("res_00000001");
        Assert.False(result.Success);
        Assert.Equal(ModelReservationDenialReason.StoreUnavailable, result.DenialReason);
    }

    [Theory]
    [InlineData("", "portfolio-control")]
    [InlineData("shared-project", "")]
    [InlineData("  ", "portfolio-control")]
    public void A_half_configured_store_is_unavailable_not_defaulted(string project, string database)
    {
        var ledger = ModelControlComposition.CreateLedger(
            Config(("Firestore:ProjectId", project), ("Firestore:DatabaseId", database)), TimeProvider.System);

        Assert.IsType<UnavailableModelControlLedger>(ledger);
    }

    [Fact]
    public async Task A_configured_store_without_a_tariff_denies_before_any_client_is_created()
    {
        var ledger = ModelControlComposition.CreateLedger(
            Config(("Firestore:ProjectId", "shared-project"), ("Firestore:DatabaseId", "portfolio-control")), TimeProvider.System);

        Assert.IsType<FirestoreModelControlLedger>(ledger);
        var reserve = await ledger.TryReserveAsync("res_00000001");
        var call = await ledger.TryBeginCallAsync("res_00000001", 10, 10);

        // No credentials exist in a unit test: reaching the store would report StoreUnavailable.
        Assert.Equal(ModelReservationDenialReason.InvalidTariffOrPolicy, reserve.DenialReason);
        Assert.Equal(ModelCallDenialReason.InvalidTariffOrPolicy, call.DenialReason);
    }

    [Fact]
    public void A_valid_tariff_builds_the_approved_policy()
    {
        var policy = ModelControlComposition.BuildPolicy(Config(ValidTariff));

        Assert.True(policy.TryValidate(DateTimeOffset.UtcNow, out _));
        Assert.Equal(ModelControlComposition.ApprovedPolicyVersion, policy.Version);
        Assert.Equal(ModelControlPolicy.ApprovedDailyBudgetMicroUsd, policy.DailyBudgetMicroUsd);
        Assert.Equal(ModelControlPolicy.ApprovedMonthlyBudgetMicroUsd, policy.MonthlyBudgetMicroUsd);
        Assert.Equal("tariff-1", policy.Tariff!.Version);
    }

    [Theory]
    [InlineData("Tariff:Version")]
    [InlineData("Tariff:ModelId")]
    [InlineData("Tariff:InputMicroUsdPerMillionTokens")]
    [InlineData("Tariff:OutputMicroUsdPerMillionTokens")]
    [InlineData("Tariff:ValidThroughUtc")]
    public void A_tariff_with_a_missing_field_is_unknown(string missing)
    {
        var tariff = ModelControlComposition.ReadTariff(
            Config(ValidTariff.Where(v => v.Item1 != missing).ToArray()).GetSection("Assistant:ModelControl:Tariff"));

        Assert.Null(tariff);
    }

    [Theory]
    [InlineData("Tariff:InputMicroUsdPerMillionTokens", "-5")]
    [InlineData("Tariff:InputMicroUsdPerMillionTokens", "abc")]
    [InlineData("Tariff:OutputMicroUsdPerMillionTokens", "1.5")]
    [InlineData("Tariff:OutputMicroUsdPerMillionTokens", "99999999999999999999")]
    [InlineData("Tariff:ValidThroughUtc", "2099-13-45")]
    [InlineData("Tariff:ValidThroughUtc", "tomorrow")]
    public void Malformed_tariff_values_are_never_parsed_into_defaults(string key, string value)
    {
        var values = ValidTariff.Select(v => v.Item1 == key ? (v.Item1, (string?)value) : v).ToArray();

        Assert.Null(ModelControlComposition.ReadTariff(Config(values).GetSection("Assistant:ModelControl:Tariff")));
    }

    [Fact]
    public void An_out_of_range_tariff_is_read_but_invalidates_the_policy()
    {
        var values = ValidTariff.Select(v => v.Item1 == "Tariff:InputMicroUsdPerMillionTokens" ? (v.Item1, (string?)"0") : v).ToArray();

        var policy = ModelControlComposition.BuildPolicy(Config(values));

        Assert.False(policy.TryValidate(DateTimeOffset.UtcNow, out _));
    }

    [Fact]
    public void The_production_assembly_has_no_in_memory_ledger_and_the_host_does_not_register_one()
    {
        var assistant = typeof(IModelControlLedger).Assembly;
        Assert.DoesNotContain(assistant.GetTypes(), type => type.Name.Contains("InMemoryModelControl", StringComparison.Ordinal));

        var program = File.ReadAllText(Path.Combine(
            TestRepositoryRoot.Find(), "backend", "src", "Hosts", "Web", "Rafael.Portfolio.Web", "Program.cs"));
        Assert.DoesNotContain("InMemoryModelControlLedger", program, StringComparison.Ordinal);
        Assert.Contains("ModelControlComposition.CreateLedger", program, StringComparison.Ordinal);
    }

    [Fact]
    public void Stored_accounting_metadata_has_no_room_for_visitor_or_content_data()
    {
        var forbidden = new[]
        {
            "ip", "address", "country", "prompt", "question", "answer", "message", "text", "content", "email", "contact",
            "cv", "resume", "tool", "payload", "transcript", "visitor", "client", "user", "name"
        };

        foreach (var type in new[] { typeof(LedgerRoot), typeof(PeriodCounter), typeof(ReservationRecord), typeof(ModelUsage) })
        {
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var words = System.Text.RegularExpressions.Regex.Split(property.Name, "(?<!^)(?=[A-Z])")
                    .Select(word => word.ToLowerInvariant());
                Assert.DoesNotContain(words, word => forbidden.Contains(word));
            }
        }
    }

    [Fact]
    public async Task The_unavailable_ledger_grants_nothing_for_any_operation()
    {
        var ledger = new UnavailableModelControlLedger();

        Assert.False((await ledger.TryReserveAsync("res_00000001")).Success);
        Assert.False((await ledger.TryBeginCallAsync("res_00000001", 10, 10)).Allowed);
        Assert.Equal(ModelControlOutcome.StoreUnavailable, await ledger.CompleteCallAsync("res_00000001", new ModelUsage(10, 10)));
        Assert.Equal(ModelControlOutcome.StoreUnavailable, await ledger.SettleAsync("res_00000001"));
        Assert.Equal(ModelControlOutcome.StoreUnavailable, await ledger.CancelUndispatchedAsync("res_00000001"));
        Assert.Equal(ModelControlOutcome.StoreUnavailable, await ledger.AbandonAsync("res_00000001"));
    }
}
