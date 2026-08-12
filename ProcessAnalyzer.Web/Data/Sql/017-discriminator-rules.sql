-- Which payload attribute names a step, as data rather than as a chain of CASE branches.
--
-- The event type alone is not enough. Two approvals by two different roles are two steps of the process, not the same
-- step twice: with the bare type they are indistinguishable, so every multi-role approval reads as 100 % rework and
-- every variant collapses into "granted → granted".
--
-- Which attribute carries that distinction is a property of the source: one names it 'role', the next 'actionType',
-- and a particular family of types may need a fourth attribute nothing else uses. Encoded as CASE branches, every
-- such family meant editing a shipped function, and a source that renamed a type silently lost the distinction
-- because the branch still matched the old name. So the rules are rows, loaded from the vocabulary, and adding one
-- needs no release.

CREATE TABLE ocel.discriminator_rule (
    -- Lowest first. The order is the answer to "two rules match, which attribute wins", which used to be the
    -- argument order of a COALESCE and therefore invisible to anybody reading the rules.
    priority   int  NOT NULL,
    -- LIKE pattern against the event type. '%' matches every type, which is how a source-wide rule is written.
    type_match text NOT NULL,
    attr_name  text NOT NULL,
    PRIMARY KEY (priority, type_match, attr_name)
);

-- STABLE, not IMMUTABLE: it reads a table now. Only views and queries call it: no index depends on it, so the
-- weaker guarantee costs nothing here, and claiming IMMUTABLE while reading a table is how a plan caches a label
-- from before the vocabulary was corrected.
CREATE OR REPLACE FUNCTION analytics.activity_of(p_type text, p_attrs jsonb)
RETURNS text
LANGUAGE sql
STABLE
AS $$
    SELECT p_type || COALESCE(' [' || (
        SELECT p_attrs ->> rule.attr_name
        FROM ocel.discriminator_rule rule
        WHERE p_type LIKE rule.type_match
          AND p_attrs ->> rule.attr_name IS NOT NULL
        ORDER BY rule.priority, rule.type_match, rule.attr_name
        LIMIT 1
    ) || ']', '');
$$;
