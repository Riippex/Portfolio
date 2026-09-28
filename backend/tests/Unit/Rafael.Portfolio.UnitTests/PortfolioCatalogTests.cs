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
}
