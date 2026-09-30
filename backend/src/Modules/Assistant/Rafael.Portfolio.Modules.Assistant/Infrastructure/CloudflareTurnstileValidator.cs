using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Rafael.Portfolio.Modules.Assistant.Application;

namespace Rafael.Portfolio.Modules.Assistant.Infrastructure;

public sealed class CloudflareTurnstileValidator : ITurnstileValidator
{
    private readonly HttpClient _httpClient;
    private readonly string _secretKey;

    public CloudflareTurnstileValidator(HttpClient httpClient, string secretKey)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

        if (string.IsNullOrWhiteSpace(secretKey))
        {
            throw new ArgumentException(
                "A Turnstile secret key is required to construct the validator. " +
                "Use the development bypass only when no secret is configured.",
                nameof(secretKey));
        }

        _secretKey = secretKey;
    }

    public async Task<bool> ValidateAsync(string? token, string? remoteIp, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        try
        {
            var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["secret"] = _secretKey,
                ["response"] = token,
                ["remoteip"] = remoteIp ?? string.Empty
            });

            var response = await _httpClient.PostAsync(
                "https://challenges.cloudflare.com/turnstile/v0/siteverify",
                content,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            var result = await response.Content.ReadFromJsonAsync<TurnstileResponse>(cancellationToken);
            return result?.Success is true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private sealed class TurnstileResponse
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }
    }
}
