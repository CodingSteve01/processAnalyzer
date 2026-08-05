-- Who is who.
--
-- The journal records that user u-217 approved something. That is enough to reconstruct a flow and useless for
-- understanding an organisation: "a:0e46fff → a:a3c9ff" is not an answer to "which department hands work to which".
-- The directory turns actors into the roles they act in, which is also the level the analysis is allowed to report
-- at — the group is a legitimate analytical dimension, the person is not.

CREATE SCHEMA IF NOT EXISTS dim;

CREATE TABLE dim.actor (
    -- The pseudonym, so ocel.* can be joined without ever holding a real id.
    actor_key    text PRIMARY KEY,
    -- The raw id stays here, next to the directory that explains it, and never reaches ocel.* or an endpoint.
    source_id    text NOT NULL UNIQUE,
    display_name text NULL,
    is_active    boolean NOT NULL DEFAULT true,
    synced_at    timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE dim.actor_group (
    actor_key  text NOT NULL REFERENCES dim.actor (actor_key) ON DELETE CASCADE,
    group_name text NOT NULL,
    PRIMARY KEY (actor_key, group_name)
);
CREATE INDEX ix_actor_group_name ON dim.actor_group (group_name);

-- Somebody in three groups would otherwise be counted three times in every per-role total. The primary role is the
-- one they actually act in most, derived from their own history rather than from the order the groups happen to be
-- listed in — and it is a view, so it follows the data as behaviour changes.
CREATE VIEW dim.actor_primary_role AS
WITH activity AS (
    SELECT e.actor_key, count(*) AS events
    FROM ocel.event e
    WHERE e.actor_key IS NOT NULL
    GROUP BY 1
),
ranked AS (
    SELECT g.actor_key,
           g.group_name,
           -- The smallest group a person belongs to is the most specific statement about what they do: everybody is
           -- in "Mitarbeiter", only three people are in "Buchhaltung".
           row_number() OVER (
               PARTITION BY g.actor_key
               ORDER BY (SELECT count(*) FROM dim.actor_group s WHERE s.group_name = g.group_name), g.group_name
           ) AS rank
    FROM dim.actor_group g
)
SELECT r.actor_key,
       r.group_name AS role,
       coalesce(a.events, 0) AS events
FROM ranked r
LEFT JOIN activity a ON a.actor_key = r.actor_key
WHERE r.rank = 1;

-- Every actor in the log with a role attached, including the ones the directory does not know: jobs, services and
-- external systems have no group, and rendering them as "unbekannt" would hide that the work was automatic.
CREATE VIEW dim.actor_role AS
SELECT e.actor_key,
       e.actor_kind,
       CASE
           WHEN e.actor_kind = 'job' THEN 'Automatischer Job'
           WHEN e.actor_kind = 'service' THEN 'Systemdienst'
           WHEN e.actor_kind = 'external' THEN 'Fremdsystem'
           WHEN e.actor_kind = 'device' THEN 'Gerät'
           ELSE coalesce(p.role, 'Ohne Gruppe')
       END AS role
FROM (SELECT DISTINCT actor_key, actor_kind FROM ocel.event WHERE actor_key IS NOT NULL) e
LEFT JOIN dim.actor_primary_role p ON p.actor_key = e.actor_key;
