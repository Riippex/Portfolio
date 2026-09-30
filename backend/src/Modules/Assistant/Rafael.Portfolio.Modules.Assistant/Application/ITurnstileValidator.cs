namespace Rafael.Portfolio.Modules.Assistant.Application;

public interface ITurnstileValidator
{
    Task<bool> ValidateAsync(string? token, string? remoteIp, CancellationToken cancellationToken = default);
}
