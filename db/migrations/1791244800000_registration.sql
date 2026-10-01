-- MIGRONDI:NAME=1791244800000_registration.sql
-- MIGRONDI:TIMESTAMP=1791244800000
-- ---------- MIGRONDI:UP ----------
-- Server-wide settings changed at run time from the admin panel or the console,
-- one row per key; a missing row means the default. registration_mode is open,
-- steam or manual (docs/AuthenticationRu.md). changed_by is the administrator,
-- NULL for the console or once that account is gone. Times are Unix milliseconds.
CREATE TABLE server_settings (
    key TEXT NOT NULL PRIMARY KEY CHECK (length(key) > 0),
    value TEXT NOT NULL,
    changed_by INTEGER REFERENCES admin_accounts(id) ON DELETE SET NULL,
    changed_at INTEGER NOT NULL
);
CREATE INDEX server_settings_admin ON server_settings(changed_by);
PRAGMA user_version = 10;
-- ---------- MIGRONDI:DOWN ----------
DROP TABLE server_settings;
PRAGMA user_version = 9;
