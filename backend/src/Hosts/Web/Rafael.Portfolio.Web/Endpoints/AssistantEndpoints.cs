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
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static RouteGroupBuilder MapAssistantEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/assistant/chat", async (
            [FromBody] AssistantChatRequest? request,
            HttpContext httpContext,
            IAssistantService assistantService,
            IAssistantRateLimiter rateLimiter,
            ITurnstileValidator turnstileValidator,
            ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("Rafael.Portfolio.Assistant");
            var stopwatch = Stopwatch.StartNew();
            var clientKey = GetClientKey(httpContext);

            if (!rateLimiter.TryAcquire(clientKey, out var retryAfter))
            {
                httpContext.Response.Headers.RetryAfter = Math.Ceiling(retryAfter.TotalSeconds).ToString();
                return Results.Json(
                    new { error = "Rate limit exceeded. Please wait before asking another question." },
                    statusCode: StatusCodes.Status429TooManyRequests);
            }

            var turnstileValid = await turnstileValidator.ValidateAsync(
                request?.TurnstileToken,
                clientKey,
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
                HashClientKey(clientKey),
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
            ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("Rafael.Portfolio.Assistant");
            var stopwatch = Stopwatch.StartNew();
            var clientKey = GetClientKey(httpContext);

            if (!rateLimiter.TryAcquire(clientKey, out var retryAfter))
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
                clientKey,
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
                HashClientKey(clientKey),
                request.Message.Length,
                groundingStatus,
                citationCount,
                stopwatch.ElapsedMilliseconds);
        })
        .WithName("AssistantChatStream")
        .WithSummary("Stream grounded assistant responses via Server-Sent Events (SSE)");

        return api;
    }

    private static string GetClientKey(HttpContext context)
    {
        var cfConnectingIp = context.Request.Headers["CF-Connecting-IP"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(cfConnectingIp))
        {
            return cfConnectingIp.Trim();
        }

        var xForwardedFor = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(xForwardedFor))
        {
            var firstIp = xForwardedFor.Split(',')[0].Trim();
            if (!string.IsNullOrEmpty(firstIp))
            {
                return firstIp;
            }
        }

        return context.Connection.RemoteIpAddress?.ToString() ?? "127.0.0.1";
    }

    private static string HashClientKey(string key)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(bytes)[..12];
    }
}
