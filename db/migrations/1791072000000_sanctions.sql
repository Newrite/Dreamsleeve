-- MIGRONDI:NAME=1791072000000_sanctions.sql
-- MIGRONDI:TIMESTAMP=1791072000000
-- ---------- MIGRONDI:UP ----------
-- Mutes (kind 0) and bans (kind 1) of the whole server. A row is in force while
-- lifted_at is NULL and expires_at is NULL (until lifted) or later than now; a
-- new sanction lifts the one of its kind in force, so at most one per player and
-- kind holds. Exactly one issuer column is set when issued; it becomes NULL once
-- that account is gone. Times are Unix milliseconds (UTC).
CREATE TABLE sanctions (
    id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT CHECK (id > 0),
    player_id INTEGER NOT NULL REFERENCES profiles(player_id) ON DELETE CASCADE,
    kind INTEGER NOT NULL CHECK (kind IN (0, 1)),
    reason TEXT NOT NULL CHECK (length(reason) > 0),
    issued_by_admin INTEGER REFERENCES admin_accounts(id) ON DELETE SET NULL,
    issued_by_player INTEGER REFERENCES profiles(player_id) ON DELETE SET NULL,
    issued_at INTEGER NOT NULL,
    expires_at INTEGER,
    lifted_at INTEGER
);
CREATE INDEX sanctions_player ON sanctions(player_id, kind, lifted_at);
CREATE INDEX sanctions_admin ON sanctions(issued_by_admin);
CREATE INDEX sanctions_issuer ON sanctions(issued_by_player);
PRAGMA user_version = 8;
-- ---------- MIGRONDI:DOWN ----------
DROP TABLE sanctions;
PRAGMA user_version = 7;
