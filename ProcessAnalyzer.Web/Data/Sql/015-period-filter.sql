-- A period filter that scopes the CASE, not the event.
--
-- Filtering events by date would cut cases in half: a case that started before the window would show a truncated
-- lifecycle, its first step would be whatever happened to fall inside, and every duration computed from it would be
-- wrong rather than merely partial. So a case is in scope when it STARTED in the window, and then all of its events
-- are, which is also how the weekly trend has always grouped.
--
-- object_lifecycle already carries first_ts. The timeline did not, so every query over it would have needed a
-- subquery against the lifecycle. One window function here instead: the predicate becomes identical everywhere, and
-- an identical predicate is one that cannot be applied inconsistently by accident.

DROP MATERIALIZED VIEW IF EXISTS analytics.object_timeline CASCADE;

CREATE MATERIALIZED VIEW analytics.object_timeline AS
SELECT
    r.object_id,
    o.type AS object_type,
    e.id AS event_id,
    analytics.activity_of(e.type, e.attrs) AS event_type,
    e.type AS raw_event_type,
    e.ts,
    e.actor_key,
    e.actor_kind,
    e.attrs,
    lag(analytics.activity_of(e.type, e.attrs)) OVER w AS prev_type,
    lag(e.ts) OVER w AS prev_ts,
    lag(e.actor_key) OVER w AS prev_actor,
    row_number() OVER w AS seq,
    -- When the case this event belongs to began. Not the event's own timestamp: that is what makes the filter
    -- scope whole cases.
    min(e.ts) OVER (PARTITION BY r.object_id) AS first_ts
FROM ocel.e2o r
JOIN ocel.event e ON e.id = r.event_id
JOIN ocel.object o ON o.id = r.object_id
WINDOW w AS (PARTITION BY r.object_id ORDER BY e.ts, e.id);

CREATE UNIQUE INDEX ux_timeline ON analytics.object_timeline (object_id, event_id);
CREATE INDEX ix_timeline_type ON analytics.object_timeline (object_type, ts);
CREATE INDEX ix_timeline_event ON analytics.object_timeline (event_type);
-- The filter's own index: every scoped query narrows by type and case start.
CREATE INDEX ix_timeline_period ON analytics.object_timeline (object_type, first_ts);

-- object_lifecycle is derived from the timeline, so the CASCADE above took it with it. Recreated verbatim: this
-- migration changes what the timeline exposes, not what a lifecycle means.
CREATE MATERIALIZED VIEW analytics.object_lifecycle AS
SELECT
    t.object_id,
    t.object_type,
    MIN(t.ts) AS first_ts,
    MAX(t.ts) AS last_ts,
    COUNT(*) AS n_events,
    analytics.biz_seconds(MIN(t.ts), MAX(t.ts)) AS biz_seconds,
    EXTRACT(EPOCH FROM (MAX(t.ts) - MIN(t.ts))) AS wall_seconds,
    bool_or(t.actor_kind = 'human') AS has_human,
    MAX(t.ts) > (SELECT MAX(ts) - interval '3 days' FROM ocel.event) AS is_open
FROM analytics.object_timeline t
GROUP BY 1, 2;

CREATE UNIQUE INDEX ux_lifecycle ON analytics.object_lifecycle (object_id);
CREATE INDEX ix_lifecycle_type ON analytics.object_lifecycle (object_type, first_ts);
