namespace Rafael.Portfolio.Modules.Portfolio.Domain;

public sealed record ProfileLink(string Platform, string Url)
{
    public const string LinkedInPlatform = "linkedin";
    public const string GitHubPlatform = "github";
    public const int MaxLinksCount = 2;
    public const int MaxUrlLength = 256;

    private static readonly HashSet<string> AllowedLinkedInHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "linkedin.com",
        "www.linkedin.com",
        "co.linkedin.com"
    };

    private static readonly HashSet<string> AllowedGitHubHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "github.com",
        "www.github.com"
    };

    public static IReadOnlyList<ProfileLink> ValidateAndNormalize(
        IReadOnlyList<ProfileLink>? links,
        string itemIdentifier = "profile")
    {
        if (links is null || links.Count == 0)
        {
            return [];
        }

        if (links.Count > MaxLinksCount)
        {
            throw new InvalidOperationException(
                $"Profile item '{itemIdentifier}' cannot declare more than {MaxLinksCount} public links.");
        }

        var seenPlatforms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalized = new List<ProfileLink>(links.Count);

        foreach (var link in links)
        {
            if (link is null)
            {
                throw new InvalidOperationException(
                    $"Profile item '{itemIdentifier}' contains a null public link entry.");
            }

            var platform = NormalizePlatform(link.Platform, itemIdentifier);
            if (!seenPlatforms.Add(platform))
            {
                throw new InvalidOperationException(
                    $"Profile item '{itemIdentifier}' declares duplicate '{platform}' public links.");
            }

            if (!IsValidPlatformUrl(platform, link.Url))
            {
                throw new InvalidOperationException(
                    $"Profile item '{itemIdentifier}' declares an invalid or unsafe URL for platform '{platform}'.");
            }

            normalized.Add(new ProfileLink(platform, link.Url));
        }

        return normalized;
    }

    public static bool IsSupportedPlatform(string? platform) =>
        string.Equals(platform, LinkedInPlatform, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(platform, GitHubPlatform, StringComparison.OrdinalIgnoreCase);

    public static bool IsValidPlatformUrl(string? platform, string? url)
    {
        if (!IsSupportedPlatform(platform) ||
            string.IsNullOrEmpty(url) ||
            url.Length > MaxUrlLength)
        {
            return false;
        }

        foreach (var ch in url)
        {
            if (char.IsWhiteSpace(ch) ||
                char.IsControl(ch) ||
                ch is '<' or '>' or '"' or '\'' or '`' or '\\')
            {
                return false;
            }
        }

        if (url.Contains('?') || url.Contains('#') || url.Contains('@'))
        {
            return false;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !uri.IsDefaultPort ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        var expectedOrigin = $"https://{uri.Host}";
        if (!url.StartsWith(expectedOrigin + "/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rawPath = url[expectedOrigin.Length..];
        var escapedPath = uri.GetComponents(UriComponents.Path | UriComponents.KeepDelimiter, UriFormat.UriEscaped);
        if (!string.Equals(rawPath, escapedPath, StringComparison.Ordinal))
        {
            return false;
        }

        if (string.Equals(platform, LinkedInPlatform, StringComparison.OrdinalIgnoreCase))
        {
            return AllowedLinkedInHosts.Contains(uri.IdnHost) &&
                   AllowedLinkedInHosts.Contains(uri.Host) &&
                   IsValidLinkedInPath(rawPath);
        }

        if (string.Equals(platform, GitHubPlatform, StringComparison.OrdinalIgnoreCase))
        {
            return AllowedGitHubHosts.Contains(uri.IdnHost) &&
                   AllowedGitHubHosts.Contains(uri.Host) &&
                   IsValidGitHubPath(rawPath);
        }

        return false;
    }

    private static string NormalizePlatform(string? platform, string itemIdentifier)
    {
        if (string.IsNullOrWhiteSpace(platform))
        {
            throw new InvalidOperationException(
                $"Profile item '{itemIdentifier}' declares a link with an empty platform.");
        }

        if (string.Equals(platform, LinkedInPlatform, StringComparison.Ordinal))
        {
            return LinkedInPlatform;
        }

        if (string.Equals(platform, GitHubPlatform, StringComparison.Ordinal))
        {
            return GitHubPlatform;
        }

        throw new InvalidOperationException(
            $"Profile item '{itemIdentifier}' declares unsupported link platform '{platform}'.");
    }

    private static bool IsValidLinkedInPath(string path)
    {
        const string prefix = "/in/";
        if (!path.StartsWith(prefix, StringComparison.Ordinal) ||
            path.Length <= prefix.Length ||
            path.EndsWith('/') ||
            path.Contains("//", StringComparison.Ordinal) ||
            path.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        var handle = path[prefix.Length..];
        if (handle.Length is < 1 or > 128)
        {
            return false;
        }

        for (var i = 0; i < handle.Length; i++)
        {
            var ch = handle[i];
            if (ch == '%')
            {
                if (i + 2 >= handle.Length ||
                    !Uri.IsHexDigit(handle[i + 1]) ||
                    !Uri.IsHexDigit(handle[i + 2]))
                {
                    return false;
                }

                // Reject percent-encoded slashes, control chars, or ASCII markup
                var hexValue = Convert.ToInt32(handle.Substring(i + 1, 2), 16);
                if (hexValue <= 0x20 || hexValue is 0x22 or 0x27 or 0x2F or 0x3C or 0x3E or 0x5C or 0x60 or 0x7F)
                {
                    return false;
                }

                i += 2;
                continue;
            }

            if (!char.IsAsciiLetterOrDigit(ch) && ch is not ('-' or '_' or '.'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsValidGitHubPath(string path)
    {
        if (!path.StartsWith('/') || path.Length <= 1 || path.IndexOf('/', 1) >= 0)
        {
            return false;
        }

        var handle = path[1..];
        if (handle.Length is < 1 or > 39 ||
            handle[0] == '-' ||
            handle[^1] == '-' ||
            handle.Contains("--", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var ch in handle)
        {
            if (!char.IsAsciiLetterOrDigit(ch) && ch != '-')
            {
                return false;
            }
        }

        return true;
    }
}
