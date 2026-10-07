using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Rafael.Portfolio.Modules.Assistant.Application;
using Rafael.Portfolio.Modules.Contact.Application;
using Rafael.Portfolio.Modules.Contact.Domain;

namespace Rafael.Portfolio.Web.Endpoints;

public static class ContactEndpoints
{
    private const int MaxRequestBodySizeBytes = 64 * 1024; // 64 KiB
    private const int StatusCodeClientClosedRequest = 499;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static RouteGroupBuilder MapContactEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/contact", async (
            HttpContext httpContext,
            IContactService contactService,
            IContactRateLimiter rateLimiter,
            ITurnstileValidator turnstileValidator,
            IConfiguration configuration,
            IHostEnvironment environment,
            ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("Rafael.Portfolio.Contact");
            var stopwatch = Stopwatch.StartNew();

            // 1. Strict 64 KiB request body limit, counted in bytes as they arrive, so it
            // holds without a Content-Length header and for multibyte UTF-8.
            if (httpContext.Request.ContentLength > MaxRequestBodySizeBytes)
            {
                return Results.Json(
                    new { error = "Request body exceeds the maximum size of 64 KiB." },
                    statusCode: StatusCodes.Status413PayloadTooLarge);
            }

            byte[]? bodyBytes;
            try
            {
                bodyBytes = await ReadBoundedBodyAsync(
                    httpContext.Request.Body,
                    MaxRequestBodySizeBytes,
                    httpContext.RequestAborted);
            }
            catch (OperationCanceledException) when (httpContext.RequestAborted.IsCancellationRequested)
            {
                return Results.StatusCode(StatusCodeClientClosedRequest);
            }

            if (bodyBytes is null)
            {
                return Results.Json(
                    new { error = "Request body exceeds the maximum size of 64 KiB." },
                    statusCode: StatusCodes.Status413PayloadTooLarge);
            }

            if (IsBlank(bodyBytes))
            {
                return Results.BadRequest(new { error = "Request body is required." });
            }

            ContactRelayRequest? request;
            try
            {
                request = JsonSerializer.Deserialize<ContactRelayRequest>(bodyBytes, JsonOptions);
            }
            catch (JsonException)
            {
                return Results.BadRequest(new { error = "Malformed JSON request body." });
            }

            // 2. Validate client identity
            var identityError = AssistantEndpoints.ValidateIdentity(httpContext, configuration, environment, out var identity);
            if (identityError is not null)
            {
                return Results.Json(new { error = identityError }, statusCode: StatusCodes.Status403Forbidden);
            }

            // 3. Isolated Contact rate limiting
            if (!rateLimiter.TryAcquire(identity!.RateLimitKey!, out var retryAfter))
            {
                httpContext.Response.Headers.RetryAfter = Math.Ceiling(retryAfter.TotalSeconds).ToString();
                return Results.Json(
                    new { error = "Rate limit exceeded. Please wait before submitting another contact message." },
                    statusCode: StatusCodes.Status429TooManyRequests);
            }

            // 4. Turnstile verification outside Development/Test
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

            // 5. Domain validation
            if (!ContactMessage.TryCreate(
                request?.Name,
                request?.Email,
                request?.Message,
                request?.Consent ?? false,
                out var message,
                out var validationError))
            {
                return Results.BadRequest(new { error = validationError });
            }

            // 6. Relay message
            var result = await contactService.SendContactMessageAsync(message!, httpContext.RequestAborted);
            stopwatch.Stop();

            // 7. Bounded observability: log metadata only, NEVER raw message text or email addresses
            logger.LogInformation(
                "ContactRelay completed: ClientHash={ClientHash}, Outcome={Outcome}, Status={Status}, LatencyMs={LatencyMs}",
                HashClientKey(identity.RateLimitKey!),
                result.OutcomeCode,
                result.Status.ToString(),
                stopwatch.ElapsedMilliseconds);

            return result.Status switch
            {
                ContactDeliveryStatus.Delivered => Results.Ok(new
                {
                    status = "delivered",
                    outcome = result.OutcomeCode
                }),
                ContactDeliveryStatus.Queued => Results.Ok(new
                {
                    status = "queued",
                    outcome = result.OutcomeCode
                }),
                ContactDeliveryStatus.Unavailable => Results.Json(
                    new { error = "Contact service is temporarily unavailable.", outcome = result.OutcomeCode },
                    statusCode: StatusCodes.Status503ServiceUnavailable),
                _ => Results.Json(
                    new { error = "Contact message delivery failed.", outcome = result.OutcomeCode },
                    statusCode: StatusCodes.Status502BadGateway)
            };
        })
        .WithName("ContactRelay")
        .WithSummary("Relay a contact message to the owner via Cloudflare Email Service");

        return api;
    }

    // Reads the request body with cancellation-aware asynchronous byte reads. Returns null as
    // soon as more than maxBytes have arrived, without buffering further. Synchronous I/O is
    // neither used nor required.
    internal static async Task<byte[]?> ReadBoundedBodyAsync(
        Stream body,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[Math.Min(maxBytes + 1, 8192)];

        while (true)
        {
            var read = await body.ReadAsync(chunk.AsMemory(), cancellationToken);
            if (read == 0)
            {
                return buffer.ToArray();
            }

            if (buffer.Length + read > maxBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }
    }

    private static bool IsBlank(byte[] bytes) =>
        bytes.All(b => b is 0x20 or 0x09 or 0x0D or 0x0A);

    private static string HashClientKey(string key)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(bytes)[..12];
    }
}
