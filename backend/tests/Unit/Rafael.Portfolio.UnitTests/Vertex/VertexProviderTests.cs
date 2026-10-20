using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using Rafael.Portfolio.Modules.Assistant.Application;
using Rafael.Portfolio.Modules.Assistant.Application.Orchestration;
using Rafael.Portfolio.Modules.Assistant.Infrastructure;

namespace Rafael.Portfolio.UnitTests.Vertex;

public sealed class VertexProviderTests
{
    private const string Id = "res_0123456789abcdef0123456789abcdef";
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
    private const string CountBody = """{"totalTokens":500,"promptTokensDetails":[{"modality":"TEXT","tokenCount":500}]}""";
    private const string Generated = """{"modelVersion":"gemini-3.1-flash-lite","usageMetadata":{"promptTokenCount":100,"candidatesTokenCount":10,"thoughtsTokenCount":5,"totalTokenCount":115},"candidates":[{"finishReason":"STOP","content":{"role":"model","parts":[{"thought":true,"text":"private reasoning"},{"text":"{\"v\":1,\"outcome\":\"answer\",\"claims\":[\"C1\"]}"}]}}]}""";
    private static ModelProviderRequest Request => new("Public facts only.", "漢字 😀 é", "{\"type\":\"object\"}", ReservationId: Id);
    private static ModelControlPolicy Policy => ModelControlPolicy.Approved("approved-2026-10",
        new ModelTariff("reviewed-2026-10", VertexModelOptions.ModelId, 275_000, 1_650_000, new DateOnly(2026, 11, 1)));
    private static VertexModelOptions Options => new(true, "synthetic-portfolio", "us", true, true, true);

    private sealed class Token : IVertexAccessToken
    {
        internal int Calls;
        public ValueTask<string> GetAsync(CancellationToken cancellationToken) { Calls++; return ValueTask.FromResult("synthetic-token"); }
    }

    private sealed class Handler : HttpMessageHandler
    {
        internal List<(Uri Uri, string Body)> Requests { get; } = [];
        internal Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? Respond { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("synthetic-token", request.Headers.Authorization.Parameter);
            Requests.Add((request.RequestUri!, await request.Content!.ReadAsStringAsync(ct)));
            return Respond is not null ? await Respond(request, ct) : Json(request.RequestUri!.AbsolutePath.EndsWith(":countTokens") ? CountBody : Generated);
        }
    }
    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static (VertexGeminiClient Client, Handler Handler, Token Token) Create(VertexModelOptions? options = null, ModelControlPolicy? policy = null)
    {
        var handler = new Handler();
        var token = new Token();
        return (new(options ?? Options, policy ?? Policy, new(new HttpClient(handler), token), new FakeTimeProvider(Now)), handler, token);
    }

    [Fact]
    public async Task Counts_full_text_request_and_generates_once_with_billing_only_reservation_label()
    {
        var (client, handler, _) = Create();
        var request = Request;
        Assert.Equal(ModelTokenCountBasis.ProviderVerifiedUpperBound, (await client.CountAsync(request)).Basis);
        var result = await client.GenerateAsync(request);
        Assert.True(result.Success);
        Assert.Equal(100, result.InputTokens);
        Assert.Equal(15, result.OutputTokens); // visible + thinking, without double-counting total
        Assert.DoesNotContain("private reasoning", result.Text!);
        Assert.Equal(2, handler.Requests.Count);
        var count = JsonNode.Parse(handler.Requests[0].Body)!.AsObject();
        var generated = JsonNode.Parse(handler.Requests[1].Body)!.AsObject();
        Assert.Equal(Id, generated["labels"]!["portfolio_reservation"]!.GetValue<string>());
        generated.Remove("labels");
        Assert.True(JsonNode.DeepEquals(count, generated));
        Assert.Equal("Public facts only.", count["systemInstruction"]!["parts"]![0]!["text"]!.GetValue<string>());
        Assert.Equal(request.UserMessage, count["contents"]![0]!["parts"]![0]!["text"]!.GetValue<string>());
        Assert.Equal(600, count["generationConfig"]!["maxOutputTokens"]!.GetValue<int>());
        Assert.Equal(1, count["generationConfig"]!["candidateCount"]!.GetValue<int>());
        Assert.Equal("MINIMAL", count["generationConfig"]!["thinkingConfig"]!["thinkingLevel"]!.GetValue<string>());
        Assert.Null(count["tools"]);
        Assert.Null(count["cachedContent"]);
        Assert.Contains(handler.Requests, r => r.Uri.Host == "us-aiplatform.googleapis.com");
        Assert.False((await client.GenerateAsync(request)).Success);
        Assert.Equal(2, handler.Requests.Count); // consumed receipt cannot authorize another call
    }

    [Fact]
    public async Task Real_adapter_contract_flows_through_orchestration_and_settles_reasoning_usage()
    {
        var (client, _, _) = Create();
        var clock = new FakeTimeProvider(Now);
        var ledger = new InMemoryModelControlLedger(Policy, clock);
        var orchestrator = new AssistantTurnOrchestrator(client, client, ledger, clock);
        var chunks = new Orchestration.OrchestrationHarness().Chunks;
        var result = await orchestrator.RunAsync("Tell me about Expinn", chunks, clock.GetTimestamp());
        Assert.NotNull(result);
        Assert.Contains(Orchestration.OrchestrationHarness.Statement, result.Answer);
        Assert.Equal(53, ledger.DayCharged(Now)); // 100 input + 15 billable output, rounded per component
        Assert.Equal(0, ledger.ActivePermits);
    }

    [Theory]
    [InlineData(250_000, 1_500_000, true)]
    [InlineData(249_999, 1_500_000, false)]
    [InlineData(250_000, 1_499_999, false)]
    public async Task Global_uses_only_the_fixed_global_endpoint_and_its_reviewed_tariff(long input, long output, bool allowed)
    {
        var tariff = Policy.Tariff! with { InputMicroUsdPerMillionTokens = input, OutputMicroUsdPerMillionTokens = output };
        var (client, handler, token) = Create(Options with { Location = "global" }, Policy with { Tariff = tariff });
        var request = Request;
        var count = await client.CountAsync(request);
        var result = await client.GenerateAsync(request);
        Assert.Equal(allowed, result.Success);
        if (allowed)
        {
            Assert.Equal(ModelTokenCountBasis.ProviderVerifiedUpperBound, count.Basis);
            Assert.Equal(2, handler.Requests.Count);
            Assert.All(handler.Requests, r =>
            {
                Assert.Equal("aiplatform.googleapis.com", r.Uri.Host);
                Assert.Contains("/locations/global/", r.Uri.AbsolutePath);
            });
        }
        else
        {
            Assert.Equal(ModelTokenCountBasis.Unverified, count.Basis);
            Assert.Empty(handler.Requests);
            Assert.Equal(0, token.Calls);
        }
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("2147483648")]
    [InlineData("\"10\"")]
    public async Task Malformed_or_overflowing_usage_never_becomes_confirmed(string invalid)
    {
        var (client, handler, _) = Create();
        var request = Request;
        await client.CountAsync(request);
        handler.Respond = (_, _) => Task.FromResult(Json(Generated.Replace("\"promptTokenCount\":100", "\"promptTokenCount\":" + invalid)));
        var result = await client.GenerateAsync(request);
        Assert.False(result.Success);
        Assert.Null(result.InputTokens);
        Assert.Null(result.OutputTokens);
    }

    [Fact]
    public async Task Input_replacement_or_extra_model_authority_cannot_enter_the_wire_request()
    {
        var (client, handler, token) = Create();
        foreach (var invalid in new[] { Request with { ReservationId = "../../outside" }, Request with { MaxOutputTokens = 601 },
                     Request with { UserMessage = new string('x', 12_001) }, Request with { OutputSchema = "{" } })
        {
            Assert.Equal(ModelTokenCountBasis.Unverified, (await client.CountAsync(invalid)).Basis);
            Assert.False((await client.GenerateAsync(invalid)).Success);
        }
        Assert.Empty(handler.Requests);
        Assert.Equal(0, token.Calls);
    }

    [Fact]
    public async Task An_equal_but_different_request_cannot_reuse_a_count_receipt()
    {
        var (client, handler, _) = Create();
        var request = Request;
        await client.CountAsync(request);
        Assert.False((await client.GenerateAsync(request with { })).Success);
        Assert.Single(handler.Requests);
    }

    public static IEnumerable<object[]> InvalidCounts() => new[]
    {
        "{}", "null", "{", "{\"totalTokens\":0}", "{\"totalTokens\":-1}",
        CountBody.Replace("500", "6001"), CountBody.Replace("TEXT", "IMAGE"),
        CountBody.Replace("\"tokenCount\":500", "\"tokenCount\":499"),
        "{\"totalTokens\":500,\"totalTokens\":1,\"promptTokensDetails\":[]}",
        new string('x', 65_537)
    }.Select(s => new object[] { s });

    [Theory]
    [MemberData(nameof(InvalidCounts))]
    public async Task Bad_counts_deny_inference_without_a_fallback_heuristic(string body)
    {
        var (client, handler, _) = Create();
        handler.Respond = (_, _) => Task.FromResult(Json(body));
        var request = Request;
        Assert.Equal(ModelTokenCountBasis.Unverified, (await client.CountAsync(request)).Basis);
        Assert.False((await client.GenerateAsync(request)).Success);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("MAX_TOKENS")]
    [InlineData("SAFETY")]
    [InlineData("RECITATION")]
    [InlineData("OTHER")]
    public async Task Noncompleted_status_preserves_billable_usage_but_not_text(string finish)
    {
        var (client, handler, _) = Create();
        var request = Request;
        await client.CountAsync(request);
        handler.Respond = (_, _) => Task.FromResult(Json(Generated.Replace("STOP", finish)));
        var result = await client.GenerateAsync(request);
        Assert.False(result.Success);
        Assert.Null(result.Text);
        Assert.Equal(100, result.InputTokens);
        Assert.Equal(15, result.OutputTokens);
    }

    [Theory]
    [InlineData("content")]
    [InlineData("candidates")]
    [InlineData("modelVersion")]
    public async Task Malformed_output_does_not_discard_known_usage(string field)
    {
        var (client, handler, _) = Create();
        var request = Request;
        await client.CountAsync(request);
        var json = JsonNode.Parse(Generated)!.AsObject();
        if (field == "content") json["candidates"]![0]!["content"] = "bad shape";
        else json[field] = "bad shape";
        handler.Respond = (_, _) => Task.FromResult(Json(json.ToJsonString()));
        var result = await client.GenerateAsync(request);
        Assert.False(result.Success);
        Assert.Equal(100, result.InputTokens);
        Assert.Equal(15, result.OutputTokens);
    }

    [Fact]
    public async Task Counting_contract_violation_disables_future_dispatch_and_reports_actual_usage()
    {
        var (client, handler, _) = Create();
        handler.Respond = (_, _) => Task.FromResult(Json(CountBody.Replace("500", "99")));
        var request = Request;
        await client.CountAsync(request);
        handler.Respond = (_, _) => Task.FromResult(Json(Generated));
        var result = await client.GenerateAsync(request);
        Assert.False(result.Success);
        Assert.Equal(100, result.InputTokens);
        Assert.False(client.IsAvailable);
        Assert.Equal(ModelTokenCountBasis.Unverified, (await client.CountAsync(Request)).Basis);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(429)]
    [InlineData(503)]
    public async Task Provider_errors_are_not_retried_or_exposed(int status)
    {
        var (client, handler, _) = Create();
        var request = Request;
        await client.CountAsync(request);
        handler.Respond = (_, _) => Task.FromResult(Json("private server message", (HttpStatusCode)status));
        var result = await client.GenerateAsync(request);
        Assert.False(result.Success);
        Assert.Null(result.InputTokens);
        Assert.DoesNotContain("private", result.ErrorMessage!);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("privacy")]
    [InlineData("counting")]
    [InlineData("recovery")]
    [InlineData("location")]
    [InlineData("project")]
    [InlineData("price")]
    [InlineData("model")]
    [InlineData("long-tariff")]
    public async Task Unreviewed_or_inconsistent_configuration_never_authenticates_or_sends(string missing)
    {
        var options = missing switch
        {
            "disabled" => Options with { Enabled = false },
            "privacy" => Options with { DataPolicyReviewed = false },
            "counting" => Options with { TokenCountingReviewed = false },
            "recovery" => Options with { RecoveryReady = false },
            "location" => Options with { Location = "us-central1" },
            "project" => Options with { ProjectId = "../../evil.example" },
            _ => Options
        };
        var tariff = missing switch
        {
            "price" => Policy.Tariff! with { InputMicroUsdPerMillionTokens = 1 },
            "model" => Policy.Tariff! with { ModelId = "other-model" },
            "long-tariff" => Policy.Tariff! with { ValidThroughUtc = new DateOnly(2099, 1, 1) },
            _ => Policy.Tariff!
        };
        var (client, handler, token) = Create(options, Policy with { Tariff = tariff });
        Assert.False(client.IsAvailable);
        Assert.Equal(ModelTokenCountBasis.Unverified, (await client.CountAsync(Request)).Basis);
        Assert.False((await client.GenerateAsync(Request)).Success);
        Assert.Empty(handler.Requests);
        Assert.Equal(0, token.Calls);
    }
}
