using Google.Api.Gax.Grpc;
using Google.Cloud.Firestore;
using Google.Cloud.Firestore.V1;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.Time.Testing;
using Rafael.Portfolio.Modules.Assistant.Application;
using Rafael.Portfolio.Modules.Assistant.Application.Orchestration;
using Rafael.Portfolio.Modules.Assistant.Infrastructure;
using Empty = Google.Protobuf.WellKnownTypes.Empty;
using ProtoTimestamp = Google.Protobuf.WellKnownTypes.Timestamp;

namespace Rafael.Portfolio.UnitTests;

// Real FirestoreDb/Transaction and ledger over an in-process RPC fake. This proves
// request grouping, mapping and deadline behavior, not server concurrency/isolation.
public sealed class FirestorePreparationBatchTests
{
    private const string Id = "res_batch_0001";
    private static readonly DateTimeOffset Now = ModelControlFixtures.Noon;

    internal sealed class Rpc(bool allowNonTransactional = false) : FirestoreClient
    {
        private readonly FirestoreSettings _settings = FirestoreSettings.GetDefault();
        public override FirestoreSettings Settings => _settings;
        internal Dictionary<string, Document> Documents { get; } = [];
        internal List<string[]> Reads { get; } = [];
        internal List<CommitRequest> Commits { get; } = [];
        internal int Begins { get; private set; }
        internal Action? BeforeRead { get; set; }
        private static ProtoTimestamp Time => ProtoTimestamp.FromDateTimeOffset(Now);

        public override Task<BeginTransactionResponse> BeginTransactionAsync(BeginTransactionRequest request, CallSettings? callSettings = null)
        {
            callSettings?.CancellationToken?.ThrowIfCancellationRequested();
            Begins++;
            return Task.FromResult(new BeginTransactionResponse { Transaction = ByteString.CopyFromUtf8("test-transaction") });
        }

        public override BatchGetDocumentsStream BatchGetDocuments(BatchGetDocumentsRequest request, CallSettings? callSettings = null)
        {
            callSettings?.CancellationToken?.ThrowIfCancellationRequested();
            if (!allowNonTransactional || request.Transaction.Length != 0)
                Assert.Equal("test-transaction", request.Transaction.ToStringUtf8());
            Reads.Add(request.Documents.ToArray());
            BeforeRead?.Invoke();
            // The wire API is explicitly unordered. Exercise the high-level SDK's
            // ordering contract instead of accidentally depending on wire order.
            return new Stream(request.Documents.Reverse().Select(name => Documents.TryGetValue(name, out var document)
                ? new BatchGetDocumentsResponse { Found = document.Clone(), ReadTime = Time }
                : new BatchGetDocumentsResponse { Missing = name, ReadTime = Time }));
        }

        public override Task<CommitResponse> CommitAsync(CommitRequest request, CallSettings? callSettings = null)
        {
            callSettings?.CancellationToken?.ThrowIfCancellationRequested();
            Commits.Add(request.Clone());
            var response = new CommitResponse { CommitTime = Time };
            foreach (var write in request.Writes)
            {
                Assert.NotNull(write.Update);
                var document = write.Update.Clone();
                document.CreateTime = Time;
                document.UpdateTime = Time;
                Documents[document.Name] = document;
                response.WriteResults.Add(new Google.Cloud.Firestore.V1.WriteResult { UpdateTime = Time });
            }
            return Task.FromResult(response);
        }

        public override Task<Empty> RollbackAsync(RollbackRequest request, CallSettings? callSettings = null) => Task.FromResult(new Empty());

        private sealed class Stream(IEnumerable<BatchGetDocumentsResponse> responses) : BatchGetDocumentsStream
        {
            public override AsyncServerStreamingCall<BatchGetDocumentsResponse> GrpcCall { get; } = new(
                new Reader(responses), Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => new Metadata(), () => { });
        }

        private sealed class Reader(IEnumerable<BatchGetDocumentsResponse> responses) : IAsyncStreamReader<BatchGetDocumentsResponse>
        {
            private readonly IEnumerator<BatchGetDocumentsResponse> _values = responses.GetEnumerator();
            public BatchGetDocumentsResponse Current => _values.Current;
            public Task<bool> MoveNext(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(_values.MoveNext());
            }
        }
    }

    private sealed class Harness
    {
        internal Rpc Client { get; } = new();
        internal FakeTimeProvider Clock { get; } = new(Now);
        internal FirestoreModelControlLedger Ledger { get; }
        internal Harness()
        {
            var db = FirestoreDb.Create("synthetic-batch-project", Client);
            Ledger = new(db, ModelControlFixtures.Policy(), Clock);
        }
        internal void ClearRequests() { Client.Reads.Clear(); Client.Commits.Clear(); }
        internal Document Reservation => Client.Documents.Single(pair => pair.Key.EndsWith("/" + Id, StringComparison.Ordinal)).Value;
    }

    [Fact]
    public async Task Reserve_batches_four_documents_and_begin_batches_two_without_changing_accounting()
    {
        var h = new Harness();
        Assert.True(await h.Ledger.InitializeAsync());
        h.ClearRequests();
        Assert.True((await h.Ledger.TryReserveAsync(Id)).Success);
        var read = Assert.Single(h.Client.Reads);
        Assert.Equal(4, read.Length);
        Assert.Contains(read, name => name.EndsWith("/model_control/state", StringComparison.Ordinal));
        Assert.Contains(read, name => name.EndsWith("/day_2026-10-08", StringComparison.Ordinal));
        Assert.Contains(read, name => name.EndsWith("/month_2026-10", StringComparison.Ordinal));
        Assert.Contains(read, name => name.EndsWith("/" + Id, StringComparison.Ordinal));
        Assert.Equal(4, Assert.Single(h.Client.Commits).Writes.Count);
        Assert.Equal(ModelControlFixtures.WorstCase, h.Reservation.Fields["chargedMicroUsd"].IntegerValue);
        h.ClearRequests();
        Assert.True((await h.Ledger.TryBeginCallAsync(Id, 500, 600)).Allowed);
        Assert.Equal(2, Assert.Single(h.Client.Reads).Length);
        Assert.Equal(1, h.Reservation.Fields["callsStarted"].IntegerValue);
        Assert.True(h.Reservation.Fields["callInFlight"].BooleanValue);
    }

    [Fact]
    public async Task Missing_root_remains_denied_without_writes_after_a_batch_read()
    {
        var h = new Harness();
        Assert.Equal(ModelReservationDenialReason.StoreNotInitialized, (await h.Ledger.TryReserveAsync(Id)).DenialReason);
        Assert.Equal(4, Assert.Single(h.Client.Reads).Length);
        Assert.All(h.Client.Commits, commit => Assert.Empty(commit.Writes));
        Assert.Empty(h.Client.Documents);
    }

    [Fact]
    public async Task Malformed_counter_in_unordered_batch_remains_state_invalid_without_writes()
    {
        var h = new Harness();
        Assert.True(await h.Ledger.InitializeAsync());
        Assert.True((await h.Ledger.TryReserveAsync(Id)).Success);
        var counter = h.Client.Documents.Single(pair => pair.Key.EndsWith("/day_2026-10-08", StringComparison.Ordinal)).Value;
        counter.Fields["chargedMicroUsd"] = new Value { StringValue = "not-an-integer" };
        h.ClearRequests();
        Assert.Equal(ModelReservationDenialReason.StateInvalid, (await h.Ledger.TryReserveAsync("res_batch_0002")).DenialReason);
        Assert.Equal(4, Assert.Single(h.Client.Reads).Length);
        Assert.All(h.Client.Commits, commit => Assert.Empty(commit.Writes));
        Assert.Equal("not-an-integer", counter.Fields["chargedMicroUsd"].StringValue);
    }

    [Fact]
    public async Task Duplicate_reservation_remains_denied_and_never_overwrites_its_obligation()
    {
        var h = new Harness();
        Assert.True(await h.Ledger.InitializeAsync());
        Assert.True((await h.Ledger.TryReserveAsync(Id)).Success);
        var before = h.Reservation.Clone();
        h.ClearRequests();
        Assert.False((await h.Ledger.TryReserveAsync(Id)).Success);
        Assert.Equal(4, Assert.Single(h.Client.Reads).Length);
        Assert.All(h.Client.Commits, commit => Assert.Empty(commit.Writes));
        Assert.Equal(before, h.Reservation);
    }

    private sealed class Counter(FakeTimeProvider clock) : IModelRequestTokenCounter
    {
        public ValueTask<ModelRequestTokenCount> CountAsync(ModelProviderRequest request, CancellationToken cancellationToken = default)
        {
            clock.Advance(TimeSpan.FromMilliseconds(400));
            return ValueTask.FromResult(new ModelRequestTokenCount(500, ModelTokenCountBasis.ProviderVerifiedUpperBound));
        }
    }

    private sealed class Provider(Rpc client) : IModelProvider
    {
        public bool IsAvailable => true;
        internal int Calls { get; private set; }
        public ValueTask<ModelProviderResponse> GenerateAsync(ModelProviderRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            client.BeforeRead = null; // The measured latency concerns preparation only.
            return ValueTask.FromResult(new ModelProviderResponse(true, Orchestration.OrchestrationHarness.Valid, 100, 10));
        }
    }

    [Fact]
    public async Task Round_trip_latency_fits_the_existing_preparation_deadline_without_widening_it()
    {
        var h = new Harness();
        Assert.True(await h.Ledger.InitializeAsync());
        h.Client.BeforeRead = () => h.Clock.Advance(TimeSpan.FromMilliseconds(600));
        var provider = new Provider(h.Client);
        var orchestrator = new AssistantTurnOrchestrator(provider, new Counter(h.Clock), h.Ledger, h.Clock);
        var start = h.Clock.GetTimestamp();
        var result = await orchestrator.RunAsync("Tell me about the role", new Orchestration.OrchestrationHarness().Chunks, start);
        Assert.NotNull(result);
        Assert.Equal(1, provider.Calls);
        Assert.Equal(TimeSpan.FromMilliseconds(1600), h.Clock.GetElapsedTime(start));
        var record = h.Client.Documents.Single(pair => pair.Key.Contains("/model_control_reservations/", StringComparison.Ordinal)).Value;
        Assert.Equal("Settled", record.Fields["state"].StringValue);
        Assert.Equal(14, record.Fields["chargedMicroUsd"].IntegerValue);
        Assert.False(record.Fields["callInFlight"].BooleanValue);
    }
}
