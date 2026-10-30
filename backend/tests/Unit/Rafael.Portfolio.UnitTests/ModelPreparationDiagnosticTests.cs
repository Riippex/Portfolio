using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Rafael.Portfolio.Modules.Assistant.Application;
using Rafael.Portfolio.Modules.Assistant.Application.Orchestration;
using Rafael.Portfolio.Modules.Assistant.Infrastructure;
using Rafael.Portfolio.UnitTests.Orchestration;

namespace Rafael.Portfolio.UnitTests;

public sealed class ModelPreparationDiagnosticTests
{
    private const string Private = "synthetic-private-prompt-response-token-address-exception";

    private sealed class RecordingLog<T> : ILogger<T>
    {
        internal List<Dictionary<string, object?>> Entries { get; } = [];
        internal bool Throw { get; init; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (Throw) throw new InvalidOperationException(Private);
            Assert.Null(exception);
            Assert.DoesNotContain(Private, formatter(state, exception));
            Entries.Add(((IEnumerable<KeyValuePair<string, object?>>)(object)state!).ToDictionary());
        }
    }

    [Theory]
    [InlineData("reserve-denied", "Reserve", "DailyBudgetExhausted")]
    [InlineData("begin-denied", "Begin", "PermitLost")]
    [InlineData("count-fault", "Count", "Fault")]
    [InlineData("count-deadline", "Count", "Deadline")]
    [InlineData("count-invalid", "Count", "Unverified")]
    public async Task Preparation_failures_have_distinct_bounded_codes_and_preserve_cancel_without_dispatch(
        string mode, string phase, string outcome)
    {
        var h = new OrchestrationHarness();
        var log = new RecordingLog<AssistantTurnOrchestrator>();
        if (mode == "reserve-denied") h.Ledger.Reserve = _ => ValueTask.FromResult(ModelReservationResult.Denied(ModelReservationDenialReason.DailyBudgetExhausted));
        if (mode == "begin-denied") h.Ledger.Begin = _ => ValueTask.FromResult(ModelCallResult.Denied(ModelCallDenialReason.PermitLost));
        if (mode == "count-fault") h.Counter.Count = (_, _) => throw new InvalidOperationException(Private);
        if (mode == "count-invalid") h.Counter.Count = (_, _) => ValueTask.FromResult(new ModelRequestTokenCount(0, ModelTokenCountBasis.Unverified));
        if (mode == "count-deadline") h.Counter.Count = (_, _) =>
        {
            h.Clock.Advance(TimeSpan.FromSeconds(3));
            return ValueTask.FromResult(new ModelRequestTokenCount(500, ModelTokenCountBasis.ProviderVerifiedUpperBound));
        };
        var orchestrator = new AssistantTurnOrchestrator(h.Provider, h.Counter, h.Ledger, h.Clock, log);
        Assert.Null(await orchestrator.RunAsync(Private, h.Chunks, h.Clock.GetTimestamp()));
        var entry = log.Entries.Last();
        Assert.Equal(phase, entry["Phase"]);
        Assert.Equal(outcome, entry["Outcome"]);
        Assert.All(log.Entries, e => Assert.Equal(["ElapsedMs", "Outcome", "Phase", "PhaseMs", "{OriginalFormat}"], e.Keys.Order(StringComparer.Ordinal).ToArray()));
        Assert.Equal(0, h.Provider.Calls);
        if (mode != "reserve-denied")
        {
            Assert.Contains("cancel", h.Ledger.Calls);
            Assert.Equal(ReservationState.Cancelled, h.Record.State);
        }
    }

    [Fact]
    public async Task Preparation_timings_are_phase_and_turn_elapsed_and_logging_failure_cannot_break_settlement()
    {
        foreach (var throws in new[] { false, true })
        {
            var h = new OrchestrationHarness();
            var log = new RecordingLog<AssistantTurnOrchestrator> { Throw = throws };
            h.Ledger.Reserve = forward => { h.Clock.Advance(TimeSpan.FromMilliseconds(100)); return forward(); };
            h.Counter.Count = (_, _) => { h.Clock.Advance(TimeSpan.FromMilliseconds(200)); return ValueTask.FromResult(new ModelRequestTokenCount(500, ModelTokenCountBasis.ProviderVerifiedUpperBound)); };
            h.Ledger.Begin = forward => { h.Clock.Advance(TimeSpan.FromMilliseconds(300)); return forward(); };
            var orchestrator = new AssistantTurnOrchestrator(h.Provider, h.Counter, h.Ledger, h.Clock, log);
            Assert.NotNull(await orchestrator.RunAsync(Private, h.Chunks, h.Clock.GetTimestamp()));
            Assert.Equal(1, h.Provider.Calls);
            Assert.Equal(ReservationState.Settled, h.Record.State);
            if (!throws)
            {
                Assert.Equal([100d, 200d, 300d], log.Entries.Select(e => e["PhaseMs"]).Cast<double>());
                Assert.Equal([100d, 300d, 600d], log.Entries.Select(e => e["ElapsedMs"]).Cast<double>());
            }
        }
    }

    private sealed class Token : IVertexAccessToken
    {
        public ValueTask<string> GetAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Private);
    }
    private sealed class Handler(string mode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (mode == "fault") throw new HttpRequestException(Private);
            if (mode == "deadline") throw new OperationCanceledException(Private);
            var body = mode switch
            {
                "schema" => "{",
                "shape" => "{\"totalTokens\":\"" + Private + "\"}",
                "range" => "{\"totalTokens\":6001}",
                "breakdown" => "{\"totalTokens\":500}",
                _ => "{\"totalTokens\":500,\"promptTokensDetails\":[{\"modality\":\"TEXT\",\"tokenCount\":500}]}"
            };
            return Task.FromResult(new HttpResponseMessage(mode == "http" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    [Theory]
    [InlineData("schema", "SchemaRejected")]
    [InlineData("shape", "SchemaRejected")]
    [InlineData("range", "CountOutOfRange")]
    [InlineData("breakdown", "TextBreakdownRejected")]
    [InlineData("fault", "ProviderFault")]
    [InlineData("deadline", "Deadline")]
    [InlineData("http", "ResponseUnavailable")]
    [InlineData("valid", "Verified")]
    public async Task Count_diagnostics_distinguish_failure_classes_without_logging_payload_values(string mode, string expected)
    {
        var log = new RecordingLog<VertexGeminiClient>();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero));
        var policy = ModelControlPolicy.Approved("approved-2026-10", new ModelTariff("test", VertexModelOptions.ModelId,
            250_000, 1_500_000, new DateOnly(2026, 11, 1)));
        using var http = new HttpClient(new Handler(mode));
        var client = new VertexGeminiClient(new(true, "synthetic-portfolio", "global", true, true, true), policy,
            new VertexTransport(http, new Token()), clock, log);
        var request = new ModelProviderRequest(Private, Private, "{\"type\":\"object\"}", ReservationId: "res_0123456789abcdef0123456789abcdef");
        var result = await client.CountAsync(request);
        Assert.Equal(mode == "valid" ? ModelTokenCountBasis.ProviderVerifiedUpperBound : ModelTokenCountBasis.Unverified, result.Basis);
        var entry = Assert.Single(log.Entries);
        Assert.Equal(expected, entry["Outcome"]);
        Assert.Equal(["DurationMs", "Outcome", "{OriginalFormat}"], entry.Keys.Order(StringComparer.Ordinal).ToArray());
    }
}
