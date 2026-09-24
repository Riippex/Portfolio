using Rafael.Portfolio.Modules.Portfolio;

namespace Rafael.Portfolio.ArchitectureTests;

public sealed class ModuleBoundaryTests
{
    [Fact]
    public void Portfolio_module_does_not_reference_a_host()
    {
        var references = typeof(PortfolioModule).Assembly.GetReferencedAssemblies();

        Assert.DoesNotContain(references, reference => reference.Name?.Contains(".Hosts.") is true);
        Assert.DoesNotContain(references, reference => reference.Name?.EndsWith(".Web") is true);
    }
}
