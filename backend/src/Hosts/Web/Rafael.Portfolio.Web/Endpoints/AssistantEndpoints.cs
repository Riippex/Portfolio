using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Rafael.Portfolio.Modules.Assistant.Application;
using Rafael.Portfolio.Modules.Assistant.Domain;
using Rafael.Portfolio.Web.Security;

namespace Rafael.Portfolio.Web.Endpoints;

public static class AssistantEndpoints
{
    private const string FallbackKeyMaterial = "rafael-portfolio-development-rate-limit-key";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    internal enum ClientIdentityStatus
    {
        ValidSigned,
        Absent,
        Invalid
    }

    internal sealed record ClientIdentity(
        ClientIdentityStatus Status,
        string? RateLimitKey,
        string? TurnstileRemoteIp,
        bool IsTeamTier = false,
        string? CountryCode = null,
        string? Stage = null);

    public static RouteGroupBuilder MapAssistantEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/assistant/chat", async (
            [FromBody] AssistantChatRequest? request,
            HttpContext httpContext,
            IAssistantService assistantService,
            IAssistantRateLimiter rateLimiter,
            ITurnstileValidator turnstileValidator,
            IConfiguration configuration,
            IHostEnvironment environment,
            ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("Rafael.Portfolio.Assistant");
            var stopwatch = Stopwatch.StartNew();

            var identityError = ValidateIdentity(httpContext, configuration, environment, out var identity);
            if (identityError is not null)
            {
                return Results.Json(new { error = identityError }, statusCode: StatusCodes.Status403Forbidden);
            }

            if (!rateLimiter.TryAcquire(identity!.RateLimitKey!, identity.IsTeamTier, identity.CountryCode, out var retryAfter))
            {
                httpContext.Response.Headers.RetryAfter = Math.Ceiling(retryAfter.TotalSeconds).ToString();
                return Results.Json(
                    new { error = "Rate limit exceeded. Please wait before asking another question." },
                    statusCode: StatusCodes.Status429TooManyRequests);
            }

            var turnstileValid = await turnstileValidator.ValidateAsync(
                request?.TurnstileToken,
                identity.TurnstileRemoteIp,
                httpContext.RequestAborted);

            if (!turnstileValid)
            {
                return Results.Json(
                    new { error = "Human verification failed or expired." },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            if (request is null || string.IsNullOrWhiteSpace(request.Message))
            {
                return Results.BadRequest(new { error = "Request message is required and cannot be empty." });
            }

            if (request.Message.Length > AssistantChatRequest.MaxMessageLength)
            {
                return Results.BadRequest(new
                {
                    error = $"Message exceeds the maximum length of {AssistantChatRequest.MaxMessageLength} characters."
                });
            }

            if (request.Slug is not null &&
                (request.Slug.Length > AssistantChatRequest.MaxSlugLength ||
                 !AssistantChatRequest.IsValidSlug(request.Slug)))
            {
                return Results.BadRequest(new
                {
                    error = "Parameter 'slug' must be a valid slug (letters, digits, hyphens)."
                });
            }

            var response = assistantService.Chat(request);
            stopwatch.Stop();

            // Bounded observability: log metadata only, never raw user message or answer text
            logger.LogInformation(
                "AssistantChat completed: ClientHash={ClientHash}, QueryLength={Length}, Status={Status}, Citations={CitationCount}, LatencyMs={LatencyMs}",
                HashClientKey(identity.RateLimitKey!),
                request.Message.Length,
                response.GroundingStatus,
                response.Citations.Count,
                stopwatch.ElapsedMilliseconds);

            return Results.Ok(response);
        })
        .WithName("AssistantChat")
        .WithSummary("Query the stateless grounded assistant over public evidence");

        api.MapPost("/assistant/chat/stream", async (
            [FromBody] AssistantChatRequest? request,
            HttpContext httpContext,
            IAssistantService assistantService,
            IAssistantRateLimiter rateLimiter,
            ITurnstileValidator turnstileValidator,
            IConfiguration configuration,
            IHostEnvironment environment,
            ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("Rafael.Portfolio.Assistant");
            var stopwatch = Stopwatch.StartNew();

            var identityError = ValidateIdentity(httpContext, configuration, environment, out var identity);
            if (identityError is not null)
            {
                httpContext.Response.StatusCode = StatusCodes.Status403Forbidden;
                httpContext.Response.ContentType = "application/json";
                await httpContext.Response.WriteAsJsonAsync(
                    new { error = identityError },
                    cancellationToken: httpContext.RequestAborted);
                return;
            }

            if (!rateLimiter.TryAcquire(identity!.RateLimitKey!, identity.IsTeamTier, identity.CountryCode, out var retryAfter))
            {
                httpContext.Response.Headers.RetryAfter = Math.Ceiling(retryAfter.TotalSeconds).ToString();
                httpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                httpContext.Response.ContentType = "application/json";
                await httpContext.Response.WriteAsJsonAsync(
                    new { error = "Rate limit exceeded. Please wait before asking another question." },
                    cancellationToken: httpContext.RequestAborted);
                return;
            }

            var turnstileValid = await turnstileValidator.ValidateAsync(
                request?.TurnstileToken,
                identity.TurnstileRemoteIp,
                httpContext.RequestAborted);

            if (!turnstileValid)
            {
                httpContext.Response.StatusCode = StatusCodes.Status403Forbidden;
                httpContext.Response.ContentType = "application/json";
                await httpContext.Response.WriteAsJsonAsync(
                    new { error = "Human verification failed or expired." },
                    cancellationToken: httpContext.RequestAborted);
                return;
            }

            if (request is null || string.IsNullOrWhiteSpace(request.Message))
            {
                httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
                httpContext.Response.ContentType = "application/json";
                await httpContext.Response.WriteAsJsonAsync(
                    new { error = "Request message is required and cannot be empty." },
                    cancellationToken: httpContext.RequestAborted);
                return;
            }

            if (request.Message.Length > AssistantChatRequest.MaxMessageLength)
            {
                httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
                httpContext.Response.ContentType = "application/json";
                await httpContext.Response.WriteAsJsonAsync(
                    new { error = $"Message exceeds the maximum length of {AssistantChatRequest.MaxMessageLength} characters." },
                    cancellationToken: httpContext.RequestAborted);
                return;
            }

            if (request.Slug is not null &&
                (request.Slug.Length > AssistantChatRequest.MaxSlugLength ||
                 !AssistantChatRequest.IsValidSlug(request.Slug)))
            {
                httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
                httpContext.Response.ContentType = "application/json";
                await httpContext.Response.WriteAsJsonAsync(
                    new { error = "Parameter 'slug' must be a valid slug (letters, digits, hyphens)." },
                    cancellationToken: httpContext.RequestAborted);
                return;
            }

            httpContext.Response.ContentType = "text/event-stream; charset=utf-8";
            httpContext.Response.Headers.CacheControl = "no-cache, no-transform";
            httpContext.Response.Headers.Connection = "keep-alive";

            var citationCount = 0;
            var groundingStatus = "unknown";

            await foreach (var streamEvent in assistantService.StreamChatAsync(request, httpContext.RequestAborted))
            {
                if (streamEvent.GroundingStatus is not null)
                {
                    groundingStatus = streamEvent.GroundingStatus;
                }

                if (streamEvent.Citation is not null)
                {
                    citationCount++;
                }

                var eventPayload = JsonSerializer.Serialize(streamEvent, JsonOptions);
                var sseMessage = $"event: {streamEvent.Type}\ndata: {eventPayload}\n\n";

                await httpContext.Response.WriteAsync(sseMessage, httpContext.RequestAborted);
                await httpContext.Response.Body.FlushAsync(httpContext.RequestAborted);
            }

            stopwatch.Stop();

            // Bounded observability: log stream metadata only
            logger.LogInformation(
                "AssistantStream completed: ClientHash={ClientHash}, QueryLength={Length}, Status={Status}, Citations={CitationCount}, LatencyMs={LatencyMs}",
                HashClientKey(identity.RateLimitKey!),
                request.Message.Length,
                groundingStatus,
                citationCount,
                stopwatch.ElapsedMilliseconds);
        })
        .WithName("AssistantChatStream")
        .WithSummary("Stream grounded assistant responses via Server-Sent Events (SSE)");

        return api;
    }

    internal static string? ValidateIdentity(
        HttpContext context,
        IConfiguration configuration,
        IHostEnvironment environment,
        out ClientIdentity? identity,
        TimeProvider? timeProvider = null)
    {
        identity = null;

        if (!PortfolioStages.TryResolve(configuration, environment, out var stage))
        {
            return "Deployment stage is not configured.";
        }

        var identitySecret = configuration["AssistantSecurity:ProxyIdentitySecret"];
        var outcome = SignedIdentity.Verify(
            context.Request,
            identitySecret,
            stage,
            timeProvider ?? TimeProvider.System,
            out var verified);

        if (outcome is SignedIdentityOutcome.Invalid)
        {
            return "Client identity signature is incomplete, oversized, or invalid.";
        }

        if (outcome is SignedIdentityOutcome.Valid)
        {
            if (verified!.Kind != SignedIdentityKind.Visitor)
            {
                return "A signed visitor identity is required.";
            }

            identity = new ClientIdentity(
                ClientIdentityStatus.ValidSigned,
                DeriveOpaqueKey(identitySecret, $"ratelimit:{verified.Ip}"),
                verified.Ip,
                IsTeamTier: verified.Tier == VisitorTier.Team,
                CountryCode: verified.Country,
                Stage: PortfolioStages.ToText(verified.Stage));
            return null;
        }

        // No identity was sent. Only a workstation (explicit local stage in a Development or
        // Test host) may continue, with a pseudonymous connection-based key; a deployed
        // stage always needs the signed identity.
        if (stage != PortfolioStage.Local || (!environment.IsDevelopment() && !environment.IsEnvironment("Test")))
        {
            return "A signed client identity is required.";
        }

        identity = DerivePseudonymousFallback(context, configuration, identitySecret);
        return null;
    }

    private static ClientIdentity DerivePseudonymousFallback(
        HttpContext context,
        IConfiguration configuration,
        string? identitySecret)
    {
        var connectionIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var clientIp = connectionIp;

        if (IsTrustedProxy(connectionIp, configuration))
        {
            var cfConnectingIp = context.Request.Headers["CF-Connecting-IP"].FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(cfConnectingIp))
            {
                clientIp = cfConnectingIp.Trim();
            }
            else
            {
                var xForwardedFor = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(xForwardedFor))
                {
                    var firstIp = xForwardedFor.Split(',')[0].Trim();
                    if (!string.IsNullOrEmpty(firstIp))
                    {
                        clientIp = firstIp;
                    }
                }
            }
        }

        return new ClientIdentity(
            ClientIdentityStatus.Absent,
            DeriveOpaqueKey(identitySecret, $"connection:{clientIp}"),
            clientIp);
    }

    internal static string DeriveOpaqueKey(string? secret, string subject)
    {
        var keyMaterial = string.IsNullOrWhiteSpace(secret) ? FallbackKeyMaterial : secret;
        return $"k:{ComputeProof(keyMaterial, subject)}";
    }

    private static bool IsTrustedProxy(string remoteIp, IConfiguration configuration)
    {
        var trustedProxies = configuration
            .GetSection("AssistantSecurity:TrustedProxies")
            .Get<string[]>() ?? [];

        return trustedProxies.Contains(remoteIp, StringComparer.Ordinal);
    }

    private static string ComputeProof(string secret, string subject)
    {
        var bytes = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(subject));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string HashClientKey(string key)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(bytes)[..12];
    }
}
