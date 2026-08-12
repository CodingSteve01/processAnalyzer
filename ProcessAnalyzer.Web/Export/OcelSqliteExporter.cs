using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProcessAnalyzer.Web.Data;

namespace ProcessAnalyzer.Web.Export;

/// <summary>
/// Writes the projected log as an OCEL 2.0 SQLite file, the format pm4py reads natively.
/// <para>
/// SQLite, not JSON: the JSON form has to be held in memory as one document, while pm4py's SQLite importer reads
/// the same log without materializing it. For a log that grows with the company, that decides whether the miner
/// runs at all.
/// </para>
/// <para>
/// The layout follows the OCEL 2.0 relational schema: a per-type table for events and objects, the two map tables
/// that name them, and the relation tables. The map tables are authoritative for the table suffix, so a type whose
/// name is not a legal identifier still round-trips.
/// </para>
/// </summary>
public sealed class OcelSqliteExporter
{
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly ILogger<OcelSqliteExporter> _logger;

    public OcelSqliteExporter(IDbContextFactory<AppDbContext> factory, ILogger<OcelSqliteExporter> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    // The activity carries its discriminator (which role approved, which action ran), so the diagram distinguishes
    // steps that share an event type. Without it every approval collapses into one box.
    private const string EventLabel = "analytics.label_activity(analytics.activity_of(e.type, e.attrs))";

    private const string EventTypeQuery = "SELECT DISTINCT " + EventLabel + " FROM ocel.event e ORDER BY 1";

    private const string ObjectTypeQuery = "SELECT DISTINCT analytics.label_object(type) FROM ocel.object ORDER BY 1";

    public async Task<ExportResult> ExportAsync(string path, CancellationToken ct)
    {
        if (File.Exists(path))
            File.Delete(path);

        await using var db = await _factory.CreateDbContextAsync(ct);
        var source = (NpgsqlConnection)db.Database.GetDbConnection();
        await source.OpenAsync(ct);

        await using var target = new SqliteConnection($"Data Source={path}");
        await target.OpenAsync(ct);
        await using var transaction = target.BeginTransaction();

        CreateCoreTables(target);

        // Exported under their German names: the export exists to be drawn, and a diagram box labelled
        // 'personnel.employee.received-from-external.v1' is unreadable for its audience. The technical key stays the
        // key inside Postgres, where identity matters.
        var eventTypes = await ReadTypesAsync(source, EventTypeQuery, ct);
        var objectTypes = await ReadTypesAsync(source, ObjectTypeQuery, ct);

        var eventSuffixes = WriteTypeMap(target, "event_map_type", eventTypes);
        var objectSuffixes = WriteTypeMap(target, "object_map_type", objectTypes);

        var events = await CopyEventsAsync(source, target, eventSuffixes, ct);
        var objects = await CopyObjectsAsync(source, target, objectSuffixes, ct);
        var relations = await CopyRelationsAsync(source, target, ct);

        transaction.Commit();
        _logger.LogInformation(
            "Exported {Events} events, {Objects} objects and {Relations} relations to {Path}",
            events,
            objects,
            relations,
            path
        );

        return new ExportResult(path, events, objects, relations, eventTypes.Count, objectTypes.Count);
    }

    /// <summary>
    /// The core tables, with the keys the OCEL 2.0 relational schema requires.
    /// </summary>
    /// <remarks>
    /// The keys are not decoration. pm4py validates them on import and reports every missing one; an export without
    /// them still loads, which is worse than failing: it would let a duplicated relation through and quietly
    /// inflate every count computed from it. The map tables come first because the type columns reference them.
    /// </remarks>
    private static void CreateCoreTables(SqliteConnection target)
    {
        Execute(
            target,
            """
            CREATE TABLE event_map_type (ocel_type TEXT PRIMARY KEY, ocel_type_map TEXT);
            CREATE TABLE object_map_type (ocel_type TEXT PRIMARY KEY, ocel_type_map TEXT);
            CREATE TABLE event (
                ocel_id TEXT PRIMARY KEY,
                ocel_type TEXT,
                FOREIGN KEY (ocel_type) REFERENCES event_map_type (ocel_type)
            );
            CREATE TABLE object (
                ocel_id TEXT PRIMARY KEY,
                ocel_type TEXT,
                FOREIGN KEY (ocel_type) REFERENCES object_map_type (ocel_type)
            );
            CREATE TABLE event_object (
                ocel_event_id TEXT,
                ocel_object_id TEXT,
                ocel_qualifier TEXT,
                PRIMARY KEY (ocel_event_id, ocel_object_id, ocel_qualifier),
                FOREIGN KEY (ocel_event_id) REFERENCES event (ocel_id),
                FOREIGN KEY (ocel_object_id) REFERENCES object (ocel_id)
            );
            CREATE TABLE object_object (
                ocel_source_id TEXT,
                ocel_target_id TEXT,
                ocel_qualifier TEXT,
                PRIMARY KEY (ocel_source_id, ocel_target_id, ocel_qualifier),
                FOREIGN KEY (ocel_source_id) REFERENCES object (ocel_id),
                FOREIGN KEY (ocel_target_id) REFERENCES object (ocel_id)
            );
            """
        );
    }

    /// <summary>
    /// Assigns each type a table suffix that is a legal identifier and unique.
    /// </summary>
    /// <remarks>
    /// Event types look like <c>dms.document.release-granted.v1</c>. Dots and dashes cannot be table names, and
    /// naively stripping them collides (<c>release-granted</c> and <c>release.granted</c> would become one table).
    /// The counter suffix on a collision is what keeps the export lossless.
    /// </remarks>
    private static Dictionary<string, string> WriteTypeMap(
        SqliteConnection target,
        string table,
        IReadOnlyList<string> types
    )
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var type in types)
        {
            var candidate = new string(type.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray()).Trim('_');
            if (candidate.Length == 0 || char.IsDigit(candidate[0]))
                candidate = "t_" + candidate;

            var suffix = candidate;
            var counter = 2;
            while (!used.Add(suffix))
                suffix = $"{candidate}_{counter++}";

            map[type] = suffix;

            using var command = target.CreateCommand();
            command.CommandText = $"INSERT INTO {table} (ocel_type, ocel_type_map) VALUES ($t, $m)";
            command.Parameters.AddWithValue("$t", type);
            command.Parameters.AddWithValue("$m", suffix);
            command.ExecuteNonQuery();
        }

        return map;
    }

    private async Task<int> CopyEventsAsync(
        NpgsqlConnection source,
        SqliteConnection target,
        Dictionary<string, string> suffixes,
        CancellationToken ct
    )
    {
        foreach (var (_, suffix) in suffixes)
            Execute(
                target,
                // The actor travels with the event as an OCEL attribute. Without it the classical analyses in the miner
                // cannot say who did anything: batch detection needs a resource, and "this happens in bursts" without
                // saying whose bursts is not something anybody can act on.
                $"CREATE TABLE event_{suffix} (ocel_id TEXT PRIMARY KEY, ocel_time TEXT, "
                    + "\"org:resource\" TEXT, FOREIGN KEY (ocel_id) REFERENCES event (ocel_id))"
            );

        await using var command = source.CreateCommand();
        command.CommandText =
            "SELECT e.id, "
            + EventLabel
            + ", e.ts, analytics.person(e.actor_key) FROM ocel.event e ORDER BY e.ts, e.id";
        await using var reader = await command.ExecuteReaderAsync(ct);

        using var insertCore = target.CreateCommand();
        insertCore.CommandText = "INSERT INTO event (ocel_id, ocel_type) VALUES ($id, $type)";
        insertCore.Parameters.Add("$id", SqliteType.Text);
        insertCore.Parameters.Add("$type", SqliteType.Text);

        var count = 0;
        while (await reader.ReadAsync(ct))
        {
            var id = reader.GetString(0);
            var type = reader.GetString(1);
            // ISO-8601 with a Z: pm4py parses the string, and a naive local timestamp would move every duration.
            var timestamp = reader.GetFieldValue<DateTime>(2).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

            insertCore.Parameters["$id"].Value = id;
            insertCore.Parameters["$type"].Value = type;
            insertCore.ExecuteNonQuery();

            // Named or pseudonymised exactly as on screen: the export must not be a way around the identity setting.
            var actor = await reader.IsDBNullAsync(3, ct) ? null : reader.GetString(3);

            using var insertTyped = target.CreateCommand();
            insertTyped.CommandText =
                $"INSERT INTO event_{suffixes[type]} (ocel_id, ocel_time, \"org:resource\") VALUES ($id, $ts, $actor)";
            insertTyped.Parameters.AddWithValue("$id", id);
            insertTyped.Parameters.AddWithValue("$ts", timestamp);
            insertTyped.Parameters.AddWithValue("$actor", (object?)actor ?? DBNull.Value);
            insertTyped.ExecuteNonQuery();
            count++;
        }

        return count;
    }

    private async Task<int> CopyObjectsAsync(
        NpgsqlConnection source,
        SqliteConnection target,
        Dictionary<string, string> suffixes,
        CancellationToken ct
    )
    {
        foreach (var (_, suffix) in suffixes)
        {
            // ocel_changed_field must exist even when nothing changes over time: without it the importer falls back
            // to insertion order to decide which row is the current state.
            Execute(
                target,
                $"CREATE TABLE object_{suffix} (ocel_id TEXT, ocel_time TEXT, ocel_changed_field TEXT, "
                    + "PRIMARY KEY (ocel_id, ocel_time), FOREIGN KEY (ocel_id) REFERENCES object (ocel_id))"
            );
        }

        await using var command = source.CreateCommand();
        command.CommandText = "SELECT id, analytics.label_object(type), first_seen FROM ocel.object ORDER BY id";
        await using var reader = await command.ExecuteReaderAsync(ct);

        using var insertCore = target.CreateCommand();
        insertCore.CommandText = "INSERT INTO object (ocel_id, ocel_type) VALUES ($id, $type)";
        insertCore.Parameters.Add("$id", SqliteType.Text);
        insertCore.Parameters.Add("$type", SqliteType.Text);

        var count = 0;
        while (await reader.ReadAsync(ct))
        {
            var id = reader.GetString(0);
            var type = reader.GetString(1);

            insertCore.Parameters["$id"].Value = id;
            insertCore.Parameters["$type"].Value = type;
            insertCore.ExecuteNonQuery();

            using var insertTyped = target.CreateCommand();
            insertTyped.CommandText =
                $"INSERT INTO object_{suffixes[type]} (ocel_id, ocel_time, ocel_changed_field) VALUES ($id, $ts, NULL)";
            insertTyped.Parameters.AddWithValue("$id", id);
            // The epoch row is the object's initial state, which is what "no attribute history yet" means in OCEL.
            insertTyped.Parameters.AddWithValue("$ts", "1970-01-01T00:00:00.000Z");
            insertTyped.ExecuteNonQuery();
            count++;
        }

        return count;
    }

    private static async Task<int> CopyRelationsAsync(
        NpgsqlConnection source,
        SqliteConnection target,
        CancellationToken ct
    )
    {
        await using var command = source.CreateCommand();
        command.CommandText = "SELECT event_id, object_id, qualifier FROM ocel.e2o";
        await using var reader = await command.ExecuteReaderAsync(ct);

        using var insert = target.CreateCommand();
        insert.CommandText =
            "INSERT INTO event_object (ocel_event_id, ocel_object_id, ocel_qualifier) VALUES ($e, $o, $q)";
        insert.Parameters.Add("$e", SqliteType.Text);
        insert.Parameters.Add("$o", SqliteType.Text);
        insert.Parameters.Add("$q", SqliteType.Text);

        var count = 0;
        while (await reader.ReadAsync(ct))
        {
            insert.Parameters["$e"].Value = reader.GetString(0);
            insert.Parameters["$o"].Value = reader.GetString(1);
            insert.Parameters["$q"].Value = reader.GetString(2);
            insert.ExecuteNonQuery();
            count++;
        }

        return count;
    }

    private static async Task<List<string>> ReadTypesAsync(NpgsqlConnection source, string sql, CancellationToken ct)
    {
        await using var command = source.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(ct);

        var types = new List<string>();
        while (await reader.ReadAsync(ct))
            types.Add(reader.GetString(0));

        return types;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}

/// <param name="Path">Where the file was written.</param>
/// <param name="Events">Events exported.</param>
/// <param name="Objects">Objects exported.</param>
/// <param name="Relations">Event-to-object relations exported.</param>
/// <param name="EventTypes">Distinct event types, one table each.</param>
/// <param name="ObjectTypes">Distinct object types, one table each.</param>
public sealed record ExportResult(string Path, int Events, int Objects, int Relations, int EventTypes, int ObjectTypes);
