-- MIGRONDI:NAME=1791158400000_moderator_audit.sql
-- MIGRONDI:TIMESTAMP=1791158400000
-- ---------- MIGRONDI:UP ----------
-- Moderators act from the game (protocol 15): an audit line names either the
-- panel administrator (admin_id) or the moderator (moderator_id, a player),
-- never both. A moderator's line keeps its text once the profile is gone.
CREATE TABLE admin_audit_actors (
    id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    admin_id INTEGER REFERENCES admin_accounts(id),
    moderator_id INTEGER REFERENCES profiles(player_id) ON DELETE SET NULL,
    action TEXT NOT NULL CHECK (length(action) > 0),
    target TEXT NOT NULL,
    details TEXT NOT NULL,
    at INTEGER NOT NULL,
    CHECK (admin_id IS NULL OR moderator_id IS NULL)
);
INSERT INTO admin_audit_actors(id, admin_id, action, target, details, at)
    SELECT id, admin_id, action, target, details, at FROM admin_audit;
DROP TABLE admin_audit;
ALTER TABLE admin_audit_actors RENAME TO admin_audit;
CREATE INDEX admin_audit_admin ON admin_audit(admin_id);
CREATE INDEX admin_audit_moderator ON admin_audit(moderator_id);
CREATE INDEX admin_audit_at ON admin_audit(at);
PRAGMA user_version = 9;
-- ---------- MIGRONDI:DOWN ----------
-- Schema 8 has no moderator actor: those lines cannot go back and are dropped.
CREATE TABLE admin_audit_admins (
    id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    admin_id INTEGER NOT NULL REFERENCES admin_accounts(id),
    action TEXT NOT NULL CHECK (length(action) > 0),
    target TEXT NOT NULL,
    details TEXT NOT NULL,
    at INTEGER NOT NULL
);
INSERT INTO admin_audit_admins(id, admin_id, action, target, details, at)
    SELECT id, admin_id, action, target, details, at FROM admin_audit WHERE admin_id IS NOT NULL;
DROP TABLE admin_audit;
ALTER TABLE admin_audit_admins RENAME TO admin_audit;
CREATE INDEX admin_audit_admin ON admin_audit(admin_id);
CREATE INDEX admin_audit_at ON admin_audit(at);
PRAGMA user_version = 8;
