using System.Xml.Linq;

namespace Rafael.Portfolio.UnitTests;

public sealed class AssistantArchitectureTests
{
    [Fact]
    public void Assistant_module_references_only_BuildingBlocks()
    {
        var root = TestRepositoryRoot.Find();
        var csprojPath = Path.Combine(
            root,
            "backend",
            "src",
            "Modules",
            "Assistant",
            "Rafael.Portfolio.Modules.Assistant",
            "Rafael.Portfolio.Modules.Assistant.csproj");

        Assert.True(File.Exists(csprojPath), $"Project file not found at {csprojPath}");

        var doc = XDocument.Load(csprojPath);
        var projectReferences = doc.Descendants("ProjectReference")
            .Select(x => x.Attribute("Include")?.Value)
            .Where(x => !string.IsNullOrEmpty(x))
            .Select(x => x!)
            .ToList();

        Assert.NotEmpty(projectReferences);

        foreach (var reference in projectReferences)
        {
            var violation = AssistantDependencyRules.FindViolation(reference);
            Assert.True(violation is null, violation);
        }
    }

    [Theory]
    [InlineData(@"..\..\..\BuildingBlocks\Rafael.Portfolio.BuildingBlocks\Rafael.Portfolio.BuildingBlocks.csproj")]
    [InlineData("../../../BuildingBlocks/Rafael.Portfolio.BuildingBlocks/Rafael.Portfolio.BuildingBlocks.csproj")]
    [InlineData(@"..\..\../BuildingBlocks\Rafael.Portfolio.BuildingBlocks/Rafael.Portfolio.BuildingBlocks.csproj")]
    [InlineData("Rafael.Portfolio.BuildingBlocks.csproj")]
    public void BuildingBlocks_reference_is_accepted_with_either_slash_style_on_any_host(string reference)
    {
        Assert.Null(AssistantDependencyRules.FindViolation(reference));
    }

    [Theory]
    // Windows-style separators, which Path.GetFileName does not split on Linux.
    [InlineData(@"..\..\Knowledge\Rafael.Portfolio.Modules.Knowledge\Rafael.Portfolio.Modules.Knowledge.csproj")]
    [InlineData(@"..\..\Portfolio\Rafael.Portfolio.Modules.Portfolio\Rafael.Portfolio.Modules.Portfolio.csproj")]
    [InlineData(@"..\..\..\Hosts\Web\Rafael.Portfolio.Web\Rafael.Portfolio.Web.csproj")]
    [InlineData(@"..\..\JobMatching\Rafael.Portfolio.Modules.JobMatching\Rafael.Portfolio.Modules.JobMatching.csproj")]
    [InlineData(@"..\..\..\BuildingBlocks\Rafael.Portfolio.BuildingBlocks\Rafael.Portfolio.BuildingBlocks.Extras.csproj")]
    // Unix-style separators.
    [InlineData("../../Knowledge/Rafael.Portfolio.Modules.Knowledge/Rafael.Portfolio.Modules.Knowledge.csproj")]
    [InlineData("../../Portfolio/Rafael.Portfolio.Modules.Portfolio/Rafael.Portfolio.Modules.Portfolio.csproj")]
    [InlineData("../../../Hosts/Web/Rafael.Portfolio.Web/Rafael.Portfolio.Web.csproj")]
    [InlineData("../../JobMatching/Rafael.Portfolio.Modules.JobMatching/Rafael.Portfolio.Modules.JobMatching.csproj")]
    [InlineData("../../../BuildingBlocks/Rafael.Portfolio.BuildingBlocks/Rafael.Portfolio.BuildingBlocks.Extras.csproj")]
    // A BuildingBlocks directory must not launder a different project file.
    [InlineData(@"..\..\..\BuildingBlocks\Rafael.Portfolio.BuildingBlocks\Other.csproj")]
    [InlineData("../../../BuildingBlocks/Rafael.Portfolio.BuildingBlocks/Other.csproj")]
    // A trailing separator names no project file.
    [InlineData(@"..\..\..\BuildingBlocks\Rafael.Portfolio.BuildingBlocks\")]
    [InlineData("../../../BuildingBlocks/Rafael.Portfolio.BuildingBlocks/")]
    public void Any_other_reference_is_rejected_with_either_slash_style_on_any_host(string reference)
    {
        Assert.NotNull(AssistantDependencyRules.FindViolation(reference));
    }

    [Theory]
    [InlineData(@"..\..\..\BuildingBlocks\Rafael.Portfolio.BuildingBlocks\Rafael.Portfolio.BuildingBlocks.csproj", "Rafael.Portfolio.BuildingBlocks.csproj")]
    [InlineData("../../../BuildingBlocks/Rafael.Portfolio.BuildingBlocks/Rafael.Portfolio.BuildingBlocks.csproj", "Rafael.Portfolio.BuildingBlocks.csproj")]
    [InlineData(@"..\mixed/styles\Project.csproj", "Project.csproj")]
    [InlineData("Project.csproj", "Project.csproj")]
    [InlineData(@"..\folder\", "")]
    public void ProjectReference_file_name_ignores_the_host_path_separator(string reference, string expected)
    {
        Assert.Equal(expected, AssistantDependencyRules.FileName(reference));
    }

    [Theory]
    [InlineData("Rafael.Portfolio.Modules.Knowledge")]
    [InlineData("Rafael.Portfolio.Modules.Portfolio")]
    [InlineData("rafael.portfolio.modules.knowledge")]
    public void Module_and_host_fragments_are_matched_without_regard_to_case(string fragment)
    {
        Assert.NotNull(AssistantDependencyRules.FindViolation($"..\\x\\{fragment}\\Rafael.Portfolio.BuildingBlocks.csproj"));
    }
}

/// <summary>
/// The Assistant module may reference only BuildingBlocks. ProjectReference paths are written
/// with whichever separator their author's OS used, and MSBuild accepts both everywhere, so
/// the rules must too: <see cref="Path.GetFileName(string?)"/> only splits on the host's own
/// separators and would treat a backslash path as one long file name on Linux.
/// </summary>
internal static class AssistantDependencyRules
{
    private const string AllowedFileName = "Rafael.Portfolio.BuildingBlocks.csproj";

    private static readonly string[] ForbiddenFragments =
    [
        ".Hosts.",
        "Modules.Knowledge",
        "Modules.Portfolio"
    ];

    /// <summary>The last path segment, splitting on both '/' and '\'.</summary>
    public static string FileName(string reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        var segments = reference.Split('/', '\\');
        return segments[^1];
    }

    /// <summary>Returns why the reference is not allowed, or null when it is.</summary>
    public static string? FindViolation(string reference)
    {
        var fileName = FileName(reference);
        if (!string.Equals(fileName, AllowedFileName, StringComparison.Ordinal))
        {
            return $"Assistant may reference only {AllowedFileName}, but references '{fileName}' ({reference}).";
        }

        foreach (var fragment in ForbiddenFragments)
        {
            if (reference.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                return $"Assistant must not reference '{fragment}' ({reference}).";
            }
        }

        return null;
    }
}
