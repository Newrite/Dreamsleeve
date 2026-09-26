-- Historical design example; not the current Dreamsleeve contract.
-- See docs/CurrentStateRu.md and the README in this example directory.
PRAGMA foreign_keys = ON;

CREATE TABLE IF NOT EXISTS parties (
    party_id                 TEXT PRIMARY KEY,
    leader_player_id         TEXT NOT NULL,
    status                   INTEGER NOT NULL CHECK (status IN (0, 1)),
    created_at_ms            INTEGER NOT NULL,
    disbanded_at_ms          INTEGER,
    disbanded_by_player_id   TEXT,
    FOREIGN KEY (leader_player_id) REFERENCES players(player_id),
    FOREIGN KEY (disbanded_by_player_id) REFERENCES players(player_id)
);

CREATE TABLE IF NOT EXISTS party_members (
    party_id                 TEXT NOT NULL,
    player_id                TEXT NOT NULL,
    joined_at_ms             INTEGER NOT NULL,
    join_order               INTEGER NOT NULL CHECK (join_order >= 1),
    invited_by_player_id     TEXT,
    PRIMARY KEY (party_id, player_id),
    UNIQUE (player_id),
    FOREIGN KEY (party_id) REFERENCES parties(party_id) ON DELETE CASCADE,
    FOREIGN KEY (player_id) REFERENCES players(player_id) ON DELETE CASCADE,
    FOREIGN KEY (invited_by_player_id) REFERENCES players(player_id)
);

CREATE TABLE IF NOT EXISTS party_invites (
    invite_id                TEXT PRIMARY KEY,
    party_id                 TEXT NOT NULL,
    invited_player_id        TEXT NOT NULL,
    invited_by_player_id     TEXT NOT NULL,
    status                   INTEGER NOT NULL CHECK (status IN (0, 1, 2, 3, 4)),
    created_at_ms            INTEGER NOT NULL,
    expires_at_ms            INTEGER NOT NULL,
    responded_at_ms          INTEGER,
    FOREIGN KEY (party_id) REFERENCES parties(party_id) ON DELETE CASCADE,
    FOREIGN KEY (invited_player_id) REFERENCES players(player_id) ON DELETE CASCADE,
    FOREIGN KEY (invited_by_player_id) REFERENCES players(player_id)
);

CREATE TABLE IF NOT EXISTS guilds (
    guild_id                 TEXT PRIMARY KEY,
    name                     TEXT NOT NULL,
    normalized_name          TEXT NOT NULL,
    created_by_player_id     TEXT NOT NULL,
    created_at_ms            INTEGER NOT NULL,
    status                   INTEGER NOT NULL CHECK (status IN (0, 1)),
    disbanded_at_ms          INTEGER,
    FOREIGN KEY (created_by_player_id) REFERENCES players(player_id)
);

CREATE TABLE IF NOT EXISTS guild_memberships (
    guild_id                 TEXT NOT NULL,
    player_id                TEXT NOT NULL,
    role                     INTEGER NOT NULL CHECK (role IN (0, 1, 2)),
    joined_at_ms             INTEGER NOT NULL,
    invited_by_player_id     TEXT,
    muted_until_ms           INTEGER,
    PRIMARY KEY (guild_id, player_id),
    FOREIGN KEY (guild_id) REFERENCES guilds(guild_id) ON DELETE CASCADE,
    FOREIGN KEY (player_id) REFERENCES players(player_id) ON DELETE CASCADE,
    FOREIGN KEY (invited_by_player_id) REFERENCES players(player_id)
);

CREATE TABLE IF NOT EXISTS guild_invites (
    invite_id                TEXT PRIMARY KEY,
    guild_id                 TEXT NOT NULL,
    invited_player_id        TEXT NOT NULL,
    invited_by_player_id     TEXT NOT NULL,
    status                   INTEGER NOT NULL CHECK (status IN (0, 1, 2, 3, 4)),
    created_at_ms            INTEGER NOT NULL,
    expires_at_ms            INTEGER NOT NULL,
    responded_at_ms          INTEGER,
    FOREIGN KEY (guild_id) REFERENCES guilds(guild_id) ON DELETE CASCADE,
    FOREIGN KEY (invited_player_id) REFERENCES players(player_id) ON DELETE CASCADE,
    FOREIGN KEY (invited_by_player_id) REFERENCES players(player_id)
);
