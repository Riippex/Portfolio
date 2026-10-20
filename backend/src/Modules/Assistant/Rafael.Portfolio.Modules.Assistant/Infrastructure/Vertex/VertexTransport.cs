using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Rafael.Portfolio.Modules.Assistant.Infrastructure;

public sealed class VertexTransport(HttpClient client, IVertexAccessToken token)
{
    internal async ValueTask<JsonDocument?> SendAsync(Uri endpoint, string body, CancellationToken cancellationToken)
    {
        if (Encoding.UTF8.GetByteCount(body) > 65_536) return null;
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await token.GetAsync(cancellationToken));
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        // No retry, redirect, streaming generation or external tool call.
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > 65_536) return null;
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken);
            if (read == 0) break;
            if (buffer.Length + read > 65_536) return null;
            buffer.Write(chunk, 0, read);
        }
        var document = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
        if (document.RootElement.ValueKind != JsonValueKind.Object || !Unique(document.RootElement))
        {
            document.Dispose();
            return null;
        }
        return document;
    }

    private static bool Unique(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
                if (!seen.Add(property.Name) || !Unique(property.Value)) return false;
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray()) if (!Unique(item)) return false;
        }
        return true;
    }
}
