-- Phase 2: the object-centric model and the analytics spine.
--
-- Everything here is derived from journal.*, and only from journal.*. That is what makes the projection rules
-- disposable: they will change every week while the instrumentation grows, and re-deriving 20M local rows takes
-- minutes, whereas re-reading them from the operational database is not allowed at all.

CREATE EXTENSION IF NOT EXISTS pgcrypto;

CREATE SCHEMA IF NOT EXISTS ocel;
CREATE SCHEMA IF NOT EXISTS analytics;

CREATE TABLE ocel.event (
    id                 text        PRIMARY KEY,
    source_id          bigint      NOT NULL UNIQUE REFERENCES journal.event (source_id) ON DELETE CASCADE,
    type               text        NOT NULL,
    ts                 timestamptz NOT NULL,
    recorded_at        timestamptz NOT NULL,
    actor_key          text        NULL,
    actor_kind         text        NOT NULL,
    initiator_key      text        NULL,
    initiator_kind     text        NULL,
    source_application text        NOT NULL,
    module             text        NULL,
    correlation_id     text        NULL,
    attrs              jsonb       NOT NULL DEFAULT '{}'
);
CREATE INDEX ix_ocel_event_type_ts ON ocel.event (type, ts);
CREATE INDEX ix_ocel_event_ts ON ocel.event (ts);
CREATE INDEX ix_ocel_event_kind ON ocel.event (actor_kind);

-- Ids are type-prefixed ('document:481203'). The OCEL primary key spans all object types, and bare numeric ids
-- from different tables collide the moment two of them share a number, which they do constantly.
CREATE TABLE ocel.object (
    id         text        PRIMARY KEY,
    type       text        NOT NULL,
    first_seen timestamptz NOT NULL,
    last_seen  timestamptz NOT NULL
);
CREATE INDEX ix_ocel_object_type ON ocel.object (type);

-- The qualifier is part of the key: the same document can be both the source and the result of one split, and
-- collapsing those two roles into one relation loses the direction of the act.
CREATE TABLE ocel.e2o (
    event_id  text NOT NULL REFERENCES ocel.event (id) ON DELETE CASCADE,
    object_id text NOT NULL REFERENCES ocel.object (id) ON DELETE CASCADE,
    qualifier text NOT NULL,
    PRIMARY KEY (event_id, object_id, qualifier)
);
CREATE INDEX ix_ocel_e2o_object ON ocel.e2o (object_id, event_id);

-- Seen-but-not-curated types are recorded, never dropped. Instrumentation is ongoing; a type that appears for the
-- first time must show up as new, not vanish because nobody had labelled it yet.
CREATE TABLE ocel.type_registry (
    kind        text   NOT NULL,
    type_name   text   NOT NULL,
    display_de  text   NULL,
    is_known    boolean NOT NULL DEFAULT false,
    event_count bigint NOT NULL DEFAULT 0,
    first_seen  timestamptz NOT NULL,
    PRIMARY KEY (kind, type_name)
);

-- Default deny. Nothing from a payload reaches the analytical model unless it is listed here, because payloads
-- carry arbitrary scalars and this store is meant to be widely readable.
--
-- Empty on purpose: which attributes a source carries is a property of that source, so the rows come from the
-- vocabulary at startup (VocabularyLoader) rather than from this file. A hard-coded list here would name attributes
-- of one particular source and silently deny every other installation's.
CREATE TABLE ocel.payload_allowlist (
    event_type text NOT NULL,
    attr_name  text NOT NULL,
    PRIMARY KEY (event_type, attr_name)
);

-- The business calendar. Without it every duration ranking is a weekend detector: the Friday-17:00 handover that
-- is picked up on Monday morning outranks every real bottleneck.
CREATE TABLE analytics.business_slot (
    dow       int  PRIMARY KEY,
    open_from time NOT NULL,
    open_to   time NOT NULL
);
INSERT INTO analytics.business_slot (dow, open_from, open_to) VALUES
    (1, '07:00', '17:00'), (2, '07:00', '17:00'), (3, '07:00', '17:00'),
    (4, '07:00', '17:00'), (5, '07:00', '17:00');

-- Deliberately empty: guessing a Bundesland would silently shift every duration. An unconfigured calendar has to
-- be visible as unconfigured.
CREATE TABLE analytics.holiday (
    day   date PRIMARY KEY,
    label text
);
