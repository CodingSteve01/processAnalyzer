-- Names instead of pseudonyms, when the operator turns them on.
--
-- The pseudonym default exists for a reason and stays the default. But "we do not even know who approves whose
-- leave" is a real gap in running a company, and it cannot be answered by a pseudonym: the answer IS the name.
-- So identity is a switch with two honest positions, not a permanent no.
--
-- What the switch does NOT change: the raw source id never leaves dim.actor, and no screen ranks people against
-- each other. The question "who works with whom, and who decides what" is answerable; "who is faster" is not, and
-- that is a deliberate line, not an oversight.

CREATE TABLE analytics.setting (
    key   text PRIMARY KEY,
    value text NOT NULL
);

-- Off by default: an installation that has not decided yet must not show names by accident.
INSERT INTO analytics.setting (key, value) VALUES ('show_actor_identity', 'false');

CREATE OR REPLACE FUNCTION analytics.show_identity()
RETURNS boolean
LANGUAGE sql
STABLE
AS $$
    SELECT coalesce((SELECT value = 'true' FROM analytics.setting WHERE key = 'show_actor_identity'), false);
$$;

-- How a person appears on screen. One function, so a page cannot accidentally print something the setting forbids.
CREATE OR REPLACE FUNCTION analytics.person(p_actor_key text)
RETURNS text
LANGUAGE sql
STABLE
AS $$
    SELECT CASE
        WHEN p_actor_key IS NULL THEN NULL
        WHEN analytics.show_identity() THEN
            coalesce((SELECT a.display_name FROM dim.actor a WHERE a.actor_key = p_actor_key), p_actor_key)
        ELSE p_actor_key
    END;
$$;

-- Person plus the group they act in: "Anna Beispiel (Buchhaltung)". Superseded by 008, which stops rendering a
-- pseudonym for machines: kept here as it shipped, because a migration that already ran must not be rewritten.
CREATE OR REPLACE FUNCTION analytics.person_with_role(p_actor_key text)
RETURNS text
LANGUAGE sql
STABLE
AS $$
    SELECT CASE
        WHEN p_actor_key IS NULL THEN NULL
        WHEN analytics.show_identity() THEN
            coalesce((SELECT a.display_name FROM dim.actor a WHERE a.actor_key = p_actor_key), p_actor_key)
            || coalesce(' (' || (SELECT r.role FROM dim.actor_role r WHERE r.actor_key = p_actor_key) || ')', '')
        ELSE coalesce((SELECT r.role FROM dim.actor_role r WHERE r.actor_key = p_actor_key), p_actor_key)
    END;
$$;
