using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Rafael.Portfolio.Modules.Assistant.Application;

namespace Rafael.Portfolio.Modules.Assistant.Infrastructure;

public sealed class CloudflareTurnstileValidator : ITurnstileValidator
{
    private readonly HttpClient? _httpClient;
    private readonly string? _secretKey;

    public CloudflareTurnstileValidator(HttpClient? httpClient = null, string? secretKey = null)
    {
        _httpClient = httpClient;
        _secretKey = secretKey;
    }

    public async Task<bool> ValidateAsync(string? token, string? remoteIp, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_secretKey))
        {
            // Bypassed when secret key is not configured (e.g. local dev, test environments)
            return true;
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        if (_httpClient is null)
        {
            return true;
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
            // Fail closed on error when Turnstile is enforced
            return false;
        }
    }

    private sealed class TurnstileResponse
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }
    }
}
