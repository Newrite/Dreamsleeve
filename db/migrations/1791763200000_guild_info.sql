-- MIGRONDI:NAME=1791763200000_guild_info.sql
-- MIGRONDI:TIMESTAMP=1791763200000
-- ---------- MIGRONDI:UP ----------
-- What the master tells about the guild (docs/GuildsRu.md): chat text as
-- written, NULL when there is none. The length bound is [Guilds]
-- InfoMaxLength, checked when the master writes it.
ALTER TABLE guilds ADD COLUMN info TEXT NULL CHECK (info IS NULL OR length(info) > 0);
PRAGMA user_version = 16;
-- ---------- MIGRONDI:DOWN ----------
ALTER TABLE guilds DROP COLUMN info;
PRAGMA user_version = 15;
