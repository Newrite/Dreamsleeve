-- MIGRONDI:NAME=1790640000000_ground_marks.sql
-- MIGRONDI:TIMESTAMP=1790640000000
-- ---------- MIGRONDI:UP ----------
-- Marks on the ground: notes and death places. The server issues ids itself and
-- inserts them explicitly; AUTOINCREMENT keeps sqlite_sequence as the high-water
-- mark so ids never repeat after deletions and restarts. The author's profile is
-- joined by player_id; only the published character name at placement is kept, as
-- in chat messages. Deleting an account removes its marks.
CREATE TABLE ground_marks (
    id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT CHECK (id > 0),
    author_id INTEGER NOT NULL REFERENCES profiles(player_id) ON DELETE CASCADE,
    character_name TEXT,
    kind INTEGER NOT NULL CHECK (kind IN (1, 2)),
    text TEXT NOT NULL,
    plugin_name TEXT NOT NULL CHECK (length(plugin_name) > 0),
    local_form_id INTEGER NOT NULL CHECK (local_form_id > 0),
    x REAL NOT NULL,
    y REAL NOT NULL,
    z REAL NOT NULL,
    heading REAL NOT NULL,
    created_at INTEGER NOT NULL
);
CREATE INDEX ground_marks_author ON ground_marks(author_id, kind, id);
PRAGMA user_version = 3;
-- ---------- MIGRONDI:DOWN ----------
DROP TABLE ground_marks;
PRAGMA user_version = 2;
