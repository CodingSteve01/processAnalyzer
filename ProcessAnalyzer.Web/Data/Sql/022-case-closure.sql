-- When is a case finished?
--
-- Until now: when nothing happened to it for three days. That is a stopgap, and against a young mirror it is worse
-- than that — with eleven hours of data every case is open by definition, so throughput, percentiles, the trend and
-- the automation rate all report on an empty set. An order whose last step was "Rückmeldung übernommen" is finished as
-- far as the business is concerned and was still counted as in flight.
--
-- Two rules instead, in this order:
--
--   1. The last step is an END STEP of that process. Then the case is done, whatever the clock says. This is how a
--      process actually ends: something happened that nothing follows.
--   2. Otherwise: silence. No step for N hours means nobody is working on it any more. Still a heuristic, but only
--      the fallback now, and configurable per process rather than three days for everything.
--
-- End steps are derived from the data and can be overridden. Derived, because a process nobody configured must still
-- work — that is the whole point of mining. Overridable, because the derivation can only see what has happened, and
-- somebody who knows the process knows an end step that has not occurred yet this week.

CREATE TABLE IF NOT EXISTS analytics.process_closure (
    object_type   text PRIMARY KEY,
    -- NULL = derive from the data. An empty array = this process has no end step, fall back to silence only.
    end_activities text[] NULL,
    silence_hours  int    NOT NULL DEFAULT 72,
    note           text   NULL,
    updated_at     timestamptz NOT NULL DEFAULT now()
);

COMMENT ON TABLE analytics.process_closure IS
    'Per process: which steps end a case, and after how much silence a case counts as abandoned.';

-- The steps that end a case, as the data has it.
--
-- A step qualifies when it is the LAST step in at least four fifths of the cases it appears in. That is the shape of an
-- ending: it happens, and then nothing. A threshold rather than "never followed by anything", because one corrected
-- case would otherwise disqualify a step that ends a thousand others.
CREATE OR REPLACE VIEW analytics.derived_end_activity AS
WITH per_step AS (
    SELECT t.object_type,
           t.event_type,
           count(*)                                                      AS occurrences,
           count(*) FILTER (WHERE t.seq = last_seq.seq)                   AS as_last_step
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
  -- And at least five occurrences. A step seen once is trivially "always last", and against a mirror that is a few
  -- hours old that would declare half the vocabulary an ending and close cases that have barely started. Below the
  -- threshold the silence rule decides, which errs towards "still running" — the safer error, because a case wrongly
  -- counted as finished shortens every duration that includes it.
  AND occurrences >= 5;

/*
 * Whether a case is still running.
 *
 * Kept as a function rather than inlined into the lifecycle view, so the rule has one home: it is read by the view,
 * by the naming of open cases on screen, and by anybody checking a single case by hand.
 */
CREATE OR REPLACE FUNCTION analytics.case_is_open(
    p_object_type text,
    p_last_activity text,
    p_last_ts timestamptz
)
RETURNS boolean
LANGUAGE sql
STABLE
AS $$
    WITH rule AS (
        SELECT COALESCE(
                   (SELECT c.end_activities FROM analytics.process_closure c WHERE c.object_type = p_object_type),
                   (SELECT array_agg(d.event_type) FROM analytics.derived_end_activity d WHERE d.object_type = p_object_type)
               ) AS end_activities,
               COALESCE(
                   (SELECT c.silence_hours FROM analytics.process_closure c WHERE c.object_type = p_object_type),
                   72
               ) AS silence_hours
    )
    SELECT NOT (
        -- Ended properly …
        (SELECT p_last_activity = ANY(COALESCE(rule.end_activities, ARRAY[]::text[])) FROM rule)
        -- … or abandoned. Measured against the newest event in the log, not against wall-clock time: a mirror that
        -- stopped pulling would otherwise close every case in it overnight.
        OR p_last_ts <= (SELECT max(ts) FROM ocel.event) - make_interval(hours => (SELECT rule.silence_hours FROM rule))
    );
$$;

-- The lifecycle view carries the last step now, because the closing rule needs it. Rebuilt rather than altered: a
-- materialized view cannot gain a column, and everything downstream reads it by name.
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
