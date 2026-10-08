using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Rafael.Portfolio.Modules.Assistant.Application;
using Rafael.Portfolio.Modules.JobMatching.Application;
using Rafael.Portfolio.Modules.JobMatching.Domain;

namespace Rafael.Portfolio.Web.Endpoints;

public static class JobMatchingEndpoints
{
    public static RouteGroupBuilder MapJobMatchingEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/jobs/analyze", (
            [FromBody] JobAnalysisRequest? request,
            HttpContext httpContext,
            IJobMatchingService jobMatchingService,
            IAssistantRateLimiter rateLimiter,
            IConfiguration configuration,
            IHostEnvironment environment,
            ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("Rafael.Portfolio.JobMatching");
            var stopwatch = Stopwatch.StartNew();

            var identityError = AssistantEndpoints.ValidateIdentity(httpContext, configuration, environment, out var identity);
            if (identityError is not null)
            {
                return Results.Json(new { error = identityError }, statusCode: StatusCodes.Status403Forbidden);
            }

            if (!rateLimiter.TryAcquire(identity!.RateLimitKey!, identity.IsTeamTier, identity.CountryCode, out var retryAfter))
            {
                httpContext.Response.Headers.RetryAfter = Math.Ceiling(retryAfter.TotalSeconds).ToString();
                return Results.Json(
                    new { error = "Rate limit exceeded. Please wait before submitting another job analysis." },
                    statusCode: StatusCodes.Status429TooManyRequests);
            }

            if (!JobAnalysisRequest.IsValid(request, out var validationError))
            {
                return Results.BadRequest(new { error = validationError });
            }

            var response = jobMatchingService.Analyze(request!);
            stopwatch.Stop();

            // Bounded observability: log metadata only, never raw vacancy text or analysis body
            logger.LogInformation(
                "JobAnalysis completed: ClientHash={ClientHash}, VacancyLength={Length}, Requirements={Reqs}, DirectMatches={Matches}, Inferences={Inferences}, Gaps={Gaps}, LatencyMs={LatencyMs}",
                HashClientKey(identity.RateLimitKey!),
                request!.VacancyText.Length,
                response.ExtractedRequirements.Count,
                response.DirectMatches.Count,
                response.Inferences.Count,
                response.Gaps.Count,
                stopwatch.ElapsedMilliseconds);

            return Results.Ok(response);
        })
        .WithName("JobMatchingAnalyze")
        .WithSummary("Analyze a job vacancy against Rafael's verified public evidence");

        return api;
    }

    private static string HashClientKey(string key)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(bytes)[..12];
    }
}
