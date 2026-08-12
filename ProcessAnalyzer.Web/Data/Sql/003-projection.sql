-- The projection itself, plus the analytics spine every metric reads.

-- Working seconds between two instants: each calendar day is clipped to its slot and holidays are skipped.
-- Used by every duration in the product, so that "two hours" means two hours somebody could have worked.
CREATE OR REPLACE FUNCTION analytics.biz_seconds(a timestamptz, b timestamptz)
RETURNS double precision
LANGUAGE sql
STABLE
AS $$
    SELECT COALESCE(SUM(EXTRACT(EPOCH FROM (
        LEAST(b, (d + s.open_to)::timestamptz) - GREATEST(a, (d + s.open_from)::timestamptz)
    ))), 0)
    FROM generate_series(date_trunc('day', a), date_trunc('day', b), interval '1 day') AS g(d)
    JOIN analytics.business_slot s ON s.dow = EXTRACT(ISODOW FROM g.d)
    LEFT JOIN analytics.holiday h ON h.day = g.d::date
    WHERE h.day IS NULL
      AND LEAST(b, (g.d + s.open_to)::timestamptz) > GREATEST(a, (g.d + s.open_from)::timestamptz);
$$;

-- Projects everything not yet projected. Set-based on purpose: the rules are joins and CASE expressions, and a
-- row-by-row projector would be slower and harder to check against the rule it implements.
CREATE OR REPLACE FUNCTION ocel.project_pending(p_actor_key text, p_version int, p_batch int)
RETURNS int
LANGUAGE plpgsql
AS $$
DECLARE
    v_ids bigint[];
    v_count int;
BEGIN
    SELECT array_agg(source_id) INTO v_ids
    FROM (
        SELECT source_id FROM journal.event
        WHERE projection_version <> p_version
        ORDER BY source_id
        LIMIT p_batch
    ) s;

    IF v_ids IS NULL THEN
        RETURN 0;
    END IF;

    INSERT INTO ocel.event (
        id, source_id, type, ts, recorded_at, actor_key, actor_kind,
        initiator_key, initiator_kind, source_application, module, correlation_id, attrs
    )
    SELECT
        e.event_id::text,
        e.source_id,
        e.event_type,
        e.occurred_at,
        e.recorded_at,
        -- Pseudonym, not identity. 'Employee A is slower than employee B' is not a question this system answers,
        -- and the raw id stays in journal.* where the governance rules can keep it.
        CASE WHEN e.performer_id IS NULL THEN NULL
             ELSE 'a:' || substr(encode(hmac(e.performer_id, p_actor_key, 'sha256'), 'hex'), 1, 12) END,
        CASE e.performer_type
            WHEN 'User' THEN 'human'
            WHEN 'Customer' THEN 'human'
            WHEN 'System' THEN 'service'
            WHEN 'ScheduledJob' THEN 'job'
            WHEN 'ExternalSystem' THEN 'external'
            WHEN 'Device' THEN 'device'
            ELSE 'service'
        END,
        CASE WHEN e.initiator_id IS NULL THEN NULL
             ELSE 'a:' || substr(encode(hmac(e.initiator_id, p_actor_key, 'sha256'), 'hex'), 1, 12) END,
        CASE e.initiator_type
            WHEN 'User' THEN 'human'
            WHEN 'Customer' THEN 'human'
            WHEN 'System' THEN 'service'
            WHEN 'ScheduledJob' THEN 'job'
            WHEN 'ExternalSystem' THEN 'external'
            WHEN 'Device' THEN 'device'
            ELSE NULL
        END,
        e.source_application,
        e.source_module,
        e.correlation_id,
        -- Allow-listed keys only. Everything else stays in the mirror and never becomes analysable.
        COALESCE((
            SELECT jsonb_object_agg(kv.key, kv.value)
            FROM jsonb_each(e.payload) AS kv
            JOIN ocel.payload_allowlist a ON a.event_type = e.event_type AND a.attr_name = kv.key
        ), '{}'::jsonb)
    FROM journal.event e
    WHERE e.source_id = ANY (v_ids)
    ON CONFLICT (id) DO NOTHING;

    INSERT INTO ocel.object (id, type, first_seen, last_seen)
    SELECT o.object_type || ':' || o.object_id, o.object_type, MIN(e.occurred_at), MAX(e.occurred_at)
    FROM journal.event_object o
    JOIN journal.event e ON e.source_id = o.event_source_id
    WHERE o.event_source_id = ANY (v_ids)
    GROUP BY 1, 2
    ON CONFLICT (id) DO UPDATE
        SET first_seen = LEAST(ocel.object.first_seen, EXCLUDED.first_seen),
            last_seen = GREATEST(ocel.object.last_seen, EXCLUDED.last_seen);

    INSERT INTO ocel.e2o (event_id, object_id, qualifier)
    SELECT e.event_id::text, o.object_type || ':' || o.object_id, o.qualifier
    FROM journal.event_object o
    JOIN journal.event e ON e.source_id = o.event_source_id
    WHERE o.event_source_id = ANY (v_ids)
    ON CONFLICT DO NOTHING;

    -- Types are registered as they appear, is_known=false until somebody curates them. A type that shows up for
    -- the first time must be visible as new instrumentation, not disappear.
    INSERT INTO ocel.type_registry (kind, type_name, event_count, first_seen)
    SELECT 'event', e.event_type, COUNT(*), MIN(e.occurred_at)
    FROM journal.event e WHERE e.source_id = ANY (v_ids) GROUP BY 2
    ON CONFLICT (kind, type_name) DO UPDATE
        SET event_count = ocel.type_registry.event_count + EXCLUDED.event_count,
            first_seen = LEAST(ocel.type_registry.first_seen, EXCLUDED.first_seen);

    INSERT INTO ocel.type_registry (kind, type_name, event_count, first_seen)
    SELECT 'object', o.object_type, COUNT(*), MIN(e.occurred_at)
    FROM journal.event_object o
    JOIN journal.event e ON e.source_id = o.event_source_id
    WHERE o.event_source_id = ANY (v_ids) GROUP BY 2
    ON CONFLICT (kind, type_name) DO UPDATE
        SET event_count = ocel.type_registry.event_count + EXCLUDED.event_count,
            first_seen = LEAST(ocel.type_registry.first_seen, EXCLUDED.first_seen);

    UPDATE journal.event SET projection_version = p_version WHERE source_id = ANY (v_ids);
    GET DIAGNOSTICS v_count = ROW_COUNT;
    RETURN v_count;
END;
$$;

-- What counts as one activity.
--
-- The event type alone is not enough. Two approvals by two different roles are two steps of the process, not the
-- same step twice, and with the bare type they are indistinguishable, so every multi-role approval reads as 100%
-- rework and every variant collapses into "granted → granted". The discriminating attribute is therefore part of
-- the activity label, taken only from the allow-listed payload keys that actually name a step.
CREATE OR REPLACE FUNCTION analytics.activity_of(p_type text, p_attrs jsonb)
RETURNS text
LANGUAGE sql
IMMUTABLE
AS $$
    SELECT p_type || COALESCE(
        ' [' || COALESCE(
            p_attrs ->> 'role',
            p_attrs ->> 'actionType',
            p_attrs ->> 'action'
        ) || ']', '');
$$;

-- One row per (object, event): the object's own history, which is what "a case" means in an object-centric log.
-- prev_* comes from a window, so every transition is available without a self join in every query.
CREATE MATERIALIZED VIEW analytics.object_timeline AS
SELECT
    r.object_id,
    o.type AS object_type,
    e.id AS event_id,
    analytics.activity_of(e.type, e.attrs) AS event_type,
    e.type AS raw_event_type,
    e.ts,
    e.actor_key,
    e.actor_kind,
    e.attrs,
    lag(analytics.activity_of(e.type, e.attrs)) OVER w AS prev_type,
    lag(e.ts) OVER w AS prev_ts,
    lag(e.actor_key) OVER w AS prev_actor,
    row_number() OVER w AS seq
FROM ocel.e2o r
JOIN ocel.event e ON e.id = r.event_id
JOIN ocel.object o ON o.id = r.object_id
WINDOW w AS (PARTITION BY r.object_id ORDER BY e.ts, e.id);

CREATE UNIQUE INDEX ux_timeline ON analytics.object_timeline (object_id, event_id);
CREATE INDEX ix_timeline_type ON analytics.object_timeline (object_type, ts);
CREATE INDEX ix_timeline_event ON analytics.object_timeline (event_type);

-- One row per object: its case. is_open matters: counting in-flight objects into a p95 drags it down and makes
-- a process look faster the busier it gets.
CREATE MATERIALIZED VIEW analytics.object_lifecycle AS
SELECT
    t.object_id,
    t.object_type,
    MIN(t.ts) AS first_ts,
    MAX(t.ts) AS last_ts,
    COUNT(*) AS n_events,
    analytics.biz_seconds(MIN(t.ts), MAX(t.ts)) AS biz_seconds,
    EXTRACT(EPOCH FROM (MAX(t.ts) - MIN(t.ts))) AS wall_seconds,
    bool_or(t.actor_kind = 'human') AS has_human,
    MAX(t.ts) > (SELECT MAX(ts) - interval '3 days' FROM ocel.event) AS is_open
FROM analytics.object_timeline t
GROUP BY 1, 2;

CREATE UNIQUE INDEX ux_lifecycle ON analytics.object_lifecycle (object_id);
CREATE INDEX ix_lifecycle_type ON analytics.object_lifecycle (object_type, first_ts);
