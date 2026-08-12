-- The generic tier: endpoints that record "an entity was written" rather than a named business fact.
--
-- These are not declared anywhere. The producer builds the type at runtime as
-- data.<entity-slug>.<created|updated|deleted|copied>.v1, so the family is open-ended and a list of types would be
-- out of date on the next release. Two thirds of a journal can sit in this tier, which as raw dotted identifiers is
-- a wall nobody reads.
--
-- So it renders by rule: the entity contributes a German singular noun, the verb a German word, and an entity added
-- later renders as soon as its noun is in the vocabulary: one row instead of four.
--
-- Singular deliberately, unlike the object labels: object labels are plural because screens count them
-- ("Vorgänge: 1.204"), but an activity reads as one thing happening once: "Vorgang geändert", never "Vorgänge
-- geändert".

-- Rendering, in one place so no screen can print something else.
--
-- The generic branch comes first: its types are not in the event catalogue, so falling through to the ⚠ marker
-- would mark two thirds of all activity as "unlabelled" when in fact it is labelled by rule.
CREATE OR REPLACE FUNCTION analytics.label_activity(p_activity text)
RETURNS text
LANGUAGE sql
STABLE
AS $$
    WITH parts AS (
        SELECT split_part(p_activity, ' [', 1)                                  AS type_name,
               nullif(rtrim(split_part(p_activity, ' [', 2), ']'), '')           AS discriminator
    ),
    generic AS (
        SELECT parts.*,
               substring(parts.type_name from '^data\.(.+)\.(?:created|updated|deleted|copied)\.v1$') AS entity,
               substring(parts.type_name from '^data\..+\.(created|updated|deleted|copied)\.v1$')     AS verb
        FROM parts
    )
    SELECT CASE
        WHEN g.entity IS NOT NULL THEN
            coalesce(
                (SELECT n.label_de FROM ocel.label n WHERE n.kind = 'entity' AND n.type_name = g.entity),
                -- An entity nobody has named yet: mark it, because a new entity in the source is exactly the moment
                -- somebody has to decide what it is called.
                '⚠ ' || g.entity
            )
            || ' '
            || coalesce((SELECT v.label_de FROM ocel.label v WHERE v.kind = 'verb' AND v.type_name = g.verb), g.verb)
        ELSE
            coalesce(e.label_de, '⚠ ' || g.type_name)
            || coalesce(' (' || coalesce(d.label_de, g.discriminator) || ')', '')
    END
    FROM generic g
    LEFT JOIN ocel.label e ON e.kind = 'event'         AND e.type_name = g.type_name
    LEFT JOIN ocel.label d ON d.kind = 'discriminator' AND d.type_name = g.discriminator;
$$;
