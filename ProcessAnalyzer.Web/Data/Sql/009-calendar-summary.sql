-- The calendar summary, split by what a holiday actually is.
--
-- Counting "factor < 1" as a half day lumps the full holidays in with them and reports every holiday as a half,
-- directly above the durations it explains.

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
