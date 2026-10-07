using System.Collections.Concurrent;
using Rafael.Portfolio.Modules.Contact.Application;

namespace Rafael.Portfolio.Modules.Contact.Infrastructure;

/// <summary>
/// Sliding-window limiter for contact attempts, held in memory only (zero persistence).
/// </summary>
/// <remarks>
/// Lock order, outermost first: <c>_admissionLock</c>, then <c>_sweepLock</c>, then a
/// client's queue. No code that holds a queue lock ever takes either of the other two, and
/// no code that holds <c>_sweepLock</c> takes <c>_admissionLock</c>, so the order cannot
/// form a cycle.
/// <para>
/// The capacity bound is exact because only one place inserts a key: <c>TryAdmit</c>, under
/// <c>_admissionLock</c>, which checks the count and inserts as one step. Removals (the
/// sweep) only ever lower the count, so a concurrent removal can make the bound conservative
/// for an instant but can never let it be exceeded.
/// </para>
/// </remarks>
public sealed class InMemoryContactRateLimiter : IContactRateLimiter
{
    private const int DefaultMaxCapacity = 10_000;
    private readonly int _limit;
    private readonly TimeSpan _window;
    private readonly int _maxCapacity;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _clients = new();
    private readonly Lock _admissionLock = new();
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
            // Existing identities never touch the admission lock. A missing one is admitted
            // (or refused) atomically; it is never inserted any other way, so a sweep that
            // removes an identity between the lookup and the lock cannot let it back in
            // past the capacity bound.
            if (!_clients.TryGetValue(clientKey, out var queue) &&
                !TryAdmit(clientKey, now, out queue))
            {
                retryAfter = _window;
                return false;
            }

            lock (queue)
            {
                // A concurrent sweep may have detached this queue; retry against the live
                // entry so an attempt is never recorded in an orphaned queue.
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

    // Admission and insertion of a new identity as one atomic step. Returns the live queue
    // (possibly inserted by a racing caller) or false when the limiter is at capacity even
    // after expired identities were removed.
    private bool TryAdmit(string clientKey, DateTimeOffset now, out Queue<DateTimeOffset> queue)
    {
        lock (_admissionLock)
        {
            if (_clients.TryGetValue(clientKey, out var existing))
            {
                queue = existing;
                return true;
            }

            if (_clients.Count >= _maxCapacity)
            {
                SweepExpiredClients(now, force: true);
                if (_clients.Count >= _maxCapacity)
                {
                    queue = null!;
                    return false;
                }
            }

            queue = new Queue<DateTimeOffset>();
            _clients[clientKey] = queue;
            return true;
        }
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
