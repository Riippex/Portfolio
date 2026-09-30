using Rafael.Portfolio.Modules.Assistant.Infrastructure;

namespace Rafael.Portfolio.UnitTests;

public sealed class AssistantRateLimiterTests
{
    [Fact]
    public void Allows_requests_up_to_configured_limit()
    {
        var limiter = new InMemorySlidingWindowRateLimiter(limit: 5, window: TimeSpan.FromSeconds(60));

        for (var i = 0; i < 5; i++)
        {
            var allowed = limiter.TryAcquire("client-1", out var retryAfter);
            Assert.True(allowed);
            Assert.Equal(TimeSpan.Zero, retryAfter);
        }
    }

    [Fact]
    public void Rejects_requests_exceeding_limit_with_positive_retry_after()
    {
        var limiter = new InMemorySlidingWindowRateLimiter(limit: 3, window: TimeSpan.FromSeconds(60));

        for (var i = 0; i < 3; i++)
        {
            Assert.True(limiter.TryAcquire("client-2", out _));
        }

        var fourthAllowed = limiter.TryAcquire("client-2", out var retryAfter);

        Assert.False(fourthAllowed);
        Assert.True(retryAfter > TimeSpan.Zero);
        Assert.True(retryAfter <= TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void Rate_limit_is_isolated_per_client_key()
    {
        var limiter = new InMemorySlidingWindowRateLimiter(limit: 2, window: TimeSpan.FromSeconds(60));

        Assert.True(limiter.TryAcquire("client-A", out _));
        Assert.True(limiter.TryAcquire("client-A", out _));
        Assert.False(limiter.TryAcquire("client-A", out _));

        // client-B should still be allowed
        Assert.True(limiter.TryAcquire("client-B", out _));
        Assert.True(limiter.TryAcquire("client-B", out _));
        Assert.False(limiter.TryAcquire("client-B", out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Throws_on_empty_client_key(string invalidKey)
    {
        var limiter = new InMemorySlidingWindowRateLimiter(limit: 5);
        Assert.Throws<ArgumentException>(() => limiter.TryAcquire(invalidKey, out _));
    }
}
