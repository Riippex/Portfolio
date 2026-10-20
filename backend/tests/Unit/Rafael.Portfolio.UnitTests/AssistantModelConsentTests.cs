using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Rafael.Portfolio.Modules.Assistant.Application;
using Rafael.Portfolio.Modules.Assistant.Domain;
using Rafael.Portfolio.Modules.Assistant.Infrastructure;
using Rafael.Portfolio.UnitTests.Orchestration;

namespace Rafael.Portfolio.UnitTests;

public sealed class AssistantModelConsentTests
{
    private const string Granted = """{"accepted":true,"noticeVersion":"rai-google-global-standard-v1"}""";

    private sealed class Evidence : IAssistantEvidenceAdapter
    {
        public IReadOnlyList<AssistantEvidenceChunk> SearchEvidence(string query, int limit = 5, string? slugFilter = null) =>
            new OrchestrationHarness().Chunks;
    }

    private sealed class Token : IVertexAccessToken
    {
        internal int Calls;
        public ValueTask<string> GetAsync(CancellationToken ct) { Calls++; return ValueTask.FromResult("synthetic-token"); }
    }

    private sealed class Transport : HttpMessageHandler
    {
        internal int Counts, Generations;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var counting = request.RequestUri!.AbsolutePath.EndsWith(":countTokens", StringComparison.Ordinal);
            if (counting) Counts++; else Generations++;
            var json = counting
                ? """{"totalTokens":500,"promptTokensDetails":[{"modality":"TEXT","tokenCount":500}]}"""
                : """{"modelVersion":"gemini-3.1-flash-lite","usageMetadata":{"promptTokenCount":100,"candidatesTokenCount":10,"totalTokenCount":110},"candidates":[{"finishReason":"STOP","content":{"parts":[{"text":"{\"v\":1,\"outcome\":\"answer\",\"claims\":[\"C1\"]}"}]}}]}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal Token Token { get; } = new();
        internal Transport Transport { get; } = new();
        internal RecordingLedger Ledger { get; }
        internal WebApplicationFactory<Program> Host { get; }
        private readonly HttpClient _providerHttp;

        internal Fixture()
        {
            var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero));
            var policy = ModelControlPolicy.Approved("approved-2026-10", new ModelTariff("synthetic", VertexModelOptions.ModelId,
                250_000, 1_500_000, new DateOnly(2026, 11, 1)));
            Ledger = new(new InMemoryModelControlLedger(policy, clock));
            _providerHttp = new HttpClient(Transport);
            var provider = new VertexGeminiClient(new(true, "synthetic-portfolio", "global", true, true, true),
                policy, new(_providerHttp, Token), clock);
            Host = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                builder.UseSetting("Portfolio:Stage", "local");
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IModelProvider>();
                    services.RemoveAll<IModelRequestTokenCounter>();
                    services.RemoveAll<IModelControlLedger>();
                    services.RemoveAll<IAssistantEvidenceAdapter>();
                    services.RemoveAll<TimeProvider>();
                    services.AddSingleton<TimeProvider>(clock);
                    services.AddSingleton<IModelProvider>(provider);
                    services.AddSingleton<IModelRequestTokenCounter>(provider);
                    services.AddSingleton<IModelControlLedger>(Ledger);
                    services.AddSingleton<IAssistantEvidenceAdapter>(new Evidence());
                });
            });
        }

        public async ValueTask DisposeAsync() { await Host.DisposeAsync(); _providerHttp.Dispose(); }
    }

    public static IEnumerable<object?[]> DeniedChoices()
    {
        foreach (var value in new string?[]
        {
            null, "null", "false", "true", "1", "\"yes\"", "[]", "{}",
            Granted.Replace("true", "false"), Granted.Replace("true", "\"true\""),
            Granted.Replace("global-standard-v1", "global-standard-v0"),
            Granted.Replace("rai-google", "RAI-GOOGLE"),
            Granted.Replace("rai-google-global-standard-v1", new string('x', 65)),
            """{"accepted":true}""", """{"noticeVersion":"rai-google-global-standard-v1"}""",
            """{"accepted":true,"noticeVersion":null}""",
            Granted.Replace("\"accepted\":true", "\"accepted\":false,\"accepted\":true"),
            Granted.Replace("}", ",\"unexpected\":true}"),
            Granted.Replace("accepted", "Accepted")
        }) yield return [value];
    }

    [Theory]
    [MemberData(nameof(DeniedChoices))]
    public async Task Direct_HTTP_and_SSE_without_exact_affirmative_choice_never_touch_credentials_counting_inference_or_ledger(string? consent)
    {
        await using var f = new Fixture();
        using var http = f.Host.CreateClient();
        http.DefaultRequestHeaders.Add("X-Model-Consent", "true");
        http.DefaultRequestHeaders.Add("X-Model-Notice-Version", AssistantModelConsent.CurrentNoticeVersion);
        foreach (var route in new[] { "/v1/assistant/chat", "/v1/assistant/chat/stream" })
        {
            var body = "{\"message\":\"Tell me about Expinn\",\"consent\":true" + (consent is null ? "" : ",\"modelConsent\":" + consent) + "}";
            using var response = await http.PostAsync(route, new StringContent(body, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("Unselected", await response.Content.ReadAsStringAsync());
        }
        Assert.Equal(0, f.Token.Calls);
        Assert.Equal(0, f.Transport.Counts);
        Assert.Equal(0, f.Transport.Generations);
        Assert.Empty(f.Ledger.Calls);
    }

    [Fact]
    public async Task Current_affirmative_HTTP_choice_reaches_the_real_adapter_in_both_routes_and_withdrawal_stops_future_calls()
    {
        await using var f = new Fixture();
        using var http = f.Host.CreateClient();
        foreach (var route in new[] { "/v1/assistant/chat", "/v1/assistant/chat/stream" })
        {
            using var response = await http.PostAsync(route, new StringContent(
                "{\"message\":\"Tell me about Expinn\",\"modelConsent\":" + Granted + "}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("Expinn", await response.Content.ReadAsStringAsync());
        }
        Assert.Equal(4, f.Token.Calls);
        Assert.Equal(2, f.Transport.Counts);
        Assert.Equal(2, f.Transport.Generations);
        Assert.Equal(2, f.Ledger.Usage.Count);
        f.Ledger.Calls.Clear();
        foreach (var route in new[] { "/v1/assistant/chat", "/v1/assistant/chat/stream" })
        {
            using var response = await http.PostAsync(route, new StringContent(
                "{\"message\":\"Tell me about Expinn\",\"modelConsent\":" + Granted.Replace("true", "false") + "}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("Unselected", await response.Content.ReadAsStringAsync());
        }
        Assert.Equal(4, f.Token.Calls);
        Assert.Equal(2, f.Transport.Counts);
        Assert.Equal(2, f.Transport.Generations);
        Assert.Empty(f.Ledger.Calls);
    }

    [Theory]
    [InlineData(false, "rai-google-global-standard-v1")]
    [InlineData(true, "old-notice")]
    [InlineData(true, null)]
    public async Task Application_callers_cannot_bypass_the_gate_without_HTTP(bool accepted, string? version)
    {
        var h = new OrchestrationHarness();
        var service = new AssistantService(new Evidence(), new DeterministicGroundedSynthesizer(), new AssistantSafetyEvaluator(), h.Orchestrator, h.Clock);
        var request = new AssistantChatRequest("Tell me about Expinn", ModelConsent: new(accepted, version));
        Assert.NotEmpty((await service.ChatAsync(request)).Answer);
        await foreach (var item in service.StreamChatAsync(request)) { }
        Assert.Empty(h.Ledger.Calls);
        Assert.Equal(0, h.Provider.Calls);
        Assert.Equal(0, h.Counter.Calls);
    }

    [Fact]
    public void Notice_version_matches_the_frontend_and_versioned_contract()
    {
        var root = TestRepositoryRoot.Find();
        var version = AssistantModelConsent.CurrentNoticeVersion;
        Assert.Contains($"MODEL_PROCESSING_NOTICE_VERSION = \"{version}\"", File.ReadAllText(Path.Combine(root, "frontend/src/modules/assistant/model.ts")));
        Assert.Contains($"`{version}`", File.ReadAllText(Path.Combine(root, "docs/contracts/assistant-chat.md")));
    }

    [Fact]
    public void Default_DTO_serialization_has_only_the_two_contract_fields_and_no_computed_authority()
    {
        var choice = new AssistantModelConsent(true, AssistantModelConsent.CurrentNoticeVersion);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(choice, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal(2, json.RootElement.EnumerateObject().Count());
        Assert.True(json.RootElement.GetProperty("accepted").GetBoolean());
        Assert.Equal(AssistantModelConsent.CurrentNoticeVersion, json.RootElement.GetProperty("noticeVersion").GetString());
    }
}
