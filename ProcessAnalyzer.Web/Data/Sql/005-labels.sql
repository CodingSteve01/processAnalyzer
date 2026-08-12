-- Plain German for everything a person reads.
--
-- A technical type is a stable key and a terrible label. Nobody should have to know that
-- 'x.document.release-granted.v1 [Accounting]' is "Beleg von der Buchhaltung freigegeben", and if they do, the
-- analysis stops being a tool for finding out how work flows and becomes a tool for people who already know.
--
-- The keys stay technical underneath: variants, comparisons and the OCEL export use the type, so renaming a label
-- never changes a number. Only the presentation changes.
--
-- The table is created here and filled at startup from the vocabulary (VocabularyLoader), because which types exist
-- and what each is called differs per installation. The rules that render them are below and do ship.

CREATE TABLE ocel.label (
    kind      text NOT NULL,          -- 'event' | 'object' | 'qualifier' | 'discriminator'
    type_name text NOT NULL,
    label_de  text NOT NULL,
    -- What this step means in one sentence, for the reader who has never seen the process.
    hint_de   text NULL,
    PRIMARY KEY (kind, type_name)
);

-- Turns an activity key into the sentence a person reads.
--
-- Unknown types are NOT hidden behind a guessed translation: they come back marked, because a type nobody has
-- labelled yet is new instrumentation, and that is worth seeing rather than papering over.
CREATE OR REPLACE FUNCTION analytics.label_activity(p_activity text)
RETURNS text
LANGUAGE sql
STABLE
AS $$
    WITH parts AS (
        SELECT split_part(p_activity, ' [', 1) AS type_name,
               nullif(rtrim(split_part(p_activity, ' [', 2), ']'), '') AS discriminator
    )
    SELECT coalesce(e.label_de, '⚠ ' || parts.type_name)
           || coalesce(' (' || coalesce(d.label_de, parts.discriminator) || ')', '')
    FROM parts
    LEFT JOIN ocel.label e ON e.kind = 'event' AND e.type_name = parts.type_name
    LEFT JOIN ocel.label d ON d.kind = 'discriminator' AND d.type_name = parts.discriminator;
$$;

CREATE OR REPLACE FUNCTION analytics.label_object(p_type text)
RETURNS text
LANGUAGE sql
STABLE
AS $$
    SELECT coalesce((SELECT label_de FROM ocel.label WHERE kind = 'object' AND type_name = p_type), '⚠ ' || p_type);
$$;
