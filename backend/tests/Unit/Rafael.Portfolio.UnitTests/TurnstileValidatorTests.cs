using Rafael.Portfolio.Modules.Assistant.Infrastructure;

namespace Rafael.Portfolio.UnitTests;

public sealed class TurnstileValidatorTests
{
    [Fact]
    public async Task Passes_when_secret_key_is_not_configured()
    {
        var validator = new CloudflareTurnstileValidator(httpClient: null, secretKey: null);

        var result = await validator.ValidateAsync(token: null, remoteIp: "127.0.0.1");

        Assert.True(result);
    }

    [Fact]
    public async Task Passes_when_secret_key_is_empty_or_whitespace()
    {
        var validator = new CloudflareTurnstileValidator(httpClient: null, secretKey: "   ");

        var result = await validator.ValidateAsync(token: null, remoteIp: "127.0.0.1");

        Assert.True(result);
    }

    [Fact]
    public async Task Fails_when_secret_key_is_configured_but_token_is_missing()
    {
        var validator = new CloudflareTurnstileValidator(httpClient: null, secretKey: "0x4AAAAAA_test_secret");

        var result = await validator.ValidateAsync(token: null, remoteIp: "127.0.0.1");

        Assert.False(result);
    }

    [Fact]
    public async Task Fails_when_secret_key_is_configured_but_token_is_empty()
    {
        var validator = new CloudflareTurnstileValidator(httpClient: null, secretKey: "0x4AAAAAA_test_secret");

        var result = await validator.ValidateAsync(token: "   ", remoteIp: "127.0.0.1");

        Assert.False(result);
    }
}
