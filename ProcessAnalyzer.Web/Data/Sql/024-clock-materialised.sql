-- The clock and the end steps as stored answers, not as questions asked per row.
--
-- Both were views over the whole timeline, and both are read by a function that every duration goes through. So one
-- request for the transitions of a process aggregated the entire log once per row, and a page that had answered in
-- milliseconds took two minutes. The rule was right and the placement was wrong.
--
-- Materialized, indexed, and refreshed with the projection: the same answer, looked up instead of recomputed.

DROP VIEW IF EXISTS analytics.process_clock;

CREATE MATERIALIZED VIEW analytics.process_clock AS
WITH per_case AS (
    SELECT object_type, object_id, min(ts) AS first_ts, max(ts) AS last_ts
    FROM analytics.object_timeline
    GROUP BY 1, 2
),
observed AS (
    SELECT object_type,
           sum(EXTRACT(EPOCH FROM (last_ts - first_ts)))     AS wall_seconds,
           sum(analytics.biz_seconds(first_ts, last_ts))      AS biz_seconds
    FROM per_case
    GROUP BY 1
)
SELECT observed.object_type,
       COALESCE(
           (SELECT c.use_business_hours FROM analytics.process_closure c WHERE c.object_type = observed.object_type),
           observed.wall_seconds IS NULL
           OR observed.wall_seconds = 0
           OR observed.biz_seconds / NULLIF(observed.wall_seconds, 0) >= 0.5
       ) AS use_business_hours,
       observed.wall_seconds,
       observed.biz_seconds
FROM observed;

CREATE UNIQUE INDEX ux_process_clock ON analytics.process_clock (object_type);

DROP VIEW IF EXISTS analytics.derived_end_activity;

CREATE MATERIALIZED VIEW analytics.derived_end_activity AS
WITH per_step AS (
    SELECT t.object_type,
           t.event_type,
           count(*)                                    AS occurrences,
           count(*) FILTER (WHERE t.seq = last_seq.seq) AS as_last_step
    FROM analytics.object_timeline t
    JOIN (
        SELECT object_id, max(seq) AS seq FROM analytics.object_timeline GROUP BY 1
    ) AS last_seq ON last_seq.object_id = t.object_id
    GROUP BY 1, 2
)
SELECT object_type,
       event_type,
       occurrences,
       as_last_step,
       round(as_last_step::numeric / NULLIF(occurrences, 0), 3) AS share_as_last_step
FROM per_step
WHERE as_last_step::numeric / NULLIF(occurrences, 0) >= 0.8
  AND occurrences >= 5;

CREATE UNIQUE INDEX ux_derived_end_activity ON analytics.derived_end_activity (object_type, event_type);

-- The lifecycle reads both through the two functions, so it has to be rebuilt after them. Same definition as in
-- 023-process-clock.sql.
DROP MATERIALIZED VIEW IF EXISTS analytics.object_lifecycle;

CREATE MATERIALIZED VIEW analytics.object_lifecycle AS
WITH last_step AS (
    SELECT DISTINCT ON (object_id) object_id, event_type, ts
    FROM analytics.object_timeline
    ORDER BY object_id, seq DESC
)
SELECT
    t.object_id,
    t.object_type,
    MIN(t.ts) AS first_ts,
    MAX(t.ts) AS last_ts,
    COUNT(*) AS n_events,
    analytics.duration_seconds(t.object_type, MIN(t.ts), MAX(t.ts)) AS duration_seconds,
    analytics.biz_seconds(MIN(t.ts), MAX(t.ts)) AS biz_seconds,
    EXTRACT(EPOCH FROM (MAX(t.ts) - MIN(t.ts))) AS wall_seconds,
    bool_or(t.actor_kind = 'human') AS has_human,
    MAX(l.event_type) AS last_activity,
    analytics.case_is_open(t.object_type, MAX(l.event_type), MAX(t.ts)) AS is_open
FROM analytics.object_timeline t
JOIN last_step l ON l.object_id = t.object_id
GROUP BY 1, 2;

CREATE UNIQUE INDEX ux_lifecycle ON analytics.object_lifecycle (object_id);
CREATE INDEX ix_lifecycle_type ON analytics.object_lifecycle (object_type, first_ts);
CREATE INDEX ix_lifecycle_open ON analytics.object_lifecycle (object_type, is_open);
