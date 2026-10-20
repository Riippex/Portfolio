using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Rafael.Portfolio.Modules.Assistant.Application;

namespace Rafael.Portfolio.Modules.Assistant.Infrastructure;

internal static partial class VertexRequest
{
    internal static JsonObject Build(ModelProviderRequest request, bool counting)
    {
        if (request.SystemPrompt.Length > 8_000 || request.UserMessage.Length > 12_000 || request.OutputSchema.Length > 8_192
            || request.MaxOutputTokens is < 1 or > 600 || !ReservationPattern().IsMatch(request.ReservationId ?? ""))
            throw new InvalidOperationException("Invalid provider request.");
        var body = new JsonObject
        {
            ["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = request.SystemPrompt }) },
            ["contents"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["parts"] = new JsonArray(new JsonObject { ["text"] = request.UserMessage })
            }),
            ["generationConfig"] = new JsonObject
            {
                ["candidateCount"] = 1,
                ["maxOutputTokens"] = request.MaxOutputTokens,
                ["responseMimeType"] = "application/json",
                ["responseJsonSchema"] = JsonNode.Parse(request.OutputSchema),
                ["thinkingConfig"] = new JsonObject { ["thinkingLevel"] = "MINIMAL", ["includeThoughts"] = false }
            }
        };
        // Labels are billing-only metadata, not model context. CountTokens has no labels field.
        if (!counting) body["labels"] = new JsonObject
        {
            ["portfolio_reservation"] = request.ReservationId,
            ["portfolio_prompt"] = VertexModelOptions.PromptVersion
        };
        return body;
    }

    [GeneratedRegex(@"^res_[a-f0-9]{32}\z")]
    private static partial Regex ReservationPattern();
}
