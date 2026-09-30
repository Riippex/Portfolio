using System.Net;
using System.Text;
using Rafael.Portfolio.Modules.Assistant.Infrastructure;

namespace Rafael.Portfolio.UnitTests;

public sealed class TurnstileValidatorTests
{
    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly string _body;
        private readonly Exception? _exception;

        public StubHttpMessageHandler(HttpStatusCode statusCode, string body)
        {
            _statusCode = statusCode;
            _body = body;
        }

        public StubHttpMessageHandler(Exception exception)
        {
            _statusCode = HttpStatusCode.OK;
            _body = string.Empty;
            _exception = exception;
        }

        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;

            if (_exception is not null)
            {
                throw _exception;
            }

            return Task.FromResult(new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json")
            });
        }
    }

    private const string TestSecret = "0x4AAAAAA_test_secret";

    [Fact]
    public void Constructor_throws_when_secret_key_is_missing()
    {
        using var httpClient = new HttpClient();

        Assert.Throws<ArgumentException>(() => new CloudflareTurnstileValidator(httpClient, null!));
        Assert.Throws<ArgumentException>(() => new CloudflareTurnstileValidator(httpClient, "   "));
    }

    [Fact]
    public void Constructor_throws_when_http_client_is_missing()
    {
        Assert.Throws<ArgumentNullException>(() => new CloudflareTurnstileValidator(null!, TestSecret));
    }

    [Fact]
    public async Task Missing_token_fails_closed_without_calling_cloudflare()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, """{"success":true}""");
        using var httpClient = new HttpClient(handler);
        var validator = new CloudflareTurnstileValidator(httpClient, TestSecret);

        Assert.False(await validator.ValidateAsync(null, "127.0.0.1"));
        Assert.False(await validator.ValidateAsync("   ", "127.0.0.1"));
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Valid_token_passes_when_cloudflare_confirms()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, """{"success":true}""");
        using var httpClient = new HttpClient(handler);
        var validator = new CloudflareTurnstileValidator(httpClient, TestSecret);

        Assert.True(await validator.ValidateAsync("valid-token", "127.0.0.1"));
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Invalid_token_fails_when_cloudflare_rejects()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, """{"success":false}""");
        using var httpClient = new HttpClient(handler);
        var validator = new CloudflareTurnstileValidator(httpClient, TestSecret);

        Assert.False(await validator.ValidateAsync("forged-token", "127.0.0.1"));
    }

    [Fact]
    public async Task Http_failure_fails_closed()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.InternalServerError, """{}""");
        using var httpClient = new HttpClient(handler);
        var validator = new CloudflareTurnstileValidator(httpClient, TestSecret);

        Assert.False(await validator.ValidateAsync("some-token", "127.0.0.1"));
    }

    [Fact]
    public async Task Network_error_fails_closed()
    {
        var handler = new StubHttpMessageHandler(new HttpRequestException("network unreachable"));
        using var httpClient = new HttpClient(handler);
        var validator = new CloudflareTurnstileValidator(httpClient, TestSecret);

        Assert.False(await validator.ValidateAsync("some-token", "127.0.0.1"));
    }

    [Fact]
    public async Task Disabled_validator_bypasses_only_when_composed_for_development()
    {
        var validator = new DisabledTurnstileValidator();

        Assert.True(await validator.ValidateAsync(null, null));
    }
}
