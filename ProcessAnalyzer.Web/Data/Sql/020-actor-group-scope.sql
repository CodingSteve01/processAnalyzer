-- Scoping a question to one group of people.
--
-- The question this answers is "what does that part of the organisation actually do": the warehouse, the drivers,
-- the office. Without it an installation where one group produces most of the events reads as if the whole company
-- worked that way, and the smaller groups are invisible under the volume.
--
-- It scopes whole CASES, not loose events: a case counts when somebody from the group took part in it, and then ALL
-- of its steps count. Dropping the other people's steps instead would leave a case whose first step is whatever the
-- group happened to do first, and every duration derived from that is wrong rather than partial. The same reason the
-- period filter scopes cases (see Period).
--
-- Membership comes from the source directory (dim.actor_group), so it is as current as the last directory sync.
-- Somebody who changed departments is counted where they are now: the alternative, membership as of the event, is
-- not in the source.
CREATE OR REPLACE FUNCTION analytics.case_touched_by_group(p_object_id text, p_group text)
RETURNS boolean
LANGUAGE sql
STABLE
AS $$
    -- A NULL group is "no filter", not "nobody": every call site passes the parameter unconditionally, so the
    -- unfiltered request must produce the same answer it did before this function existed.
    SELECT p_group IS NULL
        OR EXISTS (
            SELECT 1
            FROM ocel.e2o r
            JOIN ocel.event e ON e.id = r.event_id
            JOIN dim.actor_group g ON g.actor_key = e.actor_key
            WHERE r.object_id = p_object_id
              AND g.group_name = p_group
        );
$$;

-- The same question at event level, for the panels that count a person's own steps rather than whole cases.
CREATE OR REPLACE FUNCTION analytics.event_in_group(p_event_id text, p_group text)
RETURNS boolean
LANGUAGE sql
STABLE
AS $$
    SELECT p_group IS NULL
        OR EXISTS (
            SELECT 1
            FROM ocel.e2o r
            WHERE r.event_id = p_event_id
              AND analytics.case_touched_by_group(r.object_id, p_group)
        );
$$;

-- Without this the group predicate degenerates into a sequential scan of the whole relation table for every row of
-- every panel, and the analysis pages go from fast to unusable the moment somebody picks a group.
CREATE INDEX IF NOT EXISTS e2o_object_event_idx ON ocel.e2o (object_id, event_id);
CREATE INDEX IF NOT EXISTS e2o_event_object_idx ON ocel.e2o (event_id, object_id);
