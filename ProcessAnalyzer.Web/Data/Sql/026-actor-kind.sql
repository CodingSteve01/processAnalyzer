-- What a resource IS, as opposed to the channel one of its events came through.
--
-- The log carries a kind per EVENT, straight from the source's performer type. That is a fact about the channel: a
-- driver confirms a pickup from the tablet ('device') and corrects it at a desk an hour later ('human'), and both
-- events carry the same pseudonym because the source sends the same account id for both. Every one of the 70 people who
-- ever touched a tablet arrived with two kinds.
--
-- Three things went wrong from treating that pair as an identity:
--
--   * dim.actor_role had one row per (actor, kind), so joining it to the event log multiplied. Every event of those 70
--     people was counted twice — once under their group and once under "Gerät" — and the roles table reported 17 420
--     steps for devices where the log holds 336. "43 % of all steps are done by the machine" was that double count.
--   * "manual work" was "the channel was human", so a driver photographing a delivery note counted as automation.
--     Photographing a note is the most manual work there is.
--   * A technical account that posts through the API arrives as 'User' and inflates manual work in the other
--     direction, and nothing in the tool could say otherwise.
--
-- So: the kind is derived per ACTOR (one row, human wins over any channel it acted through), it can be corrected in the
-- tool, and "was a person involved" becomes a question about the actor.

-- The correction, maintained on the Menschen screen. Small, hand-edited, and the only hand-written row in dim.
CREATE TABLE IF NOT EXISTS dim.actor_kind_override (
    actor_key  text PRIMARY KEY,
    kind       text        NOT NULL CHECK (kind IN ('human', 'job', 'service', 'device', 'external')),
    note       text,
    updated_at timestamptz NOT NULL DEFAULT now()
);

COMMENT ON TABLE dim.actor_kind_override IS
    'Manually corrected actor kind. Survives a projection rebuild; a re-pull of the journal does not touch it.';

DROP MATERIALIZED VIEW IF EXISTS dim.actor_identity CASCADE;

CREATE MATERIALIZED VIEW dim.actor_identity AS
WITH per_kind AS (
    SELECT actor_key, actor_kind, count(*) AS events
    FROM ocel.event
    WHERE actor_key IS NOT NULL
    GROUP BY 1, 2
),
rolled AS (
    SELECT
        actor_key,
        sum(events)                                                       AS events,
        count(*)                                                          AS channels,
        -- A person who also reports through a machine is a person. Where the channel is the point, it still sits on the
        -- event's own actor_kind and is untouched by this.
        CASE
            WHEN bool_or(actor_kind = 'human') THEN 'human'
            ELSE (array_agg(actor_kind ORDER BY events DESC, actor_kind))[1]
        END                                                               AS derived_kind
    FROM per_kind
    GROUP BY 1
)
SELECT
    r.actor_key,
    r.events,
    r.channels,
    r.derived_kind,
    coalesce(o.kind, r.derived_kind) AS kind,
    o.kind IS NOT NULL               AS is_corrected,
    o.note
FROM rolled r
LEFT JOIN dim.actor_kind_override o ON o.actor_key = r.actor_key;

CREATE UNIQUE INDEX ux_actor_identity ON dim.actor_identity (actor_key);
CREATE INDEX ix_actor_identity_kind ON dim.actor_identity (kind);

-- One row per actor now, which is what stops the join from multiplying. Same three columns as before, so every query
-- over it keeps working; actor_kind is the effective kind, including a correction.
CREATE OR REPLACE VIEW dim.actor_role AS
SELECT
    i.actor_key,
    i.kind AS actor_kind,
    CASE
        WHEN i.kind = 'job' THEN 'Automatischer Job'
        WHEN i.kind = 'service' THEN 'Systemdienst'
        WHEN i.kind = 'external' THEN 'Fremdsystem'
        WHEN i.kind = 'device' THEN 'Gerät'
        ELSE coalesce(p.role, 'Ohne Gruppe')
    END AS role
FROM dim.actor_identity i
LEFT JOIN dim.actor_primary_role p ON p.actor_key = i.actor_key;

-- Was a person involved? A lookup, not a string comparison against a channel.
CREATE OR REPLACE FUNCTION analytics.is_person(p_actor_key text)
RETURNS boolean
LANGUAGE sql
STABLE
AS $$
    SELECT p_actor_key IS NOT NULL
       AND EXISTS (
           SELECT 1 FROM dim.actor_identity i
           WHERE i.actor_key = p_actor_key AND i.kind = 'human'
       );
$$;

COMMENT ON FUNCTION analytics.is_person(text) IS
    'True when this actor is a person, corrections included. The measure behind manual work and straight-through cases.';

-- The lifecycle asks the new question. Same definition as 024 otherwise.
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
    -- Was a person involved anywhere in this case. A tablet held by a driver counts; a scheduled job does not.
    bool_or(analytics.is_person(t.actor_key)) AS has_human,
    MAX(l.event_type) AS last_activity,
    analytics.case_is_open(t.object_type, MAX(l.event_type), MAX(t.ts)) AS is_open
FROM analytics.object_timeline t
JOIN last_step l ON l.object_id = t.object_id
GROUP BY 1, 2;

CREATE UNIQUE INDEX ux_lifecycle ON analytics.object_lifecycle (object_id);
CREATE INDEX ix_lifecycle_type ON analytics.object_lifecycle (object_type, first_ts);
CREATE INDEX ix_lifecycle_open ON analytics.object_lifecycle (object_type, is_open);
