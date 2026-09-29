namespace Rafael.Portfolio.UnitTests;

internal static class TestRepositoryRoot
{
    internal static string Find()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "README.md")) &&
                Directory.Exists(Path.Combine(current.FullName, "docs", "evidence")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root directory.");
    }

    internal static string EvidenceInventoryManifestPath =>
        Path.Combine(Find(), "docs", "evidence", "inventory.json");
}
