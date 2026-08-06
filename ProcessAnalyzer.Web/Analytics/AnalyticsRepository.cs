using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProcessAnalyzer.Web.Data;

namespace ProcessAnalyzer.Web.Analytics;

/// <summary>
/// Every question the dashboard asks, as SQL against the analytics spine.
/// <para>
/// Raw SQL, not LINQ: these are percentiles, window functions and entropy over a materialized view. Expressing
/// them through EF would obscure what is actually computed, and the computation is the product.
/// </para>
/// <para>
/// <b>Nothing here aggregates across object types.</b> An event that touches five objects counts five times when
/// the log is flattened (convergence), and events of unrelated objects look sequential (divergence). A cross-type
/// sum is therefore not a coarse answer, it is a wrong one — so <c>objectType</c> is required, not optional.
/// </para>
/// </summary>
public sealed class AnalyticsRepository
{
    private readonly IDbContextFactory<AppDbContext> _factory;

    public AnalyticsRepository(IDbContextFactory<AppDbContext> factory) => _factory = factory;

    /// <summary>
    /// The groups a question can be narrowed to, with how much of the log each of them accounts for.
    /// </summary>
    /// <remarks>
    /// Only groups that appear in the log. A directory carries hundreds of groups and most never touch a case; listing
    /// them all would bury the four that matter, and every one of them would answer "no data".
    /// </remarks>
    public Task<List<Dictionary<string, object?>>> ActorGroupsAsync(CancellationToken ct) =>
        QueryAsync(
            """
            SELECT g.group_name              AS gruppe,
                   count(DISTINCT e.actor_key) AS personen,
                   count(*)                  AS schritte
            FROM dim.actor_group g
            JOIN ocel.event e ON e.actor_key = g.actor_key
            GROUP BY 1
            HAVING count(*) > 0
            ORDER BY count(*) DESC, 1
            """,
            ct
        );

    /// <summary>What is in the log at all: which object types exist, how much of each, and over what period.</summary>
    public Task<List<Dictionary<string, object?>>> InventoryAsync(CancellationToken ct) =>
        QueryAsync(
            """
            SELECT o.type AS object_type,
                   analytics.label_object(o.type) AS bezeichnung,
                   count(DISTINCT o.id)                AS objects,
                   count(r.event_id)                   AS events,
                   count(DISTINCT e.type)              AS activities,
                   min(e.ts)                           AS first_seen,
                   max(e.ts)                           AS last_seen
            FROM ocel.object o
            LEFT JOIN ocel.e2o r ON r.object_id = o.id
            LEFT JOIN ocel.event e ON e.id = r.event_id
            GROUP BY 1
            ORDER BY events DESC
            """,
            ct
        );

    /// <summary>Which activities an object type actually goes through, and who performs them.</summary>
    public Task<List<Dictionary<string, object?>>> ActivitiesAsync(
        string objectType,
        Scope scope,
        CancellationToken ct
    ) =>
        QueryAsync(
            """
            SELECT analytics.label_activity(event_type) AS event_type,
                   -- The technical key alongside the label: a screen shows the label, and a click has to send back
                   -- something the next query can filter on.
                   event_type                                                        AS event_type_key,
                   count(*)                                                          AS events,
                   count(DISTINCT object_id)                                         AS objects,
                   count(*) FILTER (WHERE actor_kind = 'human')::numeric / NULLIF(count(*), 0)  AS manual_share,
                   count(*) FILTER (WHERE seq = 1)                                   AS as_first_step
            FROM analytics.object_timeline
            WHERE object_type = @objectType
              AND (@periodFrom::timestamptz IS NULL OR first_ts >= @periodFrom)
              AND (@periodUntil::timestamptz IS NULL OR first_ts < @periodUntil)
              AND analytics.case_touched_by_group(object_id, @scopeGroup)
            GROUP BY 1, 2
            ORDER BY events DESC
            """,
            ct,
            [("objectType", objectType), .. scope.Parameters()]
        );

    /// <summary>
    /// Throughput. Percentiles, never a mean: these distributions are lognormal with a fat tail, and the mean sits
    /// where almost no case actually is. Open cases are excluded — counting them drags p95 down and makes a process
    /// look faster the busier it gets.
    /// </summary>
    public Task<List<Dictionary<string, object?>>> ThroughputAsync(
        string objectType,
        Scope scope,
        CancellationToken ct
    ) =>
        QueryAsync(
            """
            SELECT count(*)                                                              AS cases,
                   percentile_cont(0.5)  WITHIN GROUP (ORDER BY duration_seconds)        AS p50_seconds,
                   percentile_cont(0.8)  WITHIN GROUP (ORDER BY duration_seconds)        AS p80_seconds,
                   percentile_cont(0.95) WITHIN GROUP (ORDER BY duration_seconds)        AS p95_seconds,
                   max(duration_seconds)                                                 AS worst_seconds,
                   avg(n_events)                                                         AS avg_steps,
                   sum(wall_seconds - biz_seconds) / NULLIF(sum(wall_seconds), 0)        AS outside_hours_share
            FROM analytics.object_lifecycle
            WHERE object_type = @objectType
              AND (@periodFrom::timestamptz IS NULL OR first_ts >= @periodFrom)
              AND (@periodUntil::timestamptz IS NULL OR first_ts < @periodUntil)
              AND analytics.case_touched_by_group(object_id, @scopeGroup) AND NOT is_open
            """,
            ct,
            [("objectType", objectType), .. scope.Parameters()]
        );

    /// <summary>
    /// Where the calendar time goes, ranked by total consumed time rather than by the slowest edge. The slowest
    /// edge is almost always a rare exception; the edge that eats the most time across the population is where the
    /// money is.
    /// </summary>
    /// <remarks>
    /// The column is elapsed time, not waiting time. A journal event carries one timestamp, so service time is not
    /// recorded and the gap between two events contains both work and waiting. Labelling it "waiting" would be an
    /// invention.
    /// </remarks>
    public Task<List<Dictionary<string, object?>>> TransitionsAsync(
        string objectType,
        Scope scope,
        CancellationToken ct
    ) =>
        QueryAsync(
            """
            SELECT analytics.label_activity(prev_type) AS from_activity,
                   analytics.label_activity(event_type) AS to_activity,
                   prev_type                            AS from_activity_key,
                   event_type                           AS to_activity_key,
                   count(*) AS n,
                   percentile_cont(0.5) WITHIN GROUP (ORDER BY analytics.duration_seconds(object_type, prev_ts, ts)) AS median_seconds,
                   sum(analytics.duration_seconds(object_type, prev_ts, ts))                       AS total_seconds
            FROM analytics.object_timeline
            WHERE object_type = @objectType
              AND (@periodFrom::timestamptz IS NULL OR first_ts >= @periodFrom)
              AND (@periodUntil::timestamptz IS NULL OR first_ts < @periodUntil)
              AND analytics.case_touched_by_group(object_id, @scopeGroup) AND prev_type IS NOT NULL
            GROUP BY 1, 2, 3, 4
            ORDER BY total_seconds DESC
            LIMIT 25
            """,
            ct,
            [("objectType", objectType), .. scope.Parameters()]
        );

    /// <summary>
    /// Rework: activities that happen more than once for the same object. The single most unambiguous waste signal
    /// in an event log — nobody plans to approve the same document twice.
    /// </summary>
    public Task<List<Dictionary<string, object?>>> ReworkAsync(string objectType, Scope scope, CancellationToken ct) =>
        QueryAsync(
            """
            WITH per AS (
                SELECT object_id, event_type, count(*) AS c
                FROM analytics.object_timeline
                WHERE object_type = @objectType
                  AND (@periodFrom::timestamptz IS NULL OR first_ts >= @periodFrom)
                  AND (@periodUntil::timestamptz IS NULL OR first_ts < @periodUntil)
                  AND analytics.case_touched_by_group(object_id, @scopeGroup)
                GROUP BY 1, 2
            ),
            total AS (
                SELECT count(DISTINCT object_id)::numeric AS n
                FROM analytics.object_timeline WHERE object_type = @objectType
                  AND (@periodFrom::timestamptz IS NULL OR first_ts >= @periodFrom)
                  AND (@periodUntil::timestamptz IS NULL OR first_ts < @periodUntil)
                  AND analytics.case_touched_by_group(object_id, @scopeGroup)
            )
            SELECT analytics.label_activity(event_type) AS event_type,
                   event_type                                               AS event_type_key,
                   count(*) FILTER (WHERE c > 1)                            AS rework_cases,
                   count(*) FILTER (WHERE c > 1) / (SELECT n FROM total)    AS rework_rate,
                   sum(c - 1) FILTER (WHERE c > 1)                          AS extra_executions
            FROM per
            GROUP BY 1, 2
            HAVING count(*) FILTER (WHERE c > 1) > 0
            ORDER BY extra_executions DESC
            """,
            ct,
            [("objectType", objectType), .. scope.Parameters()]
        );

    /// <summary>
    /// Cases that went wrong once: a rejection, a discard, a failed attempt.
    /// </summary>
    /// <remarks>
    /// Repeat-of-the-same-activity is only one shape of rework, and in this process it is the rarer one. The common
    /// shape is a step that exists solely because something was wrong — a discarded release, a rejected request, a
    /// failed declaration — after which the case loops back. Those cases carry the cost, so they are counted on
    /// their own rather than hidden inside the variant list.
    /// </remarks>
    public Task<List<Dictionary<string, object?>>> NegativeOutcomesAsync(
        string objectType,
        Scope scope,
        CancellationToken ct
    ) =>
        QueryAsync(
            """
            WITH flagged AS (
                SELECT object_id, event_type
                FROM analytics.object_timeline
                WHERE object_type = @objectType
                  AND (@periodFrom::timestamptz IS NULL OR first_ts >= @periodFrom)
                  AND (@periodUntil::timestamptz IS NULL OR first_ts < @periodUntil)
                  AND analytics.case_touched_by_group(object_id, @scopeGroup)
                  AND (raw_event_type LIKE '%discarded%' OR raw_event_type LIKE '%rejected%'
                       OR attrs ->> 'status' = 'Error' OR attrs ->> 'succeeded' = 'false')
            ),
            total AS (SELECT count(*)::numeric AS n FROM analytics.object_lifecycle WHERE object_type = @objectType
              AND (@periodFrom::timestamptz IS NULL OR first_ts >= @periodFrom)
              AND (@periodUntil::timestamptz IS NULL OR first_ts < @periodUntil)
              AND analytics.case_touched_by_group(object_id, @scopeGroup))
            SELECT analytics.label_activity(f.event_type) AS event_type,
                   count(DISTINCT f.object_id)                             AS cases,
                   count(DISTINCT f.object_id) / (SELECT n FROM total)     AS case_share,
                   -- What the detour costs: those cases against the ones that never went wrong.
                   (SELECT percentile_cont(0.5) WITHIN GROUP (ORDER BY duration_seconds)
                    FROM analytics.object_lifecycle l
                    WHERE l.object_type = @objectType
                      AND (@periodFrom::timestamptz IS NULL OR l.first_ts >= @periodFrom)
                      AND (@periodUntil::timestamptz IS NULL OR l.first_ts < @periodUntil)
                      AND analytics.case_touched_by_group(l.object_id, @scopeGroup) AND l.object_id IN (SELECT object_id FROM flagged)) AS median_with,
                   (SELECT percentile_cont(0.5) WITHIN GROUP (ORDER BY duration_seconds)
                    FROM analytics.object_lifecycle l
                    WHERE l.object_type = @objectType
                      AND (@periodFrom::timestamptz IS NULL OR l.first_ts >= @periodFrom)
                      AND (@periodUntil::timestamptz IS NULL OR l.first_ts < @periodUntil)
                      AND analytics.case_touched_by_group(l.object_id, @scopeGroup) AND l.object_id NOT IN (SELECT object_id FROM flagged)) AS median_without
            FROM flagged f
            GROUP BY 1
            ORDER BY cases DESC
            """,
            ct,
            [("objectType", objectType), .. scope.Parameters()]
        );

    /// <summary>
    /// Variants: the distinct paths through the process, most frequent first. The cumulative share answers the
    /// question that matters — how many different ways of doing this actually exist, and how much of the work runs
    /// on the standard path.
    /// </summary>
    public Task<List<Dictionary<string, object?>>> VariantsAsync(
        string objectType,
        Scope scope,
        CancellationToken ct
    ) =>
        QueryAsync(
            """
            WITH v AS (
                SELECT object_id,
                       string_agg(analytics.label_activity(event_type), ' → ' ORDER BY ts, event_id) AS variant,
                       count(*) AS steps
                FROM analytics.object_timeline
                WHERE object_type = @objectType
                  AND (@periodFrom::timestamptz IS NULL OR first_ts >= @periodFrom)
                  AND (@periodUntil::timestamptz IS NULL OR first_ts < @periodUntil)
                  AND analytics.case_touched_by_group(object_id, @scopeGroup)
                GROUP BY object_id
            ),
            agg AS (
                SELECT v.variant, count(*) AS n, avg(l.duration_seconds) AS avg_seconds
                FROM v JOIN analytics.object_lifecycle l ON l.object_id = v.object_id
                GROUP BY v.variant
            )
            SELECT variant, n, avg_seconds,
                   n::numeric / NULLIF(sum(n) OVER (), 0)                                        AS share,
                   sum(n) OVER (ORDER BY n DESC ROWS UNBOUNDED PRECEDING)::numeric / NULLIF(sum(n) OVER (), 0) AS cum_share
            FROM agg
            ORDER BY n DESC
            LIMIT 20
            """,
            ct,
            [("objectType", objectType), .. scope.Parameters()]
        );

    /// <summary>
    /// What makes cases slow: for every step, how long the cases that contain it take compared with the ones that do
    /// not.
    /// </summary>
    /// <remarks>
    /// This is the difference between a dashboard and an answer. "The median is nine hours" is a fact nobody can act
    /// on; "cases in which the release was discarded take four times as long, and that is 380 hours over these 57
    /// cases" names a thing to change and what it is worth.
    /// <para>
    /// A comparison, not a correlation claim: the step may be a symptom rather than a cause, and the number says how
    /// much time sits with it, not that removing it saves that time. Both groups need at least five cases, because a
    /// median over three cases is an anecdote with a decimal point.
    /// </para>
    /// <para>
    /// Only finished cases. A case still running has no duration yet, and counting it would pull every group towards
    /// however long the log happens to be.
    /// </para>
    /// </remarks>
    public Task<List<Dictionary<string, object?>>> DriversAsync(string objectType, Scope scope, CancellationToken ct) =>
        QueryAsync(
            """
            WITH cases AS (
                SELECT object_id, duration_seconds
                FROM analytics.object_lifecycle
                WHERE object_type = @objectType
                  AND NOT is_open
                  AND (@periodFrom::timestamptz IS NULL OR first_ts >= @periodFrom)
                  AND (@periodUntil::timestamptz IS NULL OR first_ts < @periodUntil)
                  AND analytics.case_touched_by_group(object_id, @scopeGroup)
            ),
            steps AS (
                SELECT DISTINCT object_id, event_type
                FROM analytics.object_timeline
                WHERE object_type = @objectType
            ),
            matched AS (
                SELECT a.event_type,
                       EXISTS (SELECT 1 FROM steps s WHERE s.object_id = c.object_id AND s.event_type = a.event_type)
                           AS hit,
                       c.duration_seconds
                FROM (SELECT DISTINCT event_type FROM steps) a
                CROSS JOIN cases c
            ),
            per_group AS (
                SELECT event_type,
                       hit,
                       count(*) AS n,
                       percentile_cont(0.5) WITHIN GROUP (ORDER BY duration_seconds) AS median_seconds
                FROM matched
                GROUP BY 1, 2
            )
            SELECT analytics.label_activity(w.event_type)      AS event_type,
                   w.event_type                                AS event_type_key,
                   w.n                                         AS with_cases,
                   o.n                                         AS without_cases,
                   w.median_seconds                            AS median_with_seconds,
                   o.median_seconds                            AS median_without_seconds,
                   -- The whole point: how much time sits with this step across the cases that have it.
                   (w.median_seconds - o.median_seconds) * w.n  AS extra_seconds
            FROM per_group w
            JOIN per_group o ON o.event_type = w.event_type AND NOT o.hit
            WHERE w.hit
              AND w.n >= 5
              AND o.n >= 5
              AND w.median_seconds > o.median_seconds
            ORDER BY extra_seconds DESC
            LIMIT 15
            """,
            ct,
            [("objectType", objectType), .. scope.Parameters()]
        );

    /// <summary>
    /// Automation. The headline is straight-through processing — the share of cases with no human step at all —
    /// because a per-activity rate hides that almost every case still needs a person somewhere.
    /// </summary>
    public Task<List<Dictionary<string, object?>>> AutomationAsync(
        string objectType,
        Scope scope,
        CancellationToken ct
    ) =>
        QueryAsync(
            """
            SELECT count(*)                                                             AS cases,
                   count(*) FILTER (WHERE NOT has_human)::numeric / NULLIF(count(*), 0)  AS straight_through_share,
                   (SELECT count(*) FILTER (WHERE actor_kind = 'human')::numeric / NULLIF(count(*), 0)
                    FROM analytics.object_timeline WHERE object_type = @objectType
                      AND (@periodFrom::timestamptz IS NULL OR first_ts >= @periodFrom)
                      AND (@periodUntil::timestamptz IS NULL OR first_ts < @periodUntil)
                      AND analytics.case_touched_by_group(object_id, @scopeGroup))     AS manual_event_share
            FROM analytics.object_lifecycle
            WHERE object_type = @objectType
              AND (@periodFrom::timestamptz IS NULL OR first_ts >= @periodFrom)
              AND (@periodUntil::timestamptz IS NULL OR first_ts < @periodUntil)
              AND analytics.case_touched_by_group(object_id, @scopeGroup) AND NOT is_open
            """,
            ct,
            [("objectType", objectType), .. scope.Parameters()]
        );

    /// <summary>
    /// Automation candidates: frequent, manual, and predictable. The entropy term is what separates this from a
    /// frequency chart — a step whose next step is always the same is mechanical, while a step with many possible
    /// outcomes is a judgement call, and automating a judgement call produces a support queue.
    /// </summary>
    public Task<List<Dictionary<string, object?>>> AutomationCandidatesAsync(
        string objectType,
        Scope scope,
        CancellationToken ct
    ) =>
        QueryAsync(
            """
            WITH edges AS (
                SELECT prev_type AS a, event_type AS b, count(*) AS n
                FROM analytics.object_timeline
                WHERE object_type = @objectType
                  AND (@periodFrom::timestamptz IS NULL OR first_ts >= @periodFrom)
                  AND (@periodUntil::timestamptz IS NULL OR first_ts < @periodUntil)
                  AND analytics.case_touched_by_group(object_id, @scopeGroup) AND prev_type IS NOT NULL
                GROUP BY 1, 2
            ),
            p AS (
                SELECT a, n::numeric / NULLIF(sum(n) OVER (PARTITION BY a), 0) AS pr, count(*) OVER (PARTITION BY a) AS deg
                FROM edges
            ),
            ent AS (
                SELECT a, -sum(pr * ln(pr)) / NULLIF(ln(NULLIF(max(deg), 1)), 0) AS h FROM p GROUP BY a
            ),
            act AS (
                SELECT event_type,
                       count(*) AS freq,
                       count(*) FILTER (WHERE actor_kind = 'human')::numeric / NULLIF(count(*), 0) AS manual
                FROM analytics.object_timeline WHERE object_type = @objectType
                  AND (@periodFrom::timestamptz IS NULL OR first_ts >= @periodFrom)
                  AND (@periodUntil::timestamptz IS NULL OR first_ts < @periodUntil)
                  AND analytics.case_touched_by_group(object_id, @scopeGroup) GROUP BY 1
            )
            SELECT analytics.label_activity(act.event_type) AS event_type,
                   act.event_type                          AS event_type_key,
                   act.freq, act.manual, coalesce(ent.h, 0) AS outcome_entropy,
                   act.freq * act.manual * (1 - coalesce(ent.h, 0)) AS score
            FROM act LEFT JOIN ent ON ent.a = act.event_type
            WHERE act.manual > 0
            ORDER BY score DESC
            LIMIT 15
            """,
            ct,
            [("objectType", objectType), .. scope.Parameters()]
        );

    /// <summary>
    /// Who hands work to whom. Suppressed below five distinct objects per pair: this is a coordination map, not a
    /// performance file on individuals, and a cell built from two cases would be exactly that.
    /// </summary>
    public Task<List<Dictionary<string, object?>>> HandoversAsync(
        string objectType,
        Scope scope,
        CancellationToken ct
    ) =>
        QueryAsync(
            """
            -- Roles, never pseudonyms. A pseudonym forces the reader to look somebody up to understand the row, and
            -- what the row is actually about is which part of the organisation hands work to which.
            SELECT f.role AS from_actor, t2.role AS to_actor,
                   count(DISTINCT t.object_id) AS cases, count(*) AS handovers
            FROM analytics.object_timeline t
            JOIN dim.actor_role f ON f.actor_key = t.prev_actor
            JOIN dim.actor_role t2 ON t2.actor_key = t.actor_key
            WHERE t.object_type = @objectType
              AND (@periodFrom::timestamptz IS NULL OR t.first_ts >= @periodFrom)
              AND (@periodUntil::timestamptz IS NULL OR t.first_ts < @periodUntil)
              AND analytics.case_touched_by_group(t.object_id, @scopeGroup)
              AND t.prev_actor IS NOT NULL AND t.prev_actor <> t.actor_key
            GROUP BY 1, 2
            HAVING count(DISTINCT t.object_id) >= 5
            ORDER BY handovers DESC
            LIMIT 40
            """,
            ct,
            [("objectType", objectType), .. scope.Parameters()]
        );

    /// <summary>
    /// Where cases stop. An object whose last event is not a terminal activity has either finished quietly or been
    /// abandoned, and the difference is the single most actionable thing a process view can point at.
    /// </summary>
    public Task<List<Dictionary<string, object?>>> EndpointsAsync(
        string objectType,
        Scope scope,
        CancellationToken ct
    ) =>
        QueryAsync(
            """
            -- The last step is the one with the highest position, not the one with the latest timestamp: two
            -- events of a case can share a timestamp to the millisecond, and joining on time then counts the case
            -- twice and pushes the shares past 100%.
            WITH last_step AS (
                SELECT DISTINCT ON (t.object_id) t.object_id, t.event_type
                FROM analytics.object_timeline t
                WHERE t.object_type = @objectType
                  AND (@periodFrom::timestamptz IS NULL OR t.first_ts >= @periodFrom)
                  AND (@periodUntil::timestamptz IS NULL OR t.first_ts < @periodUntil)
                  AND analytics.case_touched_by_group(t.object_id, @scopeGroup)
                ORDER BY t.object_id, t.seq DESC
            )
            SELECT analytics.label_activity(s.event_type) AS last_activity,
                   count(*) AS cases,
                   count(*)::numeric / NULLIF(sum(count(*)) OVER (), 0) AS share,
                   percentile_cont(0.5) WITHIN GROUP (ORDER BY l.duration_seconds) AS median_seconds
            FROM last_step s
            JOIN analytics.object_lifecycle l ON l.object_id = s.object_id
            GROUP BY 1
            ORDER BY cases DESC
            """,
            ct,
            [("objectType", objectType), .. scope.Parameters()]
        );

    private Task<List<Dictionary<string, object?>>> QueryAsync(
        string sql,
        CancellationToken ct,
        params (string Name, object Value)[] parameters
    ) => Query.RunAsync(_factory, sql, ct, parameters);
}
