using System.Text.RegularExpressions;

namespace Rafael.Portfolio.Modules.Knowledge.Domain;

public sealed partial record RetrievalQuery(
    string QueryText,
    int Limit = 5,
    string? SlugFilter = null)
{
    public const int MaxQueryTextLength = 200;
    public const int MaxSlugFilterLength = 64;

    public static bool IsValidSlugFilter(string slug) => SlugFilterPattern().IsMatch(slug);

    [GeneratedRegex("^[a-z0-9][a-z0-9-]*$", RegexOptions.IgnoreCase)]
    private static partial Regex SlugFilterPattern();
}
