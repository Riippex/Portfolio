using System.Collections.Concurrent;
using Rafael.Portfolio.Modules.Assistant.Application;

namespace Rafael.Portfolio.Modules.Assistant.Infrastructure;

public sealed class InMemorySlidingWindowRateLimiter : IAssistantRateLimiter
{
    private readonly int _ordinaryLimit;
    private readonly int _teamLimit;
    private readonly int _countryLimit;
    private readonly int _maxTrackedKeys;
    private readonly TimeSpan _window;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _clients = new();
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

        if (_clients.Count >= _maxTrackedKeys && !_clients.ContainsKey(clientKey) && (countryKey is null || !_clients.ContainsKey(countryKey)))
        {
            retryAfter = TimeSpan.FromSeconds(1);
            SweepExpiredClients(now);
            return false;
        }

        if (!TryCheckAndReserve(clientKey, ipLimit, countryKey, _countryLimit, now, out retryAfter))
        {
            SweepExpiredClients(now);
            return false;
        }

        SweepExpiredClients(now);
        return true;
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
            var ipQueue = _clients.GetOrAdd(ipKey, static _ => new Queue<DateTimeOffset>());
            var countryQueue = countryKey is null ? null : _clients.GetOrAdd(countryKey, static _ => new Queue<DateTimeOffset>());

            if (countryQueue is null)
            {
                lock (ipQueue)
                {
                    if (!_clients.TryGetValue(ipKey, out var currentIp) || !ReferenceEquals(currentIp, ipQueue))
                    {
                        continue;
                    }

                    Prune(ipQueue, now);

                    if (ipQueue.Count >= ipLimit)
                    {
                        var oldest = ipQueue.Peek();
                        var remaining = _window - (now - oldest);
                        retryAfter = remaining > TimeSpan.Zero ? remaining : TimeSpan.FromSeconds(1);
                        return false;
                    }

                    ipQueue.Enqueue(now);
                    retryAfter = TimeSpan.Zero;
                    return true;
                }
            }

            var firstKey = string.CompareOrdinal(ipKey, countryKey) <= 0 ? ipKey : countryKey;
            var firstQueue = ReferenceEquals(firstKey, ipKey) ? ipQueue : countryQueue;
            var secondQueue = ReferenceEquals(firstKey, ipKey) ? countryQueue : ipQueue;

            lock (firstQueue)
            {
                lock (secondQueue)
                {
                    if (!_clients.TryGetValue(ipKey, out var currentIp) || !ReferenceEquals(currentIp, ipQueue) ||
                        !_clients.TryGetValue(countryKey!, out var currentCountry) || !ReferenceEquals(currentCountry, countryQueue))
                    {
                        continue;
                    }

                    Prune(ipQueue, now);
                    Prune(countryQueue, now);

                    if (ipQueue.Count >= ipLimit)
                    {
                        var oldest = ipQueue.Peek();
                        var remaining = _window - (now - oldest);
                        retryAfter = remaining > TimeSpan.Zero ? remaining : TimeSpan.FromSeconds(1);
                        return false;
                    }

                    if (countryQueue.Count >= countryLimit)
                    {
                        var oldest = countryQueue.Peek();
                        var remaining = _window - (now - oldest);
                        retryAfter = remaining > TimeSpan.Zero ? remaining : TimeSpan.FromSeconds(1);
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
