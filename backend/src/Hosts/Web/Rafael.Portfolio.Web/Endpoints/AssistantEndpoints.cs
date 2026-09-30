using Microsoft.AspNetCore.Mvc;
using Rafael.Portfolio.Modules.Assistant.Application;
using Rafael.Portfolio.Modules.Assistant.Domain;

namespace Rafael.Portfolio.Web.Endpoints;

public static class AssistantEndpoints
{
    public static RouteGroupBuilder MapAssistantEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/assistant/chat", (
            [FromBody] AssistantChatRequest? request,
            IAssistantService assistantService) =>
        {
            if (request is null || string.IsNullOrWhiteSpace(request.Message))
            {
                return Results.BadRequest(new { error = "Request message is required and cannot be empty." });
            }

            if (request.Message.Length > AssistantChatRequest.MaxMessageLength)
            {
                return Results.BadRequest(new
                {
                    error = $"Message exceeds the maximum length of {AssistantChatRequest.MaxMessageLength} characters."
                });
            }

            if (request.Slug is not null &&
                (request.Slug.Length > AssistantChatRequest.MaxSlugLength ||
                 !AssistantChatRequest.IsValidSlug(request.Slug)))
            {
                return Results.BadRequest(new
                {
                    error = "Parameter 'slug' must be a valid slug (letters, digits, hyphens)."
                });
            }

            var response = assistantService.Chat(request);
            return Results.Ok(response);
        })
        .WithName("AssistantChat");

        return api;
    }
}
