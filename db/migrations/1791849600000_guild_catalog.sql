-- MIGRONDI:NAME=1791849600000_guild_catalog.sql
-- MIGRONDI:TIMESTAMP=1791849600000
-- ---------- MIGRONDI:UP ----------
-- The public guild catalog (docs/GuildsRu.md): whether the catalog shows a
-- guild (1, the default for every guild) and the players' pending requests to
-- join one. A request goes with its guild and with the player's profile; its
-- answer, withdrawal or expiry deletes it. Times are Unix milliseconds.
ALTER TABLE guilds ADD COLUMN listed INTEGER NOT NULL DEFAULT 1 CHECK (listed IN (0, 1));
CREATE TABLE guild_join_requests (
    guild_id INTEGER NOT NULL REFERENCES guilds(id) ON DELETE CASCADE,
    player_id INTEGER NOT NULL REFERENCES profiles(player_id) ON DELETE CASCADE,
    created_at INTEGER NOT NULL,
    expires_at INTEGER NOT NULL,
    PRIMARY KEY (guild_id, player_id)
);
CREATE INDEX guild_join_requests_player ON guild_join_requests(player_id);
PRAGMA user_version = 17;
-- ---------- MIGRONDI:DOWN ----------
DROP TABLE guild_join_requests;
ALTER TABLE guilds DROP COLUMN listed;
PRAGMA user_version = 16;
