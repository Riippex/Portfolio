namespace Rafael.Portfolio.Modules.Assistant.Application;

public interface IAssistantRateLimiter
{
    bool TryAcquire(string clientKey, out TimeSpan retryAfter);
    bool TryAcquire(string clientKey, bool isTeamTier, string? countryCode, out TimeSpan retryAfter);
}
