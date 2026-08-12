-- Object labels for the generic tier.
--
-- The activity side has rendered the generic tier by rule since 012-generic-acts.sql, but the object side did not:
-- label_object looked at 'object' labels only. Those exist for the types that are declared as business objects, and
-- an entity that only ever appears through the generic tier is not one of them, so it came back as "⚠ <slug>" on
-- every screen that counts objects, while the very same entity read as proper German in the step it appeared in.
--
-- The first installation to run against a real journal showed exactly that: a time record edited through a legacy
-- CRUD screen, labelled in the activity list, marked as unlabelled in the inventory.
--
-- The entity noun is singular where an object label would be plural ("Zeitstempel" where a declared object type
-- would say "Zeitstempel", "Beleg" where it would say "Belege"). A singular heading is a small wart; a dotted
-- identifier behind a warning sign says the tool is broken.
CREATE OR REPLACE FUNCTION analytics.label_object(p_type text)
RETURNS text
LANGUAGE sql
STABLE
AS $$
    SELECT coalesce(
        (SELECT label_de FROM ocel.label WHERE kind = 'object' AND type_name = p_type),
        -- The generic tier: one noun per entity, the same row the activity rule reads.
        (SELECT label_de FROM ocel.label WHERE kind = 'entity' AND type_name = p_type),
        -- Still marked when neither exists: a type nobody has named is new instrumentation, and that is worth seeing.
        '⚠ ' || p_type
    );
$$;
