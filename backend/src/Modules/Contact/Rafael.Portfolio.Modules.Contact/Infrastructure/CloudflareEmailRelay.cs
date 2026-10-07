using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Rafael.Portfolio.Modules.Contact.Application;
using Rafael.Portfolio.Modules.Contact.Domain;

namespace Rafael.Portfolio.Modules.Contact.Infrastructure;

public sealed class CloudflareEmailRelay : IContactRelay
{
    private readonly HttpClient _httpClient;
    private readonly ContactOptions _options;

    public CloudflareEmailRelay(HttpClient httpClient, ContactOptions options)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
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
            Subject = $"Portfolio Contact: {message.Name}",
            Text = $"From: {message.Name} <{message.Email}>\n\nMessage:\n{message.Message}"
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(payload, options: JsonContext.DefaultOptions)
        };

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken);

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized ||
                response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                return ContactDeliveryResult.Failed("provider_auth_error", "Provider rejected authentication.");
            }

            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                return ContactDeliveryResult.Failed("provider_rate_limited", "Provider rate limit reached.");
            }

            CloudflareApiResponse? apiResponse;
            try
            {
                apiResponse = await response.Content.ReadFromJsonAsync<CloudflareApiResponse>(
                    JsonContext.DefaultOptions,
                    cancellationToken);
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ContactDeliveryResult.Failed("canceled", "Delivery was canceled.");
        }
        catch (OperationCanceledException)
        {
            // Ambiguous timeout: do NOT auto-retry to prevent duplicate email sends
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
        catch (Exception)
        {
            return ContactDeliveryResult.Failed("internal_error", "Unexpected error during delivery.");
        }
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
