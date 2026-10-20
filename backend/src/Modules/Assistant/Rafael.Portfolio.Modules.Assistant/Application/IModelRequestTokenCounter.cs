namespace Rafael.Portfolio.Modules.Assistant.Application;

/// <summary>Count the entire immutable provider request, including its schema and provider overhead.
/// A real adapter must establish a model-specific upper bound before claiming it is verified.</summary>
public interface IModelRequestTokenCounter
{
    ValueTask<ModelRequestTokenCount> CountAsync(ModelProviderRequest request, CancellationToken cancellationToken = default);
}

public sealed record ModelRequestTokenCount(int InputTokens, ModelTokenCountBasis Basis);

public enum ModelTokenCountBasis { Unverified, ProviderVerifiedUpperBound }
