namespace Rafael.Portfolio.Web.Security;

/// <summary>
/// Closes the backend itself to direct callers in the private dev stage. The Worker already
/// admits only allowlisted visitors, but the dev Cloud Run service has a public URL, so every
/// data route (profile, projects, evidence, assistant, jobs, contact, and OpenAPI where
/// mapped) also requires a signed identity here: a team-tier visitor identity, or the
/// Worker's time-bound service read for GET requests. Only the exact content-free
/// <c>/health</c> endpoint stays public. In any other stage the middleware is a no-op.
/// </summary>
internal sealed class PrivateDevAccessMiddleware(
    RequestDelegate next,
    IConfiguration configuration,
    IHostEnvironment environment,
    TimeProvider timeProvider)
{
    public const string HealthPath = "/health";

    private readonly PortfolioStage _stage = PortfolioStages.Resolve(configuration, environment);

    public async Task InvokeAsync(HttpContext context)
    {
        var isHealth = string.Equals(context.Request.Path.Value, HealthPath, StringComparison.Ordinal) &&
                       (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method));

        if (_stage != PortfolioStage.Dev || isHealth)
        {
            await next(context);
            return;
        }

        var outcome = SignedIdentity.Verify(
            context.Request,
            configuration["AssistantSecurity:ProxyIdentitySecret"],
            _stage,
            timeProvider,
            out var identity);

        var admitted = outcome == SignedIdentityOutcome.Valid &&
                       identity is
        {
            Kind: SignedIdentityKind.ServiceRead
        } or
        {
            Kind: SignedIdentityKind.Visitor,
            Tier: VisitorTier.Team
        };

        if (!admitted)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "application/json";
            context.Response.Headers.CacheControl = "private, no-store";
            await context.Response.WriteAsJsonAsync(
                new { error = "Private development environment access denied." },
                context.RequestAborted);
            return;
        }

        await next(context);
    }
}
