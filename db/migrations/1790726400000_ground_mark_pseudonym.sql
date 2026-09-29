-- MIGRONDI:NAME=1790726400000_ground_mark_pseudonym.sql
-- MIGRONDI:TIMESTAMP=1790726400000
-- ---------- MIGRONDI:UP ----------
-- A mark placed while its author hid their names keeps the server pseudonym of
-- that moment: it is shown instead of the author's profile for the lifetime of
-- the mark, also after restarts. Marks placed with names shown keep NULL and
-- show the author's current profile, as before.
ALTER TABLE ground_marks ADD COLUMN author_pseudonym TEXT;
PRAGMA user_version = 4;
-- ---------- MIGRONDI:DOWN ----------
ALTER TABLE ground_marks DROP COLUMN author_pseudonym;
PRAGMA user_version = 3;
