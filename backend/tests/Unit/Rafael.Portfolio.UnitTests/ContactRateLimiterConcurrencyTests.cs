using Microsoft.Extensions.Time.Testing;
using Rafael.Portfolio.Modules.Contact.Infrastructure;

namespace Rafael.Portfolio.UnitTests;

// Dedicated threads released together by a barrier, so the interleavings the sequential
// limiter tests cannot reach are actually exercised. Every wait is bounded: a lock-order
// deadlock fails the test instead of hanging the run.
public sealed class ContactRateLimiterConcurrencyTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    private static void RunTogether(int threads, Action<int> work)
    {
        using var barrier = new Barrier(threads);
        var errors = new List<Exception>();
        var workers = Enumerable.Range(0, threads).Select(index => new Thread(() =>
        {
            try
            {
                barrier.SignalAndWait();
                work(index);
            }
            catch (Exception ex)
            {
                lock (errors)
                {
                    errors.Add(ex);
                }
            }
        })
        { IsBackground = true }).ToArray();

        foreach (var worker in workers)
        {
            worker.Start();
        }

        foreach (var worker in workers)
        {
            Assert.True(worker.Join(Deadline), "A worker did not finish: possible deadlock.");
        }

        Assert.Empty(errors);
    }

    // Samples the tracked key count while workers run and remembers the highest value seen.
    private sealed class CardinalityMonitor(InMemoryContactRateLimiter limiter) : IDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private int _max;
        private Thread? _thread;

        public int Max => Volatile.Read(ref _max);

        public CardinalityMonitor Start()
        {
            _thread = new Thread(() =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    var seen = limiter.TrackedClientCount;
                    int current;
                    while (seen > (current = Volatile.Read(ref _max)) &&
                           Interlocked.CompareExchange(ref _max, seen, current) != current)
                    {
                    }
                }
            })
            { IsBackground = true };
            _thread.Start();
            return this;
        }

        public void Dispose()
        {
            _stop.Cancel();
            _thread?.Join(Deadline);
            _stop.Dispose();
        }
    }

    [Fact]
    public void Concurrent_distinct_identities_never_exceed_a_capacity_of_one()
    {
        for (var round = 0; round < 200; round++)
        {
            var limiter = new InMemoryContactRateLimiter(limit: 3, window: Window, maxCapacity: 1);
            var admitted = 0;

            RunTogether(16, index =>
            {
                if (limiter.TryAcquire($"identity-{index}", out var retryAfter))
                {
                    Interlocked.Increment(ref admitted);
                }
                else
                {
                    Assert.True(retryAfter > TimeSpan.Zero);
                }
            });

            Assert.Equal(1, limiter.TrackedClientCount);
            Assert.Equal(1, admitted);
        }
    }

    [Theory]
    [InlineData(4, 64)]
    [InlineData(10, 96)]
    public void Concurrent_distinct_identities_never_exceed_the_configured_capacity(int capacity, int identities)
    {
        for (var round = 0; round < 50; round++)
        {
            var limiter = new InMemoryContactRateLimiter(limit: 3, window: Window, maxCapacity: capacity);
            var admitted = 0;

            using var monitor = new CardinalityMonitor(limiter).Start();
            RunTogether(identities, index =>
            {
                if (limiter.TryAcquire($"identity-{index}", out _))
                {
                    Interlocked.Increment(ref admitted);
                }
            });

            Assert.True(monitor.Max <= capacity, $"Tracked {monitor.Max} identities with capacity {capacity}.");
            Assert.Equal(capacity, limiter.TrackedClientCount);
            Assert.Equal(capacity, admitted);
        }
    }

    [Fact]
    public void Concurrent_requests_for_one_identity_never_exceed_its_budget()
    {
        for (var round = 0; round < 100; round++)
        {
            var limiter = new InMemoryContactRateLimiter(limit: 3, window: Window);
            var allowed = 0;
            var rejectedWithRetry = 0;

            RunTogether(32, _ =>
            {
                if (limiter.TryAcquire("one-visitor", out var retryAfter))
                {
                    Interlocked.Increment(ref allowed);
                }
                else if (retryAfter > TimeSpan.Zero)
                {
                    Interlocked.Increment(ref rejectedWithRetry);
                }
            });

            Assert.Equal(3, allowed);
            Assert.Equal(29, rejectedWithRetry);
            Assert.Equal(1, limiter.TrackedClientCount);
        }
    }

    [Fact]
    public void Identities_stay_isolated_under_contention()
    {
        var limiter = new InMemoryContactRateLimiter(limit: 3, window: Window);
        var perIdentity = new int[8];

        RunTogether(64, index =>
        {
            var identity = index % 8;
            if (limiter.TryAcquire($"visitor-{identity}", out _))
            {
                Interlocked.Increment(ref perIdentity[identity]);
            }
        });

        Assert.All(perIdentity, count => Assert.Equal(3, count));
        Assert.Equal(8, limiter.TrackedClientCount);
    }

    [Fact]
    public void Expired_identities_free_capacity_for_concurrent_new_admissions()
    {
        for (var round = 0; round < 100; round++)
        {
            var time = new FakeTimeProvider();
            var limiter = new InMemoryContactRateLimiter(limit: 3, window: Window, maxCapacity: 2, timeProvider: time);
            Assert.True(limiter.TryAcquire("old-a", out _));
            Assert.True(limiter.TryAcquire("old-b", out _));
            Assert.False(limiter.TryAcquire("blocked", out var retryAfter));
            Assert.True(retryAfter > TimeSpan.Zero);

            time.Advance(Window + TimeSpan.FromSeconds(1));
            var admitted = 0;

            using var monitor = new CardinalityMonitor(limiter).Start();
            RunTogether(16, index =>
            {
                if (limiter.TryAcquire($"new-{index}", out _))
                {
                    Interlocked.Increment(ref admitted);
                }
            });

            Assert.True(monitor.Max <= 2, $"Tracked {monitor.Max} identities with capacity 2.");
            Assert.Equal(2, admitted);
            Assert.Equal(2, limiter.TrackedClientCount);
        }
    }

    [Fact]
    public void Time_advancing_during_admission_never_exceeds_capacity_or_deadlocks()
    {
        const int capacity = 10;
        var time = new FakeTimeProvider();
        var limiter = new InMemoryContactRateLimiter(limit: 3, window: TimeSpan.FromSeconds(30), maxCapacity: capacity, timeProvider: time);
        var granted = 0;

        using var monitor = new CardinalityMonitor(limiter).Start();
        RunTogether(17, index =>
        {
            if (index == 16)
            {
                // The clock thread keeps expiring identities while the others admit new ones.
                for (var tick = 0; tick < 400; tick++)
                {
                    time.Advance(TimeSpan.FromSeconds(7));
                    Thread.Yield();
                }

                return;
            }

            for (var i = 0; i < 1500; i++)
            {
                // A pool of 40 identities against capacity 10 forces constant churn.
                if (limiter.TryAcquire($"identity-{(index * 7 + i) % 40}", out _))
                {
                    Interlocked.Increment(ref granted);
                }
            }
        });

        Assert.True(monitor.Max <= capacity, $"Tracked {monitor.Max} identities with capacity {capacity}.");
        Assert.True(limiter.TrackedClientCount <= capacity);
        Assert.True(granted > 0);
    }

    [Fact]
    public void An_identity_never_exceeds_its_budget_while_expiry_and_admission_race()
    {
        // A fixed clock inside one window: whatever the sweeps and admissions do around it,
        // a single identity must never be granted more than its three attempts.
        var time = new FakeTimeProvider();
        var limiter = new InMemoryContactRateLimiter(limit: 3, window: Window, maxCapacity: 4, timeProvider: time);
        var granted = 0;

        RunTogether(24, index =>
        {
            for (var i = 0; i < 200; i++)
            {
                if (index % 2 == 0)
                {
                    if (limiter.TryAcquire("protected", out _))
                    {
                        Interlocked.Increment(ref granted);
                    }
                }
                else
                {
                    // Distinct churn identities push the limiter to capacity and force sweeps.
                    limiter.TryAcquire($"churn-{index}-{i}", out _);
                }
            }
        });

        Assert.True(granted <= 3, $"Granted {granted} attempts to one identity within one window.");
    }
}
