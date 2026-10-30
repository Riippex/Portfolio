using Google.Cloud.Firestore;
using Google.Cloud.Firestore.V1;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Rafael.Portfolio.Modules.Assistant.Application;
using Rafael.Portfolio.Modules.Assistant.Application.Orchestration;
using Rafael.Portfolio.Modules.Assistant.Domain;
using Rafael.Portfolio.Modules.Assistant.Infrastructure;
using Rafael.Portfolio.UnitTests.Orchestration;
using Rafael.Portfolio.Web.Adapters;

namespace Rafael.Portfolio.UnitTests;

public sealed class ModelControlStartupPreparationTests
{
    private const string Id = "res_startup_0001";
    private static IConfiguration Config(string mode)
    {
        var values = new Dictionary<string, string?>
        {
            ["Assistant:ModelControl:PrepareClientOnStart"] = "true",
            ["Assistant:Vertex:Enabled"] = "true",
            ["Assistant:Vertex:ProjectId"] = "synthetic-portfolio",
            ["Assistant:Vertex:Location"] = "global",
            ["Assistant:Vertex:DataPolicyReviewed"] = "true",
            ["Assistant:Vertex:TokenCountingReviewed"] = "true",
            ["Assistant:Vertex:RecoveryReady"] = "true",
            ["Assistant:ModelControl:Firestore:ProjectId"] = "synthetic-portfolio",
            ["Assistant:ModelControl:Firestore:DatabaseId"] = "control-ledger",
            ["Assistant:ModelControl:Tariff:Version"] = "test",
            ["Assistant:ModelControl:Tariff:ModelId"] = VertexModelOptions.ModelId,
            ["Assistant:ModelControl:Tariff:InputMicroUsdPerMillionTokens"] = "250000",
            ["Assistant:ModelControl:Tariff:OutputMicroUsdPerMillionTokens"] = "1500000",
            ["Assistant:ModelControl:Tariff:ValidThroughUtc"] = "2026-11-01"
        };
        if (mode == "default") values.Remove("Assistant:ModelControl:PrepareClientOnStart");
        if (mode == "false") values["Assistant:ModelControl:PrepareClientOnStart"] = "false";
        if (mode == "case") values["Assistant:ModelControl:PrepareClientOnStart"] = "True";
        if (mode == "disabled") values["Assistant:Vertex:Enabled"] = "false";
        if (mode == "unreviewed") values.Remove("Assistant:Vertex:TokenCountingReviewed");
        if (mode == "mismatch") values["Assistant:Vertex:ProjectId"] = "other-project";
        if (mode == "missing-store") values.Remove("Assistant:ModelControl:Firestore:DatabaseId");
        if (mode == "missing-tariff") values.Remove("Assistant:ModelControl:Tariff:Version");
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    [Theory]
    [InlineData("default")]
    [InlineData("false")]
    [InlineData("case")]
    [InlineData("disabled")]
    [InlineData("unreviewed")]
    [InlineData("mismatch")]
    [InlineData("missing-store")]
    [InlineData("missing-tariff")]
    public async Task Only_exact_opt_in_with_the_fully_reviewed_matching_pair_can_prepare(string mode)
    {
        var clock = new FakeTimeProvider(ModelControlFixtures.Noon);
        var builds = 0;
        var ledger = ModelControlComposition.CreateLedger(Config(mode), clock, _ =>
        {
            builds++;
            throw new InvalidOperationException("No client, auth or root read is allowed here.");
        });
        Assert.False(ModelControlComposition.PreparationRequested(Config(mode), clock));
        if (ledger is FirestoreModelControlLedger store)
            Assert.Equal(ModelClientPreparationOutcome.NotRequested, await store.PrepareClientAsync());
        Assert.Equal(0, builds);
    }

    private static Document Root(string name) => new()
    {
        Name = name,
        Fields =
        {
            ["schemaVersion"] = new Value { IntegerValue = 1 },
            ["policyVersion"] = new Value { StringValue = "approved-2026-10" },
            ["lastDayKey"] = new Value { StringValue = "" },
            ["lastMonthKey"] = new Value { StringValue = "" },
            ["permits"] = new Value { MapValue = new MapValue() },
            ["liveReservations"] = new Value { IntegerValue = 0 },
            ["epoch"] = new Value { IntegerValue = 0 }
        },
        CreateTime = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(ModelControlFixtures.Noon),
        UpdateTime = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(ModelControlFixtures.Noon)
    };

    [Theory]
    [InlineData("ready", ModelClientPreparationOutcome.Ready)]
    [InlineData("missing", ModelClientPreparationOutcome.MissingRoot)]
    [InlineData("corrupt", ModelClientPreparationOutcome.InvalidState)]
    [InlineData("empty", ModelClientPreparationOutcome.InvalidState)]
    [InlineData("policy", ModelClientPreparationOutcome.PolicyMismatch)]
    [InlineData("fault", ModelClientPreparationOutcome.Unavailable)]
    public async Task Preparation_reads_one_root_once_and_never_writes_or_caches_an_allowance(string mode, ModelClientPreparationOutcome expected)
    {
        var clock = new FakeTimeProvider(ModelControlFixtures.Noon);
        var rpc = new FirestorePreparationBatchTests.Rpc(allowNonTransactional: true);
        var db = FirestoreDb.Create("synthetic-portfolio", rpc);
        var root = Root(db.Collection("model_control").Document("state").Path);
        if (mode == "corrupt") root.Fields["schemaVersion"] = new Value { StringValue = "invalid" };
        if (mode == "empty") root.Fields.Clear();
        if (mode == "policy") root.Fields["policyVersion"] = new Value { StringValue = "different" };
        if (mode != "missing") rpc.Documents[root.Name] = root;
        var builds = 0;
        var ledger = Assert.IsType<FirestoreModelControlLedger>(ModelControlComposition.CreateLedger(Config("ready"), clock, _ =>
        {
            builds++;
            if (mode == "fault") throw new InvalidOperationException("Synthetic private fault must remain opaque.");
            return Task.FromResult(db);
        }));
        Assert.Equal(ModelReservationDenialReason.StoreUnavailable, (await ledger.TryReserveAsync(Id)).DenialReason);
        Assert.Equal(0, builds);
        var preparation = ledger.PrepareClientAsync();
        Assert.Same(preparation, ledger.PrepareClientAsync());
        Assert.Equal(expected, await preparation);
        Assert.Equal(expected, await ledger.PrepareClientAsync());
        Assert.Equal(1, builds);
        Assert.Equal(0, rpc.Begins);
        Assert.Empty(rpc.Commits);
        if (mode != "fault") Assert.Equal(root.Name, Assert.Single(Assert.Single(rpc.Reads)));
        if (mode == "ready")
        {
            // Success cached a client only. Changed authoritative state must still deny.
            rpc.Documents[root.Name].Fields["schemaVersion"] = new Value { StringValue = "now-corrupt" };
            clock.Advance(TimeSpan.FromSeconds(2)); // Expire the deny-only unprepared-store memory.
            Assert.Equal(ModelReservationDenialReason.StateInvalid, (await ledger.TryReserveAsync(Id)).DenialReason);
            Assert.Equal(1, rpc.Begins); // The same supplied client served the fresh transaction.
        }
        else
        {
            clock.Advance(TimeSpan.FromSeconds(2));
            Assert.Equal(ModelReservationDenialReason.StoreUnavailable, (await ledger.TryReserveAsync(Id)).DenialReason);
            Assert.Equal(0, rpc.Begins);
        }
        Assert.All(rpc.Commits, commit => Assert.Empty(commit.Writes));
    }

    [Fact]
    public async Task A_factory_ignoring_cancellation_is_bounded_and_cannot_read_or_publish_after_timeout()
    {
        var clock = new FakeTimeProvider(ModelControlFixtures.Noon);
        var rpc = new FirestorePreparationBatchTests.Rpc(allowNonTransactional: true);
        var db = FirestoreDb.Create("synthetic-portfolio", rpc);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var late = new TaskCompletionSource<FirestoreDb>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ledger = new FirestoreModelControlLedger(_ => { entered.SetResult(); return late.Task; }, ModelControlFixtures.Policy(), clock);
        var preparation = ledger.PrepareClientAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(FirestoreModelControlLedger.ClientPreparationTimeout);
        Assert.Equal(ModelClientPreparationOutcome.TimedOut, await preparation.WaitAsync(TimeSpan.FromSeconds(5)));
        late.SetResult(db);
        await Task.Delay(30);
        Assert.Empty(rpc.Reads);
        Assert.Empty(rpc.Commits);
        Assert.Equal(ModelReservationDenialReason.StoreUnavailable, (await ledger.TryReserveAsync(Id)).DenialReason);
        Assert.Equal(ModelClientPreparationOutcome.TimedOut, await ledger.PrepareClientAsync());
    }

    [Fact]
    public async Task A_late_root_read_cannot_publish_the_client_after_the_bounded_startup_wait()
    {
        var clock = new FakeTimeProvider(ModelControlFixtures.Noon);
        var rpc = new FirestorePreparationBatchTests.Rpc(allowNonTransactional: true);
        var db = FirestoreDb.Create("synthetic-portfolio", rpc);
        var root = Root(db.Collection("model_control").Document("state").Path);
        rpc.Documents[root.Name] = root;
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rpc.BeforeRead = () => { entered.SetResult(); release.Wait(TimeSpan.FromSeconds(5)); };
        var ledger = new FirestoreModelControlLedger(_ => Task.FromResult(db), ModelControlFixtures.Policy(), clock);
        var preparation = ledger.PrepareClientAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            clock.Advance(FirestoreModelControlLedger.ClientPreparationTimeout);
            Assert.Equal(ModelClientPreparationOutcome.TimedOut, await preparation.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally { release.Set(); }
        await Task.Delay(30);
        Assert.Equal(ModelClientPreparationOutcome.TimedOut, await ledger.PrepareClientAsync());
        Assert.Equal(ModelReservationDenialReason.StoreUnavailable, (await ledger.TryReserveAsync(Id)).DenialReason);
        Assert.Single(rpc.Reads);
        Assert.Empty(rpc.Commits);
        Assert.Equal(0, rpc.Begins);
    }

    private sealed class Evidence : IAssistantEvidenceAdapter
    {
        public IReadOnlyList<AssistantEvidenceChunk> SearchEvidence(string query, int limit = 5, string? slugFilter = null) => new OrchestrationHarness().Chunks;
    }
    private sealed class NoModelToken : IVertexAccessToken
    {
        internal int Calls;
        public ValueTask<string> GetAsync(CancellationToken cancellationToken) { Calls++; throw new InvalidOperationException("No request model credentials without consent."); }
    }
    private sealed class NoModelHttp : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No provider transport without consent.");
    }

    [Fact]
    public async Task Prepared_operational_client_does_not_authorize_chat_or_stream_model_processing_without_consent()
    {
        var clock = new FakeTimeProvider(ModelControlFixtures.Noon);
        var config = Config("ready");
        var rpc = new FirestorePreparationBatchTests.Rpc(allowNonTransactional: true);
        var db = FirestoreDb.Create("synthetic-portfolio", rpc);
        var root = Root(db.Collection("model_control").Document("state").Path);
        rpc.Documents[root.Name] = root;
        var store = Assert.IsType<FirestoreModelControlLedger>(ModelControlComposition.CreateLedger(config, clock, _ => Task.FromResult(db)));
        Assert.Equal(ModelClientPreparationOutcome.Ready, await store.PrepareClientAsync());
        var token = new NoModelToken();
        using var http = new HttpClient(new NoModelHttp());
        var provider = new VertexGeminiClient(new(true, "synthetic-portfolio", "global", true, true, true),
            ModelControlComposition.BuildPolicy(config), new VertexTransport(http, token), clock);
        Assert.True(provider.IsAvailable);
        var ledger = new RecordingLedger(store);
        var service = new AssistantService(new Evidence(), new DeterministicGroundedSynthesizer(), new AssistantSafetyEvaluator(),
            new AssistantTurnOrchestrator(provider, provider, ledger, clock), clock);
        await service.ChatAsync(new AssistantChatRequest("Tell me about Expinn"));
        await foreach (var _ in service.StreamChatAsync(new AssistantChatRequest("Tell me about Expinn"))) { }
        Assert.Equal(0, token.Calls);
        Assert.Empty(ledger.Calls);
        Assert.Single(rpc.Reads); // Only the separate operational startup read occurred.
        Assert.Empty(rpc.Commits);
    }

    // Monotonic elapsed time advances while timer callbacks are deliberately held back.
    // This models a delayed scheduler without changing the production timeout.
    private sealed class TimerLagClock : TimeProvider
    {
        private readonly FakeTimeProvider _timers = new(ModelControlFixtures.Noon);
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        public override DateTimeOffset GetUtcNow() => ModelControlFixtures.Noon.AddTicks(GetTimestamp());
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            _timers.CreateTimer(callback, state, dueTime, period);
        internal void AdvanceElapsed(TimeSpan amount) => Interlocked.Add(ref _ticks, amount.Ticks);
    }

    [Theory]
    [InlineData("build", 0)]
    [InlineData("read", 1)]
    public async Task Elapsed_deadline_denies_a_late_build_or_read_even_when_the_timer_callback_has_not_fired(string phase, int expectedReads)
    {
        var clock = new TimerLagClock();
        var rpc = new FirestorePreparationBatchTests.Rpc(allowNonTransactional: true);
        var db = FirestoreDb.Create("synthetic-portfolio", rpc);
        var root = Root(db.Collection("model_control").Document("state").Path);
        rpc.Documents[root.Name] = root;
        if (phase == "read") rpc.BeforeRead = () => clock.AdvanceElapsed(FirestoreModelControlLedger.ClientPreparationTimeout);
        CancellationToken observedToken = default;
        var ledger = new FirestoreModelControlLedger(ct =>
        {
            observedToken = ct;
            if (phase == "build") clock.AdvanceElapsed(FirestoreModelControlLedger.ClientPreparationTimeout);
            return Task.FromResult(db);
        }, ModelControlFixtures.Policy(), clock);
        Assert.Equal(ModelClientPreparationOutcome.TimedOut, await ledger.PrepareClientAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(observedToken.IsCancellationRequested); // Only the elapsed check could have denied.
        Assert.Equal(expectedReads, rpc.Reads.Count);
        Assert.Equal(ModelReservationDenialReason.StoreUnavailable, (await ledger.TryReserveAsync(Id)).DenialReason);
        Assert.Empty(rpc.Commits);
    }

    private sealed class ThrowingLoggerFactory : ILoggerFactory
    {
        public ILogger CreateLogger(string categoryName) => throw new InvalidOperationException("Synthetic private logging failure.");
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }
    }

    [Fact]
    public async Task Failed_preparation_and_failed_diagnostics_do_not_break_deterministic_host_startup()
    {
        var clock = new FakeTimeProvider(ModelControlFixtures.Noon);
        var config = Config("ready");
        var ledger = ModelControlComposition.CreateLedger(config, clock,
            _ => throw new InvalidOperationException("Synthetic unavailable control store."));
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton(ledger);
        services.AddSingleton<ILoggerFactory>(new ThrowingLoggerFactory());
        using var provider = services.BuildServiceProvider();
        await ModelControlComposition.PrepareClientIfRequestedAsync(provider, config);
        Assert.Equal(ModelClientPreparationOutcome.Unavailable, await Assert.IsType<FirestoreModelControlLedger>(ledger).PrepareClientAsync());
        Assert.Equal(ModelReservationDenialReason.StoreUnavailable, (await ledger.TryReserveAsync(Id)).DenialReason);
    }
}
