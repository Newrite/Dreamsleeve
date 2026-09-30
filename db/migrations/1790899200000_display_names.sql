-- MIGRONDI:NAME=1790899200000_display_names.sql
-- MIGRONDI:TIMESTAMP=1790899200000
-- ---------- MIGRONDI:UP ----------
-- Every display name change: by the player (changed_by NULL) or by an
-- administrator. The latest own change limits how often a player may change
-- the name again, across reconnects and restarts. Times are Unix milliseconds.
CREATE TABLE display_name_changes (
    id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    player_id INTEGER NOT NULL REFERENCES profiles(player_id) ON DELETE CASCADE,
    old_name TEXT NOT NULL,
    new_name TEXT NOT NULL CHECK (length(new_name) > 0),
    changed_by INTEGER REFERENCES admin_accounts(id) ON DELETE SET NULL,
    at INTEGER NOT NULL
);
CREATE INDEX display_name_changes_player ON display_name_changes(player_id, at);
PRAGMA user_version = 6;
-- ---------- MIGRONDI:DOWN ----------
DROP TABLE display_name_changes;
PRAGMA user_version = 5;
