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

    private static readonly Dictionary<string, ProjectDetail> ProjectDetails = new(StringComparer.OrdinalIgnoreCase)
    {
        ["vextis"] = new(
            "vextis",
            "Vextis",
            "Verified architecture and outcomes will be published here.",
            "pending",
            null,
            new DateOnly(2026, 9, 28),
            [
                new("claim-vextis-01", "Multi-tenant autonomous agent architecture with sandboxed memory and allowlisted retrieval.", "pending", "docs/evidence/projects/vextis.md#architecture")
            ]),
        ["kinetiq-v"] = new(
            "kinetiq-v",
            "Kinetiq V",
            "Verified architecture and outcomes will be published here.",
            "pending",
            null,
            new DateOnly(2026, 9, 28),
            [
                new("claim-kinetiq-v-01", "Real-time computer vision processing pipeline with transient TTL state and edge-to-cloud coordination.", "pending", "docs/evidence/projects/kinetiq-v.md#pipeline")
            ]),
        ["jobty"] = new(
            "jobty",
            "JobTY",
            "Verified architecture and outcomes will be published here.",
            "pending",
            null,
            new DateOnly(2026, 9, 28),
            [
                new("claim-jobty-01", "AI vacancy evaluation engine that identifies explicit skill evidence, inferences, and gaps.", "pending", "docs/evidence/projects/jobty.md#matching-engine")
            ])
    };

    public Profile GetProfile() => Profile;

    public IReadOnlyList<ProjectSummary> GetProjects() => Projects;

    public ProjectDetail? GetProjectBySlug(string slug) =>
        ProjectDetails.GetValueOrDefault(slug);
}
