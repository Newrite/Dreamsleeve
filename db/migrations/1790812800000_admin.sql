-- MIGRONDI:NAME=1790812800000_admin.sql
-- MIGRONDI:TIMESTAMP=1790812800000
-- ---------- MIGRONDI:UP ----------
-- The server web panel. Administrators are separate from player accounts: a
-- stolen game account never opens the panel. Passwords use the same hasher as
-- players; sessions and API tokens keep only SHA-256 hashes of their secrets,
-- like auth_tokens. Times are Unix milliseconds (UTC).
CREATE TABLE admin_accounts (
    id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT CHECK (id > 0),
    username TEXT NOT NULL UNIQUE CHECK (length(username) > 0),
    password_hash TEXT NOT NULL CHECK (length(password_hash) > 0),
    created_at INTEGER NOT NULL
);
CREATE TABLE admin_sessions (
    token_hash TEXT NOT NULL PRIMARY KEY,
    admin_id INTEGER NOT NULL REFERENCES admin_accounts(id) ON DELETE CASCADE,
    created_at INTEGER NOT NULL,
    expires_at INTEGER NOT NULL
);
CREATE INDEX admin_sessions_admin ON admin_sessions(admin_id);
CREATE INDEX admin_sessions_expiry ON admin_sessions(expires_at);
CREATE TABLE admin_api_tokens (
    token_hash TEXT NOT NULL PRIMARY KEY,
    admin_id INTEGER NOT NULL REFERENCES admin_accounts(id) ON DELETE CASCADE,
    label TEXT NOT NULL CHECK (length(label) > 0),
    created_at INTEGER NOT NULL
);
CREATE INDEX admin_api_tokens_admin ON admin_api_tokens(admin_id);
-- A role belongs to a registered player (0 player, 1 moderator); the session
-- reads it when its ticket is issued. Deleting the profile removes the role.
CREATE TABLE player_roles (
    player_id INTEGER NOT NULL PRIMARY KEY REFERENCES profiles(player_id) ON DELETE CASCADE,
    role INTEGER NOT NULL CHECK (role IN (0, 1)),
    granted_by INTEGER REFERENCES admin_accounts(id) ON DELETE SET NULL,
    granted_at INTEGER NOT NULL
);
CREATE INDEX player_roles_admin ON player_roles(granted_by);
-- One line per panel mutation. Details hold public facts only, never secrets.
CREATE TABLE admin_audit (
    id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    admin_id INTEGER NOT NULL REFERENCES admin_accounts(id),
    action TEXT NOT NULL CHECK (length(action) > 0),
    target TEXT NOT NULL,
    details TEXT NOT NULL,
    at INTEGER NOT NULL
);
CREATE INDEX admin_audit_admin ON admin_audit(admin_id);
CREATE INDEX admin_audit_at ON admin_audit(at);
PRAGMA user_version = 5;
-- ---------- MIGRONDI:DOWN ----------
DROP TABLE admin_audit;
DROP TABLE player_roles;
DROP TABLE admin_api_tokens;
DROP TABLE admin_sessions;
DROP TABLE admin_accounts;
PRAGMA user_version = 4;
