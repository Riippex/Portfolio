using Rafael.Portfolio.Modules.Assistant.Application;

namespace Rafael.Portfolio.Modules.Assistant.Infrastructure;

/// <summary>Production remains unable to authorize paid dispatch, even if a provider is replaced.</summary>
public sealed class UnverifiedModelRequestTokenCounter : IModelRequestTokenCounter
{
    public ValueTask<ModelRequestTokenCount> CountAsync(ModelProviderRequest request, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new ModelRequestTokenCount(0, ModelTokenCountBasis.Unverified));
}
