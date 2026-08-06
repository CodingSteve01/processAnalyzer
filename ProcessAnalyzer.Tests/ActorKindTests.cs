using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProcessAnalyzer.Web.Analytics;
using Xunit;

namespace ProcessAnalyzer.Tests;

/// <summary>
/// A kind belongs to the actor, and a correction moves the figures.
/// </summary>
/// <remarks>
/// Every case here reproduces something that shipped. The source sends the same account id with performer type 'User'
/// and 'Device' — a driver confirming from the tablet and correcting at the desk — and the tool treated that pair as an
/// identity. dim.actor_role then had two rows for that person, joining it to the event log counted their work twice, and
/// the roles screen reported 17 420 steps under "Gerät" over a log holding 336 device events. "43 % of all steps are done
/// by the machine" was that double count, and nobody could have spotted it from the screen.
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class ActorKindTests
{
    /// <summary>The person who also uses a tablet: the same key arrives through two channels.</summary>
    private const string Driver = "a:driver";

    /// <summary>An account that looks like a person in the log and is a program.</summary>
    private const string Robot = "a:robot";

    private readonly PostgresFixture _postgres;

    public ActorKindTests(PostgresFixture postgres) => _postgres = postgres;

    [Fact]
    public async Task ActorWithTwoChannels_HasOneRow()
    {
        await SeedAsync();

        var rows = await ScalarAsync($"SELECT count(*) FROM dim.actor_role WHERE actor_key = '{Driver}'");

        Assert.Equal(1, rows);
    }

    [Fact]
    public async Task StepsAreNotCountedTwice()
    {
        await SeedAsync();

        // The join that produced the defect: every event, attributed to the role of its actor. It must add up to the
        // number of events, not to more.
        var attributed = await ScalarAsync(
            """
            SELECT count(*) FROM analytics.object_timeline t JOIN dim.actor_role r ON r.actor_key = t.actor_key
            """
        );
        var events = await ScalarAsync("SELECT count(*) FROM analytics.object_timeline");

        Assert.Equal(events, attributed);
    }

    [Fact]
    public async Task PersonWhoUsesATablet_IsAPerson()
    {
        await SeedAsync();

        // Not "the majority of their events came through a device": a tablet is a channel a person acted through, and
        // photographing a delivery note is the most manual work there is.
        Assert.Equal(1, await ScalarAsync($"SELECT analytics.is_person('{Driver}')::int"));
        Assert.Equal("human", await TextAsync($"SELECT kind FROM dim.actor_identity WHERE actor_key = '{Driver}'"));
    }

    [Fact]
    public async Task MarkingAnAccountAsABot_ChangesManualWork()
    {
        await SeedAsync();
        var repo = new AnalyticsRepository(_postgres.Factory);

        var before = await ManualShareAsync(repo);
        Assert.Equal(1.0, before, 3); // every event looks human in the log

        await CorrectAsync(Robot, "job");

        var after = await ManualShareAsync(repo);
        // Four of the eight events belong to the account now marked as a program.
        Assert.Equal(0.5, after, 3);
    }

    [Fact]
    public async Task CorrectionCanBeTakenBack()
    {
        await SeedAsync();
        await CorrectAsync(Robot, "job");
        Assert.Equal(0, await ScalarAsync($"SELECT analytics.is_person('{Robot}')::int"));

        await ExecuteAsync($"DELETE FROM dim.actor_kind_override WHERE actor_key = '{Robot}'");
        await ExecuteAsync("REFRESH MATERIALIZED VIEW dim.actor_identity");

        Assert.Equal(1, await ScalarAsync($"SELECT analytics.is_person('{Robot}')::int"));
    }

    [Fact]
    public async Task CaseRunByAProgramOnly_CountsAsStraightThrough()
    {
        await SeedAsync();
        await CorrectAsync(Robot, "job");
        await ExecuteAsync("REFRESH MATERIALIZED VIEW analytics.object_lifecycle");

        // The robot's own case has no person in it any more; the driver's still has one.
        var withoutPerson = await ScalarAsync("SELECT count(*) FROM analytics.object_lifecycle WHERE NOT has_human");

        Assert.Equal(1, withoutPerson);
    }

    private async Task<double> ManualShareAsync(AnalyticsRepository repo)
    {
        var rows = await repo.ActivitiesAsync("shipment", Scope.Everything, CancellationToken.None);
        var events = rows.Sum(row => Convert.ToDouble(row["events"]));
        var manual = rows.Sum(row => Convert.ToDouble(row["manual_share"]) * Convert.ToDouble(row["events"]));
        return manual / events;
    }

    private async Task CorrectAsync(string actorKey, string kind)
    {
        await ExecuteAsync(
            $"INSERT INTO dim.actor_kind_override (actor_key, kind) VALUES ('{actorKey}', '{kind}') "
                + "ON CONFLICT (actor_key) DO UPDATE SET kind = EXCLUDED.kind"
        );
        await ExecuteAsync("REFRESH MATERIALIZED VIEW dim.actor_identity");
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var db = await _postgres.Factory.CreateDbContextAsync(CancellationToken.None);
        await db.Database.ExecuteSqlRawAsync(sql);
    }

    private async Task<double> ScalarAsync(string sql)
    {
        await using var db = await _postgres.Factory.CreateDbContextAsync(CancellationToken.None);
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToDouble(await command.ExecuteScalarAsync());
    }

    private async Task<string?> TextAsync(string sql)
    {
        await using var db = await _postgres.Factory.CreateDbContextAsync(CancellationToken.None);
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (await command.ExecuteScalarAsync())?.ToString();
    }

    [Fact]
    public async Task ApproverIsNotTheSubmitter()
    {
        await SeedDecisionAsync(withEdit: false);
        var repo = new DiscoveryRepository(_postgres.Factory);

        var rows = await repo.DecisionsAsync(Scope.Everything, CancellationToken.None);

        // The case starts with a release by the manager and is released again by a colleague afterwards. Neither of them
        // submitted anything, so there is no submitter and no pair. The screen used to name the manager as the person who
        // submitted and the colleague as the one deciding over him — the exact opposite of what happened.
        Assert.Empty(rows);
    }

    [Fact]
    public async Task DecisionAfterTheSubmission_IsThePair()
    {
        await SeedDecisionAsync(withEdit: true);
        var repo = new DiscoveryRepository(_postgres.Factory);

        var rows = await repo.DecisionsAsync(Scope.Everything, CancellationToken.None);

        // Now a clerk edited the document between the two releases. That edit is the submission, and the waiting time runs
        // forward from it rather than backwards to a release that had already happened.
        var row = Assert.Single(rows);
        Assert.Equal("a:clerk", row["eingereicht_von_key"]?.ToString());
        Assert.Equal("a:colleague", row["entschieden_von_key"]?.ToString());
        Assert.True(Convert.ToDouble(row["wartezeit_stunden"]) >= 0);
    }

    /// <summary>
    /// One document: released by the manager, optionally edited by a clerk, then released by a colleague.
    /// </summary>
    private async Task SeedDecisionAsync(bool withEdit)
    {
        await _postgres.ResetAsync();
        await using var db = await _postgres.Factory.CreateDbContextAsync(CancellationToken.None);

        await db.Database.ExecuteSqlRawAsync(
            @"INSERT INTO journal.event (source_id, event_id, event_type, occurred_at, recorded_at, performer_type,
                                         performer_id, source_application, payload)
              SELECT n, gen_random_uuid(), 'demo.doc.step.v1',
                     timestamptz '2026-05-20 08:00+02' + make_interval(hours => n), now(), 'User', 'p' || n, 'erp',
                     {0}::jsonb
              FROM generate_series(1, 3) AS n;",
            "{}"
        );

        var edit = withEdit
            ? ", ('x2', 2, 'demo.doc.changed.v1', '2026-05-20 10:00+02', now(), 'a:clerk', 'human', 'erp', {0}::jsonb)"
            : string.Empty;

        await db.Database.ExecuteSqlRawAsync(
            @"INSERT INTO ocel.event (id, source_id, type, ts, recorded_at, actor_key, actor_kind, source_application,
                                      attrs)
              VALUES
                  ('x1', 1, 'demo.doc.release-granted.v1', '2026-05-20 09:00+02', now(), 'a:manager', 'human', 'erp', {0}::jsonb)
                  "
                + edit
                + @"
                  , ('x3', 3, 'demo.doc.release-granted.v1', '2026-05-20 11:00+02', now(), 'a:colleague', 'human', 'erp', {0}::jsonb);

              INSERT INTO ocel.object (id, type, first_seen, last_seen)
              VALUES ('doc:1', 'document', '2026-05-20 09:00+02', '2026-05-20 11:00+02');

              INSERT INTO ocel.e2o (event_id, object_id, qualifier)
              SELECT e.id, 'doc:1', 'affected' FROM ocel.event e;

              REFRESH MATERIALIZED VIEW analytics.object_timeline;
              REFRESH MATERIALIZED VIEW dim.actor_identity;
              REFRESH MATERIALIZED VIEW analytics.process_clock;
              REFRESH MATERIALIZED VIEW analytics.derived_end_activity;
              REFRESH MATERIALIZED VIEW analytics.object_lifecycle;",
            "{}"
        );
    }

    /// <summary>
    /// Two cases, two actors, eight events. The driver acts through two channels, the robot through one that lies.
    /// </summary>
    private async Task SeedAsync()
    {
        await _postgres.ResetAsync();
        await using var db = await _postgres.Factory.CreateDbContextAsync(CancellationToken.None);

        // The mirror first: ocel.event references it, which is the contract that keeps a projected event traceable to the
        // row it came from.
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO journal.event (source_id, event_id, event_type, occurred_at, recorded_at, performer_type,
                                       performer_id, source_application, payload)
            SELECT n, gen_random_uuid(),
                   'demo.shipment.step.v1',
                   timestamptz '2026-05-20 08:00+02' + make_interval(hours => n),
                   now(),
                   CASE WHEN n IN (2, 3) THEN 'Device' ELSE 'User' END,
                   CASE WHEN n <= 4 THEN 'driver' ELSE 'robot' END,
                   'erp', {0}::jsonb
            FROM generate_series(1, 8) AS n;
            """,
            "{}"
        );

        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO ocel.event (id, source_id, type, ts, recorded_at, actor_key, actor_kind, source_application,
                                    attrs)
            VALUES
                -- The driver: at the desk, then twice from the tablet, then at the desk again.
                ('d1', 1, 'demo.shipment.created.v1',   '2026-05-20 08:00+02', now(), 'a:driver', 'human',  'erp', {0}::jsonb),
                ('d2', 2, 'demo.shipment.reported.v1',  '2026-05-20 09:00+02', now(), 'a:driver', 'device', 'app', {0}::jsonb),
                ('d3', 3, 'demo.shipment.reported.v1',  '2026-05-20 10:00+02', now(), 'a:driver', 'device', 'app', {0}::jsonb),
                ('d4', 4, 'demo.shipment.closed.v1',    '2026-05-20 11:00+02', now(), 'a:driver', 'human',  'erp', {0}::jsonb),
                -- The robot: four events, all of them claiming to be a person.
                ('r1', 5, 'demo.shipment.created.v1',   '2026-05-21 08:00+02', now(), 'a:robot',  'human',  'erp', {0}::jsonb),
                ('r2', 6, 'demo.shipment.reported.v1',  '2026-05-21 09:00+02', now(), 'a:robot',  'human',  'erp', {0}::jsonb),
                ('r3', 7, 'demo.shipment.reported.v1',  '2026-05-21 10:00+02', now(), 'a:robot',  'human',  'erp', {0}::jsonb),
                ('r4', 8, 'demo.shipment.closed.v1',    '2026-05-21 11:00+02', now(), 'a:robot',  'human',  'erp', {0}::jsonb);

            INSERT INTO ocel.object (id, type, first_seen, last_seen) VALUES
                ('ship:1', 'shipment', '2026-05-20 08:00+02', '2026-05-20 11:00+02'),
                ('ship:2', 'shipment', '2026-05-21 08:00+02', '2026-05-21 11:00+02');

            INSERT INTO ocel.e2o (event_id, object_id, qualifier)
            SELECT e.id, CASE WHEN e.id LIKE 'd%' THEN 'ship:1' ELSE 'ship:2' END, 'affected'
            FROM ocel.event e;

            REFRESH MATERIALIZED VIEW analytics.object_timeline;
            REFRESH MATERIALIZED VIEW dim.actor_identity;
            REFRESH MATERIALIZED VIEW analytics.process_clock;
            REFRESH MATERIALIZED VIEW analytics.derived_end_activity;
            REFRESH MATERIALIZED VIEW analytics.object_lifecycle;
            """,
            "{}"
        );
    }
}
