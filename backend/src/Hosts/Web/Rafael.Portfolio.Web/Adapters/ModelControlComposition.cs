using System.Globalization;
using Google.Cloud.Firestore;
using Rafael.Portfolio.Modules.Assistant.Application;
using Rafael.Portfolio.Modules.Assistant.Infrastructure;

namespace Rafael.Portfolio.Web.Adapters;

/// <summary>
/// Composes the model control ledger from explicit configuration (<c>Assistant:ModelControl</c>).
/// A missing or unusable configuration registers a ledger that denies every paid request, so the
/// bounded deterministic fallback stays in force; it never produces a fresh allowance. The shared
/// database is created and owned by <c>deployment/control-ledger</c>, never by a stage.
/// </summary>
internal static class ModelControlComposition
{
    public const string SectionName = "Assistant:ModelControl";

    /// <summary>
    /// The version of the approved policy. Changing the approved limits means a new version,
    /// which the stored control document must be migrated to deliberately.
    /// </summary>
    public const string ApprovedPolicyVersion = "approved-2026-10";

    public static ModelControlPolicy BuildPolicy(IConfiguration configuration) =>
        ModelControlPolicy.Approved(ApprovedPolicyVersion, ReadTariff(configuration.GetSection($"{SectionName}:Tariff")));

    /// <summary>Returns null for an absent or malformed tariff: an unknown tariff disables paid admission.</summary>
    public static ModelTariff? ReadTariff(IConfigurationSection section)
    {
        if (!long.TryParse(section["InputMicroUsdPerMillionTokens"], NumberStyles.None, CultureInfo.InvariantCulture, out var input) ||
            !long.TryParse(section["OutputMicroUsdPerMillionTokens"], NumberStyles.None, CultureInfo.InvariantCulture, out var output) ||
            !DateOnly.TryParseExact(section["ValidThroughUtc"], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var validThrough) ||
            string.IsNullOrWhiteSpace(section["Version"]) ||
            string.IsNullOrWhiteSpace(section["ModelId"]))
        {
            return null;
        }

        return new ModelTariff(section["Version"]!, section["ModelId"]!, input, output, validThrough);
    }

    public static IModelControlLedger CreateLedger(IConfiguration configuration, TimeProvider timeProvider)
    {
        var projectId = configuration[$"{SectionName}:Firestore:ProjectId"];
        var databaseId = configuration[$"{SectionName}:Firestore:DatabaseId"];
        if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(databaseId))
        {
            return new UnavailableModelControlLedger();
        }

        var client = new Lazy<FirestoreDb>(() => new FirestoreDbBuilder
        {
            ProjectId = projectId,
            DatabaseId = databaseId
        }.Build());

        return new FirestoreModelControlLedger(client, BuildPolicy(configuration), timeProvider);
    }

    /// <summary>
    /// Controlled, opt-in initialization of the shared control document
    /// (<c>Assistant:ModelControl:InitializeStore=true</c>, set deliberately for one first start).
    /// It never overwrites an existing document and a failure never prevents startup.
    /// </summary>
    public static async Task InitializeStoreIfRequestedAsync(IServiceProvider services, IConfiguration configuration)
    {
        if (!string.Equals(configuration[$"{SectionName}:InitializeStore"], "true", StringComparison.Ordinal) ||
            services.GetRequiredService<IModelControlLedger>() is not FirestoreModelControlLedger ledger)
        {
            return;
        }

        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Rafael.Portfolio.ModelControl");
        try
        {
            var created = await ledger.InitializeAsync();
            logger.LogInformation("ModelControl store initialization requested: Created={Created}", created);
        }
        catch (Exception exception)
        {
            logger.LogWarning("ModelControl store initialization failed: {ErrorType}", exception.GetType().Name);
        }
    }
}
