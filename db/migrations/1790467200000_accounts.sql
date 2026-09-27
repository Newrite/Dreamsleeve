-- MIGRONDI:NAME=1790467200000_accounts.sql
-- MIGRONDI:TIMESTAMP=1790467200000
-- ---------- MIGRONDI:UP ----------

CREATE TABLE accounts (
    id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT CHECK (id > 0),
    username TEXT NOT NULL UNIQUE CHECK (length(username) > 0),
    password_hash TEXT NOT NULL CHECK (length(password_hash) > 0)
);

CREATE TABLE profiles (
    player_id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT CHECK (player_id > 0),
    account_id INTEGER NOT NULL UNIQUE REFERENCES accounts(id) ON DELETE CASCADE,
    display_name TEXT NOT NULL CHECK (length(display_name) > 0)
);

PRAGMA application_id = 1146309718;
PRAGMA user_version = 1;

-- ---------- MIGRONDI:DOWN ----------

DROP TABLE profiles;
DROP TABLE accounts;
PRAGMA user_version = 0;
PRAGMA application_id = 0;
