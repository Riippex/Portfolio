namespace Rafael.Portfolio.Modules.Portfolio;

public static class PortfolioModule
{
    public const string Name = "Portfolio";
}

public sealed record Profile(string Name, string Headline, string Summary, IReadOnlyList<string> FocusAreas);

public sealed record ProjectSummary(string Slug, string Name, string EvidenceStatus);

public interface IPortfolioCatalog
{
    Profile GetProfile();
    IReadOnlyList<ProjectSummary> GetProjects();
}

public sealed class PortfolioCatalog : IPortfolioCatalog
{
    private static readonly Profile Profile = new(
        "Rafael Patiño",
        "AI systems engineer",
        "Building autonomous agent systems, real-time computer vision, and cloud-native products.",
        ["Autonomous agents", "Computer vision", "Cloud systems"]);

    private static readonly ProjectSummary[] Projects =
    [
        new("vextis", "Vextis", "pending"),
        new("kinetiq-v", "Kinetiq V", "pending"),
        new("jobty", "JobTY", "pending")
    ];

    public Profile GetProfile() => Profile;

    public IReadOnlyList<ProjectSummary> GetProjects() => Projects;
}
