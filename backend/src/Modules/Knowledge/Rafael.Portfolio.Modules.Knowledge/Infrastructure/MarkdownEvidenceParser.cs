using System.Text;
using Rafael.Portfolio.Modules.Knowledge.Domain;

namespace Rafael.Portfolio.Modules.Knowledge.Infrastructure;

public static class MarkdownEvidenceParser
{
    public static IReadOnlyList<EvidenceSection> ParseSections(string markdown, IReadOnlyList<EvidenceClaim> claims)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return [];
        }

        var normalized = markdown.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized.Split('\n');

        var sections = new List<EvidenceSection>();
        string? currentHeading = null;
        var currentContent = new StringBuilder();

        foreach (var line in lines)
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                if (currentHeading is not null || currentContent.Length > 0)
                {
                    var heading = currentHeading ?? "Overview";
                    var slug = Slugify(heading);
                    var content = currentContent.ToString().Trim();
                    var matchedClaims = FindClaimsForSection(slug, claims);

                    sections.Add(new EvidenceSection(heading, slug, content, matchedClaims));
                    currentContent.Clear();
                }

                currentHeading = line[3..].Trim();
            }
            else
            {
                currentContent.AppendLine(line);
            }
        }

        if (currentHeading is not null || currentContent.Length > 0)
        {
            var heading = currentHeading ?? "Overview";
            var slug = Slugify(heading);
            var content = currentContent.ToString().Trim();
            var matchedClaims = FindClaimsForSection(slug, claims);

            sections.Add(new EvidenceSection(heading, slug, content, matchedClaims));
        }

        return sections;
    }

    public static string Slugify(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var lower = text.Trim().ToLowerInvariant();
        var builder = new StringBuilder(lower.Length);
        var previousDash = false;

        foreach (var c in lower)
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(c);
                previousDash = false;
            }
            else if (c is ' ' or '-' or '_' && !previousDash)
            {
                builder.Append('-');
                previousDash = true;
            }
        }

        return builder.ToString().Trim('-');
    }

    private static IReadOnlyList<EvidenceClaim> FindClaimsForSection(string sectionSlug, IReadOnlyList<EvidenceClaim> claims)
    {
        if (claims.Count == 0)
        {
            return [];
        }

        var matched = new List<EvidenceClaim>();
        foreach (var claim in claims)
        {
            if (string.IsNullOrWhiteSpace(claim.Citation))
            {
                continue;
            }

            var hashIndex = claim.Citation.IndexOf('#');
            if (hashIndex >= 0)
            {
                var anchor = claim.Citation[(hashIndex + 1)..].Trim();
                if (string.Equals(anchor, sectionSlug, StringComparison.OrdinalIgnoreCase))
                {
                    matched.Add(claim);
                }
            }
        }

        return matched;
    }
}
