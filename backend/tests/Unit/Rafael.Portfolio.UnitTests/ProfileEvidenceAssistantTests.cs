using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Rafael.Portfolio.Modules.Assistant.Application;
using Rafael.Portfolio.Modules.Assistant.Application.Orchestration;
using Rafael.Portfolio.Modules.Assistant.Domain;
using Rafael.Portfolio.Modules.Assistant.Infrastructure;

namespace Rafael.Portfolio.UnitTests;

/// <summary>
/// Chat and SSE answers from the real host composition and the real evidence manifest (the files
/// under docs/evidence), with the model disabled. Nothing here stubs retrieval or the adapter.
/// </summary>
public sealed class ProfileEvidenceAssistantTests
{
    private const string ProfileSource = "https://co.linkedin.com/in/rafael-pati%C3%B1o-diaz";
    private const string GitHubProfileUrl = "https://github.com/Riippex";
    private const string ProfileVersion = "2026.10.3";

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
    [InlineData("What is Rafael's LinkedIn?", "Links", "claim-profile-08", ProfileSource)]
    [InlineData("Where can I find Rafael's GitHub?", "Links", "claim-profile-09", GitHubProfileUrl)]
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
        Assert.All(claims, claim => Assert.Contains(claim, new[] { "claim-profile-04", "claim-profile-05", "claim-profile-06", "claim-profile-07", "claim-profile-08", "claim-profile-09" }));
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

    [Theory]
    [InlineData("Tell me about Vextis AI engineering")]
    [InlineData("Tell me about Kinetiq V AI engineering")]
    [InlineData("Explain the JobTY AI engineering vacancy matching")]
    public async Task The_profile_filter_does_not_hide_a_named_pending_project(string message)
    {
        await using var host = CreateHost();
        var service = host.Services.GetRequiredService<IAssistantService>();

        var response = service.Chat(new AssistantChatRequest(message, "profile"));
        Assert.Equal(AssistantGroundingStatus.NotDocumented, response.GroundingStatus);
        Assert.Empty(response.Citations);

        var events = await StreamAsync(service, message, "profile");
        Assert.Equal(AssistantGroundingStatus.NotDocumented, events[0].GroundingStatus);
        Assert.DoesNotContain(events, e => e.Citation is not null);
        Assert.Contains(events, e => e.Type == "token");
        Assert.True(events[^1].Done);
    }

    [Theory]
    [InlineData("Tell me about Vextis AI engineering")]
    [InlineData("Tell me about Kinetiq V AI engineering")]
    [InlineData("Explain the JobTY AI engineering vacancy matching")]
    public async Task The_profile_filter_does_not_hide_a_named_pending_project_over_http_chat_and_sse(string message)
    {
        await using var host = CreateHost();
        using var client = host.CreateClient();

        using var chat = await client.PostAsJsonAsync("/v1/assistant/chat", new { message, slug = "profile" });
        Assert.Equal(HttpStatusCode.OK, chat.StatusCode);
        var chatBody = await chat.Content.ReadAsStringAsync();
        Assert.Contains("\"groundingStatus\":\"not_documented\"", chatBody);
        Assert.Contains("\"citations\":[]", chatBody);

        using var stream = await client.PostAsJsonAsync("/v1/assistant/chat/stream", new { message, slug = "profile" });
        Assert.Equal(HttpStatusCode.OK, stream.StatusCode);
        var sse = await stream.Content.ReadAsStringAsync();
        Assert.Contains("\"groundingStatus\":\"not_documented\"", sse);
        Assert.DoesNotContain("event: citation", sse);
        Assert.Contains("event: token", sse);
        Assert.Contains("event: done", sse);
    }

    [Theory]
    [InlineData("Does Rafael have studies at Universidad Manuela Beltran?", "claim-profile-04")]
    [InlineData("Tell me about the AI Engineer role at Expinn", "claim-profile-05")]
    public async Task The_profile_filter_still_answers_supported_questions_over_service_http_chat_and_sse(string message, string claimId)
    {
        await using var host = CreateHost();
        var service = host.Services.GetRequiredService<IAssistantService>();

        var response = service.Chat(new AssistantChatRequest(message, "profile"));
        Assert.Equal(AssistantGroundingStatus.Grounded, response.GroundingStatus);
        Assert.All(response.Citations, AssertProfileCitation);
        Assert.Contains(response.Citations, c => c.Claims.Contains(claimId));

        var events = await StreamAsync(service, message, "profile");
        Assert.Equal(AssistantGroundingStatus.Grounded, events[0].GroundingStatus);
        Assert.Contains(events.Where(e => e.Citation is not null), e => e.Citation!.Claims.Contains(claimId));

        using var client = host.CreateClient();
        using var chat = await client.PostAsJsonAsync("/v1/assistant/chat", new { message, slug = "profile" });
        Assert.Equal(HttpStatusCode.OK, chat.StatusCode);
        var chatBody = await chat.Content.ReadAsStringAsync();
        Assert.Contains("\"groundingStatus\":\"grounded\"", chatBody);
        Assert.Contains(claimId, chatBody);

        using var stream = await client.PostAsJsonAsync("/v1/assistant/chat/stream", new { message, slug = "profile" });
        Assert.Equal(HttpStatusCode.OK, stream.StatusCode);
        var sse = await stream.Content.ReadAsStringAsync();
        Assert.Contains("event: citation", sse);
        Assert.Contains(claimId, sse);
        Assert.Contains("event: done", sse);
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
        Assert.Contains("2026.10.3", body);
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

    [Fact]
    public async Task Deterministic_assistant_answers_github_and_linkedin_questions_without_model_consent_or_live_provider()
    {
        await using var host = CreateHost();
        Assert.IsType<DisabledModelProvider>(host.Services.GetRequiredService<IModelProvider>());
        Assert.IsType<UnverifiedModelRequestTokenCounter>(host.Services.GetRequiredService<IModelRequestTokenCounter>());

        var service = host.Services.GetRequiredService<IAssistantService>();

        var githubResponse = await service.ChatAsync(new AssistantChatRequest("Where can I find Rafael's GitHub?"));
        Assert.Equal(AssistantGroundingStatus.Grounded, githubResponse.GroundingStatus);
        Assert.Contains(GitHubProfileUrl, githubResponse.Answer);
        Assert.DoesNotContain(ProfileSource, githubResponse.Answer);
        var githubCitation = Assert.Single(githubResponse.Citations);
        AssertProfileCitation(githubCitation);
        Assert.Equal("Links", githubCitation.SectionHeading);
        Assert.Equal(["claim-profile-09"], githubCitation.Claims);

        var linkedinResponse = await service.ChatAsync(new AssistantChatRequest("What is Rafael's LinkedIn?"));
        Assert.Equal(AssistantGroundingStatus.Grounded, linkedinResponse.GroundingStatus);
        Assert.Contains(ProfileSource, linkedinResponse.Answer);
        Assert.DoesNotContain(GitHubProfileUrl, linkedinResponse.Answer);
        var linkedinCitation = Assert.Single(linkedinResponse.Citations);
        AssertProfileCitation(linkedinCitation);
        Assert.Equal("Links", linkedinCitation.SectionHeading);
        Assert.Equal(["claim-profile-08"], linkedinCitation.Claims);

        using var client = host.CreateClient();
        using var githubHttp = await client.PostAsJsonAsync(
            "/v1/assistant/chat",
            new { message = "Where can I find Rafael's GitHub?" });
        Assert.Equal(HttpStatusCode.OK, githubHttp.StatusCode);
        var githubBody = await githubHttp.Content.ReadAsStringAsync();
        Assert.Contains("\"groundingStatus\":\"grounded\"", githubBody);
        Assert.Contains(GitHubProfileUrl, githubBody);
        Assert.Contains("claim-profile-09", githubBody);

        using var linkedinHttp = await client.PostAsJsonAsync(
            "/v1/assistant/chat",
            new { message = "What is Rafael's LinkedIn?" });
        Assert.Equal(HttpStatusCode.OK, linkedinHttp.StatusCode);
        var linkedinBody = await linkedinHttp.Content.ReadAsStringAsync();
        Assert.Contains("\"groundingStatus\":\"grounded\"", linkedinBody);
        Assert.Contains(ProfileSource, linkedinBody);
        Assert.Contains("claim-profile-08", linkedinBody);
    }

    [Fact]
    public async Task Removing_a_link_from_canonical_evidence_removes_it_from_api_and_assistant_results()
    {
        var tempEvidenceDir = Path.Combine(Path.GetTempPath(), $"profile-links-removal-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(tempEvidenceDir, "projects"));

        try
        {
            var repoEvidenceDir = Path.Combine(TestRepositoryRoot.Find(), "docs", "evidence");
            foreach (var file in Directory.GetFiles(repoEvidenceDir))
            {
                File.Copy(file, Path.Combine(tempEvidenceDir, Path.GetFileName(file)));
            }

            foreach (var file in Directory.GetFiles(Path.Combine(repoEvidenceDir, "projects")))
            {
                File.Copy(file, Path.Combine(tempEvidenceDir, "projects", Path.GetFileName(file)));
            }

            var manifestPath = Path.Combine(tempEvidenceDir, "inventory.json");
            var manifestNode = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(manifestPath))!;
            var profileNode = manifestNode["items"]!.AsArray()
                .Single(item => item?["slug"]?.GetValue<string>() == "profile")!;
            var linksArray = profileNode["links"]!.AsArray();
            for (var i = linksArray.Count - 1; i >= 0; i--)
            {
                if (linksArray[i]?["platform"]?.GetValue<string>() == "github")
                {
                    linksArray.RemoveAt(i);
                }
            }

            File.WriteAllText(manifestPath, manifestNode.ToJsonString());

            await using var host = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                builder.UseSetting("Portfolio:Stage", "local");
                builder.UseSetting("EvidenceInventory:ManifestPath", manifestPath);
            });

            using var client = host.CreateClient();

            var profileJson = await client.GetStringAsync("/v1/profile");
            Assert.Contains(ProfileSource, profileJson);
            Assert.DoesNotContain(GitHubProfileUrl, profileJson);

            var evidenceJson = await client.GetStringAsync("/v1/evidence/profile");
            Assert.Contains("claim-profile-08", evidenceJson);
            Assert.DoesNotContain("claim-profile-09", evidenceJson);
            Assert.DoesNotContain(GitHubProfileUrl, evidenceJson);

            using var githubChat = await client.PostAsJsonAsync(
                "/v1/assistant/chat",
                new { message = "Where can I find Rafael's GitHub?" });
            Assert.Equal(HttpStatusCode.OK, githubChat.StatusCode);
            var githubChatBody = await githubChat.Content.ReadAsStringAsync();
            Assert.Contains("\"groundingStatus\":\"not_documented\"", githubChatBody);
            Assert.Contains("\"citations\":[]", githubChatBody);
            Assert.DoesNotContain(GitHubProfileUrl, githubChatBody);

            using var githubStream = await client.PostAsJsonAsync(
                "/v1/assistant/chat/stream",
                new { message = "Where can I find Rafael's GitHub?" });
            Assert.Equal(HttpStatusCode.OK, githubStream.StatusCode);
            var githubSse = await githubStream.Content.ReadAsStringAsync();
            Assert.Contains("\"groundingStatus\":\"not_documented\"", githubSse);
            Assert.DoesNotContain("event: citation", githubSse);

            using var linkedinChat = await client.PostAsJsonAsync(
                "/v1/assistant/chat",
                new { message = "What is Rafael's LinkedIn?" });
            Assert.Equal(HttpStatusCode.OK, linkedinChat.StatusCode);
            var linkedinChatBody = await linkedinChat.Content.ReadAsStringAsync();
            Assert.Contains("\"groundingStatus\":\"grounded\"", linkedinChatBody);
            Assert.Contains("claim-profile-08", linkedinChatBody);
            Assert.Contains(ProfileSource, linkedinChatBody);
        }
        finally
        {
            Directory.Delete(tempEvidenceDir, recursive: true);
        }
    }

    [Theory]
    [InlineData(
        "linkedin",
        ProfileSource,
        "claim-profile-08",
        "What is Rafael's LinkedIn?",
        "Where can I find Rafael's GitHub?",
        GitHubProfileUrl,
        "claim-profile-09")]
    [InlineData(
        "github",
        GitHubProfileUrl,
        "claim-profile-09",
        "Where can I find Rafael's GitHub?",
        "What is Rafael's LinkedIn?",
        ProfileSource,
        "claim-profile-08")]
    public async Task Mixed_single_line_links_markdown_withdraws_removed_platform_across_api_chat_chatasync_and_sse(
        string retainedPlatform,
        string retainedUrl,
        string retainedClaimId,
        string retainedQuestion,
        string removedQuestion,
        string removedUrl,
        string removedClaimId)
    {
        var tempEvidenceDir = Path.Combine(Path.GetTempPath(), $"profile-mixed-links-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(tempEvidenceDir, "projects"));

        try
        {
            var repoEvidenceDir = Path.Combine(TestRepositoryRoot.Find(), "docs", "evidence");
            File.Copy(
                Path.Combine(repoEvidenceDir, "schema.json"),
                Path.Combine(tempEvidenceDir, "schema.json"));
            File.Copy(
                Path.Combine(repoEvidenceDir, "projects", "vextis.md"),
                Path.Combine(tempEvidenceDir, "projects", "vextis.md"));

            File.WriteAllText(
                Path.Combine(tempEvidenceDir, "profile.md"),
                """
                # Public professional links diagnostic fixture

                ## Links

                LinkedIn: https://co.linkedin.com/in/rafael-pati%C3%B1o-diaz. GitHub: https://github.com/Riippex.
                """);

            var manifestPath = Path.Combine(tempEvidenceDir, "inventory.json");
            File.WriteAllText(
                manifestPath,
                $$"""
                {
                  "version": "2026.10.2",
                  "lastUpdated": "2026-10-27",
                  "items": [
                    {
                      "id": "evidence-profile",
                      "slug": "profile",
                      "kind": "profile",
                      "title": "Rafael Patiño",
                      "summary": "Public professional links.",
                      "headline": "Software & AI Engineer",
                      "focusAreas": ["Software engineering"],
                      "version": "2026.10.2",
                      "evidenceStatus": "verified",
                      "sourceUrl": "https://co.linkedin.com/in/rafael-pati%C3%B1o-diaz",
                      "documentPath": "docs/evidence/profile.md",
                      "lastReviewed": "2026-10-27",
                      "links": [
                        { "platform": "{{retainedPlatform}}", "url": "{{retainedUrl}}" }
                      ],
                      "claims": [
                        {
                          "claimId": "claim-profile-08",
                          "statement": "The public LinkedIn profile is https://co.linkedin.com/in/rafael-pati%C3%B1o-diaz.",
                          "status": "verified",
                          "citation": "docs/evidence/profile.md#links"
                        },
                        {
                          "claimId": "claim-profile-09",
                          "statement": "The public GitHub profile is https://github.com/Riippex.",
                          "status": "verified",
                          "citation": "docs/evidence/profile.md#links"
                        }
                      ]
                    },
                    {
                      "id": "evidence-project-vextis",
                      "slug": "vextis",
                      "kind": "project",
                      "title": "Vextis",
                      "summary": "Project evidence remains pending.",
                      "version": "2026.09",
                      "evidenceStatus": "pending",
                      "sourceUrl": null,
                      "documentPath": "docs/evidence/projects/vextis.md",
                      "lastReviewed": "2026-09-28",
                      "claims": []
                    }
                  ]
                }
                """);

            await using var host = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                builder.UseSetting("Portfolio:Stage", "local");
                builder.UseSetting("EvidenceInventory:ManifestPath", manifestPath);
            });

            var service = host.Services.GetRequiredService<IAssistantService>();
            using var client = host.CreateClient();

            var profileJson = await client.GetStringAsync("/v1/profile");
            Assert.Contains(retainedUrl, profileJson);
            Assert.DoesNotContain(removedUrl, profileJson);

            var evidenceJson = await client.GetStringAsync("/v1/evidence/profile");
            using (var evidenceDoc = JsonDocument.Parse(evidenceJson))
            {
                var itemNode = evidenceDoc.RootElement.GetProperty("item");
                Assert.Equal(ProfileSource, itemNode.GetProperty("sourceUrl").GetString());

                var linkUrls = itemNode.GetProperty("links").EnumerateArray()
                    .Select(l => l.GetProperty("url").GetString())
                    .ToList();
                Assert.Equal([retainedUrl], linkUrls);

                var claimIds = itemNode.GetProperty("claims").EnumerateArray()
                    .Select(c => c.GetProperty("claimId").GetString())
                    .ToList();
                Assert.Equal([retainedClaimId], claimIds);

                var rawMarkdown = evidenceDoc.RootElement.GetProperty("rawMarkdown").GetString()!;
                Assert.Contains(retainedUrl, rawMarkdown);
                Assert.DoesNotContain(removedUrl, rawMarkdown);

                var linksSectionNode = Assert.Single(
                    evidenceDoc.RootElement.GetProperty("sections").EnumerateArray(),
                    s => s.GetProperty("slug").GetString() == "links");
                var linksSectionContent = linksSectionNode.GetProperty("content").GetString()!;
                Assert.Contains(retainedUrl, linksSectionContent);
                Assert.DoesNotContain(removedUrl, linksSectionContent);
            }

            // Sync Chat & Async ChatAsync (without modelConsent) for removed platform
            var removedSync = service.Chat(new AssistantChatRequest(removedQuestion));
            Assert.Equal(AssistantGroundingStatus.NotDocumented, removedSync.GroundingStatus);
            Assert.Empty(removedSync.Citations);
            Assert.DoesNotContain(removedUrl, removedSync.Answer);

            var removedAsync = await service.ChatAsync(new AssistantChatRequest(removedQuestion));
            Assert.Equal(AssistantGroundingStatus.NotDocumented, removedAsync.GroundingStatus);
            Assert.Empty(removedAsync.Citations);
            Assert.DoesNotContain(removedUrl, removedAsync.Answer);

            using var removedHttp = await client.PostAsJsonAsync("/v1/assistant/chat", new { message = removedQuestion });
            Assert.Equal(HttpStatusCode.OK, removedHttp.StatusCode);
            var removedHttpBody = await removedHttp.Content.ReadAsStringAsync();
            Assert.Contains("\"groundingStatus\":\"not_documented\"", removedHttpBody);
            Assert.Contains("\"citations\":[]", removedHttpBody);
            Assert.DoesNotContain(removedUrl, removedHttpBody);
            Assert.DoesNotContain(retainedClaimId, removedHttpBody);

            using var removedStream = await client.PostAsJsonAsync("/v1/assistant/chat/stream", new { message = removedQuestion });
            Assert.Equal(HttpStatusCode.OK, removedStream.StatusCode);
            var removedSse = await removedStream.Content.ReadAsStringAsync();
            Assert.Contains("\"groundingStatus\":\"not_documented\"", removedSse);
            Assert.DoesNotContain("event: citation", removedSse);
            Assert.DoesNotContain(removedUrl, removedSse);
            Assert.DoesNotContain(retainedClaimId, removedSse);

            // Retained platform still works across Chat, ChatAsync, HTTP chat and SSE
            var retainedSync = service.Chat(new AssistantChatRequest(retainedQuestion));
            Assert.Equal(AssistantGroundingStatus.Grounded, retainedSync.GroundingStatus);
            Assert.Contains(retainedUrl, retainedSync.Answer);
            Assert.DoesNotContain(removedUrl, retainedSync.Answer);
            var retainedSyncCitation = Assert.Single(retainedSync.Citations);
            Assert.Equal(ProfileSource, retainedSyncCitation.SourceUrl);
            Assert.Equal([retainedClaimId], retainedSyncCitation.Claims);

            var retainedAsync = await service.ChatAsync(new AssistantChatRequest(retainedQuestion));
            Assert.Equal(AssistantGroundingStatus.Grounded, retainedAsync.GroundingStatus);
            Assert.Contains(retainedUrl, retainedAsync.Answer);
            Assert.DoesNotContain(removedUrl, retainedAsync.Answer);
            var retainedCitation = Assert.Single(retainedAsync.Citations);
            Assert.Equal(ProfileSource, retainedCitation.SourceUrl);
            Assert.Equal([retainedClaimId], retainedCitation.Claims);

            using var retainedHttp = await client.PostAsJsonAsync("/v1/assistant/chat", new { message = retainedQuestion });
            Assert.Equal(HttpStatusCode.OK, retainedHttp.StatusCode);
            var retainedHttpBody = await retainedHttp.Content.ReadAsStringAsync();
            using (var retainedDoc = JsonDocument.Parse(retainedHttpBody))
            {
                Assert.Equal("grounded", retainedDoc.RootElement.GetProperty("groundingStatus").GetString());
                var answer = retainedDoc.RootElement.GetProperty("answer").GetString()!;
                Assert.Contains(retainedUrl, answer);
                Assert.DoesNotContain(removedUrl, answer);

                var httpCitation = Assert.Single(retainedDoc.RootElement.GetProperty("citations").EnumerateArray());
                Assert.Equal(ProfileSource, httpCitation.GetProperty("sourceUrl").GetString());
                var httpClaims = httpCitation.GetProperty("claims").EnumerateArray()
                    .Select(c => c.GetString())
                    .ToList();
                Assert.Equal([retainedClaimId], httpClaims);
            }

            using var retainedStream = await client.PostAsJsonAsync("/v1/assistant/chat/stream", new { message = retainedQuestion });
            Assert.Equal(HttpStatusCode.OK, retainedStream.StatusCode);
            var retainedSse = await retainedStream.Content.ReadAsStringAsync();
            Assert.Contains("\"groundingStatus\":\"grounded\"", retainedSse);
            Assert.Contains(retainedClaimId, retainedSse);
            Assert.DoesNotContain(removedClaimId, retainedSse);
            Assert.Contains($"\"sourceUrl\":\"{ProfileSource}\"", retainedSse);
        }
        finally
        {
            Directory.Delete(tempEvidenceDir, recursive: true);
        }
    }

    [Theory]
    [InlineData(
        true,
        "What does Rafael's LinkedIn profile say about his studies at Universidad Manuela Beltran?",
        "Studies",
        "claim-profile-04",
        "in progress")]
    [InlineData(
        true,
        "Tell me about the AI Engineer role at Expinn listed on LinkedIn",
        "Experience",
        "claim-profile-05",
        "announces an AI Engineer role at Expinn")]
    [InlineData(
        false,
        "Does Rafael have studies at Universidad Manuela Beltran?",
        "Studies",
        "claim-profile-04",
        "in progress")]
    [InlineData(
        false,
        "Tell me about the AI Engineer role at Expinn",
        "Experience",
        "claim-profile-05",
        "announces an AI Engineer role at Expinn")]
    public async Task Withdrawing_linkedin_display_link_or_all_display_links_preserves_canonical_provenance_for_studies_and_experience_across_api_chat_chatasync_http_and_sse(
        bool retainGitHubDisplayLink,
        string httpProbeQuestion,
        string httpExpectedSection,
        string httpExpectedClaim,
        string httpExpectedPhrase)
    {
        var tempEvidenceDir = Path.Combine(Path.GetTempPath(), $"profile-provenance-links-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(tempEvidenceDir, "projects"));

        try
        {
            var repoEvidenceDir = Path.Combine(TestRepositoryRoot.Find(), "docs", "evidence");
            foreach (var file in Directory.GetFiles(repoEvidenceDir))
            {
                File.Copy(file, Path.Combine(tempEvidenceDir, Path.GetFileName(file)));
            }

            foreach (var file in Directory.GetFiles(Path.Combine(repoEvidenceDir, "projects")))
            {
                File.Copy(file, Path.Combine(tempEvidenceDir, "projects", Path.GetFileName(file)));
            }

            var manifestPath = Path.Combine(tempEvidenceDir, "inventory.json");
            var manifestNode = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(manifestPath))!;
            var profileNode = manifestNode["items"]!.AsArray()
                .Single(item => item?["slug"]?.GetValue<string>() == "profile")!;
            var linksArray = profileNode["links"]!.AsArray();
            for (var i = linksArray.Count - 1; i >= 0; i--)
            {
                var platform = linksArray[i]?["platform"]?.GetValue<string>();
                if (platform == "linkedin" || (!retainGitHubDisplayLink && platform == "github"))
                {
                    linksArray.RemoveAt(i);
                }
            }

            File.WriteAllText(manifestPath, manifestNode.ToJsonString());

            await using var host = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                builder.UseSetting("Portfolio:Stage", "local");
                builder.UseSetting("EvidenceInventory:ManifestPath", manifestPath);
            });

            var service = host.Services.GetRequiredService<IAssistantService>();
            using var client = host.CreateClient();

            // 1. Public profile API omits withdrawn LinkedIn display link while keeping GitHub iff configured.
            var profileJson = await client.GetStringAsync("/v1/profile");
            Assert.DoesNotContain(ProfileSource, profileJson);
            if (retainGitHubDisplayLink)
            {
                Assert.Contains(GitHubProfileUrl, profileJson);
            }
            else
            {
                Assert.DoesNotContain(GitHubProfileUrl, profileJson);
            }

            // 2. Evidence endpoint keeps LinkedIn as item.sourceUrl and Canonical Source in rawMarkdown,
            //    while scoping or removing #links without retargeting provenance to GitHub.
            var evidenceJson = await client.GetStringAsync("/v1/evidence/profile");
            using (var evidenceDoc = JsonDocument.Parse(evidenceJson))
            {
                var itemNode = evidenceDoc.RootElement.GetProperty("item");
                Assert.Equal(ProfileSource, itemNode.GetProperty("sourceUrl").GetString());

                var rawMarkdown = evidenceDoc.RootElement.GetProperty("rawMarkdown").GetString()!;
                Assert.Contains($"- Canonical Source: {ProfileSource}", rawMarkdown);

                var claimIds = itemNode.GetProperty("claims").EnumerateArray()
                    .Select(c => c.GetProperty("claimId").GetString())
                    .ToList();
                Assert.Contains("claim-profile-04", claimIds);
                Assert.Contains("claim-profile-05", claimIds);
                Assert.Contains("claim-profile-06", claimIds);
                Assert.Contains("claim-profile-07", claimIds);
                Assert.DoesNotContain("claim-profile-08", claimIds);

                var sections = evidenceDoc.RootElement.GetProperty("sections").EnumerateArray().ToList();
                Assert.Contains(sections, s => s.GetProperty("slug").GetString() == "studies");
                Assert.Contains(sections, s => s.GetProperty("slug").GetString() == "experience");

                if (retainGitHubDisplayLink)
                {
                    Assert.Contains("claim-profile-09", claimIds);
                    var linksSection = Assert.Single(sections, s => s.GetProperty("slug").GetString() == "links");
                    var linksContent = linksSection.GetProperty("content").GetString()!;
                    Assert.Contains(GitHubProfileUrl, linksContent);
                    Assert.DoesNotContain(ProfileSource, linksContent);
                }
                else
                {
                    Assert.DoesNotContain("claim-profile-09", claimIds);
                    Assert.DoesNotContain(sections, s => s.GetProperty("slug").GetString() == "links");
                    Assert.DoesNotContain("## Links", rawMarkdown);
                }
            }

            // 3. Unchanged Studies and Experience answers (both plain and mentioning LinkedIn as source)
            //    retain LinkedIn provenance across Chat, ChatAsync, HTTP chat, and SSE — never GitHub.
            var substantiveQueries = new (string Question, string ExpectedSection, string ExpectedClaim, string ExpectedPhrase)[]
            {
                ("Does Rafael have studies at Universidad Manuela Beltran?", "Studies", "claim-profile-04", "in progress"),
                ("What does Rafael's LinkedIn profile say about his studies at Universidad Manuela Beltran?", "Studies", "claim-profile-04", "in progress"),
                ("Tell me about the AI Engineer role at Expinn", "Experience", "claim-profile-05", "announces an AI Engineer role at Expinn"),
                ("Tell me about the AI Engineer role at Expinn listed on LinkedIn", "Experience", "claim-profile-05", "announces an AI Engineer role at Expinn")
            };

            foreach (var (question, expectedSection, expectedClaim, expectedPhrase) in substantiveQueries)
            {
                var syncResult = service.Chat(new AssistantChatRequest(question));
                Assert.Equal(AssistantGroundingStatus.Grounded, syncResult.GroundingStatus);
                Assert.Contains(expectedPhrase, syncResult.Answer);
                Assert.DoesNotContain(GitHubProfileUrl, syncResult.Answer);
                Assert.Contains(syncResult.Citations, c => c.SectionHeading == expectedSection && c.Claims.Contains(expectedClaim));
                Assert.All(syncResult.Citations, c => Assert.Equal(ProfileSource, c.SourceUrl));
                Assert.DoesNotContain(syncResult.Citations, c => c.SectionHeading == "Links" || c.Claims.Contains("claim-profile-08") || c.Claims.Contains("claim-profile-09"));

                var asyncResult = await service.ChatAsync(new AssistantChatRequest(question));
                Assert.Equal(AssistantGroundingStatus.Grounded, asyncResult.GroundingStatus);
                Assert.Contains(expectedPhrase, asyncResult.Answer);
                Assert.DoesNotContain(GitHubProfileUrl, asyncResult.Answer);
                Assert.Contains(asyncResult.Citations, c => c.SectionHeading == expectedSection && c.Claims.Contains(expectedClaim));
                Assert.All(asyncResult.Citations, c => Assert.Equal(ProfileSource, c.SourceUrl));
                Assert.DoesNotContain(asyncResult.Citations, c => c.SectionHeading == "Links" || c.Claims.Contains("claim-profile-08") || c.Claims.Contains("claim-profile-09"));
            }

            // Probe HTTP /v1/assistant/chat and SSE /v1/assistant/chat/stream within the 5 req/min per-IP budget.
            using var httpResp = await client.PostAsJsonAsync("/v1/assistant/chat", new { message = httpProbeQuestion });
            Assert.Equal(HttpStatusCode.OK, httpResp.StatusCode);
            using (var httpDoc = JsonDocument.Parse(await httpResp.Content.ReadAsStringAsync()))
            {
                Assert.Equal("grounded", httpDoc.RootElement.GetProperty("groundingStatus").GetString());
                var answer = httpDoc.RootElement.GetProperty("answer").GetString()!;
                Assert.Contains(httpExpectedPhrase, answer);
                Assert.DoesNotContain(GitHubProfileUrl, answer);

                var citations = httpDoc.RootElement.GetProperty("citations").EnumerateArray().ToList();
                Assert.NotEmpty(citations);
                Assert.All(citations, c => Assert.Equal(ProfileSource, c.GetProperty("sourceUrl").GetString()));
                Assert.Contains(citations, c =>
                    c.GetProperty("sectionHeading").GetString() == httpExpectedSection &&
                    c.GetProperty("claims").EnumerateArray().Any(cl => cl.GetString() == httpExpectedClaim));
            }

            using var sseResp = await client.PostAsJsonAsync("/v1/assistant/chat/stream", new { message = httpProbeQuestion });
            Assert.Equal(HttpStatusCode.OK, sseResp.StatusCode);
            var sseBody = await sseResp.Content.ReadAsStringAsync();
            Assert.Contains("\"groundingStatus\":\"grounded\"", sseBody);
            Assert.Contains("event: citation", sseBody);
            Assert.Contains(httpExpectedClaim, sseBody);
            Assert.Contains($"\"sourceUrl\":\"{ProfileSource}\"", sseBody);
            Assert.DoesNotContain($"\"sourceUrl\":\"{GitHubProfileUrl}\"", sseBody);
            Assert.DoesNotContain("claim-profile-08", sseBody);
            Assert.DoesNotContain("claim-profile-09", sseBody);

            // 4. Direct LinkedIn display-link query answers not_documented across Chat, ChatAsync, HTTP, and SSE.
            const string withdrawnLinkedInQuery = "What is Rafael's LinkedIn?";
            var linkedinSync = service.Chat(new AssistantChatRequest(withdrawnLinkedInQuery));
            Assert.Equal(AssistantGroundingStatus.NotDocumented, linkedinSync.GroundingStatus);
            Assert.Empty(linkedinSync.Citations);
            Assert.DoesNotContain(ProfileSource, linkedinSync.Answer);

            var linkedinAsync = await service.ChatAsync(new AssistantChatRequest(withdrawnLinkedInQuery));
            Assert.Equal(AssistantGroundingStatus.NotDocumented, linkedinAsync.GroundingStatus);
            Assert.Empty(linkedinAsync.Citations);
            Assert.DoesNotContain(ProfileSource, linkedinAsync.Answer);

            using var linkedinHttp = await client.PostAsJsonAsync("/v1/assistant/chat", new { message = withdrawnLinkedInQuery });
            var linkedinHttpBody = await linkedinHttp.Content.ReadAsStringAsync();
            Assert.Contains("\"groundingStatus\":\"not_documented\"", linkedinHttpBody);
            Assert.Contains("\"citations\":[]", linkedinHttpBody);
            Assert.DoesNotContain(ProfileSource, linkedinHttpBody);
            Assert.DoesNotContain("claim-profile-09", linkedinHttpBody);

            using var linkedinStream = await client.PostAsJsonAsync("/v1/assistant/chat/stream", new { message = withdrawnLinkedInQuery });
            var linkedinSse = await linkedinStream.Content.ReadAsStringAsync();
            Assert.Contains("\"groundingStatus\":\"not_documented\"", linkedinSse);
            Assert.DoesNotContain("event: citation", linkedinSse);
            Assert.DoesNotContain(ProfileSource, linkedinSse);
            Assert.DoesNotContain("claim-profile-09", linkedinSse);

            // 5. Direct GitHub link query stays grounded iff GitHub remains an active display link.
            const string githubQuery = "Where can I find Rafael's GitHub?";
            var githubAsync = await service.ChatAsync(new AssistantChatRequest(githubQuery));
            if (retainGitHubDisplayLink)
            {
                Assert.Equal(AssistantGroundingStatus.Grounded, githubAsync.GroundingStatus);
                Assert.Contains(GitHubProfileUrl, githubAsync.Answer);
                Assert.DoesNotContain(ProfileSource, githubAsync.Answer);
                var ghCitation = Assert.Single(githubAsync.Citations);
                Assert.Equal(ProfileSource, ghCitation.SourceUrl);
                Assert.Equal(["claim-profile-09"], ghCitation.Claims);
            }
            else
            {
                Assert.Equal(AssistantGroundingStatus.NotDocumented, githubAsync.GroundingStatus);
                Assert.Empty(githubAsync.Citations);
                Assert.DoesNotContain(GitHubProfileUrl, githubAsync.Answer);
            }
        }
        finally
        {
            Directory.Delete(tempEvidenceDir, recursive: true);
        }
    }

    [Theory]
    [InlineData(
        "Tell me about the AI Engineer role at Expinn listed on LinkedIn",
        "Experience",
        "claim-profile-05",
        "announces an AI Engineer role at Expinn")]
    [InlineData(
        "What does Rafael's LinkedIn profile say about his studies at Universidad Manuela Beltran?",
        "Studies",
        "claim-profile-04",
        "in progress")]
    public async Task Questions_about_studies_or_experience_mentioning_linkedin_preserve_documented_section_answers(
        string message,
        string expectedSection,
        string expectedClaimId,
        string expectedText)
    {
        await using var host = CreateHost();
        var service = host.Services.GetRequiredService<IAssistantService>();

        var syncResponse = service.Chat(new AssistantChatRequest(message));
        Assert.Equal(AssistantGroundingStatus.Grounded, syncResponse.GroundingStatus);
        Assert.Contains(expectedText, syncResponse.Answer);
        Assert.Contains(syncResponse.Citations, c => c.SectionHeading == expectedSection && c.Claims.Contains(expectedClaimId));
        Assert.DoesNotContain(syncResponse.Citations, c => c.SectionHeading == "Links" || c.Claims.Contains("claim-profile-08"));

        var asyncResponse = await service.ChatAsync(new AssistantChatRequest(message));
        Assert.Equal(AssistantGroundingStatus.Grounded, asyncResponse.GroundingStatus);
        Assert.Contains(expectedText, asyncResponse.Answer);
        Assert.Contains(asyncResponse.Citations, c => c.SectionHeading == expectedSection && c.Claims.Contains(expectedClaimId));
        Assert.DoesNotContain(asyncResponse.Citations, c => c.SectionHeading == "Links" || c.Claims.Contains("claim-profile-08"));

        using var client = host.CreateClient();
        using var chat = await client.PostAsJsonAsync("/v1/assistant/chat", new { message });
        Assert.Equal(HttpStatusCode.OK, chat.StatusCode);
        var chatBody = await chat.Content.ReadAsStringAsync();
        Assert.Contains("\"groundingStatus\":\"grounded\"", chatBody);
        Assert.Contains(expectedText, chatBody);
        Assert.Contains(expectedClaimId, chatBody);
        Assert.DoesNotContain("claim-profile-08", chatBody);

        using var stream = await client.PostAsJsonAsync("/v1/assistant/chat/stream", new { message });
        Assert.Equal(HttpStatusCode.OK, stream.StatusCode);
        var sse = await stream.Content.ReadAsStringAsync();
        Assert.Contains("\"groundingStatus\":\"grounded\"", sse);
        Assert.Contains(expectedClaimId, sse);
        Assert.DoesNotContain("claim-profile-08", sse);
        Assert.Contains("event: done", sse);
    }

    [Fact]
    public async Task Mixed_question_asking_for_both_studies_and_explicit_github_link_returns_both_sections()
    {
        await using var host = CreateHost();
        var service = host.Services.GetRequiredService<IAssistantService>();

        var response = await service.ChatAsync(
            new AssistantChatRequest("What are Rafael's studies at Universidad Manuela Beltran and where is his GitHub link?"));

        Assert.Equal(AssistantGroundingStatus.Grounded, response.GroundingStatus);
        Assert.Contains("in progress", response.Answer);
        Assert.Contains(GitHubProfileUrl, response.Answer);
        Assert.Contains(response.Citations, c => c.SectionHeading == "Studies" && c.Claims.Contains("claim-profile-04"));
        Assert.Contains(response.Citations, c => c.SectionHeading == "Links" && c.Claims.SequenceEqual(["claim-profile-09"]));
    }
}
