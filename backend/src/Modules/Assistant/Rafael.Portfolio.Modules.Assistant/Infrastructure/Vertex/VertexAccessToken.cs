using Google.Apis.Auth.OAuth2;

namespace Rafael.Portfolio.Modules.Assistant.Infrastructure;

public interface IVertexAccessToken
{
    ValueTask<string> GetAsync(CancellationToken cancellationToken);
}

/// <summary>Managed runtime identity (or explicit operator ADC), never a stored API key.</summary>
public sealed class VertexAccessToken : IVertexAccessToken
{
    private readonly Lazy<Task<GoogleCredential>> _credential = new(async () =>
        (await GoogleCredential.GetApplicationDefaultAsync()).CreateScoped("https://www.googleapis.com/auth/cloud-platform"));

    public async ValueTask<string> GetAsync(CancellationToken cancellationToken)
    {
        var credential = await _credential.Value.WaitAsync(cancellationToken);
        return await ((ITokenAccess)credential).GetAccessTokenForRequestAsync(cancellationToken: cancellationToken);
    }
}
