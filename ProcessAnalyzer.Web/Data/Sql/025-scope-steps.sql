-- Narrowing by what happened in a case, and by what did not.
--
-- The group filter answered "whose work". The question that follows it is the one that actually explains a number:
-- these cases went through that step and those did not, so what is different about them. Without the negative half a
-- reader can only ever look at one group and guess at the other.
--
-- One function for the whole case scope, so a query cannot honour half of it. It replaces case_touched_by_group at every
-- call site; that function stays for a release as a thin wrapper, because a query somewhere that still calls it should
-- keep working rather than silently ignore a filter.
CREATE OR REPLACE FUNCTION analytics.case_in_scope(
    p_object_id text,
    p_group text,
    p_has_step text,
    p_without_step text
)
RETURNS boolean
LANGUAGE sql
STABLE
AS $$
    SELECT
        -- Whose work. NULL is "everybody", never "nobody".
        (
            p_group IS NULL
            OR EXISTS (
                SELECT 1
                FROM ocel.e2o r
                JOIN ocel.event e ON e.id = r.event_id
                JOIN dim.actor_group g ON g.actor_key = e.actor_key
                WHERE r.object_id = p_object_id
                  AND g.group_name = p_group
            )
        )
        -- Went through this step at some point. Not "stopped there": a case that passed a step and moved on is exactly
        -- what somebody means when they ask for the cases with that step in them.
        AND (
            p_has_step IS NULL
            OR EXISTS (
                SELECT 1 FROM analytics.object_timeline t
                WHERE t.object_id = p_object_id AND t.event_type = p_has_step
            )
        )
        -- And never went through that one. The half that makes a comparison possible.
        AND (
            p_without_step IS NULL
            OR NOT EXISTS (
                SELECT 1 FROM analytics.object_timeline t
                WHERE t.object_id = p_object_id AND t.event_type = p_without_step
            )
        );
$$;

-- Kept so any query that has not been moved over yet still filters by group instead of quietly returning everything.
CREATE OR REPLACE FUNCTION analytics.case_touched_by_group(p_object_id text, p_group text)
RETURNS boolean
LANGUAGE sql
STABLE
AS $$
    SELECT analytics.case_in_scope(p_object_id, p_group, NULL, NULL);
$$;

-- The event-level counterpart, for the panels that count a person's own steps rather than whole cases.
CREATE OR REPLACE FUNCTION analytics.event_in_scope(
    p_event_id text,
    p_group text,
    p_has_step text,
    p_without_step text
)
RETURNS boolean
LANGUAGE sql
STABLE
AS $$
    SELECT (p_group IS NULL AND p_has_step IS NULL AND p_without_step IS NULL)
        OR EXISTS (
            SELECT 1
            FROM ocel.e2o r
            WHERE r.event_id = p_event_id
              AND analytics.case_in_scope(r.object_id, p_group, p_has_step, p_without_step)
        );
$$;
