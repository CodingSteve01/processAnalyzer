-- Machines render as their role. See the migration for why this is a separate file.

-- Person plus the group they act in: "Anna Beispiel (Buchhaltung)". The role alone is too coarse for the question
-- this exists to answer, and the name alone hides why that person is the one doing it.
--
-- Machines are the exception: a pseudonym for a job is noise. There is nobody behind it to protect or to name, and
-- the reader is left decoding an id that only means "a job". Non-human actors render as their role alone.
CREATE OR REPLACE FUNCTION analytics.person_with_role(p_actor_key text)
RETURNS text
LANGUAGE sql
STABLE
AS $$
    SELECT CASE
        WHEN p_actor_key IS NULL THEN NULL
        WHEN (SELECT r.actor_kind FROM dim.actor_role r WHERE r.actor_key = p_actor_key) <> 'human'
            THEN (SELECT r.role FROM dim.actor_role r WHERE r.actor_key = p_actor_key)
        WHEN analytics.show_identity() THEN
            coalesce((SELECT a.display_name FROM dim.actor a WHERE a.actor_key = p_actor_key), p_actor_key)
            || coalesce(' (' || (SELECT r.role FROM dim.actor_role r WHERE r.actor_key = p_actor_key) || ')', '')
        ELSE coalesce((SELECT r.role FROM dim.actor_role r WHERE r.actor_key = p_actor_key), p_actor_key)
    END;
$$;
