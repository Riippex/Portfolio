using System.Text;
using System.Text.Json.Nodes;
using Json.Schema;

namespace Rafael.Portfolio.UnitTests;

public sealed class EvidenceSchemaValidationTests
{
    [Fact]
    public void Inventory_manifest_validates_against_canonical_schema()
    {
        var root = TestRepositoryRoot.Find();
        var schemaPath = Path.Combine(root, "docs", "evidence", "schema.json");
        var manifestPath = TestRepositoryRoot.EvidenceInventoryManifestPath;
        Assert.True(File.Exists(schemaPath), $"Schema not found at {schemaPath}");

        var schema = JsonSchema.FromText(File.ReadAllText(schemaPath));
        var instance = JsonNode.Parse(File.ReadAllText(manifestPath));
        Assert.NotNull(instance);

        var results = schema.Evaluate(instance, new EvaluationOptions
        {
            OutputFormat = OutputFormat.List
        });

        Assert.True(results.IsValid, DescribeErrors(results));
    }

    private static string DescribeErrors(EvaluationResults results)
    {
        var errors = new StringBuilder("Inventory manifest does not satisfy docs/evidence/schema.json:");
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
