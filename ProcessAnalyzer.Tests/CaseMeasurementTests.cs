using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProcessAnalyzer.Web.Analytics;
using Xunit;

namespace ProcessAnalyzer.Tests;

/// <summary>
/// How a case is measured: when it counts as finished, which clock it is measured by, and what makes one slow.
/// </summary>
/// <remarks>
/// These three decide every duration in the tool, and each of them was wrong once in a way that produced a plausible
/// number rather than an error. Every case counted as running, so percentiles reported on an empty set. Every duration
/// was measured against office hours, so a process that runs at night came out as zero. And "the median is nine hours"
/// was the deepest statement available, which is a fact nobody can act on.
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class CaseMeasurementTests
{
    private const string NightProcess = "night-run";

    private readonly PostgresFixture _postgres;

    public CaseMeasurementTests(PostgresFixture postgres) => _postgres = postgres;

    [Fact]
    public async Task ACaseEndingOnAnEndStepCountsAsFinished_EvenWhenItJustHappened()
    {
        await SeedAsync();

        // Every seeded case ends on the step that closes this process, and all of them happened minutes ago. Under the
        // old rule (silence for three days) every one of them would have counted as still running.
        var open = await ScalarAsync(
            $"SELECT count(*) FROM analytics.object_lifecycle WHERE object_type = '{NightProcess}' AND is_open"
        );
        var all = await ScalarAsync(
            $"SELECT count(*) FROM analytics.object_lifecycle WHERE object_type = '{NightProcess}'"
        );

        Assert.Equal(12, all);
        Assert.Equal(0, open);
    }

    [Fact]
    public async Task AProcessThatRunsAtNightIsMeasuredRoundTheClock()
    {
        await SeedAsync();

        // The seed runs from 22:00 to 04:00, so no part of it falls into the office calendar. Measured against office
        // hours every one of these cases would take zero, which is how the first real log read.
        var businessHours = await ScalarAsync(
            $"SELECT use_business_hours::int FROM analytics.process_clock WHERE object_type = '{NightProcess}'"
        );

        Assert.Equal(0, businessHours);
    }

    [Fact]
    public async Task DurationsExistOnceTheClockFitsTheProcess()
    {
        await SeedAsync();

        var median = await ScalarAsync(
            "SELECT percentile_cont(0.5) WITHIN GROUP (ORDER BY duration_seconds) "
                + $"FROM analytics.object_lifecycle WHERE object_type = '{NightProcess}' AND NOT is_open"
        );

        Assert.True(median > 0);
    }

    [Fact]
    public async Task TheSlowStepShowsUpAsADriver()
    {
        await SeedAsync();
        var repo = new AnalyticsRepository(_postgres.Factory);

        var drivers = await repo.DriversAsync(NightProcess, Scope.Everything, CancellationToken.None);

        // Half the seeded cases carry an extra step and take four hours longer. That is what a driver is: not "the
        // median is X", but "these cases take longer, this is the step they share, and this is what it adds up to".
        var slow = Assert.Single(drivers, row => (string)row["event_type_key"]! == "demo.night.reworked.v1");
        Assert.True(Convert.ToDouble(slow["median_with_seconds"]) > Convert.ToDouble(slow["median_without_seconds"]));
        Assert.True(Convert.ToDouble(slow["extra_seconds"]) > 0);
        Assert.Equal(6L, Convert.ToInt64(slow["with_cases"]));
        Assert.Equal(6L, Convert.ToInt64(slow["without_cases"]));
    }

    private async Task<double> ScalarAsync(string sql)
    {
        await using var db = await _postgres.Factory.CreateDbContextAsync(CancellationToken.None);
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToDouble(await command.ExecuteScalarAsync());
    }

    /// <summary>
    /// Twelve cases of a process that runs through the night. Six of them carry an extra step and end four hours later;
    /// all twelve end on the same closing step.
    /// </summary>
    private async Task SeedAsync()
    {
        await _postgres.ResetAsync();
        await using var db = await _postgres.Factory.CreateDbContextAsync(CancellationToken.None);

        // Business hours have to exist, or biz_seconds has nothing to measure against and the clock derivation would
        // be reading an empty calendar rather than a mismatched one.
        await db.Database.ExecuteSqlRawAsync("DELETE FROM analytics.holiday");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM analytics.business_slot");
        for (var day = 1; day <= 5; day++)
        {
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO analytics.business_slot (dow, open_from, open_to, hours, source) "
                    + FormattableString.Invariant($"VALUES ({day}, '07:00', '15:00', 8, 'test')")
            );
        }

        await db.Database.ExecuteSqlRawAsync(
            """
            WITH cases AS (SELECT n, n > 6 AS slow FROM generate_series(1, 12) AS n),
                 steps(seq, etype) AS (
                     VALUES (1, 'demo.night.started.v1'),
                            (2, 'demo.night.reworked.v1'),
                            (3, 'demo.night.finished.v1')
                 )
            INSERT INTO journal.event (source_id, event_id, event_type, occurred_at, recorded_at, performer_type,
                                       performer_id, source_application, payload)
            SELECT (c.n - 1) * 3 + s.seq, gen_random_uuid(), s.etype,
                   timestamptz '2026-05-20 22:00+02' + make_interval(days => c.n)
                       + make_interval(hours => CASE WHEN s.seq = 3 AND c.slow THEN 6 ELSE s.seq END),
                   now(), 'User', 'u-1', 'erp', {0}::jsonb
            FROM cases c CROSS JOIN steps s
            WHERE s.seq <> 2 OR c.slow;
            """,
            "{}"
        );

        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO ocel.event (id, source_id, type, ts, recorded_at, actor_key, actor_kind, source_application,
                                    attrs)
            SELECT 'n' || j.source_id, j.source_id, j.event_type, j.occurred_at, j.recorded_at, 'a:night', 'human',
                   'erp', {0}::jsonb
            FROM journal.event j;

            INSERT INTO ocel.object (id, type, first_seen, last_seen)
            SELECT 'night:' || n, 'night-run',
                   timestamptz '2026-05-20 22:00+02' + make_interval(days => n),
                   timestamptz '2026-05-21 04:00+02' + make_interval(days => n)
            FROM generate_series(1, 12) AS n;

            INSERT INTO ocel.e2o (event_id, object_id, qualifier)
            SELECT 'n' || j.source_id, 'night:' || ((j.source_id - 1) / 3 + 1), 'affected'
            FROM journal.event j;

            REFRESH MATERIALIZED VIEW analytics.object_timeline;
            REFRESH MATERIALIZED VIEW analytics.process_clock;
            REFRESH MATERIALIZED VIEW analytics.derived_end_activity;
            REFRESH MATERIALIZED VIEW analytics.object_lifecycle;
            """,
            "{}"
        );
    }
}
