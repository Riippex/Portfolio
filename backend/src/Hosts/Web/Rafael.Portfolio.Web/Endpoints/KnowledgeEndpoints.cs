using Rafael.Portfolio.Modules.Knowledge.Application;

namespace Rafael.Portfolio.Web.Endpoints;

public static class KnowledgeEndpoints
{
    public static RouteGroupBuilder MapKnowledgeEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/evidence", (IEvidenceSource source) => Results.Ok(source.GetAllEvidence()))
            .WithName("GetAllEvidence");

        api.MapGet("/evidence/{slug}", (string slug, IEvidenceSource source) =>
        {
            var evidence = source.GetEvidenceBySlug(slug);
            return evidence is not null ? Results.Ok(evidence) : Results.NotFound();
        })
        .WithName("GetEvidenceBySlug");

        return api;
    }
}
