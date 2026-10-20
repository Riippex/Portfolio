namespace Rafael.Portfolio.ArchitectureTests;

public sealed class AssistantOrchestrationTests
{
    [Fact]
    public void Orchestration_has_no_sync_over_async_or_cross_module_implementation_dependencies()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "backend", "Rafael.Portfolio.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var application = Path.Combine(root.FullName, "backend/src/Modules/Assistant/Rafael.Portfolio.Modules.Assistant/Application");
        foreach (var file in Directory.EnumerateFiles(application, "*.cs", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            foreach (var forbidden in new[] { ".Result", ".Wait(", "GetAwaiter().GetResult()", "Modules.Knowledge", "Modules.JobMatching" })
                Assert.DoesNotContain(forbidden, text, StringComparison.Ordinal);
        }
        Assert.False(File.Exists(Path.Combine(application, "../Infrastructure/ModelGroundedSynthesizer.cs")));
    }
}
