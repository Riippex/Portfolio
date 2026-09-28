using Rafael.Portfolio.Modules.Portfolio.Application;

namespace Rafael.Portfolio.Web.Endpoints;

public static class PortfolioEndpoints
{
    public static RouteGroupBuilder MapPortfolioEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/profile", (IPortfolioCatalog catalog) => Results.Ok(catalog.GetProfile()))
            .WithName("GetProfile");

        api.MapGet("/projects", (IPortfolioCatalog catalog) => Results.Ok(catalog.GetProjects()))
            .WithName("GetProjects");

        api.MapGet("/projects/{slug}", (string slug, IPortfolioCatalog catalog) =>
        {
            var project = catalog.GetProjectBySlug(slug);
            return project is not null ? Results.Ok(project) : Results.NotFound();
        })
        .WithName("GetProjectBySlug");

        return api;
    }
}
