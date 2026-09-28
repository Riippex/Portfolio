using System.Reflection;
using Rafael.Portfolio.Modules.Assistant;
using Rafael.Portfolio.Modules.Contact;
using Rafael.Portfolio.Modules.JobMatching;
using Rafael.Portfolio.Modules.Knowledge;
using Rafael.Portfolio.Modules.Portfolio;

namespace Rafael.Portfolio.ArchitectureTests;

public sealed class ModuleBoundaryTests
{
    public static IEnumerable<object[]> ModuleAssemblies()
    {
        yield return [typeof(PortfolioModule).Assembly];
        yield return [typeof(KnowledgeModule).Assembly];
        yield return [typeof(AssistantModule).Assembly];
        yield return [typeof(JobMatchingModule).Assembly];
        yield return [typeof(ContactModule).Assembly];
    }

    [Theory]
    [MemberData(nameof(ModuleAssemblies))]
    public void Modules_do_not_reference_a_host(Assembly moduleAssembly)
    {
        var references = moduleAssembly.GetReferencedAssemblies();

        Assert.DoesNotContain(references, reference => reference.Name?.Contains(".Hosts.") is true);
        Assert.DoesNotContain(references, reference => reference.Name?.EndsWith(".Web") is true);
    }
}
