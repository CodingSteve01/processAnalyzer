using Microsoft.EntityFrameworkCore;
using ProcessAnalyzer.Web.Data;

namespace ProcessAnalyzer.Web.Analytics;

/// <summary>
/// The single case, and how the numbers move over time.
/// </summary>
/// <remarks>
/// The aggregates say 76 documents stop at the filing step. Without these two, that is where the conversation ends:
/// nobody can open one of the 76 and see what actually happened to it, and nobody can tell whether it was 76 last
/// month too. An analysis that cannot be checked against a single real case gets believed or dismissed as a whole,
/// and neither is useful.
/// </remarks>
public sealed class CaseRepository
{
    private readonly IDbContextFactory<AppDbContext> _factory;

    public CaseRepository(IDbContextFactory<AppDbContext> factory) => _factory = factory;

    /// <summary>
    /// Cases of one type, newest first. Optionally only those standing at a given activity, or only those that passed
    /// through one at any point.
    /// </summary>
    /// <remarks>
    /// The two activity filters answer different questions and both are needed. "Standing at" is the queue in front of
    /// a step. "Passed through" is what a reader wants after seeing a step in an aggregate: show me those cases. Using
    /// the first for the second would silently answer with the few cases that happened to stop there.
    /// </remarks>
    public Task<List<Dictionary<string, object?>>> ListAsync(
        string objectType,
        Scope scope,
        string? lastActivity,
        string? withActivity,
        string? search,
        CancellationToken ct
    ) =>
        Query.RunAsync(
            _factory,
            """
            WITH last_step AS (
                SELECT DISTINCT ON (t.object_id) t.object_id, t.event_type, t.seq
                FROM analytics.object_timeline t
                WHERE t.object_type = @objectType
                  AND (@periodFrom::timestamptz IS NULL OR t.first_ts >= @periodFrom)
                  AND (@periodUntil::timestamptz IS NULL OR t.first_ts < @periodUntil)
                  AND analytics.case_in_scope(t.object_id, @scopeGroup, @scopeHasStep, @scopeWithoutStep)
                ORDER BY t.object_id, t.seq DESC
            )
            SELECT split_part(l.object_id, ':', 2)                       AS nummer,
                   l.object_id                                           AS schluessel,
                   l.n_events                                            AS schritte,
                   l.first_ts                                            AS beginn,
                   l.last_ts                                             AS letzter_schritt,
                   round((l.duration_seconds / 3600)::numeric, 1)        AS dauer_stunden,
                   analytics.label_activity(s.event_type)                AS steht_bei,
                   l.is_open                                             AS laeuft_noch
            FROM analytics.object_lifecycle l
            JOIN last_step s ON s.object_id = l.object_id
            WHERE l.object_type = @objectType
              AND (@periodFrom::timestamptz IS NULL OR l.first_ts >= @periodFrom)
              AND (@periodUntil::timestamptz IS NULL OR l.first_ts < @periodUntil)
              AND analytics.case_in_scope(l.object_id, @scopeGroup, @scopeHasStep, @scopeWithoutStep)
              AND (@lastActivity = '' OR s.event_type = @lastActivity)
              AND (
                  @withActivity = ''
                  OR EXISTS (
                      SELECT 1 FROM analytics.object_timeline w
                      WHERE w.object_id = l.object_id AND w.event_type = @withActivity
                  )
              )
              AND (@search = '' OR split_part(l.object_id, ':', 2) ILIKE '%' || @search || '%')
            ORDER BY l.last_ts DESC
            LIMIT 200
            """,
            ct,
            [
                ("objectType", objectType),
                ("lastActivity", lastActivity ?? string.Empty),
                ("withActivity", withActivity ?? string.Empty),
                ("search", search ?? string.Empty),
                .. scope.Parameters(),
            ]
        );

    /// <summary>
    /// The whole business transaction around one case: its own steps plus those of everything it touches.
    /// </summary>
    /// <remarks>
    /// An object-centric log has no single case, and that is its strength — an order, its tour, its papers and its
    /// accounting rows each have a life of their own. But the question a person asks about an order is "what happened
    /// with this order", and the answer spans all of them: created, planned onto a tour, papers back from the driver,
    /// document filed, reconciled into accounting.
    /// <para>
    /// One hop, not two. The neighbours of an order are its tour, its notes and its rows; the neighbours of THOSE reach
    /// half the log within two steps, and a timeline of nine hundred events is not an answer.
    /// </para>
    /// </remarks>
    public Task<List<Dictionary<string, object?>>> ChainAsync(string objectId, CancellationToken ct) =>
        Query.RunAsync(
            _factory,
            """
            WITH seed AS (
                SELECT object_id, object_type FROM analytics.object_lifecycle WHERE object_id = @objectId
            ),
            scope AS (
                SELECT @objectId AS object_id
                UNION
                -- Only other KINDS of object. A neighbour of the same kind is a sibling, not a stage: two orders that
                -- share one declaration are not one transaction, and pulling them in made this table read like a list of
                -- other people's orders.
                SELECT DISTINCT other.object_id
                FROM ocel.e2o mine
                JOIN ocel.e2o other ON other.event_id = mine.event_id
                JOIN analytics.object_lifecycle l ON l.object_id = other.object_id
                CROSS JOIN seed
                WHERE mine.object_id = @objectId
                  AND l.object_type <> seed.object_type
            )
            SELECT analytics.label_object(t.object_type)                            AS gehoert_zu,
                   split_part(t.object_id, ':', 2)                                  AS nummer,
                   t.object_id                                                      AS schluessel,
                   t.object_id = @objectId                                          AS ist_dieser_fall,
                   analytics.label_activity(t.event_type)                           AS was,
                   t.event_type                                                     AS was_key,
                   t.object_type                                                    AS prozess_key,
                   t.ts                                                             AS wann,
                   analytics.person(t.actor_key)                                    AS wer,
                   t.actor_kind                                                     AS art
            FROM analytics.object_timeline t
            JOIN scope s ON s.object_id = t.object_id
            ORDER BY t.ts, t.object_id
            LIMIT 300
            """,
            ct,
            ("objectId", objectId)
        );

    /// <summary>Everything that happened to one object, in order, with the gap before each step.</summary>
    /// <remarks>
    /// The gap is what makes the row worth reading: a list of timestamps requires the reader to do the subtraction,
    /// and the whole question is where the time went.
    /// </remarks>
    public Task<List<Dictionary<string, object?>>> TimelineAsync(string objectId, CancellationToken ct) =>
        Query.RunAsync(
            _factory,
            """
            SELECT t.seq                                                           AS schritt,
                   analytics.label_activity(t.event_type)                          AS was,
                   t.event_type                                                    AS was_key,
                   t.ts                                                            AS wann,
                   analytics.person_with_role(t.actor_key)                         AS wer,
                   t.actor_kind                                                    AS art,
                   CASE WHEN t.prev_ts IS NULL THEN NULL
                        ELSE round((analytics.duration_seconds(t.object_type, t.prev_ts, t.ts) / 3600)::numeric, 1) END AS wartezeit_stunden,
                   -- The other objects the same event touched. This is what an object-centric log can show and a
                   -- flattened one cannot: the document and the workflow that moved it are one event, not two.
                   (SELECT string_agg(analytics.label_object(o.type) || ' ' || split_part(o.id, ':', 2), ', ')
                    FROM ocel.e2o r JOIN ocel.object o ON o.id = r.object_id
                    WHERE r.event_id = t.event_id AND r.object_id <> t.object_id)  AS auch_beteiligt
            FROM analytics.object_timeline t
            WHERE t.object_id = @objectId
            ORDER BY t.seq
            """,
            ct,
            ("objectId", objectId)
        );

    /// <summary>
    /// The same headline figures, per week. Without this everything the tool says is a snapshot, and "did it get
    /// better" — the question that follows every change — has no answer at all.
    /// </summary>
    public Task<List<Dictionary<string, object?>>> TrendAsync(string objectType, Scope scope, CancellationToken ct) =>
        Query.RunAsync(
            _factory,
            """
            WITH weekly AS (
                SELECT date_trunc('week', l.first_ts)::date                          AS woche,
                       count(*)                                                      AS faelle,
                       percentile_cont(0.5) WITHIN GROUP (ORDER BY l.duration_seconds) AS p50,
                       percentile_cont(0.95) WITHIN GROUP (ORDER BY l.duration_seconds) AS p95,
                       avg(l.n_events)                                               AS schritte,
                       count(*) FILTER (WHERE NOT l.has_human)::numeric / NULLIF(count(*), 0)   AS automatisch
                FROM analytics.object_lifecycle l
                -- Open cases are excluded here as everywhere: a week that is still running would show a p95 that
                -- only looks good because its slow cases have not finished yet.
                WHERE l.object_type = @objectType
                  AND (@periodFrom::timestamptz IS NULL OR l.first_ts >= @periodFrom)
                  AND (@periodUntil::timestamptz IS NULL OR l.first_ts < @periodUntil)
                  AND analytics.case_in_scope(l.object_id, @scopeGroup, @scopeHasStep, @scopeWithoutStep) AND NOT l.is_open
                GROUP BY 1
            ),
            rework AS (
                SELECT date_trunc('week', l.first_ts)::date AS woche,
                       count(DISTINCT t.object_id)::numeric / NULLIF(count(DISTINCT l.object_id), 0) AS quote
                FROM analytics.object_lifecycle l
                LEFT JOIN analytics.object_timeline t
                       ON t.object_id = l.object_id
                      AND (t.raw_event_type LIKE '%discarded%' OR t.raw_event_type LIKE '%rejected%')
                WHERE l.object_type = @objectType
                  AND (@periodFrom::timestamptz IS NULL OR l.first_ts >= @periodFrom)
                  AND (@periodUntil::timestamptz IS NULL OR l.first_ts < @periodUntil)
                  AND analytics.case_in_scope(l.object_id, @scopeGroup, @scopeHasStep, @scopeWithoutStep) AND NOT l.is_open
                GROUP BY 1
            )
            SELECT w.woche,
                   w.faelle,
                   round((w.p50 / 3600)::numeric, 1)          AS p50_stunden,
                   round((w.p95 / 3600)::numeric, 1)          AS p95_stunden,
                   round(w.schritte::numeric, 1)              AS schritte,
                   round(w.automatisch * 100)                 AS automatisch_prozent,
                   round(coalesce(r.quote, 0) * 100, 1)       AS ruecklaeufer_prozent
            FROM weekly w
            LEFT JOIN rework r ON r.woche = w.woche
            ORDER BY w.woche
            """,
            ct,
            [("objectType", objectType), .. scope.Parameters()]
        );
}
