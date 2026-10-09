using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Rafael.Portfolio.Modules.Assistant.Application;
using Rafael.Portfolio.Modules.Assistant.Domain;

namespace Rafael.Portfolio.UnitTests;

/// <summary>
/// Chat and SSE answers from the real host composition and the real evidence manifest (the files
/// under docs/evidence), with the model disabled. Nothing here stubs retrieval or the adapter.
/// </summary>
public sealed class ProfileEvidenceAssistantTests
{
    private const string ProfileSource = "https://co.linkedin.com/in/rafael-pati%C3%B1o-diaz";
    private const string ProfileVersion = "2026.10.1";

    // Claim ids that once labelled different, now removed assertions must never come back.
    private static readonly string[] RetiredClaimIds = ["claim-profile-01", "claim-profile-02", "claim-profile-03"];

    // Elaborations the owner-approved profile subset does not support.
    private static readonly string[] RemovedPhrases =
    [
        "prompt systems", "intelligent feature", "serverless", "deterministic boundaries",
        "cross-session", "transcript", "Expinn Technology", "currently", "works as", "is employed"
    ];

    private static WebApplicationFactory<Program> CreateHost() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Portfolio:Stage", "local");
        });

    private static async Task<List<AssistantStreamEvent>> StreamAsync(IAssistantService service, string message, string? slug = null)
    {
        var events = new List<AssistantStreamEvent>();
        await foreach (var streamEvent in service.StreamChatAsync(new AssistantChatRequest(message, slug)))
        {
            events.Add(streamEvent);
        }

        return events;
    }

    private static void AssertProfileCitation(AssistantCitation citation)
    {
        Assert.Equal("evidence-profile", citation.DocumentId);
        Assert.Equal("profile", citation.Slug);
        Assert.Equal("verified", citation.EvidenceStatus);
        Assert.Equal(ProfileSource, citation.SourceUrl);
        Assert.Equal(ProfileVersion, citation.Version);
        Assert.DoesNotContain(citation.Claims, claim => RetiredClaimIds.Contains(claim));
    }

    private static void AssertNoRemovedPhrases(string answer) =>
        Assert.All(RemovedPhrases, phrase => Assert.DoesNotContain(phrase, answer, StringComparison.OrdinalIgnoreCase));

    [Theory]
    [InlineData("Does Rafael have studies at Universidad Manuela Beltran?", "Studies", "claim-profile-04", "in progress")]
    [InlineData("Tell me about the AI Engineer role at Expinn", "Experience", "claim-profile-05", "announces an AI Engineer role at Expinn")]
    [InlineData("What is StaffHub in Rafael's profile?", "Experience", "claim-profile-06", "lists StaffHub in its experience section")]
    [InlineData("What are the OCI and ONE training references?", "Training", "claim-profile-07", "OCI (Oracle Cloud Infrastructure) and ONE (Oracle Next Education)")]
    public async Task Supported_profile_questions_are_answered_from_the_external_source_with_fresh_claim_ids(
        string message, string section, string claimId, string expectedText)
    {
        await using var host = CreateHost();
        var service = host.Services.GetRequiredService<IAssistantService>();

        var response = service.Chat(new AssistantChatRequest(message));

        Assert.Equal(AssistantGroundingStatus.Grounded, response.GroundingStatus);
        Assert.Contains(expectedText, response.Answer);
        Assert.All(response.Citations, AssertProfileCitation);
        Assert.Contains(response.Citations, c => c.SectionHeading == section && c.Claims.Contains(claimId));
        AssertNoRemovedPhrases(response.Answer);

        // The SSE transport answers the same question from the same evidence.
        var events = await StreamAsync(service, message);
        Assert.Equal(AssistantGroundingStatus.Grounded, events[0].GroundingStatus);
        var streamed = events.Where(e => e.Citation is not null).Select(e => e.Citation!).ToList();
        Assert.All(streamed, AssertProfileCitation);
        Assert.Contains(streamed, c => c.SectionHeading == section && c.Claims.Contains(claimId));
        var text = string.Join(" ", events.Where(e => e.Type == "token").Select(e => e.Text));
        Assert.Contains(expectedText, text);
        AssertNoRemovedPhrases(text);
        Assert.True(events[^1].Done);
    }

    [Fact]
    public async Task The_verified_profile_offers_only_the_curated_sections_and_claims()
    {
        await using var host = CreateHost();
        var service = host.Services.GetRequiredService<IAssistantService>();

        var response = service.Chat(new AssistantChatRequest("Rafael Patiño profile studies experience training basis and limits"));

        Assert.Equal(AssistantGroundingStatus.Grounded, response.GroundingStatus);
        var claims = response.Citations.SelectMany(c => c.Claims).Distinct().Order().ToList();
        Assert.All(claims, claim => Assert.Contains(claim, new[] { "claim-profile-04", "claim-profile-05", "claim-profile-06", "claim-profile-07" }));
        Assert.DoesNotContain(response.Citations, c => c.SectionHeading is "Focus areas" or "Professional context" or "Engineering principles");
        AssertNoRemovedPhrases(response.Answer);
    }

    [Theory]
    [InlineData("Zero cross-session transcript retention guarantees")]
    [InlineData("Serverless runtime boundaries")]
    [InlineData("Prompt systems and tooling")]
    public async Task Removed_claims_are_not_documented_in_chat_and_sse(string message)
    {
        await using var host = CreateHost();
        var service = host.Services.GetRequiredService<IAssistantService>();

        var response = service.Chat(new AssistantChatRequest(message));
        Assert.Equal(AssistantGroundingStatus.NotDocumented, response.GroundingStatus);
        Assert.Empty(response.Citations);

        var events = await StreamAsync(service, message);
        Assert.Equal(AssistantGroundingStatus.NotDocumented, events[0].GroundingStatus);
        Assert.DoesNotContain(events, e => e.Citation is not null);
    }

    [Fact]
    public async Task An_unsupported_assertion_never_gets_a_stronger_answer_than_the_profile_supports()
    {
        await using var host = CreateHost();
        var service = host.Services.GetRequiredService<IAssistantService>();

        var response = service.Chat(new AssistantChatRequest(
            "Is Rafael currently employed at Expinn with intelligent feature engineering and serverless duties?"));

        // Whatever is cited, it is the owner-profile record and none of the removed elaborations.
        Assert.All(response.Citations, AssertProfileCitation);
        Assert.DoesNotContain("prompt systems", response.Answer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("intelligent feature", response.Answer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("serverless", response.Answer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Expinn Technology", response.Answer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("currently works", response.Answer, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Tell me about Vextis AI engineering")]
    [InlineData("Tell me about Kinetiq V AI engineering")]
    [InlineData("What AI engineering did Rafael do in kinetiq-v?")]
    [InlineData("Explain the JobTY AI engineering vacancy matching")]
    [InlineData("vextis")]
    public async Task A_named_pending_project_is_not_answered_with_unrelated_profile_evidence(string message)
    {
        await using var host = CreateHost();
        var service = host.Services.GetRequiredService<IAssistantService>();

        var response = service.Chat(new AssistantChatRequest(message));
        Assert.Equal(AssistantGroundingStatus.NotDocumented, response.GroundingStatus);
        Assert.Empty(response.Citations);

        var events = await StreamAsync(service, message);
        Assert.Equal(AssistantGroundingStatus.NotDocumented, events[0].GroundingStatus);
        Assert.DoesNotContain(events, e => e.Citation is not null);
        Assert.True(events[^1].Done);
    }

    [Fact]
    public async Task A_pending_project_slug_filter_stays_undocumented_even_with_profile_words()
    {
        await using var host = CreateHost();
        var service = host.Services.GetRequiredService<IAssistantService>();

        var response = service.Chat(new AssistantChatRequest("Tell me about Vextis AI engineering", "vextis"));

        Assert.Equal(AssistantGroundingStatus.NotDocumented, response.GroundingStatus);
        Assert.Empty(response.Citations);
    }

    [Fact]
    public async Task General_profile_questions_without_a_named_project_keep_their_verified_answer()
    {
        await using var host = CreateHost();
        var service = host.Services.GetRequiredService<IAssistantService>();

        var response = service.Chat(new AssistantChatRequest("Tell me about AI engineering"));

        Assert.Equal(AssistantGroundingStatus.Grounded, response.GroundingStatus);
        Assert.All(response.Citations, AssertProfileCitation);
    }

    [Fact]
    public async Task The_running_host_serves_the_curated_profile_and_the_pending_answer_over_chat_and_sse()
    {
        await using var host = CreateHost();
        using var client = host.CreateClient();

        using var chat = await client.PostAsJsonAsync("/v1/assistant/chat", new { message = "Tell me about the AI Engineer role at Expinn" });
        Assert.Equal(HttpStatusCode.OK, chat.StatusCode);
        var body = await chat.Content.ReadAsStringAsync();
        Assert.Contains("\"groundingStatus\":\"grounded\"", body);
        Assert.Contains("claim-profile-05", body);
        Assert.Contains("2026.10.1", body);
        Assert.DoesNotContain("claim-profile-02", body);
        Assert.DoesNotContain("Expinn Technology", body);

        using var pending = await client.PostAsJsonAsync("/v1/assistant/chat", new { message = "Tell me about Vextis AI engineering" });
        Assert.Equal(HttpStatusCode.OK, pending.StatusCode);
        var pendingBody = await pending.Content.ReadAsStringAsync();
        Assert.Contains("\"groundingStatus\":\"not_documented\"", pendingBody);
        Assert.Contains("\"citations\":[]", pendingBody);

        using var stream = await client.PostAsJsonAsync("/v1/assistant/chat/stream", new { message = "Tell me about Vextis AI engineering" });
        Assert.Equal(HttpStatusCode.OK, stream.StatusCode);
        Assert.StartsWith("text/event-stream", stream.Content.Headers.ContentType?.ToString());
        var sse = await stream.Content.ReadAsStringAsync();
        Assert.Contains("event: status", sse);
        Assert.Contains("\"groundingStatus\":\"not_documented\"", sse);
        Assert.DoesNotContain("event: citation", sse);
        Assert.Contains("event: done", sse);
    }
}
