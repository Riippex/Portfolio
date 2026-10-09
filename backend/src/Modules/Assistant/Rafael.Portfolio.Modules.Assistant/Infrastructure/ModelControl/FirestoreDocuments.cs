using Google.Cloud.Firestore;
using Rafael.Portfolio.Modules.Assistant.Application;

namespace Rafael.Portfolio.Modules.Assistant.Infrastructure;

/// <summary>Raised when a stored document cannot be interpreted. Callers deny and change nothing.</summary>
internal sealed class StoredStateInvalidException(string message) : Exception(message);

/// <summary>
/// The mapping between ledger records and Firestore documents. Every document is validated
/// before it is interpreted: the schema version must be exactly the supported one, every field
/// must have its exact type, and numbers are never narrowed or wrapped. Nothing here repairs
/// or defaults a value.
/// </summary>
internal static class FirestoreDocuments
{
    public static IDictionary<string, object> ToFields(LedgerRoot root) => new Dictionary<string, object>
    {
        ["schemaVersion"] = (long)root.SchemaVersion,
        ["policyVersion"] = root.PolicyVersion,
        ["lastDayKey"] = root.LastDayKey,
        ["lastMonthKey"] = root.LastMonthKey,
        ["permits"] = root.Permits.ToDictionary(p => p.Key, p => (object)Timestamp.FromDateTimeOffset(p.Value)),
        ["liveReservations"] = (long)root.LiveReservations,
        ["epoch"] = root.Epoch
    };

    public static IDictionary<string, object> ToFields(PeriodCounter counter, string kind) => new Dictionary<string, object>
    {
        ["schemaVersion"] = LedgerRoot.SupportedSchemaVersion,
        ["kind"] = kind,
        ["key"] = counter.Key,
        ["chargedMicroUsd"] = counter.ChargedMicroUsd,
        ["expiresAt"] = Timestamp.FromDateTimeOffset(counter.ExpiresAt)
    };

    /// <summary>
    /// The expiresAt field is written only for RESOLVED reservations. Firestore TTL deletes only
    /// documents that carry the field, so an Active or Uncertain reservation can never be
    /// removed by TTL.
    /// </summary>
    public static IDictionary<string, object> ToFields(ReservationRecord record)
    {
        var fields = new Dictionary<string, object>
        {
            ["schemaVersion"] = LedgerRoot.SupportedSchemaVersion,
            ["id"] = record.Id,
            ["state"] = record.State.ToString(),
            ["policyVersion"] = record.PolicyVersion,
            ["tariffVersion"] = record.TariffVersion,
            ["modelId"] = record.ModelId,
            ["dayKey"] = record.DayKey,
            ["monthKey"] = record.MonthKey,
            ["reservedMicroUsd"] = record.ReservedMicroUsd,
            ["chargedMicroUsd"] = record.ChargedMicroUsd,
            ["maxCalls"] = (long)record.MaxCalls,
            ["maxInputTokens"] = (long)record.MaxInputTokens,
            ["maxOutputTokens"] = (long)record.MaxOutputTokens,
            ["callsStarted"] = (long)record.CallsStarted,
            ["inputTokensUsed"] = record.InputTokensUsed,
            ["outputTokensUsed"] = record.OutputTokensUsed,
            ["callInFlight"] = record.CallInFlight,
            ["pendingInputTokens"] = (long)record.PendingInputTokens,
            ["pendingOutputTokens"] = (long)record.PendingOutputTokens,
            ["createdAt"] = Timestamp.FromDateTimeOffset(record.CreatedAt),
            ["updatedAt"] = Timestamp.FromDateTimeOffset(record.UpdatedAt)
        };

        if (record.ExpiresAt is { } expiresAt)
        {
            fields["expiresAt"] = Timestamp.FromDateTimeOffset(expiresAt);
        }

        return fields;
    }

    public static LedgerRoot ParseRoot(IDictionary<string, object> fields)
    {
        RequireSupportedSchema(fields);

        var permits = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        foreach (var (id, value) in Field<IDictionary<string, object>>(fields, "permits"))
        {
            permits[id] = value is Timestamp timestamp
                ? timestamp.ToDateTimeOffset()
                : throw new StoredStateInvalidException("A permit lease is not a timestamp.");
        }

        return new LedgerRoot(
            (int)LedgerRoot.SupportedSchemaVersion,
            Field<string>(fields, "policyVersion"),
            Field<string>(fields, "lastDayKey"),
            Field<string>(fields, "lastMonthKey"),
            permits,
            Int32(fields, "liveReservations"),
            Field<long>(fields, "epoch"));
    }

    public static PeriodCounter ParseCounter(IDictionary<string, object> fields, string kind)
    {
        RequireSupportedSchema(fields);

        if (!string.Equals(Field<string>(fields, "kind"), kind, StringComparison.Ordinal))
        {
            throw new StoredStateInvalidException("A period counter has the wrong kind.");
        }

        return new PeriodCounter(
            Field<string>(fields, "key"),
            Field<long>(fields, "chargedMicroUsd"),
            Field<Timestamp>(fields, "expiresAt").ToDateTimeOffset());
    }

    public static ReservationRecord ParseReservation(string documentId, IDictionary<string, object> fields)
    {
        RequireSupportedSchema(fields);

        if (!Enum.TryParse<ReservationState>(Field<string>(fields, "state"), ignoreCase: false, out var state) ||
            !Enum.IsDefined(state))
        {
            throw new StoredStateInvalidException("A reservation has an unknown state.");
        }

        DateTimeOffset? expiresAt = null;
        if (fields.TryGetValue("expiresAt", out var rawExpiry))
        {
            expiresAt = rawExpiry is Timestamp timestamp
                ? timestamp.ToDateTimeOffset()
                : throw new StoredStateInvalidException("A reservation expiry is not a timestamp.");
        }

        var record = new ReservationRecord(
            Field<string>(fields, "id"),
            state,
            Field<string>(fields, "policyVersion"),
            Field<string>(fields, "tariffVersion"),
            Field<string>(fields, "modelId"),
            Field<string>(fields, "dayKey"),
            Field<string>(fields, "monthKey"),
            Field<long>(fields, "reservedMicroUsd"),
            Field<long>(fields, "chargedMicroUsd"),
            Int32(fields, "maxCalls"),
            Int32(fields, "maxInputTokens"),
            Int32(fields, "maxOutputTokens"),
            Int32(fields, "callsStarted"),
            Field<long>(fields, "inputTokensUsed"),
            Field<long>(fields, "outputTokensUsed"),
            Field<bool>(fields, "callInFlight"),
            Int32(fields, "pendingInputTokens"),
            Int32(fields, "pendingOutputTokens"),
            Field<Timestamp>(fields, "createdAt").ToDateTimeOffset(),
            Field<Timestamp>(fields, "updatedAt").ToDateTimeOffset(),
            expiresAt);

        if (!string.Equals(record.Id, documentId, StringComparison.Ordinal))
        {
            throw new StoredStateInvalidException("A reservation id does not match its document.");
        }

        return record;
    }

    /// <summary>
    /// The schema version must be present, a stored integer, and exactly the supported value.
    /// Missing, malformed (wrong type, fractional), unknown and out-of-range values are all
    /// rejected before any other field is read, so a value that would wrap to the supported
    /// one when narrowed (for example 4294967297) is refused.
    /// </summary>
    public static void RequireSupportedSchema(IDictionary<string, object> fields)
    {
        if (!fields.TryGetValue("schemaVersion", out var value) ||
            value is not long version ||
            version != LedgerRoot.SupportedSchemaVersion)
        {
            throw new StoredStateInvalidException("The stored schema version is missing, malformed or unsupported.");
        }
    }

    private static int Int32(IDictionary<string, object> fields, string name)
    {
        var value = Field<long>(fields, name);
        return value is >= int.MinValue and <= int.MaxValue
            ? (int)value
            : throw new StoredStateInvalidException($"Stored field '{name}' is out of range.");
    }

    private static T Field<T>(IDictionary<string, object> fields, string name) =>
        fields.TryGetValue(name, out var value) && value is T typed
            ? typed
            : throw new StoredStateInvalidException($"Stored field '{name}' is missing or has the wrong type.");
}
