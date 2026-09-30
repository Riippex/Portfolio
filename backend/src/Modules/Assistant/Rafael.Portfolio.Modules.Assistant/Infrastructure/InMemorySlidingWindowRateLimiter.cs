using System.Collections.Concurrent;
using Rafael.Portfolio.Modules.Assistant.Application;

namespace Rafael.Portfolio.Modules.Assistant.Infrastructure;

public sealed class InMemorySlidingWindowRateLimiter : IAssistantRateLimiter
{
    private readonly int _limit;
    private readonly TimeSpan _window;
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _clients = new();

    public InMemorySlidingWindowRateLimiter(int limit = 10, TimeSpan? window = null)
    {
        _limit = limit > 0 ? limit : 10;
        _window = window ?? TimeSpan.FromSeconds(60);
    }

    public bool TryAcquire(string clientKey, out TimeSpan retryAfter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientKey);

        var now = DateTimeOffset.UtcNow;
        var queue = _clients.GetOrAdd(clientKey, _ => new Queue<DateTimeOffset>());

        lock (queue)
        {
            var cutoff = now - _window;
            while (queue.Count > 0 && queue.Peek() <= cutoff)
            {
                queue.Dequeue();
            }

            if (queue.Count < _limit)
            {
                queue.Enqueue(now);
                retryAfter = TimeSpan.Zero;
                return true;
            }

            var oldest = queue.Peek();
            var remaining = _window - (now - oldest);
            retryAfter = remaining > TimeSpan.Zero ? remaining : TimeSpan.FromSeconds(1);
            return false;
        }
    }
}
