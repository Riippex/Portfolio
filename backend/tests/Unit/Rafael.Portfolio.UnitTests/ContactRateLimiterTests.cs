using Microsoft.Extensions.Time.Testing;
using Rafael.Portfolio.Modules.Contact.Infrastructure;

namespace Rafael.Portfolio.UnitTests;

public sealed class ContactRateLimiterTests
{
    [Fact]
    public void Allows_up_to_default_three_requests_per_window()
    {
        var limiter = new InMemoryContactRateLimiter(limit: 3, window: TimeSpan.FromMinutes(10));

        for (var i = 0; i < 3; i++)
        {
            var allowed = limiter.TryAcquire("client-1", out var retryAfter);
            Assert.True(allowed);
            Assert.Equal(TimeSpan.Zero, retryAfter);
        }
    }

    [Fact]
    public void Rejects_fourth_request_with_positive_retry_after()
    {
        var limiter = new InMemoryContactRateLimiter(limit: 3, window: TimeSpan.FromMinutes(10));

        for (var i = 0; i < 3; i++)
        {
            Assert.True(limiter.TryAcquire("client-1", out _));
        }

        var allowed = limiter.TryAcquire("client-1", out var retryAfter);

        Assert.False(allowed);
        Assert.True(retryAfter > TimeSpan.Zero);
        Assert.True(retryAfter <= TimeSpan.FromMinutes(10));
    }

    [Fact]
    public void Rate_limit_is_isolated_between_different_client_keys()
    {
        var limiter = new InMemoryContactRateLimiter(limit: 3, window: TimeSpan.FromMinutes(10));

        for (var i = 0; i < 3; i++)
        {
            Assert.True(limiter.TryAcquire("client-A", out _));
        }
        Assert.False(limiter.TryAcquire("client-A", out _));

        // client-B has its own budget
        Assert.True(limiter.TryAcquire("client-B", out _));
    }

    [Fact]
    public void Re_enables_acquisition_after_sliding_window_expires()
    {
        var time = new FakeTimeProvider();
        var limiter = new InMemoryContactRateLimiter(
            limit: 3,
            window: TimeSpan.FromMinutes(10),
            timeProvider: time);

        for (var i = 0; i < 3; i++)
        {
            Assert.True(limiter.TryAcquire("client-1", out _));
        }
        Assert.False(limiter.TryAcquire("client-1", out _));

        time.Advance(TimeSpan.FromMinutes(10).Add(TimeSpan.FromSeconds(1)));

        Assert.True(limiter.TryAcquire("client-1", out _));
    }

    [Fact]
    public void Capacity_bound_prevents_unbounded_cardinality()
    {
        var time = new FakeTimeProvider();
        // Capacity of 5 keys
        var limiter = new InMemoryContactRateLimiter(
            limit: 3,
            window: TimeSpan.FromMinutes(10),
            maxCapacity: 5,
            timeProvider: time);

        for (var i = 0; i < 5; i++)
        {
            Assert.True(limiter.TryAcquire($"key-{i}", out _));
        }

        // 6th key should be rejected or evicted
        var allowed = limiter.TryAcquire("key-5", out var retryAfter);
        Assert.False(allowed);
        Assert.True(retryAfter > TimeSpan.Zero);

        // Advance time to allow eviction
        time.Advance(TimeSpan.FromMinutes(11));

        // Now new key can be acquired
        Assert.True(limiter.TryAcquire("key-new", out _));
    }
}
