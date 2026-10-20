using System.Text.Json;
using System.Text.Json.Serialization;
using Rafael.Portfolio.Modules.Assistant.Domain;

namespace Rafael.Portfolio.Web.Adapters;

/// <summary>Malformed optional consent denies model processing without blocking deterministic chat.</summary>
internal sealed class AssistantModelConsentJsonConverter : JsonConverter<AssistantModelConsent>
{
    public override AssistantModelConsent? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var value = document.RootElement;
        // Exactly these two case-sensitive fields; duplicates and unknown fields cannot grant.
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() != 2
            || !value.TryGetProperty("accepted", out var accepted) || accepted.ValueKind != JsonValueKind.True
            || !value.TryGetProperty("noticeVersion", out var version) || version.ValueKind != JsonValueKind.String
            || !version.ValueEquals(AssistantModelConsent.CurrentNoticeVersion)) return null;
        return new AssistantModelConsent(true, AssistantModelConsent.CurrentNoticeVersion);
    }

    public override void Write(Utf8JsonWriter writer, AssistantModelConsent value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteBoolean("accepted", value.Accepted);
        writer.WriteString("noticeVersion", value.NoticeVersion);
        writer.WriteEndObject();
    }
}
