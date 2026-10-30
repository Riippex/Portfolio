using Rafael.Portfolio.Modules.Assistant.Application;
using Rafael.Portfolio.Modules.Assistant.Infrastructure;

namespace Rafael.Portfolio.Web.Adapters;

internal static class ModelProviderComposition
{
    internal static void Register(IServiceCollection services, IConfiguration configuration)
    {
        static bool Yes(string? value) => string.Equals(value, "true", StringComparison.Ordinal);
        var section = configuration.GetSection("Assistant:Vertex");
        var options = new VertexModelOptions(Yes(section["Enabled"]), section["ProjectId"] ?? "", section["Location"] ?? "",
            Yes(section["DataPolicyReviewed"]), Yes(section["TokenCountingReviewed"]), Yes(section["RecoveryReady"]));
        var policy = ModelControlComposition.BuildPolicy(configuration);
        var configured = options.IsUsable(policy, DateTimeOffset.UtcNow)
            && !string.IsNullOrWhiteSpace(configuration["Assistant:ModelControl:Firestore:DatabaseId"])
            && string.Equals(options.ProjectId, configuration["Assistant:ModelControl:Firestore:ProjectId"], StringComparison.Ordinal);
        services.AddHttpClient("vertex-model", client => client.Timeout = TimeSpan.FromSeconds(9))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false })
            .RedactLoggedHeaders(_ => true);
        services.AddSingleton<IVertexAccessToken, VertexAccessToken>();
        services.AddSingleton(sp => new VertexGeminiClient(options, policy,
            new VertexTransport(sp.GetRequiredService<IHttpClientFactory>().CreateClient("vertex-model"), sp.GetRequiredService<IVertexAccessToken>()),
            sp.GetRequiredService<TimeProvider>(), sp.GetService<ILogger<VertexGeminiClient>>()));
        services.AddSingleton<IModelProvider>(sp => configured ? sp.GetRequiredService<VertexGeminiClient>() : new DisabledModelProvider());
        services.AddSingleton<IModelRequestTokenCounter>(sp => configured ? sp.GetRequiredService<VertexGeminiClient>() : new UnverifiedModelRequestTokenCounter());
    }
}
