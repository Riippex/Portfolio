using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Rafael.Portfolio.Modules.Assistant.Application;
using Rafael.Portfolio.Modules.Assistant.Domain;

namespace Rafael.Portfolio.Web.Endpoints;

public static class AssistantEndpoints
{
    private const int MaxVisitorIdLength = 128;
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
        out ClientIdentity? identity)
    {
        identity = null;
        var status = ResolveIdentityStatus(context, configuration, out var visitorId, out var identitySecret);

        if (status is ClientIdentityStatus.Invalid)
        {
            return "Client identity signature is incomplete, oversized, or invalid.";
        }

        if (status is ClientIdentityStatus.ValidSigned)
        {
            var isV1 = visitorId!.StartsWith("v1:", StringComparison.Ordinal);
            var visitorIp = visitorId;
            var countryCode = "XX";
            var isTeamTier = false;
            string? stage = null;

            if (isV1)
            {
                var parts = visitorId.Split(':');
                if (parts.Length >= 5)
                {
                    visitorIp = parts[1];
                    countryCode = parts[2];
                    isTeamTier = string.Equals(parts[3], "team", StringComparison.OrdinalIgnoreCase);
                    stage = parts[4];
                }
            }

            identity = new ClientIdentity(
                status,
                DeriveOpaqueKey(identitySecret, $"ratelimit:{visitorIp}"),
                visitorIp,
                IsTeamTier: isTeamTier,
                CountryCode: countryCode,
                Stage: stage);
            return null;
        }

        if (!environment.IsDevelopment() && !environment.IsEnvironment("Test"))
        {
            return "A signed client identity is required.";
        }

        identity = DerivePseudonymousFallback(context, configuration, identitySecret);
        return null;
    }

    private static ClientIdentityStatus ResolveIdentityStatus(
        HttpContext context,
        IConfiguration configuration,
        out string? visitorId,
        out string? identitySecret)
    {
        visitorId = null;
        identitySecret = configuration["AssistantSecurity:ProxyIdentitySecret"];

        var asserted = context.Request.Headers["X-Client-Key"].FirstOrDefault();
        var proof = context.Request.Headers["X-Client-Key-Proof"].FirstOrDefault();

        if (string.IsNullOrWhiteSpace(asserted) && string.IsNullOrWhiteSpace(proof))
        {
            return ClientIdentityStatus.Absent;
        }

        if (string.IsNullOrWhiteSpace(asserted) ||
            string.IsNullOrWhiteSpace(proof) ||
            string.IsNullOrWhiteSpace(identitySecret))
        {
            return ClientIdentityStatus.Invalid;
        }

        var candidate = asserted.Trim();
        if (candidate.Length > MaxVisitorIdLength)
        {
            return ClientIdentityStatus.Invalid;
        }

        var expected = ComputeProof(identitySecret, candidate);
        var expectedBytes = Encoding.ASCII.GetBytes(expected);
        var providedBytes = Encoding.ASCII.GetBytes(proof.Trim());

        if (expectedBytes.Length == providedBytes.Length &&
            CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes))
        {
            visitorId = candidate;
            return ClientIdentityStatus.ValidSigned;
        }

        return ClientIdentityStatus.Invalid;
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
