-- MIGRONDI:NAME=1790985600000_ground_mark_game_date.sql
-- MIGRONDI:TIMESTAMP=1790985600000
-- ---------- MIGRONDI:UP ----------
-- The in-game date the author's client reported at placement (protocol 12):
-- era, year, month 1..12, day, day of week 0..6 (0 = Sundas), hour, minute.
-- All seven are set together; marks stored before keep NULL in all of them
-- and are shown without a date. Flavour only: order and lifetime use created_at.
ALTER TABLE ground_marks ADD COLUMN game_era INTEGER CHECK (game_era BETWEEN 1 AND 99);
ALTER TABLE ground_marks ADD COLUMN game_year INTEGER CHECK (game_year BETWEEN 1 AND 99999);
ALTER TABLE ground_marks ADD COLUMN game_month INTEGER CHECK (game_month BETWEEN 1 AND 12);
ALTER TABLE ground_marks ADD COLUMN game_day INTEGER CHECK (game_day BETWEEN 1 AND 31);
ALTER TABLE ground_marks ADD COLUMN game_day_of_week INTEGER CHECK (game_day_of_week BETWEEN 0 AND 6);
ALTER TABLE ground_marks ADD COLUMN game_hour INTEGER CHECK (game_hour BETWEEN 0 AND 23);
ALTER TABLE ground_marks ADD COLUMN game_minute INTEGER CHECK (game_minute BETWEEN 0 AND 59);
PRAGMA user_version = 7;
-- ---------- MIGRONDI:DOWN ----------
ALTER TABLE ground_marks DROP COLUMN game_minute;
ALTER TABLE ground_marks DROP COLUMN game_hour;
ALTER TABLE ground_marks DROP COLUMN game_day_of_week;
ALTER TABLE ground_marks DROP COLUMN game_day;
ALTER TABLE ground_marks DROP COLUMN game_month;
ALTER TABLE ground_marks DROP COLUMN game_year;
ALTER TABLE ground_marks DROP COLUMN game_era;
PRAGMA user_version = 6;
