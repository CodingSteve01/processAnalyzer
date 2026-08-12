-- What counts as a decision, in one place.
--
-- The decision screen paired the first human step of a case with every approval in it: the first one was read as "who
-- submitted this" and the approval as "who decided about it". The first human step, however, is very often an approval
-- itself: the document arrives from a scan or a job, and the first person to touch it is the one who releases it. In the
-- document process that is 170 of 675 cases, and for those the screen said the exact opposite of the truth: the manager
-- who approves appeared as the person who submitted, and whoever approved after him as the one deciding over him.
--
-- Two mistakes, one cause: the shape of a decision was written into one of the two halves of the query and not the other.
-- So it lives here now, and both halves ask the same question.
CREATE OR REPLACE FUNCTION analytics.is_decision(p_raw_event_type text)
RETURNS boolean
LANGUAGE sql
IMMUTABLE
AS $$
    SELECT p_raw_event_type LIKE '%approved%'
        OR p_raw_event_type LIKE '%granted%'
        OR p_raw_event_type LIKE '%released%'
        OR p_raw_event_type LIKE '%rejected%'
        OR p_raw_event_type LIKE '%discarded%';
$$;

COMMENT ON FUNCTION analytics.is_decision(text) IS
    'True for a step that grants, refuses or withdraws something. Read by both halves of the decision analysis, so a submission can never be an approval.';
