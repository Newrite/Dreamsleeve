-- Historical design example; not the current Dreamsleeve contract.
-- See docs/CurrentStateRu.md and the README in this example directory.
PRAGMA foreign_keys = ON;

CREATE TABLE IF NOT EXISTS global_sanctions (
    sanction_id              TEXT PRIMARY KEY,
    player_id                TEXT NOT NULL,
    sanction_type            INTEGER NOT NULL CHECK (sanction_type IN (0, 1)),
    issued_by_player_id      TEXT,
    reason                   TEXT,
    issued_at_ms             INTEGER NOT NULL,
    expires_at_ms            INTEGER,
    revoked_at_ms            INTEGER,
    revoked_by_player_id     TEXT,
    FOREIGN KEY (player_id) REFERENCES players(player_id) ON DELETE CASCADE,
    FOREIGN KEY (issued_by_player_id) REFERENCES players(player_id),
    FOREIGN KEY (revoked_by_player_id) REFERENCES players(player_id)
);

CREATE TABLE IF NOT EXISTS text_filter_terms (
    term_id                  TEXT PRIMARY KEY,
    term                     TEXT NOT NULL,
    normalized_term          TEXT NOT NULL,
    mode                     INTEGER NOT NULL CHECK (mode IN (0, 1)),
    created_at_ms            INTEGER NOT NULL,
    created_by_player_id     TEXT,
    FOREIGN KEY (created_by_player_id) REFERENCES players(player_id)
);

CREATE TABLE IF NOT EXISTS scheduled_announcements (
    announcement_id          TEXT PRIMARY KEY,
    channel_scope            INTEGER NOT NULL CHECK (channel_scope IN (0, 1)),
    is_enabled               INTEGER NOT NULL DEFAULT 1 CHECK (is_enabled IN (0, 1)),
    interval_seconds         INTEGER NOT NULL CHECK (interval_seconds >= 1),
    message_text             TEXT NOT NULL,
    created_at_ms            INTEGER NOT NULL,
    updated_at_ms            INTEGER NOT NULL,
    created_by_player_id     TEXT,
    FOREIGN KEY (created_by_player_id) REFERENCES players(player_id)
);

CREATE TABLE IF NOT EXISTS audit_events (
    audit_event_id           TEXT PRIMARY KEY,
    category                 INTEGER NOT NULL CHECK (category IN (0, 1, 2, 3, 4, 5)),
    actor_player_id          TEXT,
    subject_player_id        TEXT,
    guild_id                 TEXT,
    party_id                 TEXT,
    action_type              TEXT NOT NULL,
    description              TEXT,
    payload_text             TEXT,
    occurred_at_ms           INTEGER NOT NULL
);
