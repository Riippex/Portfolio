using Microsoft.Extensions.Time.Testing;
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

    [Fact]
    public void Expired_timestamps_free_the_client_budget_after_the_window()
    {
        var time = new FakeTimeProvider();
        var limiter = new InMemorySlidingWindowRateLimiter(limit: 2, window: TimeSpan.FromSeconds(60), timeProvider: time);

        Assert.True(limiter.TryAcquire("client-1", out _));
        Assert.True(limiter.TryAcquire("client-1", out _));
        Assert.False(limiter.TryAcquire("client-1", out _));

        time.Advance(TimeSpan.FromSeconds(61));

        Assert.True(limiter.TryAcquire("client-1", out _));
        Assert.True(limiter.TryAcquire("client-1", out _));
        Assert.False(limiter.TryAcquire("client-1", out _));
    }

    [Fact]
    public void Expired_client_entries_are_removed_from_memory()
    {
        var time = new FakeTimeProvider();
        var limiter = new InMemorySlidingWindowRateLimiter(limit: 5, window: TimeSpan.FromSeconds(60), timeProvider: time);

        limiter.TryAcquire("stale-client", out _);
        Assert.Equal(1, limiter.TrackedClientCount);

        time.Advance(TimeSpan.FromSeconds(61));

        limiter.TryAcquire("fresh-client", out _);

        Assert.Equal(1, limiter.TrackedClientCount);
    }

    [Fact]
    public void Rotated_client_keys_do_not_grow_memory_permanently()
    {
        var time = new FakeTimeProvider();
        var limiter = new InMemorySlidingWindowRateLimiter(limit: 10, window: TimeSpan.FromSeconds(60), timeProvider: time);

        for (var round = 0; round < 5; round++)
        {
            for (var i = 0; i < 50; i++)
            {
                Assert.True(limiter.TryAcquire($"spoofed-key-{round}-{i}", out _));
            }

            time.Advance(TimeSpan.FromSeconds(61));
        }

        limiter.TryAcquire("final-client", out _);

        Assert.True(limiter.TrackedClientCount <= 51);
    }

    [Fact]
    public async Task Concurrent_acquisition_and_eviction_never_grant_detached_budget()
    {
        var time = new FakeTimeProvider();
        var limiter = new InMemorySlidingWindowRateLimiter(limit: 10, window: TimeSpan.FromSeconds(60), timeProvider: time);
        const int windows = 3;
        var allowed = new int[1];

        var workers = Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 30; i++)
            {
                if (limiter.TryAcquire("shared-key", out TimeSpan _))
                {
                    Interlocked.Increment(ref allowed[0]);
                }
            }
        })).ToArray();

        for (var w = 0; w < windows; w++)
        {
            time.Advance(TimeSpan.FromSeconds(61));
            await Task.Yield();
        }

        await Task.WhenAll(workers);

        // Without linearized eviction, acquisitions land in detached queues and
        // the window budget is silently reset; the limit must hold per window.
        Assert.True(allowed[0] <= 10 * (windows + 1), $"Granted {allowed[0]} acquisitions across {windows + 1} windows.");
        Assert.True(limiter.TrackedClientCount <= 1);
    }

    [Fact]
    public void Team_tier_grants_higher_limit()
    {
        var limiter = new InMemorySlidingWindowRateLimiter(limit: 5, teamLimit: 15, window: TimeSpan.FromSeconds(60));

        for (var i = 0; i < 15; i++)
        {
            var allowed = limiter.TryAcquire("team-client", isTeamTier: true, countryCode: "US", out var retryAfter);
            Assert.True(allowed);
            Assert.Equal(TimeSpan.Zero, retryAfter);
        }

        var sixteenth = limiter.TryAcquire("team-client", isTeamTier: true, countryCode: "US", out var retry);
        Assert.False(sixteenth);
        Assert.True(retry > TimeSpan.Zero);
    }

    [Fact]
    public void Country_limit_caps_aggregate_traffic_across_ips()
    {
        var limiter = new InMemorySlidingWindowRateLimiter(limit: 5, teamLimit: 15, countryLimit: 10, window: TimeSpan.FromSeconds(60));

        // 2 ordinary clients in same country (each takes 5 reqs -> total 10)
        for (var i = 0; i < 5; i++)
        {
            Assert.True(limiter.TryAcquire("client-1", isTeamTier: false, countryCode: "CO", out _));
            Assert.True(limiter.TryAcquire("client-2", isTeamTier: false, countryCode: "CO", out _));
        }

        // 3rd client in same country is blocked by country limit 10
        var thirdClient = limiter.TryAcquire("client-3", isTeamTier: false, countryCode: "CO", out var retry);
        Assert.False(thirdClient);
        Assert.True(retry > TimeSpan.Zero);

        // Client in a different country is allowed
        var otherCountryClient = limiter.TryAcquire("client-other", isTeamTier: false, countryCode: "MX", out _);
        Assert.True(otherCountryClient);
    }
}
