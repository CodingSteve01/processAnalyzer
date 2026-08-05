-- The calendar summary, split by what a holiday actually is.
--
-- The first version counted "factor < 1" as half days, which lumped the full holidays in with them: against real
-- data it reported 122 of 122 days as halves. Nonsense on its face — and nonsense sitting directly above the
-- durations it explains, which is how a screen loses the reader's trust in everything on it.

DROP VIEW IF EXISTS analytics.calendar_summary;

CREATE VIEW analytics.calendar_summary AS
SELECT (SELECT string_agg(to_char(DATE '2024-01-01' + (dow - 1), 'Dy') || ' ' || to_char(open_from, 'HH24:MI')
            || ' (' || trim(to_char(hours, '99D9')) || ' h)', ', ' ORDER BY dow)
        FROM analytics.business_slot)                                            AS arbeitszeit,
       (SELECT max(source) FROM analytics.business_slot)                         AS arbeitszeit_quelle,
       (SELECT count(*) FROM analytics.holiday)                                  AS feiertage,
       (SELECT count(*) FROM analytics.holiday WHERE factor = 0)                 AS ganze_tage,
       (SELECT count(*) FROM analytics.holiday WHERE factor > 0 AND factor < 1)  AS davon_halbe_tage,
       (SELECT min(day) FROM analytics.holiday)                                  AS feiertage_ab,
       (SELECT max(day) FROM analytics.holiday)                                  AS feiertage_bis;
