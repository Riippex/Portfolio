using System.Text.Json;
using Rafael.Portfolio.Modules.Portfolio.Application;
using Rafael.Portfolio.Modules.Portfolio.Domain;

namespace Rafael.Portfolio.Modules.Portfolio.Infrastructure;

public sealed class EvidenceInventoryPortfolioCatalog : IPortfolioCatalog
{
    private const string ProfileKind = "profile";
    private const string ProjectKind = "project";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly Profile _profile;
    private readonly IReadOnlyList<ProjectSummary> _projects;
    private readonly Dictionary<string, ProjectDetail> _projectDetails;

    private EvidenceInventoryPortfolioCatalog(ManifestDocument manifest)
    {
        var profileItem = manifest.Items.SingleOrDefault(item =>
            string.Equals(item.Kind, ProfileKind, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                "Canonical evidence inventory manifest must contain exactly one profile item.");

        if (string.IsNullOrWhiteSpace(profileItem.Headline) ||
            profileItem.FocusAreas is not { Count: > 0 })
        {
            throw new InvalidOperationException(
                "Canonical evidence inventory profile item must define a headline and at least one focus area.");
        }

        _profile = new Profile(
            profileItem.Title,
            profileItem.Headline,
            profileItem.Summary,
            profileItem.EvidenceStatus,
            profileItem.FocusAreas);

        var projectItems = manifest.Items
            .Where(item => string.Equals(item.Kind, ProjectKind, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (projectItems.Count == 0)
        {
            throw new InvalidOperationException(
                "Canonical evidence inventory manifest contains no project items.");
        }

        _projects = projectItems
            .Select(item => new ProjectSummary(item.Slug, item.Title, item.Summary, item.EvidenceStatus))
            .ToList();
        _projectDetails = projectItems.ToDictionary(
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

    public static string BundledManifestPath =>
        Path.Combine(AppContext.BaseDirectory, "evidence", "inventory.json");

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

        return new EvidenceInventoryPortfolioCatalog(manifest);
    }

    public Profile GetProfile() => _profile;

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
        IReadOnlyList<ManifestClaim> Claims,
        string? Headline = null,
        IReadOnlyList<string>? FocusAreas = null);

    private sealed record ManifestClaim(
        string ClaimId,
        string Statement,
        string Status,
        string Citation);
}
