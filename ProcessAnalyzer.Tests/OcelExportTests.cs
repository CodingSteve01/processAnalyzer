using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ProcessAnalyzer.Web.Export;
using Xunit;

namespace ProcessAnalyzer.Tests;

/// <summary>
/// The OCEL 2.0 export, checked against the constraints pm4py validates on import.
/// </summary>
/// <remarks>
/// A missing key does not stop the file from loading — that is the danger. The first version of this exporter
/// violated eight of the relational constraints and imported fine; a duplicated relation would have inflated every
/// count computed from it, with nothing looking wrong. Running pm4py from a .NET test is not practical, so the
/// constraints it checks are asserted here directly.
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class OcelExportTests : IAsyncLifetime
{
    private readonly PostgresFixture _postgres;
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"ocel-test-{Guid.NewGuid():N}.sqlite");

    public OcelExportTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync()
    {
        await _postgres.ResetAsync();
        await SeedAsync();
    }

    public Task DisposeAsync()
    {
        if (File.Exists(_path))
            File.Delete(_path);

        return Task.CompletedTask;
    }

    [Fact]
    public async Task Export_WritesEveryEventObjectAndRelation()
    {
        var result = await ExportAsync();

        Assert.Equal(2, result.Events);
        Assert.Equal(2, result.Objects);
        Assert.Equal(2, result.Relations);
    }

    [Fact]
    public async Task Export_NamesTypesInGerman_BecauseTheExportIsWhatGetsDrawn()
    {
        await ExportAsync();

        var types = await QueryAsync("SELECT ocel_type FROM event_map_type ORDER BY 1");

        // A diagram box reading 'demo.document.release-granted.v1' is unreadable for the people the analysis is for.
        Assert.Contains(types, type => type.StartsWith("Freigabe erteilt", StringComparison.Ordinal));
        Assert.DoesNotContain(types, type => type.Contains(".v1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Export_SeparatesStepsThatShareAnEventType()
    {
        await ExportAsync();

        var types = await QueryAsync("SELECT ocel_type FROM event_map_type ORDER BY 1");

        // Both seeded events are release-granted; only the role tells them apart. Collapsed into one activity, every
        // two-role approval reads as the same step happening twice.
        Assert.Equal(2, types.Count);
    }

    [Fact]
    public async Task Export_CarriesTheKeysPm4pyValidates()
    {
        await ExportAsync();

        var schema = string.Join("\n", await QueryAsync("SELECT sql FROM sqlite_master WHERE type = 'table'"));

        Assert.Contains(
            "PRIMARY KEY (ocel_event_id, ocel_object_id, ocel_qualifier)",
            schema,
            StringComparison.Ordinal
        );
        Assert.Contains("FOREIGN KEY (ocel_event_id) REFERENCES event (ocel_id)", schema, StringComparison.Ordinal);
        Assert.Contains("FOREIGN KEY (ocel_object_id) REFERENCES object (ocel_id)", schema, StringComparison.Ordinal);
        // Present even when nothing changes over time: without it the importer falls back to insertion order to
        // decide which row is an object's current state.
        Assert.Contains("ocel_changed_field", schema, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Export_TimestampsAreUtcWithAZ()
    {
        await ExportAsync();

        // The table suffix is derived from the label and sanitized, so the test asks the map table for it instead of
        // hard-coding the result of that sanitizing — a rule change would otherwise break the test rather than the
        // assertion it is making.
        var suffix = (await QueryAsync("SELECT ocel_type_map FROM event_map_type ORDER BY ocel_type LIMIT 1"))[0];
        var timestamps = await QueryAsync($"SELECT ocel_time FROM event_{suffix}");

        // pm4py parses the string. A naive local timestamp moves every duration the miner computes by the offset.
        Assert.All(timestamps, value => Assert.EndsWith("Z", value, StringComparison.Ordinal));
    }

    private async Task<ExportResult> ExportAsync() =>
        await new OcelSqliteExporter(_postgres.Factory, NullLogger<OcelSqliteExporter>.Instance).ExportAsync(
            _path,
            CancellationToken.None
        );

    private async Task<List<string>> QueryAsync(string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={_path}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();

        var values = new List<string>();
        while (await reader.ReadAsync())
            values.Add(reader.IsDBNull(0) ? string.Empty : reader.GetString(0));

        return values;
    }

    /// <summary>Two approvals of one document by two roles — the smallest log that can expose a collapsed activity.</summary>
    private async Task SeedAsync()
    {
        await using var db = await _postgres.Factory.CreateDbContextAsync(CancellationToken.None);
        // The JSON goes in as a parameter: ExecuteSqlRaw treats braces in the statement as format placeholders, and
        // a payload literal makes it throw on a brace it was never meant to read.
        // The mirror rows come first: ocel.event references them, which is what keeps the derived model from
        // outliving the facts it was derived from.
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO journal.event (source_id, event_id, event_type, occurred_at, recorded_at, performer_type,
                                       source_application, payload)
            VALUES (1, gen_random_uuid(), 'demo.document.release-granted.v1', '2026-05-20 08:00+02',
                    '2026-05-20 08:00+02', 'User', 'erp', {0}::jsonb),
                   (2, gen_random_uuid(), 'demo.document.release-granted.v1', '2026-05-20 10:00+02',
                    '2026-05-20 10:00+02', 'User', 'erp', {0}::jsonb);
            """,
            "{}"
        );

        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO ocel.event (id, source_id, type, ts, recorded_at, actor_kind, source_application, attrs)
            VALUES ('e1', 1, 'demo.document.release-granted.v1', '2026-05-20 08:00+02', '2026-05-20 08:00+02',
                    'human', 'erp', {0}::jsonb),
                   ('e2', 2, 'demo.document.release-granted.v1', '2026-05-20 10:00+02', '2026-05-20 10:00+02',
                    'human', 'erp', {1}::jsonb);

            INSERT INTO ocel.object (id, type, first_seen, last_seen)
            VALUES ('document:1', 'document', '2026-05-20 08:00+02', '2026-05-20 10:00+02'),
                   ('workflow:12', 'workflow', '2026-05-20 08:00+02', '2026-05-20 10:00+02');

            INSERT INTO ocel.e2o (event_id, object_id, qualifier)
            VALUES ('e1', 'document:1', 'released'), ('e2', 'workflow:12', 'executed');
            """,
            """{"role": "Management"}""",
            """{"role": "Accounting"}"""
        );
    }
}
