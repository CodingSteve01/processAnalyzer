-- Which clock a process is measured by.
--
-- Every duration in this tool was working time against one calendar: the office hours read from the source. For an
-- office process that is right, and for anything else it is a machine for producing zeros. The first real log made that
-- obvious: 98 % of the elapsed time of an order lies outside the office calendar, because the work happens at night —
-- so the median, the 80th and the 95th percentile all came out as "0.0 h" and the whole page said nothing.
--
-- A process therefore carries its own clock:
--   business hours — the office calendar. Waiting on a person, and a night is not waiting.
--   round the clock — the process runs when the work runs. A night IS the duration.
--
-- Derived by default, because nobody should have to configure a process for it to be readable, and overridable per
-- process, because the derivation only sees what has happened so far.

ALTER TABLE analytics.process_closure
    ADD COLUMN IF NOT EXISTS use_business_hours boolean NULL;

COMMENT ON COLUMN analytics.process_closure.use_business_hours IS
    'NULL = derive from the data. true = office calendar. false = round the clock.';

-- The clock a process gets when nobody configured one: round the clock as soon as most of its elapsed time falls
-- outside the office calendar. Half is the line, because a process split evenly between day and night is an office
-- process with overtime, while one at 98 % is simply not an office process.
CREATE OR REPLACE VIEW analytics.process_clock AS
-- Read from the timeline, not from the lifecycle: the lifecycle measures in the clock this view decides, and a view
-- cannot depend on what depends on it.
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
           -- No elapsed time at all (every case is a single step) keeps the office calendar: there is nothing to
           -- measure either way, and the office calendar is the more conservative claim.
           observed.wall_seconds IS NULL
           OR observed.wall_seconds = 0
           OR observed.biz_seconds / NULLIF(observed.wall_seconds, 0) >= 0.5
       ) AS use_business_hours,
       observed.wall_seconds,
       observed.biz_seconds
FROM observed;

/*
 * The duration between two moments, in the clock of the process it belongs to.
 *
 * One function, so no screen can measure differently from another. Everything that reports a duration goes through it.
 */
CREATE OR REPLACE FUNCTION analytics.duration_seconds(
    p_object_type text,
    p_from timestamptz,
    p_to timestamptz
)
RETURNS double precision
LANGUAGE sql
STABLE
AS $$
    SELECT CASE
        WHEN COALESCE((SELECT c.use_business_hours FROM analytics.process_clock c WHERE c.object_type = p_object_type), true)
            THEN analytics.biz_seconds(p_from, p_to)
        ELSE EXTRACT(EPOCH FROM (p_to - p_from))
    END;
$$;

-- The lifecycle measures in the clock of its process now. Rebuilt rather than altered, for the same reason as in
-- 022-case-closure.sql: a materialized view cannot gain a column. The closing rule from that file is unchanged.
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
    -- Both are kept: the duration a screen reports, and the two raw measures it was derived from. Without them nobody
    -- can check which clock produced a number, and "your durations are wrong" would be unanswerable.
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
