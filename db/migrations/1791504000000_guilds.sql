-- MIGRONDI:NAME=1791504000000_guilds.sql
-- MIGRONDI:TIMESTAMP=1791504000000
-- ---------- MIGRONDI:UP ----------
-- Guilds (docs/GuildsRu.md): a guild with its members and pending invitations.
-- A disbanded guild is deleted together with them, which frees its name;
-- AUTOINCREMENT never reuses an ID, so a guild's chat channel never names
-- another guild. name_key is the name in lower case: names are unique
-- regardless of case. Roles: 0 member, 1 officer, 2 master. A guild mute has
-- a reason and a time; muted_until NULL holds until lifted. muted_by and
-- invited_by keep the player ID even when that account is gone.
-- Times are Unix milliseconds.
CREATE TABLE guilds (
    id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT CHECK (id > 0),
    name TEXT NOT NULL CHECK (length(name) > 0),
    name_key TEXT NOT NULL UNIQUE CHECK (length(name_key) > 0),
    created_at INTEGER NOT NULL
);
CREATE TABLE guild_members (
    guild_id INTEGER NOT NULL REFERENCES guilds(id) ON DELETE CASCADE,
    player_id INTEGER NOT NULL REFERENCES profiles(player_id) ON DELETE CASCADE,
    role INTEGER NOT NULL CHECK (role IN (0, 1, 2)),
    joined_at INTEGER NOT NULL,
    mute_reason TEXT NULL CHECK (mute_reason IS NULL OR length(mute_reason) > 0),
    muted_by INTEGER NULL,
    muted_at INTEGER NULL,
    muted_until INTEGER NULL,
    PRIMARY KEY (guild_id, player_id),
    CHECK ((mute_reason IS NULL) = (muted_at IS NULL) AND (mute_reason IS NULL) = (muted_by IS NULL))
);
CREATE INDEX guild_members_player ON guild_members(player_id);
CREATE TABLE guild_invites (
    guild_id INTEGER NOT NULL REFERENCES guilds(id) ON DELETE CASCADE,
    player_id INTEGER NOT NULL REFERENCES profiles(player_id) ON DELETE CASCADE,
    invited_by INTEGER NOT NULL,
    created_at INTEGER NOT NULL,
    expires_at INTEGER NOT NULL,
    PRIMARY KEY (guild_id, player_id)
);
CREATE INDEX guild_invites_player ON guild_invites(player_id);
PRAGMA user_version = 13;
-- ---------- MIGRONDI:DOWN ----------
DROP TABLE guild_invites;
DROP TABLE guild_members;
DROP TABLE guilds;
PRAGMA user_version = 12;
