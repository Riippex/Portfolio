using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Rafael.Portfolio.Modules.Assistant.Application;
using Rafael.Portfolio.Modules.Assistant.Infrastructure;
using Rafael.Portfolio.Web.Adapters;

namespace Rafael.Portfolio.UnitTests.Vertex;

public sealed class VertexCompositionTests
{
    private sealed class NeverToken : IVertexAccessToken
    {
        public ValueTask<string> GetAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("Must not authenticate during composition.");
    }

    [Theory]
    [InlineData("complete", true)]
    [InlineData("missing-review", false)]
    [InlineData("wrong-ledger-project", false)]
    [InlineData("missing-ledger", false)]
    public void Only_consistent_reviewed_settings_compose_the_real_pair_without_authentication(string mode, bool enabled)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Assistant:Vertex:Enabled"] = "true",
            ["Assistant:Vertex:ProjectId"] = "synthetic-portfolio",
            ["Assistant:Vertex:Location"] = "us",
            ["Assistant:Vertex:DataPolicyReviewed"] = "true",
            ["Assistant:Vertex:TokenCountingReviewed"] = "true",
            ["Assistant:Vertex:RecoveryReady"] = "true",
            ["Assistant:ModelControl:Firestore:ProjectId"] = "synthetic-portfolio",
            ["Assistant:ModelControl:Firestore:DatabaseId"] = "control-ledger",
            ["Assistant:ModelControl:Tariff:Version"] = "reviewed-test",
            ["Assistant:ModelControl:Tariff:ModelId"] = VertexModelOptions.ModelId,
            ["Assistant:ModelControl:Tariff:InputMicroUsdPerMillionTokens"] = "275000",
            ["Assistant:ModelControl:Tariff:OutputMicroUsdPerMillionTokens"] = "1650000",
            ["Assistant:ModelControl:Tariff:ValidThroughUtc"] = DateTime.UtcNow.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        };
        if (mode == "missing-review") settings.Remove("Assistant:Vertex:DataPolicyReviewed");
        if (mode == "wrong-ledger-project") settings["Assistant:ModelControl:Firestore:ProjectId"] = "other-project";
        if (mode == "missing-ledger") settings.Remove("Assistant:ModelControl:Firestore:DatabaseId");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        ModelProviderComposition.Register(services, new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        services.AddSingleton<IVertexAccessToken, NeverToken>();
        using var provider = services.BuildServiceProvider();
        var model = provider.GetRequiredService<IModelProvider>();
        var counter = provider.GetRequiredService<IModelRequestTokenCounter>();
        if (enabled)
        {
            Assert.IsType<VertexGeminiClient>(model);
            Assert.Same(model, counter);
            Assert.True(model.IsAvailable);
        }
        else
        {
            Assert.IsType<DisabledModelProvider>(model);
            Assert.IsType<UnverifiedModelRequestTokenCounter>(counter);
        }
    }
}
