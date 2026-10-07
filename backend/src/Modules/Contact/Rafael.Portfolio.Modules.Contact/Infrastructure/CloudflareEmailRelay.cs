using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Rafael.Portfolio.Modules.Contact.Application;
using Rafael.Portfolio.Modules.Contact.Domain;

namespace Rafael.Portfolio.Modules.Contact.Infrastructure;

public sealed class CloudflareEmailRelay : IContactRelay
{
    /// <summary>Static subject: visitor-controlled text never reaches a header.</summary>
    public const string Subject = "Portfolio contact message";

    /// <summary>Upper bound on the provider response body; a success envelope is far smaller.</summary>
    public const int DefaultMaxResponseBytes = 16 * 1024;

    /// <summary>One deadline covers the request, the response headers, and the body read.</summary>
    public static readonly TimeSpan DefaultDeadline = TimeSpan.FromSeconds(15);

    private readonly HttpClient _httpClient;
    private readonly ContactOptions _options;
    private readonly TimeSpan _deadline;
    private readonly int _maxResponseBytes;

    public CloudflareEmailRelay(HttpClient httpClient, ContactOptions options)
        : this(httpClient, options, DefaultDeadline, DefaultMaxResponseBytes)
    {
    }

    public CloudflareEmailRelay(
        HttpClient httpClient,
        ContactOptions options,
        TimeSpan deadline,
        int maxResponseBytes)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(deadline, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxResponseBytes, 0);
        _deadline = deadline;
        _maxResponseBytes = maxResponseBytes;
    }

    public async Task<ContactDeliveryResult> RelayAsync(
        ContactMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (!_options.Enabled || !_options.IsConfigured)
        {
            return ContactDeliveryResult.Unavailable("Contact service is not enabled or configured.");
        }

        var endpoint = $"https://api.cloudflare.com/client/v4/accounts/{_options.AccountId}/email/sending/send";

        var payload = new CloudflareSendPayload
        {
            From = _options.SenderEmail!,
            To = _options.RecipientEmail!,
            ReplyTo = message.Email,
            Subject = Subject,
            Text = $"From: {message.Name} <{message.Email}>\n\nMessage:\n{message.Message}"
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(payload, options: JsonContext.DefaultOptions)
        };

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        // A single attempt under one deadline. There is deliberately no retry: after an
        // ambiguous outcome the provider may already have accepted the message, and a
        // second send would deliver a duplicate.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_deadline);
        var token = deadline.Token;

        try
        {
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);

            // HTTP success is required before the body is even considered: a 4xx/5xx
            // that carries a success-looking envelope must never become a delivery.
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return ContactDeliveryResult.Failed("provider_auth_error", "Provider rejected authentication.");
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                return ContactDeliveryResult.Failed("provider_rate_limited", "Provider rate limit reached.");
            }

            if (!response.IsSuccessStatusCode)
            {
                return (int)response.StatusCode >= 500
                    ? ContactDeliveryResult.Failed("provider_error", "Provider reported a server error.")
                    : ContactDeliveryResult.Failed("provider_rejected", "Provider rejected the message.");
            }

            if (response.Content.Headers.ContentLength > _maxResponseBytes)
            {
                return ContactDeliveryResult.Failed("provider_response_too_large", "Provider response exceeded the size limit.");
            }

            var body = await ReadBoundedAsync(response.Content, token);
            if (body is null)
            {
                return ContactDeliveryResult.Failed("provider_response_too_large", "Provider response exceeded the size limit.");
            }

            return Interpret(body);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ContactDeliveryResult.Failed("canceled", "Delivery was canceled.");
        }
        catch (OperationCanceledException)
        {
            // Deadline elapsed after the request may have been sent: ambiguous, never retried.
            return ContactDeliveryResult.Failed("timeout", "Delivery timed out.");
        }
        catch (TimeoutException)
        {
            return ContactDeliveryResult.Failed("timeout", "Delivery timed out.");
        }
        catch (HttpRequestException)
        {
            return ContactDeliveryResult.Failed("transport_error", "Error connecting to delivery provider.");
        }
        catch (IOException)
        {
            return ContactDeliveryResult.Failed("transport_error", "Error reading the provider response.");
        }
        catch (Exception)
        {
            return ContactDeliveryResult.Failed("internal_error", "Unexpected error during delivery.");
        }
    }

    // Reads at most _maxResponseBytes, counting bytes as they arrive so a response with
    // no Content-Length (chunked, or a lying length) can never be buffered unbounded.
    // Returns null when the limit is exceeded.
    private async Task<byte[]?> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[Math.Min(_maxResponseBytes + 1, 4096)];

        while (true)
        {
            var read = await stream.ReadAsync(chunk.AsMemory(), cancellationToken);
            if (read == 0)
            {
                return buffer.ToArray();
            }

            if (buffer.Length + read > _maxResponseBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }
    }

    private ContactDeliveryResult Interpret(byte[] body)
    {
        if (body.Length == 0)
        {
            return ContactDeliveryResult.Failed("malformed_provider_response", "Empty provider response.");
        }

        CloudflareApiResponse? apiResponse;
        try
        {
            apiResponse = JsonSerializer.Deserialize<CloudflareApiResponse>(body, JsonContext.DefaultOptions);
        }
        catch (JsonException)
        {
            return ContactDeliveryResult.Failed("malformed_provider_response", "Malformed provider response.");
        }

        if (apiResponse is null)
        {
            return ContactDeliveryResult.Failed("malformed_provider_response", "Empty provider response.");
        }

        if (!apiResponse.Success)
        {
            return ContactDeliveryResult.Failed("provider_rejected", "Provider rejected the message.");
        }

        if (apiResponse.Result is null)
        {
            return ContactDeliveryResult.Failed("empty_provider_result", "Provider returned empty result.");
        }

        var recipient = _options.RecipientEmail!;

        if (apiResponse.Result.Delivered?.Any(r => string.Equals(r, recipient, StringComparison.OrdinalIgnoreCase)) is true)
        {
            return ContactDeliveryResult.Delivered();
        }

        if (apiResponse.Result.Queued?.Any(r => string.Equals(r, recipient, StringComparison.OrdinalIgnoreCase)) is true)
        {
            return ContactDeliveryResult.Queued();
        }

        if (apiResponse.Result.PermanentBounces?.Any(r => string.Equals(r, recipient, StringComparison.OrdinalIgnoreCase)) is true)
        {
            return ContactDeliveryResult.Failed("permanent_bounce", "Recipient address bounced permanently.");
        }

        return ContactDeliveryResult.Failed("delivery_unconfirmed", "Recipient was not accepted by provider.");
    }

    private sealed class CloudflareSendPayload
    {
        [JsonPropertyName("from")]
        public required string From { get; init; }

        [JsonPropertyName("to")]
        public required string To { get; init; }

        [JsonPropertyName("reply_to")]
        public required string ReplyTo { get; init; }

        [JsonPropertyName("subject")]
        public required string Subject { get; init; }

        [JsonPropertyName("text")]
        public required string Text { get; init; }
    }

    private sealed class CloudflareApiResponse
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("result")]
        public CloudflareResult? Result { get; set; }

        [JsonPropertyName("errors")]
        public List<CloudflareError>? Errors { get; set; }
    }

    private sealed class CloudflareResult
    {
        [JsonPropertyName("delivered")]
        public List<string>? Delivered { get; set; }

        [JsonPropertyName("queued")]
        public List<string>? Queued { get; set; }

        [JsonPropertyName("permanent_bounces")]
        public List<string>? PermanentBounces { get; set; }
    }

    private sealed class CloudflareError
    {
        [JsonPropertyName("code")]
        public int Code { get; set; }

        [JsonPropertyName("message")]
        public string? Message { get; set; }
    }

    private static class JsonContext
    {
        public static readonly JsonSerializerOptions DefaultOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };
    }
}
