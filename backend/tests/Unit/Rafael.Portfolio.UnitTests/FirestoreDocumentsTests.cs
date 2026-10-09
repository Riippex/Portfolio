using Google.Cloud.Firestore;
using Rafael.Portfolio.Modules.Assistant.Application;
using Rafael.Portfolio.Modules.Assistant.Infrastructure;

namespace Rafael.Portfolio.UnitTests;

// The storage mapping is validated before anything is interpreted. These tests feed the parser
// the dictionaries Firestore would return, so schema and type rules are proven without a store.
public sealed class FirestoreDocumentsTests
{
    private static readonly DateTimeOffset Noon = ModelControlFixtures.Noon;

    private static LedgerRoot Root() => new(
        LedgerRoot.CurrentSchemaVersion,
        "policy-1",
        "2026-10-08",
        "2026-10",
        new Dictionary<string, DateTimeOffset> { ["res_00000001"] = Noon.AddMinutes(5) },
        3,
        7);

    private static ReservationRecord Active() => new(
        "res_00000001", ReservationState.Active, "policy-1", "tariff-1", "model-1", "2026-10-08", "2026-10",
        840, 840, 2, 6000, 600, 0, 0, 0, false, 0, 0, Noon, Noon, ExpiresAt: null);

    private static IDictionary<string, object> Fields(LedgerRoot root) => FirestoreDocuments.ToFields(root);

    public static TheoryData<string, object?> UnsupportedSchemaVersions() => new()
    {
        { "missing", null },
        { "zero", 0L },
        { "two", 2L },
        { "negative", -1L },
        { "wraps to one when narrowed to 32 bits", 4294967297L },
        { "wraps to one when narrowed to 16 bits", 65537L },
        { "long max", long.MaxValue },
        { "long min", long.MinValue },
        { "int, not a stored long", 1 },
        { "double one", 1.0 },
        { "string one", "1" },
        { "boolean", true },
        { "decimal", 1.0m },
        { "timestamp", Timestamp.FromDateTimeOffset(Noon) }
    };

    private static void SetSchema(IDictionary<string, object> fields, object? version)
    {
        if (version is null)
        {
            fields.Remove("schemaVersion");
        }
        else
        {
            fields["schemaVersion"] = version;
        }
    }

    [Fact]
    public void Valid_documents_round_trip_exactly()
    {
        var root = Root();
        var parsed = FirestoreDocuments.ParseRoot(Fields(root));
        Assert.Equal(root.PolicyVersion, parsed.PolicyVersion);
        Assert.Equal(root.Permits, parsed.Permits);
        Assert.Equal((root.LiveReservations, root.Epoch), (parsed.LiveReservations, parsed.Epoch));

        var counter = new PeriodCounter("2026-10-08", 840, Noon.AddDays(40));
        Assert.Equal(counter, FirestoreDocuments.ParseCounter(FirestoreDocuments.ToFields(counter, "day"), "day"));

        var record = Active();
        Assert.Equal(record, FirestoreDocuments.ParseReservation(record.Id, FirestoreDocuments.ToFields(record)));
    }

    [Theory]
    [MemberData(nameof(UnsupportedSchemaVersions))]
    public void The_control_document_rejects_any_schema_version_but_the_supported_one(string label, object? version)
    {
        var fields = Fields(Root());
        SetSchema(fields, version);

        Assert.Throws<StoredStateInvalidException>(() => FirestoreDocuments.ParseRoot(fields));
        Assert.NotEmpty(label);
    }

    [Theory]
    [MemberData(nameof(UnsupportedSchemaVersions))]
    public void A_period_counter_rejects_any_schema_version_but_the_supported_one(string label, object? version)
    {
        var fields = FirestoreDocuments.ToFields(new PeriodCounter("2026-10-08", 1, Noon), "day");
        SetSchema(fields, version);

        Assert.Throws<StoredStateInvalidException>(() => FirestoreDocuments.ParseCounter(fields, "day"));
        Assert.NotEmpty(label);
    }

    [Theory]
    [MemberData(nameof(UnsupportedSchemaVersions))]
    public void A_reservation_rejects_any_schema_version_but_the_supported_one(string label, object? version)
    {
        var fields = FirestoreDocuments.ToFields(Active());
        SetSchema(fields, version);

        Assert.Throws<StoredStateInvalidException>(() => FirestoreDocuments.ParseReservation("res_00000001", fields));
        Assert.NotEmpty(label);
    }

    [Fact]
    public void The_schema_version_is_checked_before_any_other_field_is_read()
    {
        // Every other field is garbage; the schema error is what is reported, and nothing else is touched.
        var fields = new Dictionary<string, object> { ["schemaVersion"] = 4294967297L, ["state"] = new object() };

        var error = Assert.Throws<StoredStateInvalidException>(() => FirestoreDocuments.ParseReservation("res_00000001", fields));

        Assert.Contains("schema version", error.Message);
    }

    [Theory]
    [InlineData(4294967297L)]
    [InlineData(2147483648L)]
    [InlineData(-2147483649L)]
    [InlineData(long.MaxValue)]
    public void Counts_that_do_not_fit_are_rejected_instead_of_clamped_or_wrapped(long value)
    {
        var rootFields = Fields(Root());
        rootFields["liveReservations"] = value;
        Assert.Throws<StoredStateInvalidException>(() => FirestoreDocuments.ParseRoot(rootFields));

        var reservationFields = FirestoreDocuments.ToFields(Active());
        reservationFields["callsStarted"] = value;
        Assert.Throws<StoredStateInvalidException>(() => FirestoreDocuments.ParseReservation("res_00000001", reservationFields));
    }

    [Theory]
    [InlineData("policyVersion", 5L)]
    [InlineData("lastDayKey", null)]
    [InlineData("liveReservations", "3")]
    [InlineData("liveReservations", 3.0)]
    [InlineData("epoch", null)]
    [InlineData("epoch", "7")]
    [InlineData("permits", "none")]
    [InlineData("permits", null)]
    public void The_control_document_requires_every_field_with_its_exact_type(string field, object? value)
    {
        var fields = Fields(Root());
        if (value is null)
        {
            fields.Remove(field);
        }
        else
        {
            fields[field] = value;
        }

        Assert.Throws<StoredStateInvalidException>(() => FirestoreDocuments.ParseRoot(fields));
    }

    [Fact]
    public void A_permit_lease_must_be_a_timestamp()
    {
        var fields = Fields(Root());
        fields["permits"] = new Dictionary<string, object> { ["res_00000001"] = "tomorrow" };

        Assert.Throws<StoredStateInvalidException>(() => FirestoreDocuments.ParseRoot(fields));
    }

    [Fact]
    public void Unknown_states_mismatched_ids_and_wrong_counter_kinds_are_rejected()
    {
        var fields = FirestoreDocuments.ToFields(Active());
        fields["state"] = "Bogus";
        Assert.Throws<StoredStateInvalidException>(() => FirestoreDocuments.ParseReservation("res_00000001", fields));

        fields = FirestoreDocuments.ToFields(Active());
        fields["state"] = "active"; // case-sensitive
        Assert.Throws<StoredStateInvalidException>(() => FirestoreDocuments.ParseReservation("res_00000001", fields));

        Assert.Throws<StoredStateInvalidException>(() =>
            FirestoreDocuments.ParseReservation("res_99999999", FirestoreDocuments.ToFields(Active())));

        var counter = FirestoreDocuments.ToFields(new PeriodCounter("2026-10", 1, Noon), "month");
        Assert.Throws<StoredStateInvalidException>(() => FirestoreDocuments.ParseCounter(counter, "day"));
    }

    // ------------------------------------------------------------------------ state-aware expiry

    [Theory]
    [InlineData(ReservationState.Active)]
    [InlineData(ReservationState.Uncertain)]
    public void Unresolved_reservations_are_stored_without_an_expiry_so_ttl_cannot_delete_them(ReservationState state)
    {
        var fields = FirestoreDocuments.ToFields(Active() with { State = state });

        Assert.DoesNotContain("expiresAt", fields.Keys);
    }

    [Theory]
    [InlineData(ReservationState.Settled)]
    [InlineData(ReservationState.Cancelled)]
    [InlineData(ReservationState.Lapsed)]
    public void Resolved_reservations_carry_an_expiry(ReservationState state)
    {
        var expiry = new DateTimeOffset(2026, 12, 11, 0, 0, 0, TimeSpan.Zero);

        var fields = FirestoreDocuments.ToFields(Active() with { State = state, ExpiresAt = expiry });

        Assert.Equal(expiry, ((Timestamp)fields["expiresAt"]).ToDateTimeOffset());
        Assert.Equal(expiry, FirestoreDocuments.ParseReservation("res_00000001", fields).ExpiresAt);
    }

    [Fact]
    public void A_stored_expiry_must_be_a_timestamp()
    {
        var fields = FirestoreDocuments.ToFields(Active() with { State = ReservationState.Settled, ExpiresAt = Noon });
        fields["expiresAt"] = "2026-12-11";

        Assert.Throws<StoredStateInvalidException>(() => FirestoreDocuments.ParseReservation("res_00000001", fields));
    }

    [Theory]
    [InlineData(ReservationState.Active, true)]
    [InlineData(ReservationState.Uncertain, true)]
    [InlineData(ReservationState.Settled, false)]
    [InlineData(ReservationState.Cancelled, false)]
    [InlineData(ReservationState.Lapsed, false)]
    public void An_expiry_on_an_unresolved_reservation_or_a_missing_one_on_a_resolved_reservation_is_corrupt(
        ReservationState state,
        bool unresolved)
    {
        var withExpiry = Active() with { State = state, ExpiresAt = Noon.AddDays(40) };
        var withoutExpiry = Active() with { State = state, ExpiresAt = null };

        Assert.Equal(!unresolved, ModelControlAccounting.IsSane(withExpiry));
        Assert.Equal(unresolved, ModelControlAccounting.IsSane(withoutExpiry));
    }
}
