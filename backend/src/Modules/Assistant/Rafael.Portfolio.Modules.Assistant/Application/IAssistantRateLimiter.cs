namespace Rafael.Portfolio.Modules.Assistant.Application;

public interface IAssistantRateLimiter
{
    bool TryAcquire(string clientKey, out TimeSpan retryAfter);
}
