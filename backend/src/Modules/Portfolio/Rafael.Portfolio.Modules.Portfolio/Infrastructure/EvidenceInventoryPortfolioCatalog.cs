using System.Text.Json;
using Rafael.Portfolio.Modules.Portfolio.Application;
using Rafael.Portfolio.Modules.Portfolio.Domain;

namespace Rafael.Portfolio.Modules.Portfolio.Infrastructure;

public sealed class EvidenceInventoryPortfolioCatalog : IPortfolioCatalog
{
    private const string ProjectKind = "project";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly Profile Profile = new(
        "Rafael Patiño",
        "AI systems engineer",
        "Building autonomous agent systems, real-time computer vision, and cloud-native products.",
        ["Autonomous agents", "Computer vision", "Cloud systems"]);

    private readonly IReadOnlyList<ProjectSummary> _projects;
    private readonly Dictionary<string, ProjectDetail> _projectDetails;

    private EvidenceInventoryPortfolioCatalog(IEnumerable<ManifestItem> projectItems)
    {
        var items = projectItems.ToList();
        _projects = items
            .Select(item => new ProjectSummary(item.Slug, item.Title, item.Summary, item.EvidenceStatus))
            .ToList();
        _projectDetails = items.ToDictionary(
            item => item.Slug,
            item => new ProjectDetail(
                item.Slug,
                item.Title,
                item.Summary,
                item.EvidenceStatus,
                item.SourceUrl,
                item.LastReviewed,
                item.Claims
                    .Select(claim => new ProjectClaim(claim.ClaimId, claim.Statement, claim.Status, claim.Citation))
                    .ToList()),
            StringComparer.OrdinalIgnoreCase);
    }

    public static EvidenceInventoryPortfolioCatalog FromFile(string manifestPath)
    {
        if (!File.Exists(manifestPath))
        {
            throw new InvalidOperationException(
                $"Canonical evidence inventory manifest not found at '{manifestPath}'. " +
                "Set the EvidenceInventory:ManifestPath configuration value to its location.");
        }

        var manifest = JsonSerializer.Deserialize<ManifestDocument>(File.ReadAllText(manifestPath), JsonOptions)
            ?? throw new InvalidOperationException(
                $"Canonical evidence inventory manifest at '{manifestPath}' could not be deserialized.");

        var projectItems = manifest.Items
            .Where(item => string.Equals(item.Kind, ProjectKind, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (projectItems.Count == 0)
        {
            throw new InvalidOperationException(
                $"Canonical evidence inventory manifest at '{manifestPath}' contains no project items.");
        }

        return new EvidenceInventoryPortfolioCatalog(projectItems);
    }

    public static string FindDefaultManifestPath(string baseDirectory)
    {
        var current = new DirectoryInfo(baseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "docs", "evidence", "inventory.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate docs/evidence/inventory.json above " +
            $"'{baseDirectory}'. Set the EvidenceInventory:ManifestPath configuration value.");
    }

    public Profile GetProfile() => Profile;

    public IReadOnlyList<ProjectSummary> GetProjects() => _projects;

    public ProjectDetail? GetProjectBySlug(string slug) =>
        _projectDetails.GetValueOrDefault(slug);

    private sealed record ManifestDocument(
        string Version,
        DateOnly LastUpdated,
        IReadOnlyList<ManifestItem> Items);

    private sealed record ManifestItem(
        string Id,
        string Slug,
        string Kind,
        string Title,
        string Summary,
        string Version,
        string EvidenceStatus,
        string? SourceUrl,
        string DocumentPath,
        DateOnly LastReviewed,
        IReadOnlyList<ManifestClaim> Claims);

    private sealed record ManifestClaim(
        string ClaimId,
        string Statement,
        string Status,
        string Citation);
}
