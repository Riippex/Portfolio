using System.Text.Json;
using Rafael.Portfolio.Modules.Knowledge.Application;
using Rafael.Portfolio.Modules.Knowledge.Domain;

namespace Rafael.Portfolio.Modules.Knowledge.Infrastructure;

public sealed class FileSystemEvidenceSource : IEvidenceSource
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly PublicEvidenceInventory _inventory;
    private readonly IReadOnlyList<IngestedEvidenceDocument> _documents;
    private readonly Dictionary<string, IngestedEvidenceDocument> _bySlug;
    private readonly Dictionary<string, IngestedEvidenceDocument> _byId;

    public FileSystemEvidenceSource(
        PublicEvidenceInventory inventory,
        IReadOnlyDictionary<string, string> documentsByPath)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(documentsByPath);

        var normalizedItems = new List<PublicEvidenceItem>(inventory.Items.Count);
        var documents = new List<IngestedEvidenceDocument>(inventory.Items.Count);
        var bySlug = new Dictionary<string, IngestedEvidenceDocument>(StringComparer.OrdinalIgnoreCase);
        var byId = new Dictionary<string, IngestedEvidenceDocument>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in inventory.Items)
        {
            if (!EvidenceStatus.IsValid(item.EvidenceStatus))
            {
                throw new InvalidOperationException(
                    $"Evidence item '{item.Id}' has invalid status '{item.EvidenceStatus}'.");
            }

            if (!string.Equals(item.Kind, "profile", StringComparison.OrdinalIgnoreCase) &&
                item.Links is { Count: > 0 })
            {
                throw new InvalidOperationException(
                    $"Non-profile evidence item '{item.Id}' cannot declare profile links.");
            }

            var normalizedLinks = EvidenceProfileLink.ValidateAndNormalize(item.Links, item.Id);
            var normalizedItem = item with { Links = normalizedLinks };

            var markdown = ResolveDocumentContent(normalizedItem.DocumentPath, documentsByPath)
                ?? throw new InvalidOperationException(
                    $"Evidence document for item '{normalizedItem.Id}' not found for path '{normalizedItem.DocumentPath}'.");

            var sections = MarkdownEvidenceParser.ParseSections(markdown, normalizedItem.Claims);

            ValidateClaims(normalizedItem, sections);

            if (string.Equals(normalizedItem.Kind, "profile", StringComparison.OrdinalIgnoreCase))
            {
                (normalizedItem, sections, markdown) = ReconcileProfileLinks(normalizedItem, sections, markdown);
            }

            var document = new IngestedEvidenceDocument(
                normalizedItem,
                EvidenceVisibility.Public,
                markdown,
                sections);

            normalizedItems.Add(normalizedItem);
            documents.Add(document);

            if (!bySlug.TryAdd(normalizedItem.Slug, document))
            {
                throw new InvalidOperationException(
                    $"Duplicate evidence slug '{normalizedItem.Slug}' found in inventory.");
            }

            if (!byId.TryAdd(normalizedItem.Id, document))
            {
                throw new InvalidOperationException(
                    $"Duplicate evidence id '{normalizedItem.Id}' found in inventory.");
            }
        }

        _inventory = inventory with { Items = normalizedItems };
        _documents = documents;
        _bySlug = bySlug;
        _byId = byId;
    }

    public static string BundledManifestPath =>
        Path.Combine(AppContext.BaseDirectory, "evidence", "inventory.json");

    public static FileSystemEvidenceSource FromManifestFile(string manifestPath)
    {
        if (!File.Exists(manifestPath))
        {
            throw new InvalidOperationException(
                $"Canonical evidence inventory manifest not found at '{manifestPath}'.");
        }

        var json = File.ReadAllText(manifestPath);
        EvidenceManifestValidator.Validate(json);

        var inventory = JsonSerializer.Deserialize<PublicEvidenceInventory>(json, JsonOptions)
            ?? throw new InvalidOperationException(
                $"Canonical evidence inventory manifest at '{manifestPath}' could not be deserialized.");

        var evidenceRoot = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
        var documents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in inventory.Items)
        {
            var fullPath = ResolveContainedPath(evidenceRoot, item);
            if (!File.Exists(fullPath))
            {
                throw new InvalidOperationException(
                    $"Evidence document for item '{item.Id}' not found at '{fullPath}' (referenced as '{item.DocumentPath}').");
            }

            documents[item.DocumentPath] = File.ReadAllText(fullPath);
        }

        return new FileSystemEvidenceSource(inventory, documents);
    }

    public PublicEvidenceInventory GetInventory() => _inventory;

    public IReadOnlyList<IngestedEvidenceDocument> GetAllEvidence() => _documents;

    public IngestedEvidenceDocument? GetEvidenceBySlug(string slug) =>
        _bySlug.GetValueOrDefault(slug);

    public IngestedEvidenceDocument? GetEvidenceById(string id) =>
        _byId.GetValueOrDefault(id);

    private static string? ResolveDocumentContent(string documentPath, IReadOnlyDictionary<string, string> documentsByPath)
    {
        if (documentsByPath.TryGetValue(documentPath, out var content))
        {
            return content;
        }

        var normalized = NormalizePath(documentPath);
        foreach (var (key, value) in documentsByPath)
        {
            if (string.Equals(NormalizePath(key), normalized, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return null;
    }

    private static string ResolveContainedPath(string evidenceRoot, PublicEvidenceItem item)
    {
        var normalized = NormalizePath(item.DocumentPath);
        if (string.IsNullOrWhiteSpace(normalized) || Path.IsPathRooted(normalized))
        {
            throw new InvalidOperationException(
                $"Evidence document path '{item.DocumentPath}' for item '{item.Id}' must be a relative path inside the evidence directory.");
        }

        var fullPath = Path.GetFullPath(
            Path.Combine(evidenceRoot, normalized.Replace('/', Path.DirectorySeparatorChar)));
        var rootWithSeparator = evidenceRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(rootWithSeparator, PathContainmentComparison))
        {
            throw new InvalidOperationException(
                $"Evidence document path '{item.DocumentPath}' for item '{item.Id}' escapes the evidence directory '{evidenceRoot}'.");
        }

        return fullPath;
    }

    private static StringComparison PathContainmentComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static string NormalizePath(string path)
    {
        if (path.StartsWith("docs/evidence/", StringComparison.OrdinalIgnoreCase))
        {
            return path["docs/evidence/".Length..];
        }

        if (path.StartsWith("evidence/", StringComparison.OrdinalIgnoreCase))
        {
            return path["evidence/".Length..];
        }

        return path;
    }

    private static void ValidateClaims(PublicEvidenceItem item, IReadOnlyList<EvidenceSection> sections)
    {
        var sectionSlugs = sections.Select(s => s.Slug).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var claim in item.Claims)
        {
            if (!EvidenceStatus.IsValid(claim.Status))
            {
                throw new InvalidOperationException(
                    $"Claim '{claim.ClaimId}' in item '{item.Id}' has invalid status '{claim.Status}'.");
            }

            if (string.IsNullOrWhiteSpace(claim.Citation))
            {
                throw new InvalidOperationException(
                    $"Claim '{claim.ClaimId}' in item '{item.Id}' must declare a citation.");
            }

            var hashIndex = claim.Citation.IndexOf('#');
            var citationDocument = hashIndex >= 0 ? claim.Citation[..hashIndex] : claim.Citation;
            var anchor = hashIndex >= 0 ? claim.Citation[(hashIndex + 1)..].Trim() : string.Empty;

            if (!string.Equals(citationDocument, item.DocumentPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Claim '{claim.ClaimId}' citation '{claim.Citation}' must target the item's own canonical document '{item.DocumentPath}'.");
            }

            if (string.IsNullOrWhiteSpace(anchor))
            {
                throw new InvalidOperationException(
                    $"Claim '{claim.ClaimId}' citation '{claim.Citation}' must include a non-empty section anchor.");
            }

            if (!sectionSlugs.Contains(anchor))
            {
                throw new InvalidOperationException(
                    $"Claim '{claim.ClaimId}' citation anchor '#{anchor}' does not match any section in '{item.DocumentPath}'.");
            }
        }
    }

    private static (PublicEvidenceItem Item, IReadOnlyList<EvidenceSection> Sections, string Markdown) ReconcileProfileLinks(
        PublicEvidenceItem item,
        IReadOnlyList<EvidenceSection> sections,
        string markdown)
    {
        var linksSection = sections.FirstOrDefault(s =>
            string.Equals(s.Slug, "links", StringComparison.OrdinalIgnoreCase));

        if (item.Links.Count > 0 && linksSection is null)
        {
            throw new InvalidOperationException(
                $"Profile item '{item.Id}' declares public links but '{item.DocumentPath}' has no '#links' section.");
        }

        foreach (var link in item.Links)
        {
            if (!linksSection!.Content.Contains(link.Url, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Profile item '{item.Id}' link '{link.Url}' is not documented in '{item.DocumentPath}#links'.");
            }

            if (!linksSection.Claims.Any(c =>
                    string.Equals(c.Status, EvidenceStatus.Verified, StringComparison.OrdinalIgnoreCase) &&
                    c.Statement.Contains(link.Url, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(
                    $"Profile item '{item.Id}' link '{link.Url}' has no matching verified claim in '{item.DocumentPath}#links'.");
            }
        }

        if (linksSection is null)
        {
            return (item, sections, markdown);
        }

        var allLinkLines = linksSection.Content
            .Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();

        var retainedLines = allLinkLines
            .Where(line => item.Links.Any(link => line.Contains(link.Url, StringComparison.Ordinal)))
            .ToList();

        var removedLines = allLinkLines
            .Except(retainedLines, StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);

        var updatedMarkdown = removedLines.Count == 0
            ? markdown
            : string.Join(
                "\n",
                markdown
                    .Split('\n')
                    .Where(line => !removedLines.Contains(line.TrimEnd('\r'))));

        var retainedLinkClaims = linksSection.Claims
            .Where(claim => item.Links.Any(link => claim.Statement.Contains(link.Url, StringComparison.Ordinal)))
            .ToList();

        var removedLinkClaimIds = linksSection.Claims
            .Select(c => c.ClaimId)
            .Except(retainedLinkClaims.Select(c => c.ClaimId), StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var updatedItem = removedLinkClaimIds.Count == 0
            ? item
            : item with
            {
                Claims = item.Claims
                    .Where(c => !removedLinkClaimIds.Contains(c.ClaimId))
                    .ToList()
            };

        if (retainedLines.Count == 0 || retainedLinkClaims.Count == 0)
        {
            var remainingSections = sections
                .Where(s => !string.Equals(s.Slug, "links", StringComparison.OrdinalIgnoreCase))
                .ToList();
            return (updatedItem, remainingSections, updatedMarkdown);
        }

        var updatedLinksSection = new EvidenceSection(
            linksSection.Heading,
            linksSection.Slug,
            string.Join("\n", retainedLines),
            retainedLinkClaims);

        var updatedSections = sections
            .Select(s => string.Equals(s.Slug, "links", StringComparison.OrdinalIgnoreCase) ? updatedLinksSection : s)
            .ToList();

        return (updatedItem, updatedSections, updatedMarkdown);
    }
}
