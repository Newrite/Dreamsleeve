-- MIGRONDI:NAME=1790553600000_saved_auth.sql
-- MIGRONDI:TIMESTAMP=1790553600000
-- ---------- MIGRONDI:UP ----------
CREATE TABLE account_passwords (
    account_id INTEGER NOT NULL PRIMARY KEY REFERENCES accounts(id) ON DELETE CASCADE,
    password_hash TEXT NOT NULL CHECK (length(password_hash) > 0)
);
INSERT INTO account_passwords SELECT id, password_hash FROM accounts;
ALTER TABLE accounts DROP COLUMN password_hash;
CREATE TABLE auth_tokens (
    token_hash TEXT NOT NULL PRIMARY KEY,
    account_id INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
    kind INTEGER NOT NULL CHECK (kind IN (0, 1)),
    expires_at INTEGER NOT NULL
);
CREATE INDEX auth_tokens_account ON auth_tokens(account_id, kind);
CREATE INDEX auth_tokens_expiry ON auth_tokens(expires_at);
-- Provider subjects are verified by the provider adapter, never supplied as proof by a client.
CREATE TABLE account_identities (
    provider TEXT NOT NULL,
    subject TEXT NOT NULL,
    account_id INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
    PRIMARY KEY (provider, subject)
);
INSERT INTO account_identities SELECT 'password', username, id FROM accounts;
PRAGMA user_version = 2;
-- ---------- MIGRONDI:DOWN ----------
ALTER TABLE accounts ADD COLUMN password_hash TEXT NOT NULL DEFAULT '!external-only!';
UPDATE accounts SET password_hash=COALESCE((SELECT password_hash FROM account_passwords WHERE account_id=accounts.id), '!external-only!');
DROP TABLE account_passwords;
DROP TABLE account_identities;
DROP TABLE auth_tokens;
PRAGMA user_version = 1;
