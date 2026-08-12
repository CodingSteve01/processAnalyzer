-- The business calendar, taken from the source instead of guessed here.
--
-- sources express working time as hours per weekday (WorktimeCalendarEntries.MondayHours …) and holidays as
-- calendar entries that can be half days. Neither is a clock window, so this keeps the shape the source actually has:
-- a weekday contributes a number of hours, a holiday removes all or half of them.
--
-- The source does not record when a day starts. That stays configurable and is stated on the screen, because
-- guessing it would put a made-up number underneath every duration.

ALTER TABLE analytics.business_slot ADD COLUMN IF NOT EXISTS hours numeric(5, 2) NOT NULL DEFAULT 10;
ALTER TABLE analytics.business_slot ADD COLUMN IF NOT EXISTS source text NOT NULL DEFAULT 'Standard 07-17';

-- A holiday can be a half day (Christmas Eve afternoon), which the source models with Forenoons/Afternoons. Removing
-- the whole day would overstate every duration that spans it.
ALTER TABLE analytics.holiday ADD COLUMN IF NOT EXISTS factor numeric(3, 2) NOT NULL DEFAULT 1.0;
ALTER TABLE analytics.holiday ADD COLUMN IF NOT EXISTS source text NULL;

-- Working seconds between two instants.
--
-- Each calendar day contributes at most its weekday's window, shortened by a holiday factor. The window is anchored
-- at analytics.business_slot.open_from and lasts as many hours as the weekday has — so "8 hours on Monday" from
-- the source becomes 07:00-15:00 with the default start, and changing the start moves the window without changing its
-- length.
CREATE OR REPLACE FUNCTION analytics.biz_seconds(a timestamptz, b timestamptz)
RETURNS double precision
LANGUAGE sql
STABLE
AS $$
    SELECT COALESCE(SUM(EXTRACT(EPOCH FROM (
        LEAST(b, (g.d + s.open_from + make_interval(mins => (s.hours * 60 * COALESCE(h.factor, 1))::int))::timestamptz)
        - GREATEST(a, (g.d + s.open_from)::timestamptz)
    ))), 0)
    FROM generate_series(date_trunc('day', a), date_trunc('day', b), interval '1 day') AS g(d)
    JOIN analytics.business_slot s ON s.dow = EXTRACT(ISODOW FROM g.d)
    LEFT JOIN analytics.holiday h ON h.day = g.d::date
    WHERE COALESCE(h.factor, 1) > 0
      AND LEAST(b, (g.d + s.open_from + make_interval(mins => (s.hours * 60 * COALESCE(h.factor, 1))::int))::timestamptz)
          > GREATEST(a, (g.d + s.open_from)::timestamptz);
$$;

-- What the durations on every screen are actually based on. Shown in the UI, because a calendar nobody can see is a
-- number nobody can check.
CREATE OR REPLACE VIEW analytics.calendar_summary AS
SELECT (SELECT string_agg(
            -- 2024-01-01 was a Monday, so adding dow-1 lands on the weekday. to_date(dow, 'ID') does not parse a
            -- weekday number at all and silently returned the same day for every row.
            to_char(DATE '2024-01-01' + (dow - 1), 'Dy') || ' ' || to_char(open_from, 'HH24:MI')
                || ' (' || trim(to_char(hours, '99D9')) || ' h)',
            ', ' ORDER BY dow)
        FROM analytics.business_slot)                                            AS arbeitszeit,
       (SELECT max(source) FROM analytics.business_slot)                         AS arbeitszeit_quelle,
       -- Split by what they are: counting "factor < 1" as a half day lumps the full holidays in with them and
       -- reports every holiday as a half.
       (SELECT count(*) FROM analytics.holiday)                                  AS feiertage,
       (SELECT count(*) FROM analytics.holiday WHERE factor = 0)                 AS ganze_tage,
       (SELECT count(*) FROM analytics.holiday WHERE factor > 0 AND factor < 1)  AS davon_halbe_tage,
       (SELECT min(day) FROM analytics.holiday)                                  AS feiertage_ab,
       (SELECT max(day) FROM analytics.holiday)                                  AS feiertage_bis;
