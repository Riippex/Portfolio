namespace Rafael.Portfolio.ArchitectureTests;

public sealed class ApplicationLayerTests
{
    public static IEnumerable<object[]> ModuleNames()
    {
        yield return ["Portfolio"];
        yield return ["Knowledge"];
        yield return ["Assistant"];
        yield return ["JobMatching"];
        yield return ["Contact"];
    }

    [Theory]
    [MemberData(nameof(ModuleNames))]
    public void Application_layer_does_not_reference_infrastructure_namespaces(string module)
    {
        var applicationDir = Path.Combine(
            FindRepositoryRoot(),
            "backend", "src", "Modules", module,
            $"Rafael.Portfolio.Modules.{module}", "Application");

        if (!Directory.Exists(applicationDir))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(applicationDir, "*.cs", SearchOption.AllDirectories))
        {
            var content = File.ReadAllText(file);
            Assert.DoesNotContain(
                $"Rafael.Portfolio.Modules.{module}.Infrastructure",
                content,
                StringComparison.Ordinal);
        }
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "backend", "Rafael.Portfolio.sln")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root directory.");
    }
}
