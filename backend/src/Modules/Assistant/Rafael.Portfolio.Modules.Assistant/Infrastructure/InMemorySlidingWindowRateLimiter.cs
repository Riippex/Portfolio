using System.Collections.Concurrent;
using Rafael.Portfolio.Modules.Assistant.Application;

namespace Rafael.Portfolio.Modules.Assistant.Infrastructure;

public sealed class InMemorySlidingWindowRateLimiter : IAssistantRateLimiter
{
    private readonly int _limit;
    private readonly TimeSpan _window;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _clients = new();
    private readonly Lock _sweepLock = new();
    private DateTimeOffset _lastSweep;

    public InMemorySlidingWindowRateLimiter(
        int limit = 10,
        TimeSpan? window = null,
        TimeProvider? timeProvider = null)
    {
        _limit = limit > 0 ? limit : 10;
        _window = window ?? TimeSpan.FromSeconds(60);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _lastSweep = _timeProvider.GetUtcNow();
    }

    internal int TrackedClientCount => _clients.Count;

    public bool TryAcquire(string clientKey, out TimeSpan retryAfter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientKey);

        var now = _timeProvider.GetUtcNow();
        var queue = _clients.GetOrAdd(clientKey, static _ => new Queue<DateTimeOffset>());

        bool allowed;
        lock (queue)
        {
            Prune(queue, now);

            if (queue.Count < _limit)
            {
                queue.Enqueue(now);
                retryAfter = TimeSpan.Zero;
                allowed = true;
            }
            else
            {
                var oldest = queue.Peek();
                var remaining = _window - (now - oldest);
                retryAfter = remaining > TimeSpan.Zero ? remaining : TimeSpan.FromSeconds(1);
                allowed = false;
            }
        }

        SweepExpiredClients(now);
        return allowed;
    }

    private void SweepExpiredClients(DateTimeOffset now)
    {
        if (now - _lastSweep < _window)
        {
            return;
        }

        lock (_sweepLock)
        {
            if (now - _lastSweep < _window)
            {
                return;
            }

            _lastSweep = now;

            foreach (var (key, queue) in _clients)
            {
                lock (queue)
                {
                    Prune(queue, now);
                    if (queue.Count == 0)
                    {
                        _clients.TryRemove(new KeyValuePair<string, Queue<DateTimeOffset>>(key, queue));
                    }
                }
            }
        }
    }

    private void Prune(Queue<DateTimeOffset> queue, DateTimeOffset now)
    {
        var cutoff = now - _window;
        while (queue.Count > 0 && queue.Peek() <= cutoff)
        {
            queue.Dequeue();
        }
    }
}
