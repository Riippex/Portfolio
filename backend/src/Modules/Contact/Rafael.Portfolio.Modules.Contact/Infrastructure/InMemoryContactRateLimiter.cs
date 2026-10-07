using System.Collections.Concurrent;
using Rafael.Portfolio.Modules.Contact.Application;

namespace Rafael.Portfolio.Modules.Contact.Infrastructure;

public sealed class InMemoryContactRateLimiter : IContactRateLimiter
{
    private const int DefaultMaxCapacity = 10_000;
    private readonly int _limit;
    private readonly TimeSpan _window;
    private readonly int _maxCapacity;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _clients = new();
    private readonly Lock _sweepLock = new();
    private DateTimeOffset _lastSweep;

    public InMemoryContactRateLimiter(
        int limit = 3,
        TimeSpan? window = null,
        int maxCapacity = DefaultMaxCapacity,
        TimeProvider? timeProvider = null)
    {
        _limit = limit > 0 ? limit : 3;
        _window = window ?? TimeSpan.FromMinutes(10);
        _maxCapacity = maxCapacity > 0 ? maxCapacity : DefaultMaxCapacity;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _lastSweep = _timeProvider.GetUtcNow();
    }

    internal int TrackedClientCount => _clients.Count;

    public bool TryAcquire(string clientKey, out TimeSpan retryAfter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientKey);

        var now = _timeProvider.GetUtcNow();
        bool allowed;

        while (true)
        {
            // Enforce maximum capacity bound to prevent exhaustion
            if (!_clients.ContainsKey(clientKey) && _clients.Count >= _maxCapacity)
            {
                SweepExpiredClients(now, force: true);
                if (_clients.Count >= _maxCapacity)
                {
                    retryAfter = _window;
                    return false;
                }
            }

            var queue = _clients.GetOrAdd(clientKey, static _ => new Queue<DateTimeOffset>());

            lock (queue)
            {
                if (!_clients.TryGetValue(clientKey, out var current) ||
                    !ReferenceEquals(current, queue))
                {
                    continue;
                }

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

                break;
            }
        }

        SweepExpiredClients(now);
        return allowed;
    }

    private void SweepExpiredClients(DateTimeOffset now, bool force = false)
    {
        if (!force && now - _lastSweep < _window)
        {
            return;
        }

        lock (_sweepLock)
        {
            if (!force && now - _lastSweep < _window)
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
