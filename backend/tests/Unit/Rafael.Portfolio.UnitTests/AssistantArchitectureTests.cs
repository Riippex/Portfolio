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
            .ToList();

        Assert.NotEmpty(projectReferences);

        foreach (var reference in projectReferences)
        {
            var fileName = Path.GetFileName(reference);
            Assert.Equal("Rafael.Portfolio.BuildingBlocks.csproj", fileName);
            Assert.DoesNotContain(".Hosts.", reference, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Modules.Knowledge", reference, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Modules.Portfolio", reference, StringComparison.OrdinalIgnoreCase);
        }
    }
}
