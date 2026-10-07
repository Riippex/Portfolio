namespace Rafael.Portfolio.Modules.Contact.Application;

public interface IContactRateLimiter
{
    bool TryAcquire(string clientKey, out TimeSpan retryAfter);
}
