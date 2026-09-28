using Rafael.Portfolio.Modules.Portfolio.Domain;

namespace Rafael.Portfolio.Modules.Portfolio.Application;

public interface IPortfolioCatalog
{
    Profile GetProfile();

    IReadOnlyList<ProjectSummary> GetProjects();

    ProjectDetail? GetProjectBySlug(string slug);
}
