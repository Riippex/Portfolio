namespace Rafael.Portfolio.Web.Security;

/// <summary>
/// The deployment stage the backend serves. It is one explicit value, <c>Portfolio:Stage</c>
/// (environment variable <c>Portfolio__Stage</c>), written by the deployment configuration
/// and checked against the stage inside every signed identity. It is never inferred from
/// <c>ASPNETCORE_ENVIRONMENT</c> or the request.
/// </summary>
internal enum PortfolioStage
{
    /// <summary>Workstation development and tests only; no Cloudflare edge in front.</summary>
    Local,

    /// <summary>The private development stage: every data route needs a signed identity.</summary>
    Dev,

    /// <summary>The public production stage.</summary>
    Prod
}

internal static class PortfolioStages
{
    public const string ConfigurationKey = "Portfolio:Stage";

    /// <summary>Exact, case-sensitive match: "local", "dev" or "prod".</summary>
    public static bool TryParse(string? value, out PortfolioStage stage)
    {
        switch (value)
        {
            case "local":
                stage = PortfolioStage.Local;
                return true;
            case "dev":
                stage = PortfolioStage.Dev;
                return true;
            case "prod":
                stage = PortfolioStage.Prod;
                return true;
            default:
                stage = default;
                return false;
        }
    }

    public static string ToText(PortfolioStage stage) => stage switch
    {
        PortfolioStage.Local => "local",
        PortfolioStage.Dev => "dev",
        _ => "prod"
    };

    /// <summary>
    /// Resolves the configured stage. A missing value is accepted only as "local" in the
    /// Development and Test host environments; every deployed host must state "dev" or
    /// "prod". "local" is refused anywhere else, so a deployment cannot opt out of the stage
    /// rules by naming itself local.
    /// </summary>
    public static bool TryResolve(IConfiguration configuration, IHostEnvironment environment, out PortfolioStage stage)
    {
        var localHost = environment.IsDevelopment() || environment.IsEnvironment("Test");
        var configured = configuration[ConfigurationKey];

        if (string.IsNullOrEmpty(configured))
        {
            stage = PortfolioStage.Local;
            return localHost;
        }

        if (!TryParse(configured, out stage))
        {
            return false;
        }

        return stage != PortfolioStage.Local || localHost;
    }

    public static PortfolioStage Resolve(IConfiguration configuration, IHostEnvironment environment)
    {
        if (TryResolve(configuration, environment, out var stage))
        {
            return stage;
        }

        throw new InvalidOperationException(
            $"{ConfigurationKey} must be 'dev' or 'prod' outside Development/Test environments " +
            "('local' is allowed only there). The stage is explicit and never inferred.");
    }
}
