-- MIGRONDI:NAME=1791417600000_devices.sql
-- MIGRONDI:TIMESTAMP=1791417600000
-- ---------- MIGRONDI:UP ----------
-- Devices (docs/AuthenticationRu.md, «Устройства»): the per-server hash a client
-- sends, 64 hex characters. player_devices: where a player signed in from, kept
-- like sign_in_addresses. device_bans: devices an account ban also covers; a row
-- holds exactly as long as its sanction, and goes with it. Times are Unix milliseconds.
CREATE TABLE player_devices (
    player_id INTEGER NOT NULL REFERENCES profiles(player_id) ON DELETE CASCADE,
    device TEXT NOT NULL CHECK (length(device) = 64),
    first_seen INTEGER NOT NULL,
    last_seen INTEGER NOT NULL,
    sign_ins INTEGER NOT NULL CHECK (sign_ins > 0),
    PRIMARY KEY (player_id, device)
);
CREATE INDEX player_devices_device ON player_devices(device);
CREATE INDEX player_devices_seen ON player_devices(last_seen);
CREATE TABLE device_bans (
    sanction_id INTEGER NOT NULL REFERENCES sanctions(id) ON DELETE CASCADE,
    device TEXT NOT NULL CHECK (length(device) = 64),
    PRIMARY KEY (sanction_id, device)
);
CREATE INDEX device_bans_device ON device_bans(device);
PRAGMA user_version = 12;
-- ---------- MIGRONDI:DOWN ----------
DROP TABLE device_bans;
DROP TABLE player_devices;
PRAGMA user_version = 11;
