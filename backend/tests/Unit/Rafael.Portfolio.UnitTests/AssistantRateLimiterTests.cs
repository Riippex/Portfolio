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

    [Fact]
    public void Team_traffic_counts_toward_the_country_limit()
    {
        var limiter = new InMemorySlidingWindowRateLimiter(limit: 5, teamLimit: 15, countryLimit: 10, window: TimeSpan.FromSeconds(60));

        for (var i = 0; i < 5; i++)
        {
            Assert.True(limiter.TryAcquire("team-1", isTeamTier: true, countryCode: "CO", out _));
            Assert.True(limiter.TryAcquire("team-2", isTeamTier: true, countryCode: "CO", out _));
        }

        Assert.False(limiter.TryAcquire("team-3", isTeamTier: true, countryCode: "CO", out var retry));
        Assert.True(retry > TimeSpan.Zero);
        Assert.False(limiter.TryAcquire("ordinary", isTeamTier: false, countryCode: "CO", out _));
        Assert.True(limiter.TryAcquire("team-3", isTeamTier: true, countryCode: "MX", out _));
    }

    [Fact]
    public void Country_codes_are_normalized_so_spellings_share_one_aggregate()
    {
        var limiter = new InMemorySlidingWindowRateLimiter(limit: 5, countryLimit: 2, window: TimeSpan.FromSeconds(60));

        Assert.True(limiter.TryAcquire("a", false, "co", out _));
        Assert.True(limiter.TryAcquire("b", false, " CO ", out _));
        Assert.False(limiter.TryAcquire("c", false, "Co", out _));
    }

    [Fact]
    public void Chat_and_job_calls_share_one_visitor_quota()
    {
        // Both endpoints call the same limiter with the same visitor key.
        var limiter = new InMemorySlidingWindowRateLimiter(limit: 5, window: TimeSpan.FromSeconds(60));

        for (var i = 0; i < 3; i++)
        {
            Assert.True(limiter.TryAcquire("shared-visitor", false, "CO", out _));
        }

        for (var i = 0; i < 2; i++)
        {
            Assert.True(limiter.TryAcquire("shared-visitor", false, "CO", out _));
        }

        Assert.False(limiter.TryAcquire("shared-visitor", false, "CO", out _));
    }

    [Fact]
    public void A_new_visitor_is_refused_at_capacity_even_when_its_country_key_already_exists()
    {
        var limiter = new InMemorySlidingWindowRateLimiter(limit: 5, window: TimeSpan.FromSeconds(60), maxTrackedKeys: 4);

        // visitor-1, visitor-2, visitor-3 and country:CO fill the table.
        for (var i = 1; i <= 3; i++)
        {
            Assert.True(limiter.TryAcquire($"visitor-{i}", false, "CO", out _));
        }

        Assert.Equal(4, limiter.TrackedClientCount);

        Assert.False(limiter.TryAcquire("visitor-4", false, "CO", out var retry));
        Assert.True(retry > TimeSpan.Zero);
        Assert.Equal(4, limiter.TrackedClientCount);

        // Known visitors keep working.
        Assert.True(limiter.TryAcquire("visitor-1", false, "CO", out _));
    }

    [Fact]
    public void A_visitor_with_a_new_country_needs_room_for_both_keys()
    {
        var limiter = new InMemorySlidingWindowRateLimiter(limit: 5, window: TimeSpan.FromSeconds(60), maxTrackedKeys: 3);

        Assert.True(limiter.TryAcquire("visitor-1", false, "CO", out _));
        Assert.Equal(2, limiter.TrackedClientCount);

        // Two missing keys do not fit in the one free slot, so nothing is inserted.
        Assert.False(limiter.TryAcquire("visitor-2", false, "MX", out _));
        Assert.Equal(2, limiter.TrackedClientCount);

        Assert.True(limiter.TryAcquire("visitor-2", false, "CO", out _));
        Assert.Equal(3, limiter.TrackedClientCount);
    }

    [Fact]
    public void Refused_traffic_never_grows_the_table_past_capacity()
    {
        var limiter = new InMemorySlidingWindowRateLimiter(limit: 5, window: TimeSpan.FromSeconds(60), maxTrackedKeys: 50);

        for (var i = 0; i < 5_000; i++)
        {
            limiter.TryAcquire($"rotated-{i}", false, i % 2 == 0 ? "CO" : "MX", out _);
            Assert.True(limiter.TrackedClientCount <= 50, $"Tracked {limiter.TrackedClientCount} keys after {i + 1} requests.");
        }
    }

    [Fact]
    public void A_visitor_refused_only_for_its_country_leaves_no_key_behind()
    {
        var limiter = new InMemorySlidingWindowRateLimiter(limit: 5, countryLimit: 2, window: TimeSpan.FromSeconds(60));

        Assert.True(limiter.TryAcquire("visitor-1", false, "CO", out _));
        Assert.True(limiter.TryAcquire("visitor-2", false, "CO", out _));
        Assert.Equal(3, limiter.TrackedClientCount);

        for (var i = 3; i < 500; i++)
        {
            Assert.False(limiter.TryAcquire($"visitor-{i}", false, "CO", out _));
        }

        Assert.Equal(3, limiter.TrackedClientCount);
    }

    [Fact]
    public void Capacity_is_reclaimed_once_entries_expire()
    {
        var time = new FakeTimeProvider();
        var limiter = new InMemorySlidingWindowRateLimiter(limit: 5, window: TimeSpan.FromSeconds(60), timeProvider: time, maxTrackedKeys: 3);

        Assert.True(limiter.TryAcquire("visitor-1", false, "CO", out _));
        Assert.True(limiter.TryAcquire("visitor-2", false, "CO", out _));
        Assert.False(limiter.TryAcquire("visitor-3", false, "CO", out _));

        time.Advance(TimeSpan.FromSeconds(61));

        Assert.True(limiter.TryAcquire("visitor-3", false, "CO", out _));
        Assert.True(limiter.TryAcquire("visitor-4", false, "CO", out _));
        Assert.True(limiter.TrackedClientCount <= 3);
    }

    [Fact]
    public async Task Concurrent_admissions_never_exceed_the_capacity_bound()
    {
        const int capacity = 40;
        var limiter = new InMemorySlidingWindowRateLimiter(limit: 5, window: TimeSpan.FromSeconds(60), maxTrackedKeys: capacity);
        var highWater = 0;
        using var stop = new CancellationTokenSource();

        var monitor = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                var observed = limiter.TrackedClientCount;
                int current;
                while (observed > (current = Volatile.Read(ref highWater)))
                {
                    Interlocked.CompareExchange(ref highWater, observed, current);
                }
            }
        });

        var admitted = 0;
        var workers = Enumerable.Range(0, 16).Select(worker => Task.Run(() =>
        {
            for (var i = 0; i < 400; i++)
            {
                // Every worker uses its own new visitors, but they all share two countries.
                if (limiter.TryAcquire($"visitor-{worker}-{i}", false, i % 2 == 0 ? "CO" : "MX", out _))
                {
                    Interlocked.Increment(ref admitted);
                }
            }
        })).ToArray();

        await Task.WhenAll(workers);
        await stop.CancelAsync();
        await monitor;

        Assert.True(Volatile.Read(ref highWater) <= capacity, $"Observed {highWater} tracked keys with capacity {capacity}.");
        Assert.True(limiter.TrackedClientCount <= capacity);
        // Two country keys plus at most capacity - 2 visitors can ever have been admitted.
        Assert.True(admitted <= capacity - 2, $"Admitted {admitted} new visitors with capacity {capacity}.");
        Assert.True(admitted > 0);
    }

    [Fact]
    public async Task Concurrent_requests_cannot_exceed_a_visitor_or_country_limit()
    {
        var limiter = new InMemorySlidingWindowRateLimiter(limit: 5, teamLimit: 15, countryLimit: 20, window: TimeSpan.FromSeconds(60));
        var admitted = 0;

        var workers = Enumerable.Range(0, 16).Select(worker => Task.Run(() =>
        {
            for (var i = 0; i < 100; i++)
            {
                if (limiter.TryAcquire($"visitor-{worker % 8}", isTeamTier: worker % 2 == 0, "CO", out _))
                {
                    Interlocked.Increment(ref admitted);
                }
            }
        })).ToArray();

        await Task.WhenAll(workers);

        Assert.Equal(20, admitted);
    }
}
