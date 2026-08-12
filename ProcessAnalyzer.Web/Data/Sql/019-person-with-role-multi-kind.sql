-- One actor key, more than one kind. See the migration for why this is a separate file.

-- dim.actor_role is one row per (actor key, actor kind): it reads the distinct pairs straight out of the event log.
-- A driver confirms a pickup from the truck and corrects it at a desk an hour later, so the same key arrives as
-- 'device' and as 'human', and the key gets two rows. The scalar subqueries here assumed there could only ever be
-- one, so every screen that names an actor failed with 21000 (more than one row returned by a subquery) as soon as a
-- single person had used both. With demo data nobody ever did.
--
-- The human wins. A device is a channel a person acted through, not a second person, and naming the key after the
-- channel would hide who was behind it. Where the channel is the point, the distinction sits on the event's own
-- actor_kind and is untouched by this label.
CREATE OR REPLACE FUNCTION analytics.person_with_role(p_actor_key text)
RETURNS text
LANGUAGE sql
STABLE
AS $$
    WITH identity AS (
        SELECT r.actor_kind, r.role
        FROM dim.actor_role r
        WHERE r.actor_key = p_actor_key
        -- Deterministic by construction: human first, then a stable order so a machine that somehow carries two
        -- kinds still renders the same string on every screen and in every refresh.
        ORDER BY (r.actor_kind = 'human') DESC, r.actor_kind, r.role
        LIMIT 1
    )
    SELECT CASE
        WHEN p_actor_key IS NULL THEN NULL
        WHEN (SELECT actor_kind FROM identity) <> 'human'
            THEN (SELECT role FROM identity)
        WHEN analytics.show_identity() THEN
            coalesce((SELECT a.display_name FROM dim.actor a WHERE a.actor_key = p_actor_key), p_actor_key)
            || coalesce(' (' || (SELECT role FROM identity) || ')', '')
        ELSE coalesce((SELECT role FROM identity), p_actor_key)
    END;
$$;
