using System.Text.Json;
using Rafael.Portfolio.Modules.Knowledge.Domain;
using Rafael.Portfolio.Modules.Portfolio.Application;
using Rafael.Portfolio.Modules.Portfolio.Domain;
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

        Assert.Contains("Software engineering", profile.FocusAreas);
        Assert.Contains("AI engineering", profile.FocusAreas);
        Assert.Contains("Cloud fundamentals", profile.FocusAreas);
    }

    [Fact]
    public void Profile_exposes_approved_linkedin_and_github_public_links()
    {
        var catalog = CreateCatalog();
        var profile = catalog.GetProfile();

        Assert.Equal(
            [
                new ProfileLink("linkedin", "https://co.linkedin.com/in/rafael-pati%C3%B1o-diaz"),
                new ProfileLink("github", "https://github.com/Riippex")
            ],
            profile.Links);
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
        Assert.Equal(
            profileItem.Links.Select(link => (link.Platform, link.Url)),
            profile.Links.Select(link => (link.Platform, link.Url)));
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

    [Fact]
    public void Catalog_handles_missing_empty_and_removed_profile_links()
    {
        var missingLinksPath = WriteTempManifest(linksJson: null);
        try
        {
            var catalog = EvidenceInventoryPortfolioCatalog.FromFile(missingLinksPath);
            Assert.Empty(catalog.GetProfile().Links);
        }
        finally
        {
            File.Delete(missingLinksPath);
        }

        var emptyLinksPath = WriteTempManifest(linksJson: "[]");
        try
        {
            var catalog = EvidenceInventoryPortfolioCatalog.FromFile(emptyLinksPath);
            Assert.Empty(catalog.GetProfile().Links);
        }
        finally
        {
            File.Delete(emptyLinksPath);
        }

        var singleLinkPath = WriteTempManifest(
            linksJson: """[{"platform":"linkedin","url":"https://co.linkedin.com/in/rafael-pati%C3%B1o-diaz"}]""");
        try
        {
            var catalog = EvidenceInventoryPortfolioCatalog.FromFile(singleLinkPath);
            var link = Assert.Single(catalog.GetProfile().Links);
            Assert.Equal("linkedin", link.Platform);
            Assert.Equal("https://co.linkedin.com/in/rafael-pati%C3%B1o-diaz", link.Url);
        }
        finally
        {
            File.Delete(singleLinkPath);
        }
    }

    [Theory]
    [InlineData("""[{"platform":"github","url":"javascript:alert(1)"}]""")]
    [InlineData("""[{"platform":"github","url":"data:text/html,<script>alert(1)</script>"}]""")]
    [InlineData("""[{"platform":"github","url":"http://github.com/Riippex"}]""")]
    [InlineData("""[{"platform":"github","url":"https://github.com@evil.example/Riippex"}]""")]
    [InlineData("""[{"platform":"github","url":"https://github.com.evil.example/Riippex"}]""")]
    [InlineData("""[{"platform":"linkedin","url":"https://co.linkedin.com.evil.example/in/rafael"}]""")]
    [InlineData("""[{"platform":"github","url":"https://github.com/Riippex\"><script>"}]""")]
    [InlineData("""[{"platform":"linkedin","url":"https://co.linkedin.com/in/rafael%2fadmin"}]""")]
    [InlineData("""[{"platform":"github","url":"https://github.com/Riippex/Portfolio"}]""")]
    [InlineData("""[{"platform":"twitter","url":"https://twitter.com/example"}]""")]
    [InlineData("""[{"platform":"github","url":"https://github.com/Riippex"},{"platform":"github","url":"https://github.com/Other"}]""")]
    public void Catalog_rejects_malformed_unsupported_duplicate_or_unsafe_profile_links(string linksJson)
    {
        var manifestPath = WriteTempManifest(linksJson);
        try
        {
            Assert.Throws<InvalidOperationException>(() =>
                EvidenceInventoryPortfolioCatalog.FromFile(manifestPath));
        }
        finally
        {
            File.Delete(manifestPath);
        }
    }

    private static string WriteTempManifest(string? linksJson)
    {
        var path = Path.Combine(Path.GetTempPath(), $"portfolio-catalog-{Guid.NewGuid():N}.json");
        var linksProperty = linksJson is null ? string.Empty : $"\"links\": {linksJson},";
        File.WriteAllText(
            path,
            $$"""
            {
              "version": "2026.10.2",
              "lastUpdated": "2026-10-27",
              "items": [
                {
                  "id": "evidence-profile",
                  "slug": "profile",
                  "kind": "profile",
                  "title": "Rafael Patiño",
                  "summary": "Summary",
                  "headline": "Software & AI Engineer",
                  "focusAreas": ["Software engineering"],
                  {{linksProperty}}
                  "version": "2026.10.2",
                  "evidenceStatus": "verified",
                  "sourceUrl": "https://co.linkedin.com/in/rafael-pati%C3%B1o-diaz",
                  "documentPath": "docs/evidence/profile.md",
                  "lastReviewed": "2026-10-27",
                  "claims": []
                },
                {
                  "id": "evidence-project-vextis",
                  "slug": "vextis",
                  "kind": "project",
                  "title": "Vextis",
                  "summary": "Summary",
                  "version": "2026.09",
                  "evidenceStatus": "pending",
                  "sourceUrl": null,
                  "documentPath": "docs/evidence/projects/vextis.md",
                  "lastReviewed": "2026-09-28",
                  "claims": []
                }
              ]
            }
            """);
        return path;
    }
}
