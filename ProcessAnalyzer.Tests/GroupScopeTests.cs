using Microsoft.EntityFrameworkCore;
using ProcessAnalyzer.Web.Analytics;
using Xunit;

namespace ProcessAnalyzer.Tests;

/// <summary>
/// Every panel honours the group filter, or none should offer it.
/// </summary>
/// <remarks>
/// The predicate is written into each query rather than wrapped around them, which is fast and readable and one
/// forgotten line away from a page that shows unfiltered figures under a control claiming otherwise. Nothing fails
/// when a filter is merely absent — the number is simply the wrong answer to the question on screen.
/// <para>
/// So this asserts both directions for every scoped method: rows without a filter, and nothing at all for a group
/// that does not exist. A query that forgot the predicate keeps returning its rows and fails here.
/// </para>
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class GroupScopeTests
{
    private const string ObjectType = "document";
    private const string NoSuchGroup = "Gruppe die es nicht gibt";

    private readonly PostgresFixture _postgres;

    public GroupScopeTests(PostgresFixture postgres) => _postgres = postgres;

    public static TheoryData<string> ScopedAnalytics =>
        new(
            "activities",
            "throughput",
            "transitions",
            "rework",
            "negative-outcomes",
            "variants",
            "automation",
            "automation-candidates",
            "handovers",
            "endpoints"
        );

    public static TheoryData<string> ScopedDiscovery =>
        new("processes", "decisions", "collaboration", "roles", "who-does-what", "handovers", "role-handovers");

    [Theory]
    [MemberData(nameof(ScopedAnalytics))]
    public async Task AnalyticsPanel_IsEmptyForAGroupNobodyIsIn(string panel)
    {
        await SeedAsync();
        var repo = new AnalyticsRepository(_postgres.Factory);

        var unfiltered = await RunAnalyticsAsync(repo, panel, Scope.Everything);
        var filtered = await RunAnalyticsAsync(repo, panel, new Scope(Period.All, NoSuchGroup));

        Assert.NotEmpty(unfiltered);

        // Two panels are a single aggregate row rather than a list, so filtering everything away leaves the row and
        // empties the count. Asserting "no rows" for them would have to be written as an exception in the query
        // instead, and a summary that disappears is harder to read than one that says nothing happened.
        if (panel is "throughput" or "automation")
        {
            Assert.Equal(0L, Convert.ToInt64(Assert.Single(filtered)["cases"]));
            return;
        }

        Assert.Empty(filtered);
    }

    [Theory]
    [MemberData(nameof(ScopedDiscovery))]
    public async Task DiscoveryPanel_IsEmptyForAGroupNobodyIsIn(string panel)
    {
        await SeedAsync();
        var repo = new DiscoveryRepository(_postgres.Factory);

        var unfiltered = await RunDiscoveryAsync(repo, panel, Scope.Everything);
        var filtered = await RunDiscoveryAsync(repo, panel, new Scope(Period.All, NoSuchGroup));

        Assert.NotEmpty(unfiltered);
        Assert.Empty(filtered);
    }

    [Theory]
    [MemberData(nameof(ScopedAnalytics))]
    public async Task AnalyticsPanel_HonoursTheStepFilters(string panel)
    {
        await SeedAsync();
        var repo = new AnalyticsRepository(_postgres.Factory);

        // The activity key, not the label: the filter travels as the technical key because a label cannot be sent back
        // as a filter. Every seeded case contains this step, so "without it" must leave nothing.
        const string step = "demo.document.classification-resolved.v1";
        var withStep = new Scope(Period.All, null, HasStep: step);
        var withoutStep = new Scope(Period.All, null, WithoutStep: step);

        var kept = await RunAnalyticsAsync(repo, panel, withStep);
        var dropped = await RunAnalyticsAsync(repo, panel, withoutStep);

        Assert.NotEmpty(kept);

        if (panel is "throughput" or "automation")
        {
            Assert.Equal(0L, Convert.ToInt64(Assert.Single(dropped)["cases"]));
            return;
        }

        Assert.Empty(dropped);
    }

    [Fact]
    public async Task GroupThatExists_KeepsItsCases()
    {
        await SeedAsync();
        var repo = new AnalyticsRepository(_postgres.Factory);

        var rows = await repo.ActivitiesAsync(ObjectType, new Scope(Period.All, "Innendienst"), CancellationToken.None);

        Assert.NotEmpty(rows);
    }

    private static Task<List<Dictionary<string, object?>>> RunAnalyticsAsync(
        AnalyticsRepository repo,
        string panel,
        Scope scope
    ) =>
        panel switch
        {
            "activities" => repo.ActivitiesAsync(ObjectType, scope, CancellationToken.None),
            "throughput" => repo.ThroughputAsync(ObjectType, scope, CancellationToken.None),
            "transitions" => repo.TransitionsAsync(ObjectType, scope, CancellationToken.None),
            "rework" => repo.ReworkAsync(ObjectType, scope, CancellationToken.None),
            "negative-outcomes" => repo.NegativeOutcomesAsync(ObjectType, scope, CancellationToken.None),
            "variants" => repo.VariantsAsync(ObjectType, scope, CancellationToken.None),
            "automation" => repo.AutomationAsync(ObjectType, scope, CancellationToken.None),
            "automation-candidates" => repo.AutomationCandidatesAsync(ObjectType, scope, CancellationToken.None),
            "handovers" => repo.HandoversAsync(ObjectType, scope, CancellationToken.None),
            "endpoints" => repo.EndpointsAsync(ObjectType, scope, CancellationToken.None),
            _ => throw new ArgumentOutOfRangeException(nameof(panel), panel, "unknown panel"),
        };

    private static Task<List<Dictionary<string, object?>>> RunDiscoveryAsync(
        DiscoveryRepository repo,
        string panel,
        Scope scope
    ) =>
        panel switch
        {
            "processes" => repo.ProcessesAsync(scope, CancellationToken.None),
            "decisions" => repo.DecisionsAsync(scope, CancellationToken.None),
            "collaboration" => repo.CollaborationAsync(scope, CancellationToken.None),
            "roles" => repo.RolesAsync(scope, CancellationToken.None),
            "who-does-what" => repo.WhoDoesWhatAsync(scope, CancellationToken.None),
            "handovers" => repo.HandoversAsync(scope, CancellationToken.None),
            "role-handovers" => repo.RoleHandoverMatrixAsync(scope, CancellationToken.None),
            _ => throw new ArgumentOutOfRangeException(nameof(panel), panel, "unknown panel"),
        };

    /// <summary>
    /// Six cases, two people, six steps each: enough that every panel has something to say, including the ones with a
    /// floor under them — the handover matrix only reports a pair after five cases, and a seed of one case would make
    /// this test assert that an empty result is correct.
    /// </summary>
    private async Task SeedAsync()
    {
        await _postgres.ResetAsync();
        await using var db = await _postgres.Factory.CreateDbContextAsync(CancellationToken.None);

        // The step list is the same for every case: upload, classify, grant, discard, grant again, send. That covers a
        // handover between two people, a repeated activity (rework), a negative outcome and an end step.
        await db.Database.ExecuteSqlRawAsync(
            """
            WITH cases AS (SELECT n FROM generate_series(1, 6) AS n),
                 steps(seq, etype, actor) AS (
                     VALUES (1, 'demo.document.uploaded.v1', 'a:one'),
                            (2, 'demo.document.classification-resolved.v1', 'a:two'),
                            (3, 'demo.document.release-granted.v1', 'a:two'),
                            (4, 'demo.document.release-discarded.v1', 'a:one'),
                            (5, 'demo.document.release-granted.v1', 'a:two'),
                            (6, 'demo.document.email-sent.v1', 'a:one')
                 )
            INSERT INTO journal.event (source_id, event_id, event_type, occurred_at, recorded_at, performer_type,
                                       performer_id, source_application, payload)
            SELECT (c.n - 1) * 6 + s.seq,
                   gen_random_uuid(),
                   s.etype,
                   timestamptz '2026-05-20 08:00+02' + make_interval(hours => s.seq, days => c.n),
                   timestamptz '2026-05-20 08:00+02' + make_interval(hours => s.seq, days => c.n),
                   'User', 'u-1', 'erp', {0}::jsonb
            FROM cases c CROSS JOIN steps s;
            """,
            "{}"
        );

        await db.Database.ExecuteSqlRawAsync(
            """
            WITH cases AS (SELECT n FROM generate_series(1, 6) AS n),
                 steps(seq, etype, actor) AS (
                     VALUES (1, 'demo.document.uploaded.v1', 'a:one'),
                            (2, 'demo.document.classification-resolved.v1', 'a:two'),
                            (3, 'demo.document.release-granted.v1', 'a:two'),
                            (4, 'demo.document.release-discarded.v1', 'a:one'),
                            (5, 'demo.document.release-granted.v1', 'a:two'),
                            (6, 'demo.document.email-sent.v1', 'a:one')
                 )
            INSERT INTO ocel.event (id, source_id, type, ts, recorded_at, actor_key, actor_kind, source_application,
                                    attrs)
            SELECT 'e' || c.n || '-' || s.seq,
                   (c.n - 1) * 6 + s.seq,
                   s.etype,
                   timestamptz '2026-05-20 08:00+02' + make_interval(hours => s.seq, days => c.n),
                   timestamptz '2026-05-20 08:00+02' + make_interval(hours => s.seq, days => c.n),
                   s.actor, 'human', 'erp', {0}::jsonb
            FROM cases c CROSS JOIN steps s;
            """,
            "{}"
        );

        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO ocel.object (id, type, first_seen, last_seen)
            SELECT 'doc:' || n, 'document',
                   timestamptz '2026-05-20 09:00+02' + make_interval(days => n),
                   timestamptz '2026-05-20 14:00+02' + make_interval(days => n)
            FROM generate_series(1, 6) AS n;

            INSERT INTO ocel.e2o (event_id, object_id, qualifier)
            SELECT e.id, 'doc:' || split_part(substring(e.id from 2), '-', 1), 'affected'
            FROM ocel.event e;

            -- The directory survives a reset (it is not derived from the log), so these are idempotent.
            -- Source ids of their own: dim.actor is unique on source_id too, and another test in this collection
            -- seeds a person with 'u-1'. Colliding there fails in whichever test happens to run second.
            INSERT INTO dim.actor (actor_key, source_id, display_name, is_active)
            VALUES ('a:one', 'grp-u-1', 'Erste Person', true),
                   ('a:two', 'grp-u-2', 'Zweite Person', true)
            ON CONFLICT DO NOTHING;

            INSERT INTO dim.actor_group (actor_key, group_name)
            VALUES ('a:one', 'Innendienst'),
                   ('a:two', 'Innendienst')
            ON CONFLICT DO NOTHING;

            REFRESH MATERIALIZED VIEW analytics.object_timeline;
            REFRESH MATERIALIZED VIEW analytics.object_lifecycle;
            """
        );
    }
}
