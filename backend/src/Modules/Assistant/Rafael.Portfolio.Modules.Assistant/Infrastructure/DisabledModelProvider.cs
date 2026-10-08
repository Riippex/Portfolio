using Rafael.Portfolio.Modules.Assistant.Application;

namespace Rafael.Portfolio.Modules.Assistant.Infrastructure;

public sealed class DisabledModelProvider : IModelProvider
{
    public bool IsAvailable => false;

    public ValueTask<ModelProviderResponse> GenerateAsync(ModelProviderRequest request, CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult(new ModelProviderResponse(
            Success: false,
            Text: null,
            ErrorMessage: "Model provider is disabled or unconfigured in current environment."));
    }
}
