using Rafael.Portfolio.Modules.Assistant.Application;

namespace Rafael.Portfolio.Modules.Assistant.Infrastructure;

public sealed class DisabledTurnstileValidator : ITurnstileValidator
{
    public Task<bool> ValidateAsync(string? token, string? remoteIp, CancellationToken cancellationToken = default) =>
        Task.FromResult(true);
}
