using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Rafael.Portfolio.Modules.Assistant.Application;

namespace Rafael.Portfolio.Modules.Assistant.Infrastructure;

/// <summary>Text-only REST adapter. Counting and inference use one canonical input body.
/// No SDK automatic retries, external tools, cache creation or model-written visitor prose.</summary>
public sealed class VertexGeminiClient(VertexModelOptions options, ModelControlPolicy policy,
    VertexTransport transport, TimeProvider clock) : IModelProvider, IModelRequestTokenCounter
{
    private sealed class CountReceipt(string body, int count)
    {
        internal string Body { get; } = body;
        internal int Count { get; } = count;
        private int _used;
        internal bool Consume() => Interlocked.Exchange(ref _used, 1) == 0;
    }

    private readonly ConditionalWeakTable<ModelProviderRequest, CountReceipt> _counts = new();
    private int _countFault;
    public bool IsAvailable => Volatile.Read(ref _countFault) == 0 && options.IsUsable(policy, clock.GetUtcNow());
    private static ModelRequestTokenCount Unverified => new(0, ModelTokenCountBasis.Unverified);

    public async ValueTask<ModelRequestTokenCount> CountAsync(ModelProviderRequest request, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable) return Unverified;
        _counts.Remove(request);
        try
        {
            var body = VertexRequest.Build(request, counting: true).ToJsonString();
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(TimeSpan.FromSeconds(2));
            using var response = await transport.SendAsync(options.Endpoint("countTokens"), body, budget.Token);
            if (response is null || !Integer(response.RootElement, "totalTokens", out var count) || count is < 1 or > 6000)
                return Unverified;
            // No media estimates: require an exact TEXT-only modality breakdown matching total.
            if (!response.RootElement.TryGetProperty("promptTokensDetails", out var details)
                || details.ValueKind != JsonValueKind.Array || details.GetArrayLength() != 1
                || !details[0].TryGetProperty("modality", out var modality) || modality.GetString() != "TEXT"
                || !Integer(details[0], "tokenCount", out var textCount) || textCount != count) return Unverified;
            _counts.AddOrUpdate(request, new CountReceipt(body, count));
            return new(count, ModelTokenCountBasis.ProviderVerifiedUpperBound);
        }
        catch (Exception) { return Unverified; }
    }

    public async ValueTask<ModelProviderResponse> GenerateAsync(ModelProviderRequest request, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable || !_counts.TryGetValue(request, out var receipt) || !receipt.Consume()) return Failed();
        _counts.Remove(request);
        ModelUsage? observedUsage = null;
        try
        {
            var body = JsonNode.Parse(receipt.Body)!.AsObject();
            body["labels"] = new JsonObject
            {
                ["portfolio_reservation"] = request.ReservationId,
                ["portfolio_prompt"] = VertexModelOptions.PromptVersion
            };
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(TimeSpan.FromSeconds(8));
            using var response = await transport.SendAsync(options.Endpoint("generateContent"), body.ToJsonString(), budget.Token);
            if (response is null) return Failed();
            var root = response.RootElement;
            var usage = observedUsage = ReadUsage(root);
            if (usage is null) return Failed();
            if (usage.Value.InputTokens > receipt.Count)
            {
                // A violated counting contract disables further inference in this process.
                Interlocked.Exchange(ref _countFault, 1);
                return Failed(usage);
            }
            var text = ReadText(root);
            return new ModelProviderResponse(text is not null, text,
                (int)usage.Value.InputTokens, (int)usage.Value.OutputTokens);
        }
        catch (Exception) { return Failed(observedUsage); }
    }

    private static ModelUsage? ReadUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usageMetadata", out var usage) || usage.ValueKind != JsonValueKind.Object
            || !Integer(usage, "promptTokenCount", out var input) || input < 1
            || !Integer(usage, "totalTokenCount", out var total) || total < input
            || !OptionalInteger(usage, "candidatesTokenCount", out var candidates)
            || !OptionalInteger(usage, "thoughtsTokenCount", out var thoughts)
            || !OptionalInteger(usage, "toolUsePromptTokenCount", out var tools)
            || tools != 0 || (long)input + candidates + thoughts != total) return null;
        return new ModelUsage(input, total - input); // includes billable reasoning, not just visible text
    }

    private static string? ReadText(JsonElement root)
    {
        if (!root.TryGetProperty("modelVersion", out var model) || model.ValueKind != JsonValueKind.String
            || model.GetString() != VertexModelOptions.ModelId
            || !root.TryGetProperty("candidates", out var candidates) || candidates.ValueKind != JsonValueKind.Array
            || candidates.GetArrayLength() != 1) return null;
        var candidate = candidates[0];
        if (!candidate.TryGetProperty("finishReason", out var finish) || finish.ValueKind != JsonValueKind.String || finish.GetString() != "STOP"
            || !candidate.TryGetProperty("content", out var content)
            || !content.TryGetProperty("parts", out var parts) || parts.ValueKind != JsonValueKind.Array || parts.GetArrayLength() > 8) return null;
        var result = new StringBuilder();
        foreach (var part in parts.EnumerateArray())
        {
            if (part.TryGetProperty("thought", out var thought) && thought.ValueKind == JsonValueKind.True) continue;
            if (part.TryGetProperty("functionCall", out _) || !part.TryGetProperty("text", out var text)
                || text.ValueKind != JsonValueKind.String) return null;
            result.Append(text.GetString());
            if (result.Length > 256) return null;
        }
        return result.Length == 0 ? null : result.ToString();
    }

    private static bool Integer(JsonElement value, string name, out int number)
    {
        number = 0;
        return value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out number) && number >= 0;
    }

    private static bool OptionalInteger(JsonElement value, string name, out int number)
    {
        number = 0;
        return !value.TryGetProperty(name, out _) || Integer(value, name, out number);
    }

    private static ModelProviderResponse Failed(ModelUsage? usage = null) => new(false, null,
        usage is null ? null : (int)usage.Value.InputTokens, usage is null ? null : (int)usage.Value.OutputTokens,
        "Provider response unavailable or invalid.");
}
