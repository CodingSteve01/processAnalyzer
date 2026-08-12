using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using ProcessAnalyzer.Web.Data;

namespace ProcessAnalyzer.Web.Sync;

// Bulk insert path of the mirror (see JournalMirror.cs). Raw parameterized SQL rather than EF, because the
// writes need ON CONFLICT DO NOTHING and EF has no upsert.
public sealed partial class JournalMirror
{
    // Postgres accepts 65535 parameters per statement; an event carries 18 columns, so 2000 rows per statement
    // (36000 parameters) stays well below the ceiling and leaves room for the schema to grow.
    private const int InsertBatchSize = 2000;

    // pulled_at and projection_version are deliberately absent: both have database defaults, and the mirror has
    // no opinion about them beyond "now" and "not projected yet".
    private const string EventColumns =
        "source_id, event_id, event_type, occurred_at, recorded_at, performer_type, performer_id, "
        + "initiator_type, initiator_id, correlation_id, trace_id, source_application, "
        + "source_module, source_version, payload, payload_raw, mandate_id";

    // Position of "payload" in EventColumns. That parameter needs an explicit ::jsonb cast: Npgsql sends strings
    // as text and Postgres has no implicit text -> jsonb coercion, so an uncast insert fails outright.
    //
    // Derived, never written down. A hand-counted index silently moves the cast onto the neighbouring column the
    // moment a column is added or removed, which is exactly what happened when causation_id was dropped, and the
    // failure surfaced as "column payload is of type jsonb but expression is of type text" three layers away.
    private static readonly int PayloadColumnIndex = Array.IndexOf(
        EventColumns.Split(',', StringSplitOptions.TrimEntries),
        "payload"
    );

    private const string ObjectColumns = "source_id, event_source_id, object_type, object_id, qualifier, attributes";

    /// <summary>
    /// Writes events and their objects in one transaction and returns the number of events actually inserted.
    /// Conflicts are ignored rather than treated as errors: after a crash between the write and the watermark
    /// update the same page is legitimately re-read, and re-reading has to be a no-op, not a failure.
    /// </summary>
    public async Task<int> WriteEventsAsync(
        IReadOnlyList<SourceEvent> events,
        IReadOnlyList<SourceEventObject> objects,
        CancellationToken ct
    )
    {
        if (events.Count == 0 && objects.Count == 0)
            return 0;

        await using var db = await _factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var inserted = 0;

        // Events before objects: an object row points at its event by source_id and the FK is enforced, so the
        // parent has to be in the same transaction and written first.
        foreach (var batch in events.Chunk(InsertBatchSize))
        {
            inserted += await InsertEventsAsync(db, batch, ct);
            db.ChangeTracker.Clear();
        }

        foreach (var batch in objects.Chunk(InsertBatchSize))
        {
            await InsertObjectsAsync(db, batch, ct);
            db.ChangeTracker.Clear();
        }

        await tx.CommitAsync(ct);
        return inserted;
    }

    private async Task<int> InsertEventsAsync(AppDbContext db, IReadOnlyList<SourceEvent> batch, CancellationToken ct)
    {
        await using var command = CreateCommand(db);
        var tuples = new List<string>(batch.Count);
        var index = 0;

        foreach (var e in batch)
        {
            var (payload, payloadRaw) = NormalizePayload(e.SourceId, e.PayloadJson);
            var names = new[]
            {
                Param(command, ref index, e.SourceId),
                Param(command, ref index, e.EventId),
                Param(command, ref index, e.EventType),
                Param(command, ref index, e.OccurredAtUtc),
                Param(command, ref index, e.RecordedAtUtc),
                Param(command, ref index, e.PerformerType),
                Param(command, ref index, e.PerformerId),
                Param(command, ref index, e.InitiatorType),
                Param(command, ref index, e.InitiatorId),
                Param(command, ref index, e.CorrelationId),
                Param(command, ref index, e.TraceId),
                Param(command, ref index, e.SourceApplication),
                Param(command, ref index, e.SourceModule),
                Param(command, ref index, e.SourceVersion),
                Param(command, ref index, payload),
                Param(command, ref index, payloadRaw),
                Param(command, ref index, e.MandateId),
            };
            tuples.Add(BuildTuple(names, PayloadColumnIndex));
        }

        // Timestamps go to the driver exactly as the source reader handed them over. The reader owns stamping
        // Kind=Utc; if anything else feeds this mirror, Npgsql rejecting the value is the intended guard, and
        // "fixing" the Kind here would silently shift business time by the local offset instead.
        command.CommandText =
            $"INSERT INTO journal.event ({EventColumns}) VALUES {string.Join(", ", tuples)} "
            + "ON CONFLICT (source_id) DO NOTHING;";
        return await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task InsertObjectsAsync(
        AppDbContext db,
        IReadOnlyList<SourceEventObject> batch,
        CancellationToken ct
    )
    {
        await using var command = CreateCommand(db);
        var tuples = new List<string>(batch.Count);
        var index = 0;

        foreach (var o in batch)
        {
            var names = new[]
            {
                Param(command, ref index, o.SourceId),
                Param(command, ref index, o.EventSourceId),
                Param(command, ref index, o.ObjectType),
                Param(command, ref index, o.ObjectId),
                Param(command, ref index, o.Qualifier),
                Param(command, ref index, (object?)o.Attributes ?? DBNull.Value),
            };
            // The classification is jsonb in the mirror and text on the wire, so the last column carries the same cast the
            // payload does. Null stays null: most references say nothing about their object, and an empty document would
            // claim they did.
            tuples.Add(BuildTuple(names, names.Length - 1));
        }

        // Conflict on the natural key rather than the mirrored id: re-reading an event's links has to be a no-op
        // even in the one case where the source renumbered the link rows themselves.
        command.CommandText =
            $"INSERT INTO journal.event_object ({ObjectColumns}) VALUES {string.Join(", ", tuples)} "
            + "ON CONFLICT (event_source_id, object_type, object_id, qualifier) DO NOTHING;";
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Splits the source payload into a valid jsonb value and, when the text is not JSON, the untouched original.
    /// One malformed payload must never kill a run: losing a single event's detail is recoverable and stays
    /// visible in payload_raw, losing the rest of the page is neither. An absent payload is '{}', not an error.
    /// </summary>
    private (string Payload, string? PayloadRaw) NormalizePayload(long sourceId, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return ("{}", null);

        try
        {
            using var _ = JsonDocument.Parse(text);
            return (text, null);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(
                ex,
                "Event {SourceId} has an unparseable payload, kept verbatim in payload_raw",
                sourceId
            );
            return ("{}", text);
        }
    }

    private static DbCommand CreateCommand(AppDbContext db)
    {
        var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        return command;
    }

    private static string Param(DbCommand command, ref int index, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = $"@p{index++}";
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
        return parameter.ParameterName;
    }

    private static string BuildTuple(IReadOnlyList<string> names, int jsonbIndex) =>
        "(" + string.Join(", ", names.Select((name, i) => i == jsonbIndex ? $"{name}::jsonb" : name)) + ")";
}
