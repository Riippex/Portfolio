using System.Text.Json;
using Rafael.Portfolio.Modules.Knowledge.Domain;
using Rafael.Portfolio.Modules.Portfolio.Application;
using Rafael.Portfolio.Modules.Portfolio.Infrastructure;

namespace Rafael.Portfolio.UnitTests;

public sealed class PortfolioCatalogTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static IPortfolioCatalog CreateCatalog() =>
        EvidenceInventoryPortfolioCatalog.FromFile(TestRepositoryRoot.EvidenceInventoryManifestPath);

    private static PublicEvidenceInventory LoadManifest()
    {
        var json = File.ReadAllText(TestRepositoryRoot.EvidenceInventoryManifestPath);
        var manifest = JsonSerializer.Deserialize<PublicEvidenceInventory>(json, JsonOptions);
        Assert.NotNull(manifest);
        return manifest;
    }

    [Fact]
    public void Profile_exposes_documented_focus_areas()
    {
        var catalog = CreateCatalog();
        var profile = catalog.GetProfile();

        Assert.Contains("Autonomous agents", profile.FocusAreas);
        Assert.Contains("Computer vision", profile.FocusAreas);
    }

    [Fact]
    public void Catalog_profile_matches_canonical_inventory_without_drift()
    {
        var catalog = CreateCatalog();
        var profileItem = Assert.Single(LoadManifest().Items, item => item.Kind == "profile");
        var profile = catalog.GetProfile();

        Assert.Equal(profileItem.Title, profile.Name);
        Assert.Equal(profileItem.Headline, profile.Headline);
        Assert.Equal(profileItem.Summary, profile.Summary);
        Assert.Equal(profileItem.EvidenceStatus, profile.EvidenceStatus);
        Assert.Equal(profileItem.FocusAreas, profile.FocusAreas);
    }

    [Fact]
    public void Projects_are_marked_pending_until_evidence_is_connected()
    {
        var catalog = CreateCatalog();

        Assert.All(catalog.GetProjects(), project => Assert.Equal("pending", project.EvidenceStatus));
    }

    [Fact]
    public void Projects_expose_non_empty_summary_and_name()
    {
        var catalog = CreateCatalog();

        Assert.All(catalog.GetProjects(), project =>
        {
            Assert.False(string.IsNullOrWhiteSpace(project.Name));
            Assert.False(string.IsNullOrWhiteSpace(project.Summary));
        });
    }

    [Theory]
    [InlineData("vextis")]
    [InlineData("kinetiq-v")]
    [InlineData("jobty")]
    public void GetProjectBySlug_returns_detail_for_known_slugs(string slug)
    {
        var catalog = CreateCatalog();
        var project = catalog.GetProjectBySlug(slug);

        Assert.NotNull(project);
        Assert.Equal(slug, project.Slug);
        Assert.Equal("pending", project.EvidenceStatus);
        Assert.NotEmpty(project.Claims);
        Assert.All(project.Claims, claim => Assert.Equal("pending", claim.Status));
    }

    [Fact]
    public void GetProjectBySlug_returns_null_for_unknown_slug()
    {
        var catalog = CreateCatalog();
        var project = catalog.GetProjectBySlug("unknown-project");

        Assert.Null(project);
    }

    [Fact]
    public void Catalog_project_data_matches_canonical_inventory_without_drift()
    {
        var catalog = CreateCatalog();
        var manifestProjects = LoadManifest().Items
            .Where(item => item.Kind == "project")
            .ToList();

        Assert.Equal(
            manifestProjects.Select(item => item.Slug).Order(),
            catalog.GetProjects().Select(project => project.Slug).Order());

        Assert.All(manifestProjects, item =>
        {
            var detail = catalog.GetProjectBySlug(item.Slug);
            Assert.NotNull(detail);
            Assert.Equal(item.Title, detail.Name);
            Assert.Equal(item.Summary, detail.Summary);
            Assert.Equal(item.EvidenceStatus, detail.EvidenceStatus);
            Assert.Equal(item.SourceUrl, detail.SourceUrl);
            Assert.Equal(item.LastReviewed, detail.LastReviewed);
            Assert.Equal(item.Claims.Count, detail.Claims.Count);
            Assert.All(item.Claims.Zip(detail.Claims), pair =>
            {
                Assert.Equal(pair.First.ClaimId, pair.Second.ClaimId);
                Assert.Equal(pair.First.Statement, pair.Second.Statement);
                Assert.Equal(pair.First.Status, pair.Second.Status);
                Assert.Equal(pair.First.Citation, pair.Second.Citation);
            });
        });
    }
}
