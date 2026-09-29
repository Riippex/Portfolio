using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace Rafael.Portfolio.Modules.Knowledge.Infrastructure;

internal static class EvidenceManifestValidator
{
    private const string SchemaResourceSuffix = "evidence-schema.json";

    public static void Validate(string manifestJson)
    {
        JsonNode? instance;
        try
        {
            instance = JsonNode.Parse(manifestJson);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Evidence inventory manifest is not valid JSON.", ex);
        }

        if (instance is null)
        {
            throw new InvalidOperationException("Evidence inventory manifest is empty.");
        }

        var results = LoadSchema().Evaluate(instance, new EvaluationOptions
        {
            OutputFormat = OutputFormat.List
        });

        if (!results.IsValid)
        {
            throw new InvalidOperationException(DescribeErrors(results));
        }
    }

    private static JsonSchema LoadSchema()
    {
        var assembly = typeof(EvidenceManifestValidator).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .SingleOrDefault(name => name.EndsWith(SchemaResourceSuffix, StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                "Canonical evidence schema resource is not embedded in the Knowledge module.");

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Canonical evidence schema resource '{resourceName}' could not be opened.");
        using var reader = new StreamReader(stream);
        return JsonSchema.FromText(reader.ReadToEnd());
    }

    private static string DescribeErrors(EvaluationResults results)
    {
        var errors = new StringBuilder(
            "Evidence inventory manifest does not satisfy the canonical JSON Schema:");
        foreach (var detail in results.Details.Where(detail => detail.HasErrors))
        {
            foreach (var error in detail.Errors ?? new Dictionary<string, string>())
            {
                errors.Append(Environment.NewLine)
                    .Append("- ")
                    .Append(detail.InstanceLocation)
                    .Append(": ")
                    .Append(error.Key)
                    .Append(' ')
                    .Append(error.Value);
            }
        }

        return errors.ToString();
    }
}
