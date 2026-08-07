-- What a case IS, next to what happened to it.
--
-- Everything in this store so far describes events: what happened, when, by whom, to which object. Nothing describes the
-- object itself. So every document kind the source knows is one and the same process, a million documents are one case
-- type, and a posted purchase invoice cannot be told apart from a posted sales credit memo.
-- That is not a missing detail — it is the reason the real processes are invisible: the analysis can only group by what it
-- can see, and a distinction it cannot see becomes an average.
--
-- The source now states these classifications on the object reference itself (journal.event_object.attributes), because
-- they belong to the object and do not change when a step happens. Mirrored 1:1 like everything else in journal.*, then
-- projected into a form the analysis can filter and group by.
--
-- OCEL 2.0 models object attributes as values over time. Here the last value wins instead: these are classifications, not
-- measurements, and a document kind that changes is a correction rather than a history worth keeping. Keeping every version
-- would turn one lookup into a windowed query in every panel that groups by a property.

ALTER TABLE journal.event_object ADD COLUMN IF NOT EXISTS attributes jsonb;

CREATE TABLE IF NOT EXISTS ocel.object_attribute (
    object_id text        NOT NULL REFERENCES ocel.object (id) ON DELETE CASCADE,
    name      text        NOT NULL,
    value     text        NOT NULL,
    ts        timestamptz NOT NULL,
    PRIMARY KEY (object_id, name)
);

COMMENT ON TABLE ocel.object_attribute IS
    'What an object is: kind, purchase/sale, invoice/credit memo, area. Last value per name wins.';

-- The lookup every property filter and every group-by needs: all cases carrying one value.
CREATE INDEX IF NOT EXISTS ix_object_attribute_value ON ocel.object_attribute (name, value);

-- Only the rows that say something. Most references carry no classification, so the projection below reads a small
-- fraction of the biggest table in the mirror.
CREATE INDEX IF NOT EXISTS ix_journal_eo_classified
    ON journal.event_object (event_source_id)
    WHERE attributes IS NOT NULL;

-- Projection: fold every classification into its object, newest statement wins.
--
-- Deliberately NOT tied to the batch that project_pending is working on. A classification is a statement about an object,
-- not an event, and folding the whole set is both idempotent and self-healing: a run that was interrupted, a mirror that
-- gained the column halfway through its history, or an object that appeared after the statement about it all end up
-- correct on the next run. The partial index above is what keeps that affordable.
CREATE OR REPLACE FUNCTION ocel.project_object_attributes()
RETURNS integer
LANGUAGE plpgsql
AS $$
DECLARE
    v_count integer;
BEGIN
    INSERT INTO ocel.object_attribute (object_id, name, value, ts)
    SELECT DISTINCT ON (o.object_type || ':' || o.object_id, a.key)
           o.object_type || ':' || o.object_id,
           a.key,
           a.value,
           e.occurred_at
    FROM journal.event_object o
    JOIN journal.event e ON e.source_id = o.event_source_id
    CROSS JOIN LATERAL jsonb_each_text(o.attributes) AS a(key, value)
    WHERE o.attributes IS NOT NULL
      -- An empty value says nothing and would hide a real one written earlier.
      AND a.value <> ''
      -- The object has to exist: the reference may have been dropped for a meaningless id, and a classification without
      -- an object is a row nobody can ever join.
      AND EXISTS (SELECT 1 FROM ocel.object x WHERE x.id = o.object_type || ':' || o.object_id)
    ORDER BY o.object_type || ':' || o.object_id, a.key, e.occurred_at DESC, o.source_id DESC
    ON CONFLICT (object_id, name) DO UPDATE
        -- Only forward. A batch may arrive out of order, and an older statement must not overwrite a newer one.
        SET value = EXCLUDED.value,
            ts = EXCLUDED.ts
        WHERE ocel.object_attribute.ts <= EXCLUDED.ts;

    GET DIAGNOSTICS v_count = ROW_COUNT;
    RETURN v_count;
END;
$$;

-- What a case is, in one place, for the panels and for the scope.
CREATE OR REPLACE VIEW analytics.case_property AS
SELECT a.object_id,
       o.type AS object_type,
       a.name,
       a.value
FROM ocel.object_attribute a
JOIN ocel.object o ON o.id = a.object_id;

-- Does this case carry that classification? NULL name means "no property filter", never "no cases".
CREATE OR REPLACE FUNCTION analytics.case_has_property(p_object_id text, p_name text, p_value text)
RETURNS boolean
LANGUAGE sql
STABLE
AS $$
    SELECT p_name IS NULL
        OR EXISTS (
            SELECT 1 FROM ocel.object_attribute a
            WHERE a.object_id = p_object_id
              AND a.name = p_name
              -- A name without a value asks "classified at all", which is the question behind every coverage gap.
              AND (p_value IS NULL OR a.value = p_value)
        );
$$;

-- The whole case scope, now including what the case is. Same shape as before: one function, so a query cannot honour
-- half of it.
CREATE OR REPLACE FUNCTION analytics.case_in_scope(
    p_object_id text,
    p_group text,
    p_has_step text,
    p_without_step text,
    p_property text,
    p_property_value text
)
RETURNS boolean
LANGUAGE sql
STABLE
AS $$
    SELECT analytics.case_in_scope(p_object_id, p_group, p_has_step, p_without_step)
       AND analytics.case_has_property(p_object_id, p_property, p_property_value);
$$;

CREATE OR REPLACE FUNCTION analytics.event_in_scope(
    p_event_id text,
    p_group text,
    p_has_step text,
    p_without_step text,
    p_property text,
    p_property_value text
)
RETURNS boolean
LANGUAGE sql
STABLE
AS $$
    SELECT (
            p_group IS NULL
            AND p_has_step IS NULL
            AND p_without_step IS NULL
            AND p_property IS NULL
        )
        OR EXISTS (
            SELECT 1
            FROM ocel.e2o r
            WHERE r.event_id = p_event_id
              AND analytics.case_in_scope(
                      r.object_id, p_group, p_has_step, p_without_step, p_property, p_property_value
                  )
        );
$$;

-- Which classifications exist, and how many cases carry them. The list a reader picks a filter from, and the honest
-- answer to "can we even see this distinction yet".
CREATE OR REPLACE VIEW analytics.property_coverage AS
WITH totals AS (
    -- ocel.object calls it "type"; the mirror side calls it "object_type". Aliased here so the join below reads the
    -- same as everywhere else rather than making the reader remember which side they are on.
    SELECT type AS object_type, count(*) AS cases FROM ocel.object GROUP BY 1
)
SELECT p.object_type,
       analytics.label_object(p.object_type) AS prozess,
       p.name,
       p.value,
       count(*)                              AS faelle,
       round(count(*)::numeric / NULLIF((SELECT cases FROM totals t WHERE t.object_type = p.object_type), 0), 3)
                                             AS anteil
FROM analytics.case_property p
GROUP BY p.object_type, p.name, p.value
ORDER BY count(*) DESC;
