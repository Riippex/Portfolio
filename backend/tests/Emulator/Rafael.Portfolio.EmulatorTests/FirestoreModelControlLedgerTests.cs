using Google.Cloud.Firestore;
using Grpc.Core;
using Rafael.Portfolio.Modules.Assistant.Application;
using Rafael.Portfolio.Modules.Assistant.Infrastructure;

namespace Rafael.Portfolio.EmulatorTests;

// Transactional behaviour of the real Firestore adapter against the Firestore emulator. Fake
// adapters prove the rules (unit tests); only these prove concurrency, restarts and storage.
public sealed class FirestoreModelControlLedgerTests(FirestoreEmulatorFixture emulator) : IClassFixture<FirestoreEmulatorFixture>
{
    private const long Worst = Fixtures.WorstCase;
    private static readonly DateTimeOffset Noon = Fixtures.Noon;

    private static string Id(int n) => Fixtures.Id(n);

    // The concurrency tests prove correctness, not liveness under client-side deadlines: a
    // transaction cancelled while waiting for a lock can still take the lock later and hold it
    // until the server expires it, which only ever makes the ledger deny. A generous deadline
    // keeps these tests from creating that situation.
    private static readonly TimeSpan PatientDeadline = TimeSpan.FromSeconds(60);

    private static async Task<T> Retried<T>(Func<ValueTask<T>> operation, Func<T, bool> unavailable)
    {
        T result = await operation();
        for (var attempt = 0; attempt < 10 && unavailable(result); attempt++)
        {
            result = await operation();
        }

        return result;
    }

    private sealed class Harness
    {
        private readonly FirestoreEmulatorFixture _emulator;

        public Harness(FirestoreEmulatorFixture emulator, ModelControlPolicy policy)
        {
            _emulator = emulator;
            Project = FirestoreEmulatorFixture.UniqueProject();
            Policy = policy;
            Clock = new TestClock(Noon);
            Raw = new RawStore(emulator.NewClient(Project));
        }

        public string Project { get; }

        public FirestoreDb NewClientForTests() => _emulator.NewClient(Project);

        public ModelControlPolicy Policy { get; }

        public TestClock Clock { get; }

        public RawStore Raw { get; }

        /// <summary>A replica: its own client and ledger instance over the same database.</summary>
        public FirestoreModelControlLedger Replica(ModelControlPolicy? policy = null, TimeSpan? timeout = null) =>
            new(_emulator.NewClient(Project), policy ?? Policy, Clock, timeout);
    }

    private async Task<(Harness Harness, FirestoreModelControlLedger Ledger)> StartAsync(ModelControlPolicy? policy = null, bool initialize = true)
    {
        var harness = new Harness(emulator, policy ?? Fixtures.Policy());
        var ledger = harness.Replica();
        if (initialize)
        {
            Assert.True(await ledger.InitializeAsync());
        }

        return (harness, ledger);
    }

    // --------------------------------------------------------------------------- initialization

    [Fact]
    public async Task Initialization_creates_the_control_document_once_and_never_overwrites_it()
    {
        var (h, ledger) = await StartAsync();
        Assert.True((await ledger.TryReserveAsync(Id(1))).Success);
        var before = await h.Raw.Fields(h.Raw.Root);

        Assert.False(await h.Replica().InitializeAsync());

        var after = await h.Raw.Fields(h.Raw.Root);
        Assert.Equal(before!["liveReservations"], after!["liveReservations"]);
        Assert.Equal(1, await h.Raw.PermitCount());
    }

    [Fact]
    public async Task A_store_that_was_never_initialized_denies_and_writes_nothing()
    {
        var (h, ledger) = await StartAsync(initialize: false);

        var result = await ledger.TryReserveAsync(Id(1));

        Assert.Equal(ModelReservationDenialReason.StoreNotInitialized, result.DenialReason);
        Assert.Null(await h.Raw.Fields(h.Raw.Root));
        Assert.Null(await h.Raw.Fields(h.Raw.Day("2026-10-08")));
        Assert.Equal(0, await h.Raw.ReservationDocuments());
    }

    [Fact]
    public async Task Initialization_refuses_an_invalid_policy()
    {
        var harness = new Harness(emulator, Fixtures.Policy(daily: -1));

        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Replica().InitializeAsync());

        Assert.Null(await harness.Raw.Fields(harness.Raw.Root));
    }

    // ------------------------------------------------------------------------------ privacy

    [Fact]
    public async Task Stored_documents_hold_only_bounded_accounting_metadata()
    {
        var (h, ledger) = await StartAsync();
        await ledger.TryReserveAsync(Id(1));
        await ledger.TryBeginCallAsync(Id(1), 1000, 100);
        await ledger.CompleteCallAsync(Id(1), new ModelUsage(1000, 100));
        await ledger.SettleAsync(Id(1));

        string[] root = ["schemaVersion", "policyVersion", "lastDayKey", "lastMonthKey", "permits", "liveReservations", "epoch"];
        string[] counter = ["schemaVersion", "kind", "key", "chargedMicroUsd", "expiresAt"];
        string[] reservation =
        [
            "schemaVersion", "id", "state", "policyVersion", "tariffVersion", "modelId", "dayKey", "monthKey", "reservedMicroUsd",
            "chargedMicroUsd", "maxCalls", "maxInputTokens", "maxOutputTokens", "callsStarted", "inputTokensUsed", "outputTokensUsed",
            "callInFlight", "pendingInputTokens", "pendingOutputTokens", "createdAt", "updatedAt", "expiresAt"
        ];

        Assert.Equal(root.Order(), (await h.Raw.Fields(h.Raw.Root))!.Keys.Order());
        Assert.Equal(counter.Order(), (await h.Raw.Fields(h.Raw.Day("2026-10-08")))!.Keys.Order());
        Assert.Equal(counter.Order(), (await h.Raw.Fields(h.Raw.Month("2026-10")))!.Keys.Order());
        Assert.Equal(reservation.Order(), (await h.Raw.Fields(h.Raw.Reservation(Id(1))))!.Keys.Order());
    }

    // ------------------------------------------------------------------ concurrency and replicas

    [Fact]
    public async Task Independent_replicas_never_grant_more_than_two_permits()
    {
        var (h, _) = await StartAsync();
        var first = h.Replica(timeout: PatientDeadline);
        var second = h.Replica(timeout: PatientDeadline);

        // Contending transactions that cannot commit report StoreUnavailable and change nothing;
        // callers retry, so the loop converges on the true answer.
        var results = await Task.WhenAll(Enumerable.Range(1, 8).Select(i =>
            Task.Run(() => Retried(
                () => (i % 2 == 0 ? first : second).TryReserveAsync(Id(i)),
                r => r.DenialReason == ModelReservationDenialReason.StoreUnavailable))));

        var granted = results.Count(r => r.Success);
        var spread = string.Join(", ", results.GroupBy(r => r.DenialReason?.ToString() ?? "Granted").Select(g => $"{g.Key}={g.Count()}"));
        // Never more than two, whatever the interleaving. Contending transactions that still
        // cannot commit after their retries are refused (StoreUnavailable), which is safe.
        Assert.True(granted is >= 1 and <= 2, $"Granted {granted}; outcomes: {spread}");
        Assert.Equal(granted, await h.Raw.PermitCount());
        Assert.Equal(granted * Worst, await h.Raw.Charged(h.Raw.Day("2026-10-08")));
        Assert.Equal(granted * Worst, await h.Raw.Charged(h.Raw.Month("2026-10")));
        Assert.All(results.Where(r => !r.Success), r => Assert.True(
            r.DenialReason is ModelReservationDenialReason.ConcurrencyLimitReached or ModelReservationDenialReason.StoreUnavailable));
    }

    [Fact]
    public async Task Independent_replicas_cannot_oversubscribe_the_daily_allowance()
    {
        // Room for six worst-case turns; each completed turn settles for 100 micro-USD.
        const long daily = 6 * Worst;
        var (h, _) = await StartAsync(Fixtures.Policy(daily: daily, monthly: daily));
        var replicas = new[] { h.Replica(timeout: PatientDeadline), h.Replica(timeout: PatientDeadline) };
        var settledTurns = 0;
        var next = 0;

        await Task.WhenAll(Enumerable.Range(0, 3).Select(worker => Task.Run(async () =>
        {
            var ledger = replicas[worker % replicas.Length];
            for (var attempt = 0; attempt < 25; attempt++)
            {
                var id = Id(Interlocked.Increment(ref next));
                var reserved = await ledger.TryReserveAsync(id);
                if (!reserved.Success)
                {
                    if (reserved.DenialReason is ModelReservationDenialReason.DailyBudgetExhausted)
                    {
                        return;
                    }

                    continue;
                }

                // A transaction that cannot commit under contention reports StoreUnavailable and
                // changes nothing, so the idempotent steps of the turn are simply retried.
                Assert.True((await Retried(() => ledger.TryBeginCallAsync(id, 1000, 100), r => r.DenialReason == ModelCallDenialReason.StoreUnavailable)).Allowed);
                Assert.Equal(ModelControlOutcome.Applied, await Retried(() => ledger.CompleteCallAsync(id, new ModelUsage(1000, 100)), o => o == ModelControlOutcome.StoreUnavailable));
                Assert.Equal(ModelControlOutcome.Applied, await Retried(() => ledger.SettleAsync(id), o => o == ModelControlOutcome.StoreUnavailable));
                Interlocked.Increment(ref settledTurns);
            }
        })));

        var charged = await h.Raw.Charged(h.Raw.Day("2026-10-08"));
        Assert.True(charged <= daily, $"Charged {charged} against a {daily} allowance.");
        Assert.Equal(settledTurns * 140L, charged); // exact: no lost update, no double charge
        Assert.Equal(charged, await h.Raw.Charged(h.Raw.Month("2026-10")));
        Assert.Equal(0, await h.Raw.PermitCount());
        Assert.True(settledTurns > 6, "Settled turns free budget again, so more than six turns must fit.");
    }

    [Fact]
    public async Task Concurrent_duplicate_ids_across_replicas_grant_exactly_one_reservation()
    {
        var (h, _) = await StartAsync();
        var first = h.Replica(timeout: PatientDeadline);
        var second = h.Replica(timeout: PatientDeadline);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            Task.Run(() => Retried(
                () => (i % 2 == 0 ? first : second).TryReserveAsync(Id(1)),
                r => r.DenialReason == ModelReservationDenialReason.StoreUnavailable))));

        Assert.Equal(1, results.Count(r => r.Success));
        Assert.Equal(1, await h.Raw.PermitCount());
        Assert.Equal(Worst, await h.Raw.Charged(h.Raw.Day("2026-10-08")));
        Assert.Equal(1, await h.Raw.ReservationDocuments());
    }

    [Fact]
    public async Task A_duplicate_id_cannot_start_a_second_provider_call()
    {
        var (h, ledger) = await StartAsync();
        await ledger.TryReserveAsync(Id(1));
        Assert.True((await ledger.TryBeginCallAsync(Id(1), 1000, 100)).Allowed);

        var other = h.Replica();
        Assert.Equal(ModelReservationDenialReason.DuplicateReservationId, (await other.TryReserveAsync(Id(1))).DenialReason);
        Assert.Equal(ModelCallDenialReason.CallInFlight, (await other.TryBeginCallAsync(Id(1), 10, 10)).DenialReason);
    }

    [Fact]
    public async Task Contention_never_leaves_counters_permits_and_reservations_inconsistent()
    {
        var (h, _) = await StartAsync();
        var replicas = new[] { h.Replica(timeout: PatientDeadline), h.Replica(timeout: PatientDeadline), h.Replica(timeout: PatientDeadline) };

        var results = await Task.WhenAll(Enumerable.Range(1, 12).Select(i =>
            Task.Run(() => replicas[i % replicas.Length].TryReserveAsync(Id(i)).AsTask())));

        var granted = results.Count(r => r.Success);
        Assert.InRange(granted, 0, 2);
        Assert.Equal(granted, await h.Raw.PermitCount());
        Assert.All(results.Where(r => !r.Success), r => Assert.True(
            r.DenialReason is ModelReservationDenialReason.ConcurrencyLimitReached or ModelReservationDenialReason.StoreUnavailable));
        Assert.Equal(granted, await h.Raw.ReservationDocuments());
        var day = await h.Raw.Fields(h.Raw.Day("2026-10-08"));
        Assert.Equal(granted * Worst, day is null ? 0 : (long)day["chargedMicroUsd"]);
    }

    // ----------------------------------------------------------------- restart and stage sharing

    [Fact]
    public async Task State_survives_a_restart_and_a_new_process_continues_the_same_turn()
    {
        var (h, ledger) = await StartAsync();
        await ledger.TryReserveAsync(Id(1));
        await ledger.TryBeginCallAsync(Id(1), 1000, 100);
        await ledger.CompleteCallAsync(Id(1), new ModelUsage(1000, 100));
        await ledger.TryReserveAsync(Id(2));
        await ledger.AbandonAsync(Id(2));

        var restarted = h.Replica(); // new client, new instance, nothing in memory

        Assert.Equal(ModelReservationDenialReason.ConcurrencyLimitReached, (await restarted.TryReserveAsync(Id(3))).DenialReason);
        Assert.Equal(ModelControlOutcome.Applied, await restarted.SettleAsync(Id(1)));
        Assert.Equal(Worst + 140, await h.Raw.Charged(h.Raw.Day("2026-10-08")));
        h.Clock.Advance(TimeSpan.FromSeconds(2)); // past the one-second memory of the earlier denial
        Assert.True((await restarted.TryReserveAsync(Id(3))).Success);
    }

    [Fact]
    public async Task Dev_and_prod_deployments_share_one_allowance_and_one_set_of_permits()
    {
        // Both stages point at the same project and database, so there is exactly one set of
        // documents; there is no per-stage state to create a second allowance from.
        var (h, dev) = await StartAsync(Fixtures.Policy(daily: 3 * Worst, monthly: 3 * Worst));
        var prod = h.Replica(Fixtures.Policy(daily: 3 * Worst, monthly: 3 * Worst));

        Assert.True((await dev.TryReserveAsync(Id(1))).Success);
        Assert.True((await prod.TryReserveAsync(Id(2))).Success);
        Assert.Equal(ModelReservationDenialReason.ConcurrencyLimitReached, (await dev.TryReserveAsync(Id(3))).DenialReason);
        Assert.Equal(ModelReservationDenialReason.ConcurrencyLimitReached, (await prod.TryReserveAsync(Id(4))).DenialReason);

        Assert.Equal(2 * Worst, await h.Raw.Charged(h.Raw.Day("2026-10-08")));
        Assert.Equal(2, await h.Raw.PermitCount());
    }

    [Fact]
    public async Task A_stage_with_its_own_empty_database_is_a_separate_allowance_which_is_why_stages_must_not_have_one()
    {
        var (shared, ledger) = await StartAsync(Fixtures.Policy(daily: Worst, monthly: Worst));
        var (isolated, other) = await StartAsync(Fixtures.Policy(daily: Worst, monthly: Worst));

        Assert.True((await ledger.TryReserveAsync(Id(1))).Success);
        Assert.True((await other.TryReserveAsync(Id(1))).Success); // a second, independent allowance

        Assert.Equal(Worst, await shared.Raw.Charged(shared.Raw.Day("2026-10-08")));
        Assert.Equal(Worst, await isolated.Raw.Charged(isolated.Raw.Day("2026-10-08")));
    }

    [Fact]
    public async Task A_remembered_denial_can_only_refuse_and_never_replaces_the_store()
    {
        var (h, ledger) = await StartAsync();
        await ledger.TryReserveAsync(Id(1));
        await ledger.TryReserveAsync(Id(2));
        Assert.Equal(ModelReservationDenialReason.ConcurrencyLimitReached, (await ledger.TryReserveAsync(Id(3))).DenialReason);
        await ledger.CancelUndispatchedAsync(Id(1)); // a permit is free in the store now

        // The same instance still refuses for a moment, and writes nothing while doing so.
        Assert.Equal(ModelReservationDenialReason.ConcurrencyLimitReached, (await ledger.TryReserveAsync(Id(4))).DenialReason);
        Assert.Null(await h.Raw.Fields(h.Raw.Reservation(Id(4))));

        // Memory is local and carries no authority: another process asks the store and is granted.
        Assert.True((await h.Replica().TryReserveAsync(Id(5))).Success);

        h.Clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(ModelReservationDenialReason.ConcurrencyLimitReached, (await ledger.TryReserveAsync(Id(4))).DenialReason); // permit taken by Id(5)
        Assert.Equal(2, await h.Raw.PermitCount());
    }

    // ------------------------------------------------------------------------- UTC boundaries

    [Fact]
    public async Task Settlement_after_midnight_refunds_only_the_original_day()
    {
        var (h, ledger) = await StartAsync();
        h.Clock.Now = new DateTimeOffset(2026, 10, 8, 23, 59, 0, TimeSpan.Zero);
        await ledger.TryReserveAsync(Id(1));
        await ledger.TryBeginCallAsync(Id(1), 1000, 100);
        h.Clock.Advance(TimeSpan.FromMinutes(2));
        await ledger.TryReserveAsync(Id(2));

        await ledger.CompleteCallAsync(Id(1), new ModelUsage(1000, 100));
        Assert.Equal(ModelControlOutcome.Applied, await ledger.SettleAsync(Id(1)));

        Assert.Equal(140, await h.Raw.Charged(h.Raw.Day("2026-10-08")));
        Assert.Equal(Worst, await h.Raw.Charged(h.Raw.Day("2026-10-09")));
        Assert.Equal(140 + Worst, await h.Raw.Charged(h.Raw.Month("2026-10")));
    }

    [Fact]
    public async Task Settlement_after_a_month_boundary_refunds_only_the_original_month()
    {
        var (h, ledger) = await StartAsync();
        h.Clock.Now = new DateTimeOffset(2026, 10, 31, 23, 59, 30, TimeSpan.Zero);
        await ledger.TryReserveAsync(Id(1));
        await ledger.TryBeginCallAsync(Id(1), 1000, 100);
        h.Clock.Advance(TimeSpan.FromMinutes(1));
        await ledger.TryReserveAsync(Id(2));

        await ledger.CompleteCallAsync(Id(1), new ModelUsage(1000, 100));
        await ledger.SettleAsync(Id(1));

        Assert.Equal(140, await h.Raw.Charged(h.Raw.Month("2026-10")));
        Assert.Equal(Worst, await h.Raw.Charged(h.Raw.Month("2026-11")));
        Assert.Equal(140, await h.Raw.Charged(h.Raw.Day("2026-10-31")));
        Assert.Equal(Worst, await h.Raw.Charged(h.Raw.Day("2026-11-01")));
    }

    [Fact]
    public async Task A_new_day_restores_daily_credit_but_not_monthly_credit()
    {
        var (h, ledger) = await StartAsync(Fixtures.Policy(daily: Worst, monthly: 2 * Worst));
        await ledger.TryReserveAsync(Id(1));
        await ledger.AbandonAsync(Id(1));
        h.Clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal(ModelReservationDenialReason.DailyBudgetExhausted, (await ledger.TryReserveAsync(Id(2))).DenialReason);

        h.Clock.Advance(TimeSpan.FromDays(1));
        Assert.True((await ledger.TryReserveAsync(Id(3))).Success);
        await ledger.AbandonAsync(Id(3));
        h.Clock.Advance(TimeSpan.FromDays(1));

        Assert.Equal(ModelReservationDenialReason.MonthlyBudgetExhausted, (await ledger.TryReserveAsync(Id(4))).DenialReason);
        Assert.Equal(2 * Worst, await h.Raw.Charged(h.Raw.Month("2026-10")));
    }

    [Fact]
    public async Task Counters_expire_forty_days_after_their_period_and_only_resolved_reservations_expire()
    {
        var (h, ledger) = await StartAsync();
        await ledger.TryReserveAsync(Id(1));
        await ledger.TryReserveAsync(Id(2));

        static DateTimeOffset At(IDictionary<string, object> fields) => ((Timestamp)fields["expiresAt"]).ToDateTimeOffset();

        Assert.Equal(new DateTimeOffset(2026, 11, 18, 0, 0, 0, TimeSpan.Zero), At((await h.Raw.Fields(h.Raw.Day("2026-10-08")))!));
        Assert.Equal(new DateTimeOffset(2026, 12, 11, 0, 0, 0, TimeSpan.Zero), At((await h.Raw.Fields(h.Raw.Month("2026-10")))!));

        // An unresolved reservation has no expiresAt field, so Firestore TTL cannot match it.
        Assert.DoesNotContain("expiresAt", (await h.Raw.Fields(h.Raw.Reservation(Id(1))))!.Keys);
        await ledger.AbandonAsync(Id(2));
        Assert.Equal("Uncertain", await h.Raw.State(Id(2)));
        Assert.DoesNotContain("expiresAt", (await h.Raw.Fields(h.Raw.Reservation(Id(2))))!.Keys);

        // Resolving it sets the expiry: 40 days after its month ended.
        await ledger.CancelUndispatchedAsync(Id(1));
        Assert.Equal(new DateTimeOffset(2026, 12, 11, 0, 0, 0, TimeSpan.Zero), At((await h.Raw.Fields(h.Raw.Reservation(Id(1))))!));
    }

    // ----------------------------------------------------------- overflow and corrupt/lost state

    [Theory]
    [InlineData(long.MaxValue)]
    [InlineData(long.MaxValue / 2)]
    [InlineData(-1L)]
    [InlineData(long.MinValue)]
    public async Task A_corrupt_or_extreme_balance_denies_without_modifying_any_counter(long corrupt)
    {
        var (h, ledger) = await StartAsync();
        await ledger.TryReserveAsync(Id(1));
        await ledger.AbandonAsync(Id(1));
        await h.Raw.Day("2026-10-08").UpdateAsync("chargedMicroUsd", corrupt);
        var monthBefore = await h.Raw.Charged(h.Raw.Month("2026-10"));
        var rootBefore = await h.Raw.Fields(h.Raw.Root);

        var result = await ledger.TryReserveAsync(Id(2));

        Assert.False(result.Success);
        Assert.Equal(corrupt, await h.Raw.Charged(h.Raw.Day("2026-10-08")));
        Assert.Equal(monthBefore, await h.Raw.Charged(h.Raw.Month("2026-10")));
        Assert.Equal(rootBefore!["liveReservations"], (await h.Raw.Fields(h.Raw.Root))!["liveReservations"]);
        Assert.Equal(1, await h.Raw.ReservationDocuments());
    }

    [Fact]
    public async Task A_counter_of_the_wrong_type_denies()
    {
        var (h, ledger) = await StartAsync();
        await ledger.TryReserveAsync(Id(1));
        await ledger.AbandonAsync(Id(1));
        await h.Raw.Day("2026-10-08").UpdateAsync("chargedMicroUsd", "not a number");

        Assert.Equal(ModelReservationDenialReason.StateInvalid, (await ledger.TryReserveAsync(Id(2))).DenialReason);
    }

    [Fact]
    public async Task Lost_current_period_state_denies_instead_of_granting_a_fresh_allowance()
    {
        var (h, ledger) = await StartAsync();
        await ledger.TryReserveAsync(Id(1));
        await ledger.AbandonAsync(Id(1));

        await h.Raw.Day("2026-10-08").DeleteAsync();
        Assert.Equal(ModelReservationDenialReason.StateInvalid, (await ledger.TryReserveAsync(Id(2))).DenialReason);

        await h.Raw.Day("2026-10-08").SetAsync(new Dictionary<string, object>
        {
            ["schemaVersion"] = 1,
            ["kind"] = "day",
            ["key"] = "2026-10-08",
            ["chargedMicroUsd"] = Worst,
            ["expiresAt"] = Timestamp.FromDateTimeOffset(Noon.AddDays(40))
        });
        await h.Raw.Month("2026-10").DeleteAsync();
        Assert.Equal(ModelReservationDenialReason.StateInvalid, (await ledger.TryReserveAsync(Id(2))).DenialReason);
        Assert.Equal(1, await h.Raw.ReservationDocuments());
    }

    [Fact]
    public async Task A_corrupt_control_document_denies()
    {
        var (h, ledger) = await StartAsync();
        await h.Raw.Root.UpdateAsync("schemaVersion", "one");
        Assert.Equal(ModelReservationDenialReason.StateInvalid, (await ledger.TryReserveAsync(Id(1))).DenialReason);

        await h.Raw.Root.UpdateAsync("schemaVersion", 99L);
        Assert.Equal(ModelReservationDenialReason.StateInvalid, (await ledger.TryReserveAsync(Id(1))).DenialReason);
        Assert.Equal(0, await h.Raw.ReservationDocuments());
    }

    [Theory]
    [InlineData(4294967297L)] // wraps to 1 when narrowed to 32 bits
    [InlineData(65537L)]
    [InlineData(0L)]
    [InlineData(2L)]
    [InlineData(-1L)]
    [InlineData(long.MaxValue)]
    public async Task An_unsupported_root_schema_version_grants_nothing_and_changes_nothing(long version)
    {
        var (h, ledger) = await StartAsync();
        await h.Raw.Root.UpdateAsync("schemaVersion", version);
        var before = await h.Raw.Fields(h.Raw.Root);

        var result = await ledger.TryReserveAsync(Id(1));

        Assert.False(result.Success);
        Assert.Equal(ModelReservationDenialReason.StateInvalid, result.DenialReason);
        var after = await h.Raw.Fields(h.Raw.Root);
        Assert.Equal(version, (long)after!["schemaVersion"]);
        Assert.Equal(before!["liveReservations"], after["liveReservations"]);
        Assert.Equal(before["epoch"], after["epoch"]);
        Assert.Equal(0, await h.Raw.ReservationDocuments());
        Assert.Null(await h.Raw.Fields(h.Raw.Day("2026-10-08")));
    }

    [Fact]
    public async Task A_missing_or_malformed_root_schema_version_grants_nothing()
    {
        var (h, ledger) = await StartAsync();

        await h.Raw.Root.UpdateAsync("schemaVersion", FieldValue.Delete);
        Assert.Equal(ModelReservationDenialReason.StateInvalid, (await ledger.TryReserveAsync(Id(1))).DenialReason);

        await h.Raw.Root.UpdateAsync("schemaVersion", 1.0);
        Assert.Equal(ModelReservationDenialReason.StateInvalid, (await ledger.TryReserveAsync(Id(1))).DenialReason);

        Assert.Equal(0, await h.Raw.ReservationDocuments());
        Assert.Equal(CapacityReconciliation.StateInvalid, await ledger.ReconcileCapacityAsync());
    }

    [Fact]
    public async Task An_unsupported_counter_or_reservation_schema_version_denies_without_mutation()
    {
        var (h, ledger) = await StartAsync();
        await ledger.TryReserveAsync(Id(1));
        await h.Raw.Day("2026-10-08").UpdateAsync("schemaVersion", 4294967297L);

        Assert.Equal(ModelReservationDenialReason.StateInvalid, (await ledger.TryReserveAsync(Id(2))).DenialReason);
        Assert.Equal(1, await h.Raw.ReservationDocuments());
        Assert.Equal(Worst, await h.Raw.Charged(h.Raw.Month("2026-10")));

        await h.Raw.Day("2026-10-08").UpdateAsync("schemaVersion", 1L);
        await h.Raw.Reservation(Id(1)).UpdateAsync("schemaVersion", 4294967297L);
        Assert.Equal(ModelCallDenialReason.StateInvalid, (await ledger.TryBeginCallAsync(Id(1), 10, 10)).DenialReason);
        Assert.Equal(ModelControlOutcome.StateInvalid, await ledger.CancelUndispatchedAsync(Id(1)));
        Assert.Equal(ModelControlOutcome.StateInvalid, await ledger.ReconcileAsync(Id(1), ReconciliationResolution.NotDispatched));
        Assert.Equal(Worst, await h.Raw.Charged(h.Raw.Day("2026-10-08")));
        Assert.Equal(1, await h.Raw.PermitCount());
    }

    [Fact]
    public async Task Counts_that_overflow_a_32_bit_integer_are_rejected_not_clamped()
    {
        var (h, ledger) = await StartAsync();
        await h.Raw.Root.UpdateAsync("liveReservations", 4294967297L);

        Assert.Equal(ModelReservationDenialReason.StateInvalid, (await ledger.TryReserveAsync(Id(1))).DenialReason);
        Assert.Equal(0, await h.Raw.ReservationDocuments());
    }

    [Fact]
    public async Task A_policy_version_that_does_not_match_the_control_document_denies()
    {
        var (h, _) = await StartAsync();
        var other = h.Replica(ModelControlPolicy.Approved("policy-2", Fixtures.Tariff()));

        Assert.Equal(ModelReservationDenialReason.InvalidTariffOrPolicy, (await other.TryReserveAsync(Id(1))).DenialReason);
        Assert.Equal(0, await h.Raw.ReservationDocuments());
    }

    [Fact]
    public async Task A_corrupt_reservation_document_blocks_calls_and_settlement()
    {
        var (h, ledger) = await StartAsync();
        await ledger.TryReserveAsync(Id(1));
        await h.Raw.Reservation(Id(1)).UpdateAsync("reservedMicroUsd", -5L);

        Assert.Equal(ModelCallDenialReason.StateInvalid, (await ledger.TryBeginCallAsync(Id(1), 10, 10)).DenialReason);
        Assert.Equal(ModelControlOutcome.StateInvalid, await ledger.CancelUndispatchedAsync(Id(1)));

        await h.Raw.Reservation(Id(1)).UpdateAsync("state", "Bogus");
        Assert.Equal(ModelControlOutcome.StateInvalid, await ledger.AbandonAsync(Id(1)));
        Assert.Equal(Worst, await h.Raw.Charged(h.Raw.Day("2026-10-08")));
    }

    // ---------------------------------------------------------- usage, tariffs and per-turn limits

    [Fact]
    public async Task Per_turn_call_and_token_allowances_are_enforced_before_dispatch()
    {
        var (_, ledger) = await StartAsync();
        await ledger.TryReserveAsync(Id(1));

        Assert.True((await ledger.TryBeginCallAsync(Id(1), 4000, 300)).Allowed);
        Assert.Equal(ModelCallDenialReason.CallInFlight, (await ledger.TryBeginCallAsync(Id(1), 10, 10)).DenialReason);
        await ledger.CompleteCallAsync(Id(1), new ModelUsage(4000, 300));

        Assert.Equal(ModelCallDenialReason.InputTokenLimitReached, (await ledger.TryBeginCallAsync(Id(1), 2001, 100)).DenialReason);
        Assert.Equal(ModelCallDenialReason.OutputTokenLimitReached, (await ledger.TryBeginCallAsync(Id(1), 100, 301)).DenialReason);
        Assert.True((await ledger.TryBeginCallAsync(Id(1), 2000, 300)).Allowed);
        await ledger.CompleteCallAsync(Id(1), new ModelUsage(2000, 300));
        Assert.Equal(ModelCallDenialReason.CallLimitReached, (await ledger.TryBeginCallAsync(Id(1), 1, 1)).DenialReason);
    }

    [Theory]
    [InlineData(-1L, 10L)]
    [InlineData(10L, -1L)]
    [InlineData(0L, 10L)]
    [InlineData(long.MaxValue, 10L)]
    public async Task Invalid_usage_keeps_the_charge_and_blocks_settlement(long input, long output)
    {
        var (h, ledger) = await StartAsync();
        await ledger.TryReserveAsync(Id(1));
        await ledger.TryBeginCallAsync(Id(1), 1000, 100);

        Assert.Equal(ModelControlOutcome.InvalidUsage, await ledger.CompleteCallAsync(Id(1), new ModelUsage(input, output)));

        Assert.Equal("Uncertain", await h.Raw.State(Id(1)));
        Assert.Equal(Worst, await h.Raw.Charged(h.Raw.Day("2026-10-08")));
        Assert.Equal(ModelControlOutcome.InvalidState, await ledger.SettleAsync(Id(1)));
        Assert.Equal(Worst, await h.Raw.Charged(h.Raw.Month("2026-10")));
    }

    [Fact]
    public async Task Usage_beyond_the_allowance_is_charged_to_the_original_periods_and_never_refunded()
    {
        var (h, ledger) = await StartAsync();
        await ledger.TryReserveAsync(Id(1));
        await ledger.TryBeginCallAsync(Id(1), 6000, 600);

        Assert.Equal(ModelControlOutcome.AllowanceExceeded, await ledger.CompleteCallAsync(Id(1), new ModelUsage(90_000, 9_000)));

        Assert.Equal(9_000 + 3_600, await h.Raw.Charged(h.Raw.Day("2026-10-08")));
        Assert.Equal(9_000 + 3_600, await h.Raw.Charged(h.Raw.Month("2026-10")));
        Assert.Equal("Settled", await h.Raw.State(Id(1))); // priced overage is final and resolved
        Assert.Equal(ModelControlOutcome.InvalidState, await ledger.SettleAsync(Id(1)));
    }

    [Fact]
    public async Task Settlement_reconciles_to_the_tariff_cost_rounded_up()
    {
        var (h, ledger) = await StartAsync();
        await ledger.TryReserveAsync(Id(1));
        await ledger.TryBeginCallAsync(Id(1), 5, 5);
        await ledger.CompleteCallAsync(Id(1), new ModelUsage(5, 3));

        Assert.Equal(ModelControlOutcome.Applied, await ledger.SettleAsync(Id(1)));

        Assert.Equal(3, await h.Raw.Charged(h.Raw.Day("2026-10-08")));
        Assert.Equal("Settled", await h.Raw.State(Id(1)));
        Assert.Equal(0, await h.Raw.PermitCount());
    }

    [Fact]
    public async Task An_expired_tariff_disables_new_paid_admission()
    {
        var (h, _) = await StartAsync(Fixtures.Policy(tariff: Fixtures.Tariff(new DateOnly(2026, 10, 8))));
        var ledger = h.Replica();
        Assert.True((await ledger.TryReserveAsync(Id(1))).Success);
        await ledger.CancelUndispatchedAsync(Id(1));

        h.Clock.Advance(TimeSpan.FromDays(1));

        Assert.Equal(ModelReservationDenialReason.InvalidTariffOrPolicy, (await ledger.TryReserveAsync(Id(2))).DenialReason);
    }

    [Fact]
    public async Task A_tariff_change_mid_turn_blocks_new_calls_and_keeps_the_charge()
    {
        var (h, ledger) = await StartAsync();
        await ledger.TryReserveAsync(Id(1));
        await ledger.TryBeginCallAsync(Id(1), 1000, 100);
        await ledger.CompleteCallAsync(Id(1), new ModelUsage(1000, 100));

        var repriced = h.Replica(Fixtures.Policy(tariff: Fixtures.Tariff(version: "tariff-2")));

        Assert.Equal(ModelCallDenialReason.InvalidTariffOrPolicy, (await repriced.TryBeginCallAsync(Id(1), 10, 10)).DenialReason);
        Assert.Equal(ModelControlOutcome.InvalidTariffOrPolicy, await repriced.SettleAsync(Id(1)));
        Assert.Equal(Worst, await h.Raw.Charged(h.Raw.Day("2026-10-08")));
        Assert.Equal(0, await h.Raw.PermitCount());
    }

    [Fact]
    public async Task Malformed_policy_and_tariff_deny_without_touching_the_store()
    {
        var (h, _) = await StartAsync();
        var bad = new[]
        {
            Fixtures.Policy(daily: 0), Fixtures.Policy(daily: ModelControlPolicy.ApprovedDailyBudgetMicroUsd + 1),
            Fixtures.Policy(monthly: -1), ModelControlPolicy.Approved(Fixtures.PolicyVersion, null),
            Fixtures.Policy(tariff: Fixtures.Tariff() with { InputMicroUsdPerMillionTokens = 0 }),
            Fixtures.Policy(tariff: Fixtures.Tariff(new DateOnly(2020, 1, 1))),
            Fixtures.Policy() with { MaxActivePermits = 3 }, Fixtures.Policy() with { MaxCalls = 3 },
            Fixtures.Policy() with { MaxInputTokens = 6001 }, Fixtures.Policy() with { MaxOutputTokens = 601 }
        };

        foreach (var policy in bad)
        {
            var result = await h.Replica(policy).TryReserveAsync(Id(1));
            Assert.Equal(ModelReservationDenialReason.InvalidTariffOrPolicy, result.DenialReason);
        }

        Assert.Equal(0, await h.Raw.ReservationDocuments());
        Assert.Null(await h.Raw.Fields(h.Raw.Day("2026-10-08")));
    }

    // ------------------------------------------------------------ uncertain outcomes and permits

    [Fact]
    public async Task Abandoning_holds_the_charge_and_a_lease_end_frees_only_permits_with_no_call_in_flight()
    {
        var (h, ledger) = await StartAsync();
        await ledger.TryReserveAsync(Id(1));
        await ledger.TryBeginCallAsync(Id(1), 1000, 100); // dispatched, outcome unknown
        await ledger.TryReserveAsync(Id(2)); // never dispatched

        Assert.Equal(ModelControlOutcome.Applied, await ledger.AbandonAsync(Id(1)));
        Assert.Equal(ModelControlOutcome.Applied, await ledger.AbandonAsync(Id(2)));

        h.Clock.Advance(TimeSpan.FromMinutes(1)); // a caller timeout is not a lease end
        Assert.Equal(ModelReservationDenialReason.ConcurrencyLimitReached, (await ledger.TryReserveAsync(Id(3))).DenialReason);
        Assert.Equal(2, await h.Raw.PermitCount());
        Assert.Equal(2 * Worst, await h.Raw.Charged(h.Raw.Day("2026-10-08")));

        h.Clock.Advance(ModelControlPolicy.ApprovedPermitLease);
        Assert.True((await h.Replica().TryReserveAsync(Id(3))).Success);

        // Id(2) never dispatched, so its permit is free again; Id(1) may still be running, so its is not.
        var permits = (IDictionary<string, object>)(await h.Raw.Fields(h.Raw.Root))!["permits"];
        Assert.Equal([Id(1), Id(3)], permits.Keys.Order());
        Assert.Equal("Uncertain", await h.Raw.State(Id(1)));
        Assert.Equal("Uncertain", await h.Raw.State(Id(2)));
        Assert.Equal(3 * Worst, await h.Raw.Charged(h.Raw.Day("2026-10-08")));
        Assert.Equal(ModelControlOutcome.InvalidState, await ledger.SettleAsync(Id(1)));
    }

    [Fact]
    public async Task Two_started_calls_keep_their_permits_after_six_minutes_and_a_third_turn_is_refused()
    {
        // Reviewed defect: after six minutes a third reservation and call were allowed while two
        // dispatched calls were still CallInFlight.
        var (h, ledger) = await StartAsync();
        await ledger.TryReserveAsync(Id(1));
        await ledger.TryReserveAsync(Id(2));
        Assert.True((await ledger.TryBeginCallAsync(Id(1), 1000, 100)).Allowed);
        Assert.True((await ledger.TryBeginCallAsync(Id(2), 1000, 100)).Allowed);

        h.Clock.Advance(TimeSpan.FromMinutes(6));
        var restarted = h.Replica();
        var third = await restarted.TryReserveAsync(Id(3));

        Assert.Equal(ModelReservationDenialReason.ConcurrencyLimitReached, third.DenialReason);
        Assert.Null(await h.Raw.Fields(h.Raw.Reservation(Id(3))));
        Assert.Equal(2, await h.Raw.PermitCount());
        Assert.Equal(2 * Worst, await h.Raw.Charged(h.Raw.Day("2026-10-08")));
        Assert.True((bool)(await h.Raw.Fields(h.Raw.Reservation(Id(1))))!["callInFlight"]);

        h.Clock.Advance(TimeSpan.FromHours(12));
        Assert.Equal(ModelReservationDenialReason.ConcurrencyLimitReached, (await h.Replica().TryReserveAsync(Id(3))).DenialReason);
    }

    [Fact]
    public async Task Explicit_reconciliation_is_the_only_way_to_release_an_unresolved_call()
    {
        var (h, ledger) = await StartAsync();
        await ledger.TryReserveAsync(Id(1));
        await ledger.TryReserveAsync(Id(2));
        await ledger.TryBeginCallAsync(Id(1), 1000, 100);
        await ledger.TryBeginCallAsync(Id(2), 1000, 100);
        h.Clock.Advance(TimeSpan.FromMinutes(6));
        // The next reservation attempt marks nothing resolved and frees nothing.
        Assert.False((await ledger.TryReserveAsync(Id(3))).Success);

        var listed = await h.Replica().ListUnresolvedAsync();
        Assert.Equal([Id(1), Id(2)], listed.Select(u => u.Id).Order());
        Assert.All(listed, u => Assert.True(u.CallInFlight));

        var operatorLedger = h.Replica();
        Assert.Equal(ModelControlOutcome.Applied, await operatorLedger.ReconcileAsync(Id(1), ReconciliationResolution.NotDispatched));
        Assert.Equal("Cancelled", await h.Raw.State(Id(1)));
        Assert.Equal(Worst, await h.Raw.Charged(h.Raw.Day("2026-10-08")));
        Assert.Equal(1, await h.Raw.PermitCount());

        Assert.Equal(ModelControlOutcome.Applied,
            await operatorLedger.ReconcileAsync(Id(2), ReconciliationResolution.Completed, new ModelUsage(1000, 100)));
        Assert.Equal(140, await h.Raw.Charged(h.Raw.Day("2026-10-08")));
        Assert.Equal(0, await h.Raw.PermitCount());
        Assert.Contains("expiresAt", (await h.Raw.Fields(h.Raw.Reservation(Id(2))))!.Keys);

        h.Clock.Advance(TimeSpan.FromSeconds(2)); // past the one-second memory of the earlier refusal
        Assert.True((await h.Replica().TryReserveAsync(Id(3))).Success);
        Assert.Equal([Id(3)], (await operatorLedger.ListUnresolvedAsync()).Select(u => u.Id));
    }

    // ------------------------------------- reconciliation scope and independent periods

    [Fact]
    public async Task Disowning_the_pending_call_keeps_and_prices_what_earlier_calls_confirmed()
    {
        var (h, ledger) = await StartAsync();
        await ledger.TryReserveAsync(Id(1));
        await ledger.TryBeginCallAsync(Id(1), 1000, 100);
        await ledger.CompleteCallAsync(Id(1), new ModelUsage(1000, 100));
        await ledger.TryBeginCallAsync(Id(1), 100, 10);
        await ledger.AbandonAsync(Id(1));
        await ledger.TryReserveAsync(Id(2));

        Assert.Equal(ModelControlOutcome.Applied, await h.Replica().ReconcileAsync(Id(1), ReconciliationResolution.NotDispatched));

        var stored = (await h.Raw.Fields(h.Raw.Reservation(Id(1))))!;
        Assert.Equal("Settled", (string)stored["state"]);
        Assert.Equal(140L, (long)stored["chargedMicroUsd"]);
        Assert.False((bool)stored["callInFlight"]);
        Assert.Contains("expiresAt", stored.Keys);
        Assert.Equal(140 + Worst, await h.Raw.Charged(h.Raw.Day("2026-10-08")));
        Assert.Equal(140 + Worst, await h.Raw.Charged(h.Raw.Month("2026-10")));
        Assert.Equal(1, await h.Raw.PermitCount());
    }

    [Fact]
    public async Task Disowning_a_call_when_none_is_pending_but_earlier_calls_completed_is_refused()
    {
        var (h, ledger) = await StartAsync();
        await ledger.TryReserveAsync(Id(1));
        await ledger.TryBeginCallAsync(Id(1), 1000, 100);
        await ledger.CompleteCallAsync(Id(1), new ModelUsage(1000, 100));
        await ledger.AbandonAsync(Id(1));

        Assert.Equal(ModelControlOutcome.InvalidState, await h.Replica().ReconcileAsync(Id(1), ReconciliationResolution.NotDispatched));

        Assert.Equal("Uncertain", await h.Raw.State(Id(1)));
        Assert.Equal(Worst, await h.Raw.Charged(h.Raw.Day("2026-10-08")));
        Assert.Equal(Worst, await h.Raw.Charged(h.Raw.Month("2026-10")));
        Assert.Equal(1, await h.Raw.PermitCount());
        Assert.DoesNotContain("expiresAt", (await h.Raw.Fields(h.Raw.Reservation(Id(1))))!.Keys);
    }

    private async Task<(Harness H, FirestoreModelControlLedger Ledger)> AnOctoberCallInLateNovember()
    {
        var (h, ledger) = await StartAsync();
        await ledger.TryReserveAsync(Id(1));
        await ledger.TryBeginCallAsync(Id(1), 1000, 100);
        h.Clock.Now = new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);
        Assert.True((await ledger.TryReserveAsync(Id(2))).Success); // creates the current November counters
        await h.Raw.Day("2026-10-08").DeleteAsync(); // expired October 9 + 40 days; October's month is not
        return (h, ledger);
    }

    [Fact]
    public async Task An_expired_day_counter_is_skipped_and_the_live_month_counter_is_still_reconciled()
    {
        var (h, _) = await AnOctoberCallInLateNovember();

        Assert.Equal(ModelControlOutcome.Applied,
            await h.Replica().ReconcileAsync(Id(1), ReconciliationResolution.Completed, new ModelUsage(1000, 100)));

        Assert.Equal(140L, await h.Raw.Charged(h.Raw.Month("2026-10")));
        Assert.Null(await h.Raw.Fields(h.Raw.Day("2026-10-08"))); // never recreated
        Assert.Equal(Worst, await h.Raw.Charged(h.Raw.Day("2026-11-25"))); // current periods untouched
        Assert.Equal(Worst, await h.Raw.Charged(h.Raw.Month("2026-11")));
        Assert.Equal("Settled", await h.Raw.State(Id(1)));
        Assert.Equal(1, await h.Raw.PermitCount());
    }

    [Fact]
    public async Task Disowning_a_call_with_an_expired_day_counter_refunds_the_live_month_counter_only()
    {
        var (h, _) = await AnOctoberCallInLateNovember();

        Assert.Equal(ModelControlOutcome.Applied, await h.Replica().ReconcileAsync(Id(1), ReconciliationResolution.NotDispatched));

        Assert.Equal(0L, await h.Raw.Charged(h.Raw.Month("2026-10")));
        Assert.Null(await h.Raw.Fields(h.Raw.Day("2026-10-08")));
        Assert.Equal(Worst, await h.Raw.Charged(h.Raw.Month("2026-11")));
        Assert.Equal("Cancelled", await h.Raw.State(Id(1)));
    }

    [Fact]
    public async Task A_missing_unexpired_or_corrupt_counter_fails_closed_without_mutation()
    {
        var (h, _) = await AnOctoberCallInLateNovember();

        // The remaining October counter is corrupt: nothing is released or changed.
        await h.Raw.Month("2026-10").UpdateAsync("chargedMicroUsd", -5L);
        Assert.Equal(ModelControlOutcome.StateInvalid,
            await h.Replica().ReconcileAsync(Id(1), ReconciliationResolution.Completed, new ModelUsage(1000, 100)));
        Assert.Equal(-5L, await h.Raw.Charged(h.Raw.Month("2026-10")));
        Assert.Null(await h.Raw.Fields(h.Raw.Day("2026-10-08")));
        Assert.Equal(2, await h.Raw.PermitCount());
        Assert.True((bool)(await h.Raw.Fields(h.Raw.Reservation(Id(1))))!["callInFlight"]);

        // The month counter is gone while its period is still live: also fail closed.
        await h.Raw.Month("2026-10").DeleteAsync();
        Assert.Equal(ModelControlOutcome.StateInvalid, await h.Replica().ReconcileAsync(Id(1), ReconciliationResolution.NotDispatched));
        Assert.Equal(2, await h.Raw.PermitCount());
        Assert.Equal(Worst, await h.Raw.Charged(h.Raw.Day("2026-11-25")));
        Assert.Equal("Uncertain", await h.Raw.State(Id(1)));
    }

    [Fact]
    public async Task Cancelling_before_any_call_refunds_the_whole_reservation()
    {
        var (h, ledger) = await StartAsync();
        await ledger.TryReserveAsync(Id(1));

        Assert.Equal(ModelControlOutcome.Applied, await ledger.CancelUndispatchedAsync(Id(1)));

        Assert.Equal(0, await h.Raw.Charged(h.Raw.Day("2026-10-08")));
        Assert.Equal(0, await h.Raw.PermitCount());
        Assert.Equal("Cancelled", await h.Raw.State(Id(1)));
    }

    [Fact]
    public async Task Cancelling_after_a_call_began_refunds_nothing()
    {
        var (h, ledger) = await StartAsync();
        await ledger.TryReserveAsync(Id(1));
        await ledger.TryBeginCallAsync(Id(1), 1000, 100);

        Assert.Equal(ModelControlOutcome.InvalidState, await ledger.CancelUndispatchedAsync(Id(1)));

        Assert.Equal(Worst, await h.Raw.Charged(h.Raw.Day("2026-10-08")));
        Assert.Equal(1, await h.Raw.PermitCount());
    }

    // ------------------------------------------------------------ capacity, cleanup, reconcile

    [Fact]
    public async Task Capacity_is_bounded()
    {
        var (h, ledger) = await StartAsync();
        await h.Raw.Root.UpdateAsync("liveReservations", (long)ModelControlPolicy.ApprovedMaxLiveReservations);

        var result = await ledger.TryReserveAsync(Id(1));

        Assert.Equal(ModelReservationDenialReason.CapacityExhausted, result.DenialReason);
        Assert.Equal(0, await h.Raw.ReservationDocuments());
    }

    [Fact]
    public async Task Cleanup_removes_only_expired_inactive_metadata_and_lowers_the_live_count()
    {
        var (h, ledger) = await StartAsync();
        await ledger.TryReserveAsync(Id(1));
        await ledger.TryBeginCallAsync(Id(1), 1000, 100);
        await ledger.CompleteCallAsync(Id(1), new ModelUsage(1000, 100));
        await ledger.SettleAsync(Id(1));
        await ledger.TryReserveAsync(Id(2));

        Assert.Equal(0, await ledger.CleanupExpiredAsync()); // nothing has expired yet

        h.Clock.Now = new DateTimeOffset(2026, 12, 12, 0, 0, 0, TimeSpan.Zero); // past month end + 40 days
        var removed = await ledger.CleanupExpiredAsync();

        Assert.True(removed >= 3); // the settled reservation and both counters (at least)
        Assert.Null(await h.Raw.Fields(h.Raw.Reservation(Id(1))));
        Assert.NotNull(await h.Raw.Fields(h.Raw.Reservation(Id(2)))); // still Active: never erased
        Assert.Null(await h.Raw.Fields(h.Raw.Day("2026-10-08")));
        Assert.Null(await h.Raw.Fields(h.Raw.Month("2026-10")));
        Assert.Equal(1L, (await h.Raw.Fields(h.Raw.Root))!["liveReservations"]);
    }

    [Fact]
    public async Task A_full_store_reclaims_expired_metadata_once_and_then_admits()
    {
        var (h, ledger) = await StartAsync();
        await ledger.TryReserveAsync(Id(1));
        await ledger.CancelUndispatchedAsync(Id(1));
        await h.Raw.Root.UpdateAsync("liveReservations", (long)ModelControlPolicy.ApprovedMaxLiveReservations);
        Assert.Equal(ModelReservationDenialReason.CapacityExhausted, (await ledger.TryReserveAsync(Id(2))).DenialReason);

        h.Clock.Now = new DateTimeOffset(2026, 12, 12, 0, 0, 0, TimeSpan.Zero);
        var result = await ledger.TryReserveAsync(Id(3));

        Assert.True(result.Success);
        Assert.Null(await h.Raw.Fields(h.Raw.Reservation(Id(1))));
        Assert.Equal(ModelControlPolicy.ApprovedMaxLiveReservations - 1 + 1, (int)(long)(await h.Raw.Fields(h.Raw.Root))!["liveReservations"]);
    }

    [Fact]
    public async Task Reconciliation_sets_the_live_count_from_the_observed_collection()
    {
        var (h, ledger) = await StartAsync();
        await ledger.TryReserveAsync(Id(1));
        await ledger.AbandonAsync(Id(1));
        await h.Raw.Root.UpdateAsync("liveReservations", 1500L);

        Assert.Equal(CapacityReconciliation.Applied, await ledger.ReconcileCapacityAsync());

        Assert.Equal(1L, (await h.Raw.Fields(h.Raw.Root))!["liveReservations"]);
    }

    [Fact]
    public async Task Capacity_reconciliation_never_overwrites_an_admission_that_happens_while_counting()
    {
        // Reviewed defect: documents were counted outside the transaction and the stale total then
        // overwrote LiveReservations, losing a concurrent admission. The hook admits a reservation
        // at exactly that point: after counting, before the guarded write.
        var (h, ledger) = await StartAsync();
        await ledger.TryReserveAsync(Id(1));
        var admitter = h.Replica();
        var reconciler = new FirestoreModelControlLedger(
            h.NewClientForTests(), h.Policy, h.Clock, afterCapacityCount: async _ =>
            {
                Assert.True((await admitter.TryReserveAsync(Id(2))).Success);
            });

        var first = await reconciler.ReconcileCapacityAsync();

        Assert.Equal(CapacityReconciliation.ConcurrentChange, first);
        Assert.Equal(2L, (await h.Raw.Fields(h.Raw.Root))!["liveReservations"]); // the admission survived
        Assert.Equal(2, await h.Raw.ReservationDocuments());

        Assert.Equal(CapacityReconciliation.Applied, await h.Replica().ReconcileCapacityAsync());
        Assert.Equal(2L, (await h.Raw.Fields(h.Raw.Root))!["liveReservations"]);
    }

    [Fact]
    public async Task The_capacity_ceiling_still_holds_when_reconciliation_races_the_last_admission()
    {
        var (h, ledger) = await StartAsync();
        var ceiling = ModelControlPolicy.ApprovedMaxLiveReservations;
        await h.Raw.Root.UpdateAsync("liveReservations", (long)(ceiling - 1)); // one slot left
        var admitter = h.Replica();
        var reconciler = new FirestoreModelControlLedger(
            h.NewClientForTests(), h.Policy, h.Clock, afterCapacityCount: async _ =>
            {
                Assert.True((await admitter.TryReserveAsync(Id(1))).Success); // takes the last slot
            });

        Assert.Equal(CapacityReconciliation.ConcurrentChange, await reconciler.ReconcileCapacityAsync());

        Assert.Equal((long)ceiling, (await h.Raw.Fields(h.Raw.Root))!["liveReservations"]);
        h.Clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(ModelReservationDenialReason.CapacityExhausted, (await ledger.TryReserveAsync(Id(2))).DenialReason);
        Assert.Equal(1, await h.Raw.ReservationDocuments());
    }

    [Fact]
    public async Task Cleanup_advances_the_epoch_so_a_concurrent_reconciliation_cannot_overwrite_it()
    {
        var (h, ledger) = await StartAsync();
        await ledger.TryReserveAsync(Id(1));
        await ledger.CancelUndispatchedAsync(Id(1));
        h.Clock.Now = new DateTimeOffset(2026, 12, 12, 0, 0, 0, TimeSpan.Zero);
        var reconciler = new FirestoreModelControlLedger(
            h.NewClientForTests(), h.Policy, h.Clock, afterCapacityCount: async _ =>
            {
                Assert.True(await h.Replica().CleanupExpiredAsync() > 0);
            });

        Assert.Equal(CapacityReconciliation.ConcurrentChange, await reconciler.ReconcileCapacityAsync());
        Assert.Equal(0L, (await h.Raw.Fields(h.Raw.Root))!["liveReservations"]);
    }

    [Fact]
    public async Task Cleanup_never_removes_unresolved_reservations_even_when_a_stray_expiry_is_present()
    {
        var (h, ledger) = await StartAsync();
        await ledger.TryReserveAsync(Id(1)); // Active
        await ledger.TryReserveAsync(Id(2));
        await ledger.AbandonAsync(Id(2)); // Uncertain
        var past = Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        await h.Raw.Reservation(Id(1)).UpdateAsync("expiresAt", past);
        await h.Raw.Reservation(Id(2)).UpdateAsync("expiresAt", past);
        h.Clock.Now = new DateTimeOffset(2027, 6, 1, 0, 0, 0, TimeSpan.Zero);

        await ledger.CleanupExpiredAsync();

        Assert.NotNull(await h.Raw.Fields(h.Raw.Reservation(Id(1))));
        Assert.NotNull(await h.Raw.Fields(h.Raw.Reservation(Id(2))));
        Assert.Equal(2L, (await h.Raw.Fields(h.Raw.Root))!["liveReservations"]);
    }

    // ---------------------------------------------------------------------------- store failures

    [Fact]
    public async Task An_unreachable_store_denies_every_operation_within_the_deadline()
    {
        var unreachable = new FirestoreDbBuilder
        {
            ProjectId = FirestoreEmulatorFixture.UniqueProject(),
            Endpoint = "127.0.0.1:1",
            ChannelCredentials = ChannelCredentials.Insecure,
            EmulatorDetection = Google.Api.Gax.EmulatorDetection.None
        }.Build();
        var ledger = new FirestoreModelControlLedger(unreachable, Fixtures.Policy(), new TestClock(Noon), TimeSpan.FromSeconds(3));
        var started = DateTime.UtcNow;

        Assert.Equal(ModelReservationDenialReason.StoreUnavailable, (await ledger.TryReserveAsync(Id(1))).DenialReason);
        Assert.Equal(ModelCallDenialReason.StoreUnavailable, (await ledger.TryBeginCallAsync(Id(1), 10, 10)).DenialReason);
        Assert.Equal(ModelControlOutcome.StoreUnavailable, await ledger.CompleteCallAsync(Id(1), new ModelUsage(10, 10)));
        Assert.Equal(ModelControlOutcome.StoreUnavailable, await ledger.SettleAsync(Id(1)));
        Assert.Equal(ModelControlOutcome.StoreUnavailable, await ledger.CancelUndispatchedAsync(Id(1)));
        Assert.Equal(ModelControlOutcome.StoreUnavailable, await ledger.AbandonAsync(Id(1)));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(30), "Operations must be bounded by their deadline.");
    }

    [Fact]
    public async Task A_caller_cancellation_is_not_reported_as_a_grant()
    {
        var (h, ledger) = await StartAsync();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<Exception>(async () => await ledger.TryReserveAsync(Id(1), cancelled.Token));

        // Whatever happened, the ledger never recorded more than one reservation for the id.
        Assert.InRange(await h.Raw.ReservationDocuments(), 0, 1);
    }
}
