-- MIGRONDI:NAME=1791331200000_addresses.sql
-- MIGRONDI:TIMESTAMP=1791331200000
-- ---------- MIGRONDI:UP ----------
-- Client IP addresses (docs/AuthenticationRu.md, «Адреса входа и баны IP»), 16
-- bytes each with IPv4 mapped into IPv6, so byte order is address order.
-- sign_in_addresses: where a player signed in or registered from; rows older than
-- [Authentication.Service] SignInHistoryDays after their last sign-in are deleted.
-- address_bans: ranges banned from the panel, in force while lifted_at is NULL and
-- expires_at is NULL (until lifted) or later than now. Times are Unix milliseconds.
CREATE TABLE sign_in_addresses (
    player_id INTEGER NOT NULL REFERENCES profiles(player_id) ON DELETE CASCADE,
    address BLOB NOT NULL CHECK (length(address) = 16),
    first_seen INTEGER NOT NULL,
    last_seen INTEGER NOT NULL,
    sign_ins INTEGER NOT NULL CHECK (sign_ins > 0),
    PRIMARY KEY (player_id, address)
);
CREATE INDEX sign_in_addresses_address ON sign_in_addresses(address);
CREATE INDEX sign_in_addresses_seen ON sign_in_addresses(last_seen);
CREATE TABLE address_bans (
    id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT CHECK (id > 0),
    network BLOB NOT NULL CHECK (length(network) = 16),
    prefix INTEGER NOT NULL CHECK (prefix BETWEEN 0 AND 128),
    reason TEXT NOT NULL CHECK (length(reason) > 0),
    issued_by INTEGER REFERENCES admin_accounts(id) ON DELETE SET NULL,
    issued_at INTEGER NOT NULL,
    expires_at INTEGER,
    lifted_at INTEGER
);
CREATE INDEX address_bans_open ON address_bans(lifted_at);
CREATE INDEX address_bans_admin ON address_bans(issued_by);
PRAGMA user_version = 11;
-- ---------- MIGRONDI:DOWN ----------
DROP TABLE address_bans;
DROP TABLE sign_in_addresses;
PRAGMA user_version = 10;
