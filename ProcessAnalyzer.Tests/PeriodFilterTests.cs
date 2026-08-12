using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace ProcessAnalyzer.Tests;

/// <summary>
/// The period filter scopes whole cases by their start, and it really excludes.
/// </summary>
/// <remarks>
/// Two failures worth a test. A filter that quietly does nothing looks identical to a filter that works when the data
/// happens to fit the window, and it would be believed. And a filter applied to events rather than cases truncates
/// every case that began earlier: its first step becomes whatever fell inside the window, so cycle time, rework and
/// the variant list all come out wrong rather than merely partial.
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class PeriodFilterTests
{
    private readonly PostgresFixture _postgres;

    public PeriodFilterTests(PostgresFixture postgres) => _postgres = postgres;

    [Fact]
    public async Task ACaseThatStartedBeforeTheWindow_IsExcludedWithAllItsEvents()
    {
        await SeedTwoCasesAsync();

        // The old case starts in May, the recent one in July. A window from June must return the recent case only,
        // and none of the old case's events, not even the ones that fall inside the window.
        var inWindow = await ObjectsInPeriodAsync("2026-06-01", null);
        Assert.Equal(["doc:new"], inWindow);

        var everything = await ObjectsInPeriodAsync(null, null);
        Assert.Equal(["doc:new", "doc:old"], everything);
    }

    [Fact]
    public async Task AWindowThatEndsBeforeACaseStarts_ExcludesIt()
    {
        await SeedTwoCasesAsync();

        // Upper bound is exclusive on the case start.
        Assert.Equal(["doc:old"], await ObjectsInPeriodAsync(null, "2026-07-01"));
    }

    [Fact]
    public async Task TheEventsOfAnIncludedCase_AreNotCutAtTheWindowEdge()
    {
        await SeedTwoCasesAsync();

        // doc:old runs from May into July. Asked without a window it must show both of its steps: the point of
        // scoping by case start is that a case is whole or absent, never half.
        await using var db = await _postgres.Factory.CreateDbContextAsync(CancellationToken.None);
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        await connection.OpenAsync(CancellationToken.None);

        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM analytics.object_timeline WHERE object_id = 'doc:old'",
            connection
        );

        Assert.Equal(2L, Convert.ToInt64(await command.ExecuteScalarAsync()));
    }

    private async Task<List<string>> ObjectsInPeriodAsync(string? from, string? until)
    {
        await using var db = await _postgres.Factory.CreateDbContextAsync(CancellationToken.None);
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        await connection.OpenAsync(CancellationToken.None);

        await using var command = new NpgsqlCommand(
            """
            SELECT DISTINCT object_id
            FROM analytics.object_timeline
            WHERE object_type = 'document'
              AND (@periodFrom::timestamptz IS NULL OR first_ts >= @periodFrom)
              AND (@periodUntil::timestamptz IS NULL OR first_ts < @periodUntil)
            ORDER BY object_id
            """,
            connection
        );
        command.Parameters.AddWithValue(
            "periodFrom",
            from is null ? DBNull.Value : DateTime.Parse(from).ToUniversalTime()
        );
        command.Parameters.AddWithValue(
            "periodUntil",
            until is null ? DBNull.Value : DateTime.Parse(until).ToUniversalTime()
        );

        var ids = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            ids.Add(reader.GetString(0));

        return ids;
    }

    private async Task SeedTwoCasesAsync()
    {
        await _postgres.ResetAsync();

        await using var db = await _postgres.Factory.CreateDbContextAsync(CancellationToken.None);

        // The mirror rows first: ocel.event references them, which is what keeps the derived model from outliving the
        // facts it came from. The JSON goes in as a parameter: ExecuteSqlRaw reads braces as format placeholders.
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO journal.event (source_id, event_id, event_type, occurred_at, recorded_at, performer_type,
                                       source_application, payload)
            VALUES (1, gen_random_uuid(), 'dms.document.uploaded.v1',        '2026-05-10 08:00+02',
                    '2026-05-10 08:00+02', 'User', 'erp', {0}::jsonb),
                   (2, gen_random_uuid(), 'dms.document.release-granted.v1', '2026-07-15 08:00+02',
                    '2026-07-15 08:00+02', 'User', 'erp', {0}::jsonb),
                   (3, gen_random_uuid(), 'dms.document.uploaded.v1',        '2026-07-02 08:00+02',
                    '2026-07-02 08:00+02', 'User', 'erp', {0}::jsonb);
            """,
            "{}"
        );

        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO ocel.event (id, source_id, type, ts, recorded_at, actor_kind, source_application, attrs)
            VALUES ('e1', 1, 'dms.document.uploaded.v1',        '2026-05-10 08:00+02', '2026-05-10 08:00+02',
                    'human', 'erp', {0}::jsonb),
                   ('e2', 2, 'dms.document.release-granted.v1', '2026-07-15 08:00+02', '2026-07-15 08:00+02',
                    'human', 'erp', {0}::jsonb),
                   ('e3', 3, 'dms.document.uploaded.v1',        '2026-07-02 08:00+02', '2026-07-02 08:00+02',
                    'human', 'erp', {0}::jsonb);

            -- doc:old spans May to July, doc:new starts in July. That overlap is the point: a window from June must
            -- exclude the old case entirely, including its July event.
            INSERT INTO ocel.object (id, type, first_seen, last_seen)
            VALUES ('doc:old', 'document', '2026-05-10 08:00+02', '2026-07-15 08:00+02'),
                   ('doc:new', 'document', '2026-07-02 08:00+02', '2026-07-02 08:00+02');

            INSERT INTO ocel.e2o (event_id, object_id, qualifier)
            VALUES ('e1', 'doc:old', 'affected'),
                   ('e2', 'doc:old', 'affected'),
                   ('e3', 'doc:new', 'affected');

            REFRESH MATERIALIZED VIEW analytics.object_timeline;
            REFRESH MATERIALIZED VIEW dim.actor_identity;
            REFRESH MATERIALIZED VIEW analytics.object_lifecycle;
            """,
            "{}"
        );
    }
}
