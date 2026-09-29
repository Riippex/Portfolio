using System.Text.Json;
using Rafael.Portfolio.Modules.Knowledge.Domain;

namespace Rafael.Portfolio.UnitTests;

public sealed class EvidenceInventoryTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static PublicEvidenceInventory LoadInventory()
    {
        var path = TestRepositoryRoot.EvidenceInventoryManifestPath;
        Assert.True(File.Exists(path), $"Inventory manifest not found at {path}");

        var json = File.ReadAllText(path);
        var inventory = JsonSerializer.Deserialize<PublicEvidenceInventory>(json, JsonOptions);
        Assert.NotNull(inventory);
        return inventory;
    }

    [Fact]
    public void Inventory_manifest_loads_and_has_valid_version()
    {
        var inventory = LoadInventory();

        Assert.False(string.IsNullOrWhiteSpace(inventory.Version));
        Assert.NotEmpty(inventory.Items);
    }

    [Fact]
    public void Inventory_contains_profile_and_all_catalog_projects()
    {
        var inventory = LoadInventory();
        var slugs = inventory.Items.Select(item => item.Slug).ToHashSet();

        Assert.Contains("profile", slugs);
        Assert.Contains("vextis", slugs);
        Assert.Contains("kinetiq-v", slugs);
        Assert.Contains("jobty", slugs);
    }

    [Fact]
    public void Unverified_projects_remain_pending()
    {
        var inventory = LoadInventory();
        var projects = inventory.Items.Where(item => item.Kind == "project");

        Assert.All(projects, project =>
        {
            Assert.Equal(EvidenceStatus.Pending, project.EvidenceStatus);
            Assert.All(project.Claims, claim => Assert.Equal(EvidenceStatus.Pending, claim.Status));
        });
    }

    [Fact]
    public void Profile_evidence_remains_pending_without_external_source()
    {
        var inventory = LoadInventory();
        var profile = Assert.Single(inventory.Items, item => item.Kind == "profile");

        // The Portfolio repository containing the claims is circular evidence, so
        // the profile stays pending until the owner links an externally
        // inspectable public artifact.
        if (string.IsNullOrWhiteSpace(profile.SourceUrl))
        {
            Assert.Equal(EvidenceStatus.Pending, profile.EvidenceStatus);
            Assert.All(profile.Claims, claim => Assert.Equal(EvidenceStatus.Pending, claim.Status));
        }
        else
        {
            Assert.False(
                profile.SourceUrl.Contains("github.com/Riippex/Portfolio", StringComparison.OrdinalIgnoreCase),
                "The Portfolio repository cannot be its own evidence source.");
        }
    }

    [Fact]
    public void All_referenced_document_paths_exist_on_disk()
    {
        var root = TestRepositoryRoot.Find();
        var inventory = LoadInventory();

        Assert.All(inventory.Items, item =>
        {
            var docFullPath = Path.Combine(root, item.DocumentPath.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(docFullPath), $"Document path {item.DocumentPath} does not exist at {docFullPath}");
        });
    }
}
