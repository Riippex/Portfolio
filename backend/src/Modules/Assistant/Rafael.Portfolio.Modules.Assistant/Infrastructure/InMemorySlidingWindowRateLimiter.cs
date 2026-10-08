using System.Collections.Concurrent;
using Rafael.Portfolio.Modules.Assistant.Application;

namespace Rafael.Portfolio.Modules.Assistant.Infrastructure;

/// <summary>
/// Sliding-window limiter for the shared chat and job-analysis quota, held in memory only.
/// Each request is counted against its visitor key and, when a country is known, against that
/// country's aggregate key; team visitors get a higher per-visitor limit but still count
/// toward the country limit.
/// </summary>
/// <remarks>
/// Lock order, outermost first: <c>_admissionLock</c>, then <c>_sweepLock</c>, then a key's
/// queue (the visitor and country queues are taken in key order). No code that holds a queue
/// lock ever takes either of the other two, and no code that holds <c>_sweepLock</c> takes
/// <c>_admissionLock</c>, so the order cannot form a cycle.
/// <para>
/// The capacity bound is exact because the only place a key is inserted is
/// <c>TryAdmit</c>, under <c>_admissionLock</c>, which checks the count for every key the
/// request needs and inserts them as one step. A request that cannot be admitted inserts
/// nothing, so refused traffic cannot grow the table, whether or not a country key already
/// exists. Removals (the sweep) only lower the count, so a concurrent removal can make the
/// bound conservative for an instant but never let it be exceeded.
/// </para>
/// </remarks>
public sealed class InMemorySlidingWindowRateLimiter : IAssistantRateLimiter
{
    private readonly int _ordinaryLimit;
    private readonly int _teamLimit;
    private readonly int _countryLimit;
    private readonly int _maxTrackedKeys;
    private readonly TimeSpan _window;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _clients = new();
    private readonly Lock _admissionLock = new();
    private readonly Lock _sweepLock = new();
    private DateTimeOffset _lastSweep;

    public InMemorySlidingWindowRateLimiter(
        int limit = 5,
        TimeSpan? window = null,
        TimeProvider? timeProvider = null,
        int teamLimit = 15,
        int countryLimit = 100,
        int maxTrackedKeys = 10000)
    {
        _ordinaryLimit = limit > 0 ? limit : 5;
        _teamLimit = teamLimit > 0 ? teamLimit : 15;
        _countryLimit = countryLimit > 0 ? countryLimit : 100;
        _maxTrackedKeys = maxTrackedKeys > 0 ? maxTrackedKeys : 10000;
        _window = window ?? TimeSpan.FromSeconds(60);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _lastSweep = _timeProvider.GetUtcNow();
    }

    internal int TrackedClientCount => _clients.Count;

    public bool TryAcquire(string clientKey, out TimeSpan retryAfter) =>
        TryAcquire(clientKey, isTeamTier: false, countryCode: null, out retryAfter);

    public bool TryAcquire(
        string clientKey,
        bool isTeamTier,
        string? countryCode,
        out TimeSpan retryAfter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientKey);

        var now = _timeProvider.GetUtcNow();
        var ipLimit = isTeamTier ? _teamLimit : _ordinaryLimit;
        var normalizedCountry = string.IsNullOrWhiteSpace(countryCode) ? null : countryCode.Trim().ToUpperInvariant();
        var countryKey = normalizedCountry is null ? null : $"country:{normalizedCountry}";

        var allowed = TryCheckAndReserve(clientKey, ipLimit, countryKey, _countryLimit, now, out retryAfter);
        SweepExpiredClients(now);
        return allowed;
    }

    private bool TryCheckAndReserve(
        string ipKey,
        int ipLimit,
        string? countryKey,
        int countryLimit,
        DateTimeOffset now,
        out TimeSpan retryAfter)
    {
        while (true)
        {
            // Existing keys never touch the admission lock. Missing keys are admitted (or the
            // whole request refused) atomically; they are never inserted any other way, so a
            // sweep that removes a key between this lookup and the locks below cannot let a
            // new one in past the capacity bound.
            if (!TryAdmit(ipKey, countryKey, now, out var ipQueue, out var countryQueue))
            {
                retryAfter = _window;
                return false;
            }

            if (countryQueue is null)
            {
                lock (ipQueue)
                {
                    if (!IsLive(ipKey, ipQueue))
                    {
                        continue;
                    }

                    Prune(ipQueue, now);

                    if (ipQueue.Count >= ipLimit)
                    {
                        retryAfter = RetryAfterFor(ipQueue, now);
                        return false;
                    }

                    ipQueue.Enqueue(now);
                    retryAfter = TimeSpan.Zero;
                    return true;
                }
            }

            var firstKey = string.CompareOrdinal(ipKey, countryKey) <= 0 ? ipKey : countryKey!;
            var firstQueue = ReferenceEquals(firstKey, ipKey) ? ipQueue : countryQueue;
            var secondQueue = ReferenceEquals(firstKey, ipKey) ? countryQueue : ipQueue;

            lock (firstQueue)
            {
                lock (secondQueue)
                {
                    // A concurrent sweep may have detached either queue; retry against the
                    // live entries so an attempt is never recorded in an orphaned queue.
                    if (!IsLive(ipKey, ipQueue) || !IsLive(countryKey!, countryQueue))
                    {
                        continue;
                    }

                    Prune(ipQueue, now);
                    Prune(countryQueue, now);

                    if (ipQueue.Count >= ipLimit)
                    {
                        retryAfter = RetryAfterFor(ipQueue, now);
                        return false;
                    }

                    if (countryQueue.Count >= countryLimit)
                    {
                        retryAfter = RetryAfterFor(countryQueue, now);

                        // A visitor refused only for its country leaves no empty key behind.
                        // Removal happens under the queue lock, so a waiter sees it as detached.
                        if (ipQueue.Count == 0)
                        {
                            _clients.TryRemove(new KeyValuePair<string, Queue<DateTimeOffset>>(ipKey, ipQueue));
                        }

                        return false;
                    }

                    ipQueue.Enqueue(now);
                    if (!string.Equals(ipKey, countryKey, StringComparison.Ordinal))
                    {
                        countryQueue.Enqueue(now);
                    }

                    retryAfter = TimeSpan.Zero;
                    return true;
                }
            }
        }
    }

    // Returns the live queues for the visitor key and (when given) the country key, inserting
    // any that are missing in one atomic step, or false when the table cannot hold them even
    // after expired keys were removed. All-or-nothing: a refused request leaves no key behind.
    private bool TryAdmit(
        string ipKey,
        string? countryKey,
        DateTimeOffset now,
        out Queue<DateTimeOffset> ipQueue,
        out Queue<DateTimeOffset>? countryQueue)
    {
        if (_clients.TryGetValue(ipKey, out ipQueue!))
        {
            if (countryKey is null)
            {
                countryQueue = null;
                return true;
            }

            if (_clients.TryGetValue(countryKey, out countryQueue))
            {
                return true;
            }
        }

        lock (_admissionLock)
        {
            if (!HasRoomFor(ipKey, countryKey))
            {
                SweepExpiredClients(now, force: true);
                if (!HasRoomFor(ipKey, countryKey))
                {
                    ipQueue = null!;
                    countryQueue = null;
                    return false;
                }
            }

            ipQueue = _clients.GetOrAdd(ipKey, static _ => new Queue<DateTimeOffset>());
            countryQueue = countryKey is null
                ? null
                : _clients.GetOrAdd(countryKey, static _ => new Queue<DateTimeOffset>());
            return true;
        }
    }

    // Called under _admissionLock, the only place that inserts keys.
    private bool HasRoomFor(string ipKey, string? countryKey)
    {
        var missing = 0;
        if (!_clients.ContainsKey(ipKey))
        {
            missing++;
        }

        if (countryKey is not null &&
            !string.Equals(ipKey, countryKey, StringComparison.Ordinal) &&
            !_clients.ContainsKey(countryKey))
        {
            missing++;
        }

        return _clients.Count + missing <= _maxTrackedKeys;
    }

    private bool IsLive(string key, Queue<DateTimeOffset> queue) =>
        _clients.TryGetValue(key, out var current) && ReferenceEquals(current, queue);

    private TimeSpan RetryAfterFor(Queue<DateTimeOffset> queue, DateTimeOffset now)
    {
        var remaining = _window - (now - queue.Peek());
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.FromSeconds(1);
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
