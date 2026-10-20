namespace Rafael.Portfolio.Modules.Assistant.Application.Orchestration;

internal readonly record struct PortResult<T>(bool Known, T Value);

// Deny-only, process-local backpressure. This is NOT a replacement for global ledger permits.
internal sealed class OrphanGuard
{
    private int _pending;
    internal bool Denied => Volatile.Read(ref _pending) >= 2;

    internal void Observe(Task task)
    {
        Interlocked.Increment(ref _pending);
        _ = ObserveAsync(task, this);
    }

    private static async Task ObserveAsync(Task task, OrphanGuard guard)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception) { /* Never log an exception that might contain provider payloads. */ }
        finally { Interlocked.Decrement(ref guard._pending); }
    }
}

internal sealed class TurnBudgets(TimeProvider clock, long start, OrphanGuard orphans)
{
    internal TimeSpan Elapsed => clock.GetElapsedTime(start);
    internal TimeSpan EpilogueEnd => TimeSpan.FromSeconds(Math.Min(15, Elapsed.TotalSeconds + 4));

    internal async ValueTask<PortResult<T>> AwaitAsync<T>(Func<CancellationToken, ValueTask<T>> operation,
        TimeSpan end, CancellationToken caller = default)
    {
        var remaining = end - Elapsed;
        if (remaining <= TimeSpan.Zero || caller.IsCancellationRequested) return default;
        using var phase = CancellationTokenSource.CreateLinkedTokenSource(caller);
        // The clock-aware delay bounds the wait even when the port ignores cancellation.
        var deadline = Task.Delay(remaining, clock, phase.Token);
        Task<T>? task = null;
        try
        {
            task = operation(phase.Token).AsTask();
            var winner = await Task.WhenAny(task, deadline).ConfigureAwait(false);
            if (winner != task || caller.IsCancellationRequested || Elapsed >= end)
            {
                orphans.Observe(task);
                return default;
            }
            return new PortResult<T>(true, await task.ConfigureAwait(false));
        }
        catch (Exception) { return default; }
        finally
        {
            // Cancel the operation as a courtesy and clear the timer on early completion.
            // Cancellation callback failures cannot escape or expose provider text.
            try
            {
                var cancellation = phase.CancelAsync();
                if (!cancellation.IsCompletedSuccessfully) orphans.Observe(cancellation);
            }
            catch (Exception) { }
        }
    }
}
