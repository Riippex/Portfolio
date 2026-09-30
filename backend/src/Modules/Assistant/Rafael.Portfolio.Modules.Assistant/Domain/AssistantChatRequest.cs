using System.Text.RegularExpressions;

namespace Rafael.Portfolio.Modules.Assistant.Domain;

public sealed partial record AssistantChatRequest(
    string Message,
    string? Slug = null)
{
    public const int MaxMessageLength = 500;
    public const int MaxSlugLength = 64;

    public static bool IsValidSlug(string slug) => SlugFilterPattern().IsMatch(slug);

    [GeneratedRegex("^[a-z0-9][a-z0-9-]*$", RegexOptions.IgnoreCase)]
    private static partial Regex SlugFilterPattern();
}
