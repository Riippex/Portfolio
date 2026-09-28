using Rafael.Portfolio.BuildingBlocks;

namespace Rafael.Portfolio.Modules.Portfolio;

public sealed class PortfolioModule : IModule
{
    private PortfolioModule()
    {
    }

    public static string Name => "Portfolio";
}
