-- Naming from inside the tool.
--
-- Labels ship as four TSV files, which works for a deployment carrying its own vocabulary but not for the moment
-- somebody needs it: a step shows up unnamed, the person looking at it knows the house word, and writing it down
-- would take a file on a server and a restart. The word gets lost.
--
-- Both sources live in the same table, told apart by where they came from:
--   'file'  read from the vocabulary at startup. The loader may overwrite these freely.
--   'ui'    typed by a person here. The loader must leave them alone, or a restart would undo the correction.
--
-- The file value is kept alongside, so "back to what the vocabulary says" stays possible without a restart, and so a
-- reader can see that a name was changed here and what it was before.
ALTER TABLE ocel.label
    ADD COLUMN IF NOT EXISTS source        text        NOT NULL DEFAULT 'file',
    ADD COLUMN IF NOT EXISTS file_label_de text        NULL,
    ADD COLUMN IF NOT EXISTS file_hint_de  text        NULL,
    ADD COLUMN IF NOT EXISTS updated_at    timestamptz NULL;

ALTER TABLE ocel.label
    DROP CONSTRAINT IF EXISTS label_source_known;

ALTER TABLE ocel.label
    ADD CONSTRAINT label_source_known CHECK (source IN ('file', 'ui'));

-- What still has no word, ranked by how often it occurs, which is the order in which naming is worth doing.
--
-- Every tier the rendering rules read from is in here, not only event types: the generic tier renders from an entity
-- noun plus a verb, so a missing entity noun marks four activities at once, and a missing discriminator label leaves a
-- step reading "(Accounting)" in the middle of a German sentence.
CREATE OR REPLACE VIEW analytics.naming_gap AS
WITH observed AS (
    SELECT 'event'::text AS kind, e.type AS type_name, count(*) AS occurrences
    FROM ocel.event e
    -- The generic tier is named by rule, so its raw type is not a gap; its entity is, and that is the row below.
    WHERE e.type NOT LIKE 'data.%'
    GROUP BY 1, 2

    UNION ALL
    SELECT 'entity', substring(e.type from '^data\.(.+)\.(?:created|updated|deleted|copied)\.v1$'), count(*)
    FROM ocel.event e
    WHERE e.type LIKE 'data.%'
    GROUP BY 1, 2

    UNION ALL
    SELECT 'object', o.type, count(*)
    FROM ocel.object o
    GROUP BY 1, 2

    UNION ALL
    SELECT 'discriminator', d.value, d.n
    FROM (
        SELECT nullif(rtrim(split_part(analytics.activity_of(e.type, e.attrs), ' [', 2), ']'), '') AS value,
               count(*) AS n
        FROM ocel.event e
        GROUP BY 1
    ) d
    WHERE d.value IS NOT NULL
)
SELECT observed.kind,
       observed.type_name,
       observed.occurrences
FROM observed
WHERE observed.type_name IS NOT NULL
  AND NOT EXISTS (
      SELECT 1
      FROM ocel.label l
      WHERE l.type_name = observed.type_name
        AND (
            l.kind = observed.kind
            -- An object type falls back to the entity noun when nothing declares it as a business object, so a type
            -- that reads fine on every screen must not be listed as unnamed. The list is only worth working through
            -- if every row is real work.
            OR (observed.kind = 'object' AND l.kind = 'entity')
        )
  )
ORDER BY observed.occurrences DESC, observed.kind, observed.type_name;
