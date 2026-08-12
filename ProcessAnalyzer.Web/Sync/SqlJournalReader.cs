using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
using ProcessAnalyzer.Web.Options;

namespace ProcessAnalyzer.Web.Sync;

// The read side against the source operational database. Raw ADO.NET, never EF: an EF model over a foreign schema
// invites migrations, change tracking and lazy loading against a database this process must never write to.
//
// Every statement here is keyed on the clustered primary key dbo.BusinessEvents.Id — no SELECT *, no JOIN, no
// aggregate, no LIKE. That is not style, it is the price of admission: this reader runs against the live operational
// database, so any read that turns into a scan or a hash join competes for the same memory grants and locks as order
// processing. A seek on the clustered index costs the source system nothing measurable; anything else eventually does.
public sealed class SqlJournalReader : IJournalSource
{
    private const int CommandTimeoutSeconds = 120;

    // SQL Server refuses a statement with more than 2100 parameters. 1000 ids per statement keeps a comfortable margin
    // and still keeps the round-trip count low.
    private const int MaxIdsPerStatement = 1000;

    private const string EventColumns =
        "e.Id, e.EventId, e.EventType, e.OccurredAt, e.RecordedAt, "
        + "e.PerformerType, e.PerformerId, e.InitiatorType, e.InitiatorId, "
        + "e.CorrelationId, e.TraceId, "
        + "e.SourceApplication, e.SourceModule, e.SourceVersion, e.Payload, e.MandateId";

    private readonly ProcessAnalyzerOptions _options;
    private readonly ILogger<SqlJournalReader> _logger;

    public SqlJournalReader(ProcessAnalyzerOptions options, ILogger<SqlJournalReader> logger)
    {
        _options = options;
        _logger = logger;
        EnsureReadOnlyIntent(options.SourceConnectionString);
    }

    /// Reads the next page of events after <paramref name="afterId" />, ordered by Id.
    ///
    /// There is deliberately NO lag predicate in this SQL. Id is an IDENTITY value handed out at INSERT time, inside
    /// the business transaction that produced the event — so a reader can see Id=1005 committed while Id=1003 is still
    /// an open transaction. If this query filtered on RecordedAt, 1005 would pass the filter, the caller would advance
    /// the watermark past 1003, and 1003 would be lost forever with every counter still looking healthy. The lag rule
    /// therefore belongs to the caller, which walks the page and stops at the first row inside the lag window, keeping
    /// only the contiguous prefix of ids it is allowed to trust.
    public async Task<IReadOnlyList<SourceEvent>> ReadEventsAsync(long afterId, int batchSize, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);

        var sql = $"""
            SELECT TOP (@batch) {EventColumns}
            FROM dbo.BusinessEvents e WITH (READCOMMITTED)
            WHERE e.Id > @watermark
            ORDER BY e.Id;
            """;

        await using var connection = await OpenAsync(ct);
        await using var command = CreateCommand(connection, sql);
        command.Parameters.Add("@batch", SqlDbType.Int).Value = batchSize;
        command.Parameters.Add("@watermark", SqlDbType.BigInt).Value = afterId;

        var events = await ReadEventsAsync(command, ct);
        _logger.LogDebug("Read {Count} events after id {AfterId}", events.Count, afterId);
        return events;
    }

    /// Reads the object rows for an explicit list of event ids. The list is explicit — never a JOIN back to
    /// dbo.BusinessEvents — so the source only ever sees a bounded seek list, and so that events whose page the caller
    /// held back never drag their objects along.
    /// <summary>
    /// Whether the source carries object classifications yet.
    /// </summary>
    /// <remarks>
    /// The column ships with a release of the source system, and this mirror is deployed independently of it. Selecting a
    /// column that does not exist fails the whole read, so the absence has to be a fact the reader knows rather than a
    /// crash it discovers. Checked per read: cheap against the catalogue, and it means a source that gains the column
    /// starts being mirrored on the next pull instead of on the next restart.
    /// </remarks>
    private static async Task<bool> HasObjectAttributesAsync(SqlConnection connection, CancellationToken ct)
    {
        const string sql = """
            SELECT CONVERT(int, COUNT(*))
            FROM sys.columns
            WHERE object_id = OBJECT_ID('dbo.BusinessEventObjects') AND name = 'Attributes';
            """;

        await using var command = CreateCommand(connection, sql);
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) > 0;
    }

    public async Task<IReadOnlyList<SourceEventObject>> ReadObjectsAsync(
        IReadOnlyList<long> eventSourceIds,
        CancellationToken ct
    )
    {
        if (eventSourceIds.Count == 0)
            return [];

        var results = new List<SourceEventObject>(eventSourceIds.Count);
        await using var connection = await OpenAsync(ct);

        // Asked once per read, not per chunk, and never assumed: the classification column arrives with a release of the
        // source system, and a mirror that selects a column the source does not have yet stops pulling entirely. Missing
        // is a normal state here, not an error.
        var hasAttributes = await HasObjectAttributesAsync(connection, ct);
        var attributeColumn = hasAttributes ? ", o.Attributes" : string.Empty;

        foreach (var chunk in Chunk(eventSourceIds))
        {
            var sql = $"""
                SELECT o.Id, o.BusinessEventId, o.ObjectType, o.ObjectId, o.Qualifier{attributeColumn}
                FROM dbo.BusinessEventObjects o WITH (READCOMMITTED)
                WHERE o.BusinessEventId IN ({BuildIdList(chunk)})
                ORDER BY o.BusinessEventId, o.Id;
                """;

            await using var command = CreateCommand(connection, sql);
            AddIdParameters(command, chunk);

            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                results.Add(
                    new SourceEventObject(
                        reader.GetInt64(0),
                        reader.GetInt64(1),
                        reader.GetString(2),
                        reader.GetString(3),
                        reader.GetString(4),
                        hasAttributes && !reader.IsDBNull(5) ? reader.GetString(5) : null
                    )
                );
            }
        }

        return results;
    }

    /// The left side of the gap sweep: every (Id, EventId) the source recorded in the given window, up to the id the
    /// mirror has already passed. The sweep exists because the contiguous-prefix rule is only as good as the lag
    /// window — if a transaction stays open longer than the lag, its row is skipped and the loss is completely silent:
    /// no error, no retry, just a dashboard that under-counts. Comparing source keys against mirrored keys is the only
    /// thing that turns that into a number someone can see.
    ///
    /// This is the one read not driven by the primary key. It stays bounded on both ends — a few days of RecordedAt
    /// and a hard Id ceiling — so it can never degrade into a full-table scan of the journal.
    public async Task<IReadOnlyList<SourceEventKey>> ReadKeysForSweepAsync(
        DateTime sinceUtc,
        long maxId,
        CancellationToken ct
    )
    {
        const string sql = """
            SELECT e.Id, e.EventId
            FROM dbo.BusinessEvents e WITH (READCOMMITTED)
            WHERE e.RecordedAt >= @since AND e.Id <= @maxId
            ORDER BY e.Id;
            """;

        await using var connection = await OpenAsync(ct);
        await using var command = CreateCommand(connection, sql);
        command.Parameters.Add("@since", SqlDbType.DateTime2).Value = sinceUtc;
        command.Parameters.Add("@maxId", SqlDbType.BigInt).Value = maxId;

        var keys = new List<SourceEventKey>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            keys.Add(new SourceEventKey(reader.GetInt64(0), reader.GetGuid(1)));

        return keys;
    }

    /// Re-reads specific events the sweep found missing. Same projection as the paged read, so the write side cannot
    /// tell a sweep-repaired event from a normally pulled one.
    public async Task<IReadOnlyList<SourceEvent>> ReadEventsByIdAsync(
        IReadOnlyList<long> sourceIds,
        CancellationToken ct
    )
    {
        if (sourceIds.Count == 0)
            return [];

        var results = new List<SourceEvent>(sourceIds.Count);
        await using var connection = await OpenAsync(ct);

        foreach (var chunk in Chunk(sourceIds))
        {
            var sql = $"""
                SELECT {EventColumns}
                FROM dbo.BusinessEvents e WITH (READCOMMITTED)
                WHERE e.Id IN ({BuildIdList(chunk)})
                ORDER BY e.Id;
                """;

            await using var command = CreateCommand(connection, sql);
            AddIdParameters(command, chunk);
            results.AddRange(await ReadEventsAsync(command, ct));
        }

        return results;
    }

    /// Asks the source whether this login could INSERT into the journal table. The startup guard refuses to boot when
    /// the answer is yes: no analytical workload ever runs against the operational database, and the only version of
    /// that rule that survives contact with a hurried deployment is one the program itself enforces. A rule that lives
    /// in a README is a rule that gets bypassed by pasting the wrong connection string into an env file.
    public async Task<bool> IsWriteCapableAsync(CancellationToken ct)
    {
        const string sql = "SELECT CONVERT(int, HAS_PERMS_BY_NAME('dbo.BusinessEvents','OBJECT','INSERT'));";

        await using var connection = await OpenAsync(ct);
        await using var command = CreateCommand(connection, sql);
        var raw = await command.ExecuteScalarAsync(ct);

        // NULL means the login cannot even see the object, so the permission question has no answer. An unanswered
        // question is not a clean bill of health — report write-capable and let the guard stop the boot.
        if (raw is null || raw is DBNull)
        {
            _logger.LogWarning(
                "HAS_PERMS_BY_NAME returned NULL for dbo.BusinessEvents; treating the login as write-capable"
            );
            return true;
        }

        return Convert.ToInt32(raw) != 0;
    }

    private static async Task<List<SourceEvent>> ReadEventsAsync(SqlCommand command, CancellationToken ct)
    {
        var events = new List<SourceEvent>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            events.Add(MapEvent(reader));

        return events;
    }

    // Ordinals are fixed by EventColumns; both event queries share it so the mapping cannot drift between them.
    // Payload stays a raw string: it is untrusted JSON from a foreign system, and the write side is the layer that has
    // to survive a malformed document without killing the pull.
    private static SourceEvent MapEvent(SqlDataReader r) =>
        new(
            r.GetInt64(0),
            r.GetGuid(1),
            r.GetString(2),
            ReadUtc(r, 3),
            ReadUtc(r, 4),
            r.GetString(5),
            NullableString(r, 6),
            NullableString(r, 7),
            NullableString(r, 8),
            NullableString(r, 9),
            NullableString(r, 10),
            r.GetString(11),
            NullableString(r, 12),
            NullableString(r, 13),
            NullableString(r, 14),
            r.IsDBNull(15) ? null : r.GetInt64(15)
        );

    // datetime2 carries no offset, so SqlClient hands the value back as DateTimeKind.Unspecified. Storing that into a
    // Postgres timestamptz would reinterpret it in the server's local zone — a silent shift of every event time and
    // therefore of every duration this tool will later measure. The source columns are documented as UTC; say so here.
    private static DateTime ReadUtc(SqlDataReader r, int ordinal) =>
        DateTime.SpecifyKind(r.GetDateTime(ordinal), DateTimeKind.Utc);

    private static string? NullableString(SqlDataReader r, int ordinal) =>
        r.IsDBNull(ordinal) ? null : r.GetString(ordinal);

    // Sorted and de-duplicated before chunking so the per-chunk ORDER BY also holds across chunk boundaries: callers
    // get one globally ordered sequence regardless of how the ids arrived.
    private static IEnumerable<long[]> Chunk(IReadOnlyList<long> ids) =>
        ids.Distinct().Order().Chunk(MaxIdsPerStatement);

    private static string BuildIdList(long[] chunk) => string.Join(",", chunk.Select((_, i) => "@id" + i));

    private static void AddIdParameters(SqlCommand command, long[] chunk)
    {
        for (var i = 0; i < chunk.Length; i++)
            command.Parameters.Add("@id" + i, SqlDbType.BigInt).Value = chunk[i];
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqlConnection(_options.SourceConnectionString);
        try
        {
            await connection.OpenAsync(ct);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static SqlCommand CreateCommand(SqlConnection connection, string sql)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;

        // The journal is large and the source is a busy OLTP box; a page read that hits a slow moment must be allowed
        // to finish rather than fail the whole run and leave the watermark stalled.
        command.CommandTimeout = CommandTimeoutSeconds;
        return command;
    }

    // The connection string is used exactly as configured — rewriting it here would hide whatever the operator
    // actually deployed. But ApplicationIntent=ReadOnly is not a preference: it is what routes this workload to a
    // readable secondary instead of the primary that serves dispatchers. Refuse to construct without it.
    private static void EnsureReadOnlyIntent(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException(
                $"{ProcessAnalyzerOptions.SectionName}:{nameof(ProcessAnalyzerOptions.SourceConnectionString)} is not configured."
            );

        SqlConnectionStringBuilder builder;
        try
        {
            builder = new SqlConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException(
                $"{ProcessAnalyzerOptions.SectionName}:{nameof(ProcessAnalyzerOptions.SourceConnectionString)} is not a valid SQL Server connection string.",
                ex
            );
        }

        if (builder.ApplicationIntent != ApplicationIntent.ReadOnly)
            throw new InvalidOperationException(
                $"{ProcessAnalyzerOptions.SectionName}:{nameof(ProcessAnalyzerOptions.SourceConnectionString)} must contain "
                    + "ApplicationIntent=ReadOnly. This sidecar reads the operational database and must never be routed to the primary."
            );
    }
}
