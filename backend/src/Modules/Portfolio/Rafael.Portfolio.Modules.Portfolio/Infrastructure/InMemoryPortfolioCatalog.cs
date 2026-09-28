using Rafael.Portfolio.Modules.Portfolio.Application;
using Rafael.Portfolio.Modules.Portfolio.Domain;

namespace Rafael.Portfolio.Modules.Portfolio.Infrastructure;

public sealed class InMemoryPortfolioCatalog : IPortfolioCatalog
{
    private static readonly Profile Profile = new(
        "Rafael Patiño",
        "AI systems engineer",
        "Building autonomous agent systems, real-time computer vision, and cloud-native products.",
        ["Autonomous agents", "Computer vision", "Cloud systems"]);

    private static readonly ProjectSummary[] Projects =
    [
        new("vextis", "Vextis", "Verified architecture and outcomes will be published here.", "pending"),
        new("kinetiq-v", "Kinetiq V", "Verified architecture and outcomes will be published here.", "pending"),
        new("jobty", "JobTY", "Verified architecture and outcomes will be published here.", "pending")
    ];

    public Profile GetProfile() => Profile;

    public IReadOnlyList<ProjectSummary> GetProjects() => Projects;
}
