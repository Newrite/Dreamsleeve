-- MIGRONDI:NAME=1791676800000_guild_titles.sql
-- MIGRONDI:TIMESTAMP=1791676800000
-- ---------- MIGRONDI:UP ----------
-- Custom roles of a guild (docs/GuildsRu.md): names the master gives, with no
-- rights. name_key is the name in lower case: unique inside the guild
-- regardless of case. AUTOINCREMENT never reuses an ID. A member holds at most
-- one (guild_members.title_id). The guild store clears the holders before it
-- deletes a role and reads every title_id back against its guild, so the
-- column needs no foreign key, which would keep DOWN from dropping it.
CREATE TABLE guild_titles (
    id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT CHECK (id > 0),
    guild_id INTEGER NOT NULL REFERENCES guilds(id) ON DELETE CASCADE,
    name TEXT NOT NULL CHECK (length(name) > 0),
    name_key TEXT NOT NULL CHECK (length(name_key) > 0),
    UNIQUE (guild_id, name_key)
);
ALTER TABLE guild_members ADD COLUMN title_id INTEGER NULL CHECK (title_id IS NULL OR title_id > 0);
PRAGMA user_version = 15;
-- ---------- MIGRONDI:DOWN ----------
ALTER TABLE guild_members DROP COLUMN title_id;
DROP TABLE guild_titles;
PRAGMA user_version = 14;
