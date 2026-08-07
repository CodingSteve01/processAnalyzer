-- Which column layouts exist at all, and who shares one.
--
-- The question a user-interface rebuild actually has to answer: not "which columns exist" — the schema says that — but
-- which combinations people put on screen, how many distinct ones there are per screen, and whether a screen serves one
-- layout or fifteen. A screen with sixty people on one layout can be rebuilt from that layout. A screen with eight
-- people on eight layouts cannot be rebuilt at all until somebody looks at why.
--
-- The layout is identified by its ORDERED column list, hashed. Ordered on purpose: two people with the same columns in
-- a different order have arranged their work differently, and collapsing them would hide exactly the difference this is
-- looking for. The readable column list travels with the hash so nobody has to join back to see what a layout is.
CREATE OR REPLACE VIEW analytics.column_layout AS
WITH layout AS (
    SELECT v.id,
           v.path,
           v.actor_key,
           v.name,
           string_agg(c.property, ' | ' ORDER BY c.ord) AS columns,
           count(*)                                     AS column_count
    FROM dim.saved_view v
    JOIN dim.saved_view_column c ON c.view_id = v.id
    GROUP BY v.id, v.path, v.actor_key, v.name
)
SELECT path,
       md5(columns)                        AS layout_key,
       count(DISTINCT actor_key)           AS personen,
       count(*)                            AS ansichten,
       min(column_count)                   AS spalten,
       -- Truncated for the table: a layout can be two hundred columns wide, and the first dozen is what identifies it
       -- to a reader. The full list stays available through the layout key.
       left(columns, 300)                  AS spaltenfolge
FROM layout
GROUP BY path, md5(columns), left(columns, 300)
ORDER BY count(DISTINCT actor_key) DESC, count(*) DESC;

-- Who shares a layout with whom. The pair list behind the counts, so "these two work the same way" is a name and not a
-- number — and so a layout that looks shared but is one person with three saved views does not read as agreement.
CREATE OR REPLACE VIEW analytics.layout_sharing AS
WITH layout AS (
    SELECT v.id, v.path, v.actor_key, string_agg(c.property, ' | ' ORDER BY c.ord) AS columns
    FROM dim.saved_view v
    JOIN dim.saved_view_column c ON c.view_id = v.id
    GROUP BY v.id, v.path, v.actor_key
),
keyed AS (SELECT DISTINCT path, md5(columns) AS layout_key, actor_key FROM layout)
SELECT a.path,
       a.layout_key,
       coalesce(pa.display_name, a.actor_key) AS person,
       coalesce(pb.display_name, b.actor_key) AS teilt_mit
FROM keyed a
JOIN keyed b ON b.path = a.path AND b.layout_key = a.layout_key AND b.actor_key < a.actor_key
LEFT JOIN dim.actor pa ON pa.actor_key = a.actor_key
LEFT JOIN dim.actor pb ON pb.actor_key = b.actor_key
ORDER BY a.path, a.layout_key;
