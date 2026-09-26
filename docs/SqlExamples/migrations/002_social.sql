-- Historical design example; not the current Dreamsleeve contract.
-- See docs/CurrentStateRu.md and the README in this example directory.
PRAGMA foreign_keys = ON;

CREATE TABLE IF NOT EXISTS friend_requests (
    request_id               TEXT PRIMARY KEY,
    from_player_id           TEXT NOT NULL,
    to_player_id             TEXT NOT NULL,
    status                   INTEGER NOT NULL CHECK (status IN (0, 1, 2, 3, 4)),
    created_at_ms            INTEGER NOT NULL,
    responded_at_ms          INTEGER,
    note                     TEXT,
    CHECK (from_player_id <> to_player_id),
    FOREIGN KEY (from_player_id) REFERENCES players(player_id) ON DELETE CASCADE,
    FOREIGN KEY (to_player_id) REFERENCES players(player_id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS friendships (
    player_low_id            TEXT NOT NULL,
    player_high_id           TEXT NOT NULL,
    created_at_ms            INTEGER NOT NULL,
    created_by_request_id    TEXT,
    PRIMARY KEY (player_low_id, player_high_id),
    CHECK (player_low_id < player_high_id),
    FOREIGN KEY (player_low_id) REFERENCES players(player_id) ON DELETE CASCADE,
    FOREIGN KEY (player_high_id) REFERENCES players(player_id) ON DELETE CASCADE,
    FOREIGN KEY (created_by_request_id) REFERENCES friend_requests(request_id)
);

CREATE TABLE IF NOT EXISTS player_blocks (
    blocker_player_id        TEXT NOT NULL,
    blocked_player_id        TEXT NOT NULL,
    created_at_ms            INTEGER NOT NULL,
    reason                   TEXT,
    PRIMARY KEY (blocker_player_id, blocked_player_id),
    CHECK (blocker_player_id <> blocked_player_id),
    FOREIGN KEY (blocker_player_id) REFERENCES players(player_id) ON DELETE CASCADE,
    FOREIGN KEY (blocked_player_id) REFERENCES players(player_id) ON DELETE CASCADE
);
