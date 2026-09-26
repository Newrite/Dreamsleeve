-- Historical design example; not the current Dreamsleeve contract.
-- See docs/CurrentStateRu.md and the README in this example directory.
PRAGMA foreign_keys = ON;

CREATE TABLE IF NOT EXISTS players (
    player_id                TEXT PRIMARY KEY,
    current_nickname         TEXT NOT NULL,
    normalized_nickname      TEXT NOT NULL,
    created_at_ms            INTEGER NOT NULL,
    updated_at_ms            INTEGER NOT NULL,
    last_login_at_ms         INTEGER,
    is_deleted               INTEGER NOT NULL DEFAULT 0 CHECK (is_deleted IN (0, 1))
);

CREATE TABLE IF NOT EXISTS player_identities (
    identity_id              TEXT PRIMARY KEY,
    player_id                TEXT NOT NULL,
    public_key               BLOB NOT NULL,
    algorithm                TEXT NOT NULL,
    key_version              INTEGER NOT NULL CHECK (key_version >= 1),
    is_active                INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0, 1)),
    created_at_ms            INTEGER NOT NULL,
    revoked_at_ms            INTEGER,
    revoked_reason           TEXT,
    FOREIGN KEY (player_id) REFERENCES players(player_id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS player_identity_rebinds (
    rebind_id                TEXT PRIMARY KEY,
    player_id                TEXT NOT NULL,
    old_identity_id          TEXT,
    new_identity_id          TEXT NOT NULL,
    performed_by_player_id   TEXT,
    note                     TEXT,
    created_at_ms            INTEGER NOT NULL,
    FOREIGN KEY (player_id) REFERENCES players(player_id) ON DELETE CASCADE,
    FOREIGN KEY (old_identity_id) REFERENCES player_identities(identity_id),
    FOREIGN KEY (new_identity_id) REFERENCES player_identities(identity_id),
    FOREIGN KEY (performed_by_player_id) REFERENCES players(player_id)
);

CREATE TABLE IF NOT EXISTS player_server_roles (
    player_id                TEXT NOT NULL,
    role                     INTEGER NOT NULL CHECK (role IN (0, 1, 2)),
    granted_by_player_id     TEXT,
    granted_at_ms            INTEGER NOT NULL,
    PRIMARY KEY (player_id, role),
    FOREIGN KEY (player_id) REFERENCES players(player_id) ON DELETE CASCADE,
    FOREIGN KEY (granted_by_player_id) REFERENCES players(player_id)
);

CREATE TABLE IF NOT EXISTS player_sessions (
    session_id               TEXT PRIMARY KEY,
    player_id                TEXT NOT NULL,
    resume_token_hash        BLOB NOT NULL UNIQUE,
    issued_at_ms             INTEGER NOT NULL,
    expires_at_ms            INTEGER NOT NULL,
    last_seen_at_ms          INTEGER NOT NULL,
    revoked_at_ms            INTEGER,
    remote_addr              TEXT,
    client_platform          TEXT,
    client_build             TEXT,
    FOREIGN KEY (player_id) REFERENCES players(player_id) ON DELETE CASCADE
);
