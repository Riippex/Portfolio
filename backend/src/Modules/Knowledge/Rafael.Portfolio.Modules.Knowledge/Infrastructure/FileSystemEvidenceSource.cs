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
            ValidateProvenance(normalizedItem, markdown);

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

    private static void ValidateProvenance(PublicEvidenceItem item, string markdown)
    {
        var isVerifiedItem = string.Equals(item.EvidenceStatus, EvidenceStatus.Verified, StringComparison.OrdinalIgnoreCase);
        var hasVerifiedClaims = item.Claims.Any(c =>
            string.Equals(c.Status, EvidenceStatus.Verified, StringComparison.OrdinalIgnoreCase));

        if (!isVerifiedItem && hasVerifiedClaims)
        {
            throw new InvalidOperationException(
                $"Evidence item '{item.Id}' with status '{item.EvidenceStatus}' cannot retain verified claims without verified provenance.");
        }

        if (item.SourceUrl is not null && !IsValidApprovedSourceUrl(item.SourceUrl))
        {
            throw new InvalidOperationException(
                $"Evidence item '{item.Id}' has invalid or unapproved sourceUrl '{item.SourceUrl}'.");
        }

        if (isVerifiedItem && string.IsNullOrWhiteSpace(item.SourceUrl))
        {
            throw new InvalidOperationException(
                $"Verified evidence item '{item.Id}' must declare an approved HTTPS sourceUrl.");
        }

        var canonicalSourceValues = ExtractPreambleMetadataValues(markdown, "- Canonical Source:");
        if (canonicalSourceValues.Count > 1)
        {
            throw new InvalidOperationException(
                $"Evidence document '{item.DocumentPath}' for item '{item.Id}' declares multiple 'Canonical Source' lines.");
        }

        var declaredCanonicalSource = canonicalSourceValues.Count == 1 ? canonicalSourceValues[0] : null;
        var hasRecordHeader = ExtractPreambleMetadataValues(markdown, "- Identifier:").Count > 0;

        if (isVerifiedItem)
        {
            if (hasRecordHeader && string.IsNullOrWhiteSpace(declaredCanonicalSource))
            {
                throw new InvalidOperationException(
                    $"Verified evidence document '{item.DocumentPath}' for item '{item.Id}' must declare a '- Canonical Source:' matching sourceUrl.");
            }

            if (declaredCanonicalSource is not null &&
                !string.Equals(declaredCanonicalSource, item.SourceUrl, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Verified evidence item '{item.Id}' sourceUrl '{item.SourceUrl}' does not match '{item.DocumentPath}' Canonical Source '{declaredCanonicalSource}'.");
            }
        }
        else if (!string.IsNullOrWhiteSpace(declaredCanonicalSource) &&
                 !string.Equals(declaredCanonicalSource, item.SourceUrl, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Pending evidence item '{item.Id}' sourceUrl '{item.SourceUrl ?? "<null>"}' does not match '{item.DocumentPath}' Canonical Source '{declaredCanonicalSource}'.");
        }
    }

    private static bool IsValidApprovedSourceUrl(string sourceUrl)
    {
        if (string.IsNullOrWhiteSpace(sourceUrl) ||
            sourceUrl.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)) ||
            sourceUrl.Contains("github.com/Riippex/Portfolio", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri)
            && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrEmpty(uri.UserInfo)
            && !string.IsNullOrWhiteSpace(uri.Host);
    }

    private static List<string> ExtractPreambleMetadataValues(string markdown, string prefix)
    {
        var values = new List<string>();
        foreach (var rawLine in markdown.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                break;
            }

            if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                values.Add(line[prefix.Length..].Trim());
            }
        }

        return values;
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
            if (!EvidenceProfileLink.ContainsExactUrl(linksSection!.Content, link.Url))
            {
                throw new InvalidOperationException(
                    $"Profile item '{item.Id}' link '{link.Url}' is not documented in '{item.DocumentPath}#links'.");
            }

            if (!linksSection.Claims.Any(c => EvidenceProfileLink.MatchesVerifiedClaim(link, c)))
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

        var retainedLinkClaims = linksSection.Claims
            .Where(claim => item.Links.Any(link => EvidenceProfileLink.MatchesVerifiedClaim(link, claim)))
            .ToList();

        var removedLinkClaimIds = linksSection.Claims
            .Select(c => c.ClaimId)
            .Except(retainedLinkClaims.Select(c => c.ClaimId), StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Display links (item.Links / #links claims) and canonical provenance (item.SourceUrl)
        // are independent concerns: withdrawing a display link never rewrites item.SourceUrl.
        var updatedItem = removedLinkClaimIds.Count == 0
            ? item
            : item with
            {
                Claims = item.Claims
                    .Where(c => !removedLinkClaimIds.Contains(c.ClaimId))
                    .ToList()
            };

        if (item.Links.Count == 0 || retainedLinkClaims.Count == 0)
        {
            var remainingSections = sections
                .Where(s => !string.Equals(s.Slug, "links", StringComparison.OrdinalIgnoreCase))
                .ToList();
            return (updatedItem, remainingSections, ReplaceLinksSectionInMarkdown(markdown, null));
        }

        var renderedLines = new List<string>(item.Links.Count);
        foreach (var link in item.Links)
        {
            var cleanLines = allLinkLines
                .Where(line => EvidenceProfileLink.IsCleanLineForLink(line, link))
                .ToList();

            if (cleanLines.Count > 0)
            {
                renderedLines.AddRange(cleanLines);
            }
            else
            {
                foreach (var claim in retainedLinkClaims.Where(c => EvidenceProfileLink.MatchesVerifiedClaim(link, c)))
                {
                    renderedLines.Add($"- {claim.Statement}");
                }
            }
        }

        var updatedContent = string.Join("\n", renderedLines);
        var updatedMarkdown = string.Equals(updatedContent, linksSection.Content, StringComparison.Ordinal)
            ? markdown
            : ReplaceLinksSectionInMarkdown(markdown, updatedContent);

        var updatedLinksSection = new EvidenceSection(
            linksSection.Heading,
            linksSection.Slug,
            updatedContent,
            retainedLinkClaims);

        var updatedSections = sections
            .Select(s => string.Equals(s.Slug, "links", StringComparison.OrdinalIgnoreCase) ? updatedLinksSection : s)
            .ToList();

        return (updatedItem, updatedSections, updatedMarkdown);
    }

    private static string ReplaceLinksSectionInMarkdown(string markdown, string? replacementContent)
    {
        var lines = markdown.Split('\n').Select(line => line.TrimEnd('\r')).ToList();
        var result = new List<string>(lines.Count);
        var inLinksSection = false;

        foreach (var line in lines)
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                var heading = line[3..].Trim();
                if (string.Equals(heading, "Links", StringComparison.OrdinalIgnoreCase))
                {
                    inLinksSection = true;
                    if (!string.IsNullOrWhiteSpace(replacementContent))
                    {
                        result.Add(line);
                        result.Add(string.Empty);
                        result.AddRange(replacementContent.Split('\n'));
                        result.Add(string.Empty);
                    }

                    continue;
                }

                if (inLinksSection)
                {
                    inLinksSection = false;
                }
            }

            if (!inLinksSection)
            {
                result.Add(line);
            }
        }

        return string.Join("\n", result).TrimEnd();
    }
}
