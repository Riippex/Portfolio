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

        _inventory = inventory;

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

            var markdown = ResolveDocumentContent(item.DocumentPath, documentsByPath)
                ?? throw new InvalidOperationException(
                    $"Evidence document for item '{item.Id}' not found for path '{item.DocumentPath}'.");

            var sections = MarkdownEvidenceParser.ParseSections(markdown, item.Claims);

            ValidateClaims(item, sections);

            var document = new IngestedEvidenceDocument(
                item,
                EvidenceVisibility.Public,
                markdown,
                sections);

            documents.Add(document);

            if (!bySlug.TryAdd(item.Slug, document))
            {
                throw new InvalidOperationException(
                    $"Duplicate evidence slug '{item.Slug}' found in inventory.");
            }

            if (!byId.TryAdd(item.Id, document))
            {
                throw new InvalidOperationException(
                    $"Duplicate evidence id '{item.Id}' found in inventory.");
            }
        }

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
}
