using Rafael.Portfolio.Modules.Portfolio.Infrastructure;

namespace Rafael.Portfolio.UnitTests;

public sealed class PortfolioCatalogTests
{
    [Fact]
    public void Profile_exposes_documented_focus_areas()
    {
        var catalog = new InMemoryPortfolioCatalog();
        var profile = catalog.GetProfile();

        Assert.Contains("Autonomous agents", profile.FocusAreas);
        Assert.Contains("Computer vision", profile.FocusAreas);
    }

    [Fact]
    public void Projects_are_marked_pending_until_evidence_is_connected()
    {
        var catalog = new InMemoryPortfolioCatalog();

        Assert.All(catalog.GetProjects(), project => Assert.Equal("pending", project.EvidenceStatus));
    }

    [Fact]
    public void Projects_expose_non_empty_summary_and_name()
    {
        var catalog = new InMemoryPortfolioCatalog();

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
        var catalog = new InMemoryPortfolioCatalog();
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
        var catalog = new InMemoryPortfolioCatalog();
        var project = catalog.GetProjectBySlug("unknown-project");

        Assert.Null(project);
    }
}
