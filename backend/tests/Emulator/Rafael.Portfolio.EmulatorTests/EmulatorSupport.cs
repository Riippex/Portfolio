using Google.Cloud.Firestore;
using Rafael.Portfolio.Modules.Assistant.Application;
using Rafael.Portfolio.Modules.Assistant.Infrastructure;

namespace Rafael.Portfolio.EmulatorTests;

/// <summary>
/// Raised when the Firestore emulator is not available. These tests never skip or pass without
/// it: an absent emulator makes every test fail with this message, so the check is recorded as
/// INCOMPLETE instead of silently green.
/// </summary>
public sealed class EmulatorUnavailableException(string message) : Exception(message);

/// <summary>
/// Connects only to an emulator named by FIRESTORE_EMULATOR_HOST, never to Google Cloud. Each
/// test uses a unique throwaway project id, which the emulator isolates, so no production
/// project, credential or shared state is involved.
/// </summary>
public sealed class FirestoreEmulatorFixture
{
    public const string HostVariable = "FIRESTORE_EMULATOR_HOST";

    public FirestoreEmulatorFixture()
    {
        Host = Environment.GetEnvironmentVariable(HostVariable) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(Host))
        {
            throw new EmulatorUnavailableException(
                $"{HostVariable} is not set: the Firestore emulator checks are INCOMPLETE (not passed). " +
                "Start the emulator and set the variable; see docs/runbooks/model-control.md.");
        }

        if (Host.Contains("googleapis.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new EmulatorUnavailableException($"{HostVariable} must name a local emulator, not a Google endpoint.");
        }

        try
        {
            var probe = NewClient(UniqueProject());
            probe.Collection("probe").Document("probe").GetSnapshotAsync(new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token)
                .GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            throw new EmulatorUnavailableException(
                $"The Firestore emulator at {Host} is not reachable ({exception.GetType().Name}): the checks are INCOMPLETE (not passed).");
        }
    }

    public string Host { get; }

    public static string UniqueProject() => $"emulator-{Guid.NewGuid():N}";

    /// <summary>A new client, as a separate replica or a restarted process would create.</summary>
    public FirestoreDb NewClient(string projectId) =>
        new FirestoreDbBuilder
        {
            ProjectId = projectId,
            EmulatorDetection = Google.Api.Gax.EmulatorDetection.EmulatorOnly
        }.Build();
}

public sealed class TestClock(DateTimeOffset now) : TimeProvider
{
    private readonly Lock _lock = new();
    private DateTimeOffset _now = now;

    public DateTimeOffset Now
    {
        get
        {
            lock (_lock)
            {
                return _now;
            }
        }
        set
        {
            lock (_lock)
            {
                _now = value;
            }
        }
    }

    public void Advance(TimeSpan by) => Now += by;

    public override DateTimeOffset GetUtcNow() => Now;
}

public static class Fixtures
{
    // 6,000 input at 100,000 and 600 output at 400,000 micro-USD per million tokens: 600 + 240.
    public const long WorstCase = 840;

    public static readonly DateTimeOffset Noon = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    public const string PolicyVersion = "policy-1";

    public static ModelTariff Tariff(DateOnly? validThrough = null, string version = "tariff-1") =>
        new(version, "gemini-3.1-flash-lite", 100_000, 400_000, validThrough ?? new DateOnly(2099, 1, 1));

    public static ModelControlPolicy Policy(long? daily = null, long? monthly = null, ModelTariff? tariff = null) =>
        ModelControlPolicy.Approved(PolicyVersion, tariff ?? Tariff()) with
        {
            DailyBudgetMicroUsd = daily ?? ModelControlPolicy.ApprovedDailyBudgetMicroUsd,
            MonthlyBudgetMicroUsd = monthly ?? ModelControlPolicy.ApprovedMonthlyBudgetMicroUsd
        };

    public static string Id(int n) => $"res_{n:D8}";
}

/// <summary>Raw access to the stored documents, independent of the ledger under test.</summary>
public sealed class RawStore(FirestoreDb db)
{
    public DocumentReference Root => db.Collection(FirestoreModelControlLedger.ControlCollection).Document(FirestoreModelControlLedger.ControlDocument);

    public DocumentReference Day(string key) => db.Collection(FirestoreModelControlLedger.PeriodsCollection).Document("day_" + key);

    public DocumentReference Month(string key) => db.Collection(FirestoreModelControlLedger.PeriodsCollection).Document("month_" + key);

    public DocumentReference Reservation(string id) => db.Collection(FirestoreModelControlLedger.ReservationsCollection).Document(id);

    public async Task<IDictionary<string, object>?> Fields(DocumentReference reference)
    {
        var snapshot = await reference.GetSnapshotAsync();
        return snapshot.Exists ? snapshot.ToDictionary() : null;
    }

    public async Task<long> Charged(DocumentReference counter) =>
        (long)(await Fields(counter))!["chargedMicroUsd"];

    public async Task<int> ReservationDocuments() =>
        (await db.Collection(FirestoreModelControlLedger.ReservationsCollection).GetSnapshotAsync()).Count;

    public async Task<int> PermitCount()
    {
        var fields = await Fields(Root);
        return ((IDictionary<string, object>)fields!["permits"]).Count;
    }

    public async Task<string> State(string id) => (string)(await Fields(Reservation(id)))!["state"];
}
