using Microsoft.EntityFrameworkCore;
using ProcessAnalyzer.Web.Data;

namespace ProcessAnalyzer.Web.Analytics;

/// <summary>
/// The questions somebody asks who does not yet know what the company does.
/// <para>
/// The rest of the analytics answers "how does this process perform". These answer the prior question — which
/// processes are there at all, who runs them, what starts them and what comes out — and they answer it in words,
/// with no type keys, no ids and no pseudonyms anywhere in the output.
/// </para>
/// </summary>
public sealed class DiscoveryRepository
{
    private readonly IDbContextFactory<AppDbContext> _factory;

    public DiscoveryRepository(IDbContextFactory<AppDbContext> factory) => _factory = factory;

    /// <summary>
    /// Every process found in the log, ranked by how much work runs through it, with what starts it, what ends it,
    /// how long it takes and who is involved.
    /// </summary>
    /// <remarks>
    /// One row per object type, because in an object-centric log that is what a process is: a kind of thing that has
    /// a life. Nothing has to be configured for this to work — a new object type appears here the moment the first
    /// fact mentions it, which is the entire reason for mining rather than modelling.
    /// </remarks>
    public Task<List<Dictionary<string, object?>>> ProcessesAsync(Scope scope, CancellationToken ct) =>
        Query.RunAsync(
            _factory,
            """
            WITH lifecycle AS (
                SELECT l.object_type,
                       count(*)                                                        AS cases,
                       percentile_cont(0.5) WITHIN GROUP (ORDER BY l.duration_seconds) AS median_seconds,
                       avg(l.n_events)                                                 AS avg_steps,
                       count(*) FILTER (WHERE NOT l.has_human)::numeric / NULLIF(count(*), 0)     AS automatic_share,
                       min(l.first_ts)                                                 AS since
                FROM analytics.object_lifecycle l
                WHERE (@periodFrom::timestamptz IS NULL OR l.first_ts >= @periodFrom)
                  AND (@periodUntil::timestamptz IS NULL OR l.first_ts < @periodUntil)
                  AND analytics.case_in_scope(l.object_id, @scopeGroup, @scopeHasStep, @scopeWithoutStep)
                GROUP BY 1
            ),
            starts AS (
                SELECT DISTINCT ON (object_type) object_type, event_type, count(*) OVER (PARTITION BY object_type, event_type) AS n
                FROM analytics.object_timeline
                WHERE seq = 1
                  AND (@periodFrom::timestamptz IS NULL OR first_ts >= @periodFrom)
                  AND (@periodUntil::timestamptz IS NULL OR first_ts < @periodUntil)
                  AND analytics.case_in_scope(object_id, @scopeGroup, @scopeHasStep, @scopeWithoutStep)
                ORDER BY object_type, n DESC
            ),
            ends AS (
                SELECT DISTINCT ON (t.object_type) t.object_type, t.event_type,
                       count(*) OVER (PARTITION BY t.object_type, t.event_type) AS n
                FROM (
                    SELECT DISTINCT ON (object_id) object_id, object_type, event_type
                    FROM analytics.object_timeline
                    WHERE (@periodFrom::timestamptz IS NULL OR first_ts >= @periodFrom)
                      AND (@periodUntil::timestamptz IS NULL OR first_ts < @periodUntil)
                      AND analytics.case_in_scope(object_id, @scopeGroup, @scopeHasStep, @scopeWithoutStep)
                    ORDER BY object_id, seq DESC
                ) t
                ORDER BY t.object_type, n DESC
            ),
            roles AS (
                SELECT t.object_type, string_agg(DISTINCT r.role, ', ' ORDER BY r.role) AS involved
                FROM analytics.object_timeline t
                JOIN dim.actor_role r ON r.actor_key = t.actor_key
                WHERE (@periodFrom::timestamptz IS NULL OR t.first_ts >= @periodFrom)
                  AND (@periodUntil::timestamptz IS NULL OR t.first_ts < @periodUntil)
                  AND analytics.case_in_scope(t.object_id, @scopeGroup, @scopeHasStep, @scopeWithoutStep)
                GROUP BY 1
            )
            SELECT analytics.label_object(l.object_type)        AS prozess,
                   -- The key beside the name: a screen shows the name, a click has to send back something the next
                   -- query can scope to.
                   l.object_type                                AS technischer_typ,
                   -- A type with a handful of instances but thousands of events is a configuration record that many
                   -- cases refer to, not a case itself. Ranking it next to real processes reads as "one case that
                   -- takes 593 hours", which is the opposite of what the data says.
                   CASE WHEN l.cases <= 5 AND l.avg_steps > 50 THEN 'Stammdaten' ELSE 'Ablauf' END AS art,
                   l.cases                                      AS faelle,
                   round(l.median_seconds / 3600.0)             AS dauer_stunden,
                   round(l.avg_steps, 1)                        AS schritte,
                   round(l.automatic_share * 100)               AS automatisch_prozent,
                   analytics.label_activity(s.event_type)       AS beginnt_mit,
                   analytics.label_activity(e.event_type)       AS endet_mit,
                   coalesce(r.involved, 'niemand — läuft vollautomatisch') AS beteiligte,
                   l.since                                      AS seit
            FROM lifecycle l
            LEFT JOIN starts s ON s.object_type = l.object_type
            LEFT JOIN ends e ON e.object_type = l.object_type
            LEFT JOIN roles r ON r.object_type = l.object_type
            ORDER BY l.cases DESC
            """,
            ct,
            scope.Parameters()
        );

    /// <summary>
    /// Who decides about whose work: the person who started a case, and the person who approved or released it.
    /// </summary>
    /// <remarks>
    /// The question this answers — "who approves whose leave, and whose times" — is one an organisation is expected
    /// to know and frequently does not, because the answer lives in whoever happened to click. It is a relationship,
    /// not a performance measure: it says who depends on whom, and nothing about how well anybody works.
    /// <para>
    /// No k-anonymity floor here. A supervisor-to-employee relation is a pair by nature; suppressing pairs below five
    /// cases would suppress exactly the relations the question is about, and would answer "nobody approves anything".
    /// </para>
    /// </remarks>
    public Task<List<Dictionary<string, object?>>> DecisionsAsync(Scope scope, CancellationToken ct) =>
        Query.RunAsync(
            _factory,
            """
            WITH submitted AS (
                SELECT DISTINCT ON (t.object_id) t.object_id, t.object_type, t.actor_key, t.ts
                FROM analytics.object_timeline t
                WHERE t.actor_kind = 'human'
                  AND (@periodFrom::timestamptz IS NULL OR t.first_ts >= @periodFrom)
                  AND (@periodUntil::timestamptz IS NULL OR t.first_ts < @periodUntil)
                  AND analytics.case_in_scope(t.object_id, @scopeGroup, @scopeHasStep, @scopeWithoutStep)
                ORDER BY t.object_id, t.seq
            ),
            decided AS (
                SELECT t.object_id, t.actor_key, t.ts, t.event_type
                FROM analytics.object_timeline t
                WHERE t.actor_kind = 'human'
                  AND (@periodFrom::timestamptz IS NULL OR t.first_ts >= @periodFrom)
                  AND (@periodUntil::timestamptz IS NULL OR t.first_ts < @periodUntil)
                  AND analytics.case_in_scope(t.object_id, @scopeGroup, @scopeHasStep, @scopeWithoutStep)
                  AND (t.raw_event_type LIKE '%approved%' OR t.raw_event_type LIKE '%granted%'
                       OR t.raw_event_type LIKE '%released%' OR t.raw_event_type LIKE '%rejected%'
                       OR t.raw_event_type LIKE '%discarded%')
            ),
            pairs AS (
                SELECT s.object_type,
                       s.actor_key AS eingereicht_von,
                       d.actor_key AS entschieden_von,
                       d.event_type,
                       count(*) AS wie_oft,
                       percentile_cont(0.5) WITHIN GROUP (ORDER BY analytics.duration_seconds(s.object_type, s.ts, d.ts)) AS wartezeit
                FROM submitted s
                JOIN decided d ON d.object_id = s.object_id AND d.actor_key <> s.actor_key
                GROUP BY 1, 2, 3, 4
            )
            -- Ranked inside each process. Document approvals outnumber leave approvals by two orders of magnitude,
            -- and a flat top-100 would answer "who approves invoices" a hundred times and "who approves whose leave"
            -- never — which is the question that prompted this screen.
            SELECT analytics.label_object(object_type)              AS worum_geht_es,
                   analytics.person_with_role(eingereicht_von)      AS eingereicht_von,
                   analytics.person_with_role(entschieden_von)      AS entschieden_von,
                   -- The keys travel with the row: a person on screen has to lead somewhere, and a display name cannot
                   -- be sent back as a filter.
                   eingereicht_von                                  AS eingereicht_von_key,
                   entschieden_von                                  AS entschieden_von_key,
                   analytics.label_activity(event_type)         AS entscheidung,
                   wie_oft,
                   round((wartezeit / 3600)::numeric, 1)        AS wartezeit_stunden
            FROM (
                SELECT *, row_number() OVER (PARTITION BY object_type ORDER BY wie_oft DESC, wartezeit DESC) AS rang
                FROM pairs
            ) ranked
            WHERE rang <= 12
            ORDER BY object_type, wie_oft DESC
            """,
            ct,
            scope.Parameters()
        );

    /// <summary>
    /// Who works with whom: pairs of people who touch the same case, however far apart in the flow.
    /// </summary>
    /// <remarks>
    /// The handover matrix only shows direct passes. Real dependencies are wider — two people can never hand over to
    /// each other and still be unable to finish without one another. This is the pairing that shows that.
    /// </remarks>
    public Task<List<Dictionary<string, object?>>> CollaborationAsync(Scope scope, CancellationToken ct) =>
        Query.RunAsync(
            _factory,
            """
            WITH participants AS (
                SELECT DISTINCT t.object_id, t.object_type, t.actor_key
                FROM analytics.object_timeline t
                WHERE t.actor_kind = 'human'
                  AND (@periodFrom::timestamptz IS NULL OR t.first_ts >= @periodFrom)
                  AND (@periodUntil::timestamptz IS NULL OR t.first_ts < @periodUntil)
                  AND analytics.case_in_scope(t.object_id, @scopeGroup, @scopeHasStep, @scopeWithoutStep)
            ),
            -- Grouped on the keys, labelled afterwards. The other way round runs person_with_role over every pair
            -- row before aggregating — four correlated lookups times a few thousand pairs, which turned a 7 ms
            -- query into a 13 second request and blocked the whole page behind it.
            pairs AS (
                SELECT a.object_type, a.actor_key AS links, b.actor_key AS rechts, count(*) AS gemeinsame_faelle
                FROM participants a
                JOIN participants b ON b.object_id = a.object_id AND b.actor_key > a.actor_key
                GROUP BY 1, 2, 3
                ORDER BY 4 DESC
                LIMIT 60
            )
            SELECT analytics.label_object(object_type)     AS worum_geht_es,
                   analytics.person_with_role(links)       AS person,
                   analytics.person_with_role(rechts)      AS zusammen_mit,
                   links                                   AS person_key,
                   rechts                                  AS zusammen_mit_key,
                   gemeinsame_faelle
            FROM pairs
            ORDER BY gemeinsame_faelle DESC
            """,
            ct,
            scope.Parameters()
        );

    /// <summary>What the durations are measured against — the calendar, in words.</summary>
    /// <remarks>
    /// Shown on the screen because a calendar nobody can see is a number nobody can check. Every duration in this
    /// tool is working time, so if the calendar is wrong, every figure is wrong in the same direction and nothing
    /// about the result looks suspicious.
    /// </remarks>
    public Task<List<Dictionary<string, object?>>> CalendarAsync(CancellationToken ct) =>
        Query.RunAsync(_factory, "SELECT * FROM analytics.calendar_summary", ct);

    /// <summary>Which groups exist, how many people are in them, and how much of the recorded work they do.</summary>
    public Task<List<Dictionary<string, object?>>> RolesAsync(Scope scope, CancellationToken ct) =>
        Query.RunAsync(
            _factory,
            """
            WITH work AS (
                SELECT r.role, count(*) AS events, count(DISTINCT e.actor_key) AS active_actors
                FROM ocel.event e
                JOIN dim.actor_role r ON r.actor_key = e.actor_key
                WHERE (@periodFrom::timestamptz IS NULL OR e.ts >= @periodFrom)
                  AND (@periodUntil::timestamptz IS NULL OR e.ts < @periodUntil)
                  AND analytics.event_in_scope(e.id, @scopeGroup, @scopeHasStep, @scopeWithoutStep)
                GROUP BY 1
            ),
            headcount AS (
                SELECT p.role,
                       count(*) AS members,
                       -- Departed and blocked accounts stay in their groups forever. Counting them as present turns
                       -- "six people do this work" into a number nobody in the department recognises.
                       count(*) FILTER (WHERE a.is_active) AS members_present
                FROM dim.actor_primary_role p
                JOIN dim.actor a ON a.actor_key = p.actor_key
                GROUP BY 1
            )
            SELECT w.role                                             AS rolle,
                   coalesce(h.members_present, 0)                      AS personen,
                   coalesce(h.members, 0) - coalesce(h.members_present, 0) AS ausgeschieden,
                   w.active_actors                                    AS davon_aktiv,
                   w.events                                           AS schritte,
                   round(w.events::numeric / NULLIF(sum(w.events) OVER (), 0) * 100, 1) AS anteil_prozent
            FROM work w
            LEFT JOIN headcount h ON h.role = w.role
            ORDER BY w.events DESC
            """,
            ct,
            scope.Parameters()
        );

    /// <summary>Who does what: every step, and the role that performs it most.</summary>
    /// <remarks>
    /// This is the answer to "which workflows are executed by which people" — at the level of the group, which is
    /// both the useful level and the only one this system reports at.
    /// </remarks>
    public Task<List<Dictionary<string, object?>>> WhoDoesWhatAsync(Scope scope, CancellationToken ct) =>
        Query.RunAsync(
            _factory,
            """
            WITH per_role AS (
                SELECT t.event_type, r.role, count(*) AS events, count(DISTINCT t.object_id) AS cases
                FROM analytics.object_timeline t
                JOIN dim.actor_role r ON r.actor_key = t.actor_key
                WHERE (@periodFrom::timestamptz IS NULL OR t.first_ts >= @periodFrom)
                  AND (@periodUntil::timestamptz IS NULL OR t.first_ts < @periodUntil)
                  AND analytics.case_in_scope(t.object_id, @scopeGroup, @scopeHasStep, @scopeWithoutStep)
                GROUP BY 1, 2
            ),
            totals AS (SELECT event_type, sum(events) AS total FROM per_role GROUP BY 1),
            -- Where a step is most at home. A step belongs to a process before it belongs to a role, and without that
            -- the row is a dead end: a reader who sees who does something cannot get to the cases it happened in.
            home AS (
                SELECT DISTINCT ON (t.event_type) t.event_type, t.object_type
                FROM analytics.object_timeline t
                WHERE (@periodFrom::timestamptz IS NULL OR t.first_ts >= @periodFrom)
                  AND (@periodUntil::timestamptz IS NULL OR t.first_ts < @periodUntil)
                GROUP BY t.event_type, t.object_type
                ORDER BY t.event_type, count(*) DESC
            )
            SELECT analytics.label_activity(p.event_type)                       AS schritt,
                   p.role                                                       AS wer,
                   p.events                                                     AS wie_oft,
                   round(p.events::numeric / NULLIF(t.total, 0) * 100)          AS anteil_am_schritt,
                   -- More than one role doing the same step is either a shared responsibility or an unclear one,
                   -- and the difference is worth a look either way.
                   (SELECT count(*) FROM per_role q WHERE q.event_type = p.event_type) AS rollen_am_schritt,
                   p.event_type                                                 AS schritt_key,
                   h.object_type                                                AS prozess_key
            FROM per_role p
            JOIN totals t ON t.event_type = p.event_type
            LEFT JOIN home h ON h.event_type = p.event_type
            ORDER BY t.total DESC, p.events DESC
            """,
            ct,
            scope.Parameters()
        );

    /// <summary>
    /// What comes in and what goes out: the facts where something crosses the company boundary.
    /// </summary>
    /// <remarks>
    /// Handovers are the events that make a process start or finish somewhere else — data arriving from a leading
    /// system, a document leaving by mail, a declaration to an authority. They are the outline of the company's
    /// dealings with the outside, and they are recorded precisely because a read is not one.
    /// </remarks>
    public Task<List<Dictionary<string, object?>>> HandoversAsync(Scope scope, CancellationToken ct) =>
        Query.RunAsync(
            _factory,
            """
            SELECT CASE
                       WHEN e.type LIKE '%received%' THEN 'kommt rein'
                       ELSE 'geht raus'
                   END                                            AS richtung,
                   analytics.label_activity(analytics.activity_of(e.type, e.attrs)) AS vorgang,
                   count(*)                                       AS anzahl,
                   count(DISTINCT date_trunc('day', e.ts))        AS an_tagen,
                   coalesce(r.role, 'Automatischer Job')          AS ausgeloest_von,
                   max(e.ts)                                      AS zuletzt
            FROM ocel.event e
            LEFT JOIN dim.actor_role r ON r.actor_key = e.actor_key
            WHERE (e.type LIKE '%received%'
               OR e.type LIKE '%email-sent%'
               OR e.type LIKE '%handed-over%'
               OR e.type LIKE '%reported%'
               OR e.type LIKE '%requested%')
              AND (@periodFrom::timestamptz IS NULL OR e.ts >= @periodFrom)
              AND (@periodUntil::timestamptz IS NULL OR e.ts < @periodUntil)
              AND analytics.event_in_scope(e.id, @scopeGroup, @scopeHasStep, @scopeWithoutStep)
            GROUP BY 1, 2, 5
            ORDER BY anzahl DESC
            """,
            ct,
            scope.Parameters()
        );

    /// <summary>Who hands work to whom, between groups rather than between people.</summary>
    public Task<List<Dictionary<string, object?>>> RoleHandoverMatrixAsync(Scope scope, CancellationToken ct) =>
        Query.RunAsync(
            _factory,
            """
            SELECT f.role                        AS von,
                   t2.role                       AS an,
                   count(DISTINCT t.object_id)   AS faelle,
                   count(*)                      AS uebergaben
            FROM analytics.object_timeline t
            JOIN dim.actor_role f ON f.actor_key = t.prev_actor
            JOIN dim.actor_role t2 ON t2.actor_key = t.actor_key
            WHERE t.prev_actor IS NOT NULL AND t.prev_actor <> t.actor_key
              AND (@periodFrom::timestamptz IS NULL OR t.first_ts >= @periodFrom)
              AND (@periodUntil::timestamptz IS NULL OR t.first_ts < @periodUntil)
              AND analytics.case_in_scope(t.object_id, @scopeGroup, @scopeHasStep, @scopeWithoutStep)
            GROUP BY 1, 2
            HAVING count(DISTINCT t.object_id) >= 5
            ORDER BY uebergaben DESC
            LIMIT 40
            """,
            ct,
            scope.Parameters()
        );

    /// <summary>
    /// The process landscape: which processes hand work to which, and how much.
    /// </summary>
    /// <remarks>
    /// The mined pictures answer how one process runs, and the combined one is a wall of crossing edges. Neither answers
    /// the question somebody asks first: what does this company do end to end. That answer is in the log and needs no
    /// mining — an event that touches two kinds of object IS the handover between them, and there are thousands of them.
    /// <para>
    /// The direction comes from which of the two cases started earlier, decided per shared event and then by majority. A
    /// tour created after the order it carries is downstream of it, however the two are wired in the database. Near ties
    /// are reported rather than resolved by coin toss: <c>richtung_klarheit</c> says how one-sided the pair actually is.
    /// </para>
    /// </remarks>
    public Task<List<Dictionary<string, object?>>> LandscapeAsync(Scope scope, CancellationToken ct) =>
        Query.RunAsync(
            _factory,
            """
            WITH shared AS (
                SELECT ea.object_id   AS a_case,
                       la.object_type AS a_type,
                       la.first_ts    AS a_start,
                       eb.object_id   AS b_case,
                       lb.object_type AS b_type,
                       lb.first_ts    AS b_start,
                       ea.event_id
                FROM ocel.e2o ea
                JOIN ocel.e2o eb ON eb.event_id = ea.event_id AND eb.object_id <> ea.object_id
                JOIN analytics.object_lifecycle la ON la.object_id = ea.object_id
                JOIN analytics.object_lifecycle lb ON lb.object_id = eb.object_id
                WHERE la.object_type <> lb.object_type
                  AND (@periodFrom::timestamptz IS NULL OR la.first_ts >= @periodFrom)
                  AND (@periodUntil::timestamptz IS NULL OR la.first_ts < @periodUntil)
                  AND analytics.case_in_scope(ea.object_id, @scopeGroup, @scopeHasStep, @scopeWithoutStep)
            ),
            -- One row per unordered pair, so the same handover is not reported twice with the arrow reversed.
            pair AS (
                SELECT least(a_type, b_type)    AS links,
                       greatest(a_type, b_type) AS rechts,
                       count(DISTINCT event_id) AS ereignisse,
                       count(DISTINCT a_case)   AS faelle,
                       -- How often the left-hand process started first. A half means no direction at all.
                       avg(
                           CASE
                               WHEN a_type = least(a_type, b_type) AND a_start <= b_start THEN 1.0
                               WHEN a_type = greatest(a_type, b_type) AND b_start <= a_start THEN 1.0
                               ELSE 0.0
                           END
                       ) AS links_zuerst
                FROM shared
                GROUP BY 1, 2
            )
            SELECT CASE WHEN links_zuerst >= 0.5 THEN links ELSE rechts END AS von_key,
                   CASE WHEN links_zuerst >= 0.5 THEN rechts ELSE links END AS an_key,
                   analytics.label_object(CASE WHEN links_zuerst >= 0.5 THEN links ELSE rechts END) AS von,
                   analytics.label_object(CASE WHEN links_zuerst >= 0.5 THEN rechts ELSE links END) AS an,
                   ereignisse,
                   faelle,
                   round(abs(links_zuerst - 0.5) * 2, 2) AS richtung_klarheit
            FROM pair
            WHERE faelle >= 3
            ORDER BY ereignisse DESC
            LIMIT 60
            """,
            ct,
            scope.Parameters()
        );

    /// <summary>
    /// Event types that were registered in the source but never observed, and types observed but never labelled.
    /// </summary>
    /// <remarks>
    /// The honest counterpart to every other screen. A clean-looking model can mean "the process is simple" or "we
    /// only instrumented three steps of it", and only this list tells them apart.
    /// </remarks>
    public Task<List<Dictionary<string, object?>>> CoverageAsync(CancellationToken ct) =>
        Query.RunAsync(
            _factory,
            """
            -- Rendered through the same rule every screen uses, not through a raw join on event labels.
            --
            -- Two thirds of the types are the generic tier, which is named by rule from an entity noun plus a verb and
            -- has no event label by design. Joining on 'event' alone reported all of them as "not yet named" while they
            -- read as proper German everywhere else, and that turns the one honest page in the tool into the one that
            -- cries wolf. A type is unnamed here when the rule cannot name it either — the marker does that.
            SELECT r.type_name                                                  AS technischer_typ,
                   analytics.label_activity(r.type_name)                        AS bezeichnung,
                   r.event_count                                                AS beobachtet,
                   r.first_seen                                                 AS erstmals
            FROM ocel.type_registry r
            WHERE r.kind = 'event'
            ORDER BY (analytics.label_activity(r.type_name) LIKE '⚠%') DESC, r.event_count DESC
            """,
            ct
        );
}
