-- MIGRONDI:NAME=1791936000000_death_mark_levels.sql
-- MIGRONDI:TIMESTAMP=1791936000000
-- ---------- MIGRONDI:UP ----------
-- Levels at the moment of death (protocol 22): the dead character's and the
-- killer's. Only death marks hold them; marks stored before keep NULL in both
-- and are shown without levels. NULL killer_level also means no killer, or one
-- without a level. Levels are not bounded above: mods raise them.
ALTER TABLE ground_marks ADD COLUMN character_level INTEGER CHECK (character_level IS NULL OR (character_level >= 1 AND kind = 2));
ALTER TABLE ground_marks ADD COLUMN killer_level INTEGER CHECK (killer_level IS NULL OR (killer_level >= 1 AND kind = 2));
PRAGMA user_version = 18;
-- ---------- MIGRONDI:DOWN ----------
ALTER TABLE ground_marks DROP COLUMN killer_level;
ALTER TABLE ground_marks DROP COLUMN character_level;
PRAGMA user_version = 17;
