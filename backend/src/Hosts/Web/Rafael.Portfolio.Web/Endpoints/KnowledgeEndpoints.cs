using Microsoft.AspNetCore.Mvc;
using Rafael.Portfolio.Modules.Knowledge.Application;

namespace Rafael.Portfolio.Web.Endpoints;

public static class KnowledgeEndpoints
{
    public static RouteGroupBuilder MapKnowledgeEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/evidence", (IEvidenceSource source) => Results.Ok(source.GetAllEvidence()))
            .WithName("GetAllEvidence");

        api.MapGet("/evidence/search", (
            [FromQuery(Name = "q")] string? query,
            [FromQuery] int? limit,
            [FromQuery] string? slug,
            IEvidenceRetriever retriever) =>
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return Results.BadRequest(new { error = "Query parameter 'q' is required and cannot be empty." });
            }

            var results = retriever.Retrieve(query, limit ?? 5, slug);
            return Results.Ok(results);
        })
        .WithName("SearchEvidence");

        api.MapGet("/evidence/{slug}", (string slug, IEvidenceSource source) =>
        {
            var evidence = source.GetEvidenceBySlug(slug);
            return evidence is not null ? Results.Ok(evidence) : Results.NotFound();
        })
        .WithName("GetEvidenceBySlug");

        return api;
    }
}
