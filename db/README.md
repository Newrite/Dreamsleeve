# SQLite storage

The server persists accounts and profiles in SQLite, together with ground marks, the
admin panel's tables and sanctions (see «Schema versions»). Registration creates the
account and profile rows in one transaction; login reads the existing profile. Account IDs and player IDs
are separate positive `INTEGER PRIMARY KEY AUTOINCREMENT` values. Their supported
range is `1..Int64.MaxValue`; the network still carries player IDs as `uint64`.
Deleting an account cascades to its profile and never reuses committed IDs.

`account_passwords.password_hash` contains the opaque ASP.NET Core Identity password hash.
Passwords, hashing, tickets and authorization are owned by the account service,
outside the database functions. A password rehash uses compare-and-swap against
the hash that was verified, so stale work cannot overwrite newer credentials.

## Startup and ownership

`SqliteAccountStore.initialize` uses Migrondi.Core to apply checked-in migrations
before listeners start, verifies `application_id`, `user_version` and the required
projection, then enables WAL. A newer schema, a database belonging to another
application, missing migrations or migration errors fail startup. There is no
fallback to an empty in-memory store.

Each store call owns a pooled connection, enables foreign keys and uses the
configured finite `BusyTimeoutSeconds`. Ordinary calls open in `ReadWrite` mode;
only initialization can create the file. SQLite commands are synchronous even
when an ADO.NET method is named `Async`. The account and admin services therefore
execute whole store operations in their bounded workers, outside the agent's mailbox;
ground mark writes go through their own sequential writer (`SqliteGroundMarkStore.startWriter`).
Cancellation is checked before work and before committing a registration; an
already running native call still has to finish, bounded by the provider timeout.

`Migrondi.Core` includes other supported database drivers transitively. This is
the cost of using its existing migration runner during startup; the application
uses only the SQLite provider. The SQL schema remains the source of truth.

## Migrations and generated types

Each migration is one `{timestamp}_{name}.sql` file with Migrondi V1 metadata and
`MIGRONDI:UP` / `MIGRONDI:DOWN` sections. Migrondi wraps each migration in a
transaction. Never edit an already deployed migration: add another migration and
advance `PRAGMA user_version` together with the supported version in
`SqliteDatabase.fs`. Automatic startup only applies migrations; it never rolls back.

The Infrastructure project copies `db/migrations/*.sql` into build and publish
output. Keep that directory with the server binaries.

From the repository root:

```powershell
dotnet tool restore
dotnet fsi Scripts/generate_sqlite_schema.fsx
dotnet build src/Dreamsleeve.Server.Infrastructure/Dreamsleeve.Server.Infrastructure.fsproj
```

The script recreates only `build/sqlhydra/schema.db`, applies the real migrations
through Migrondi, then invokes the pinned SqlHydra.Cli. Configuration lives in
`src/Dreamsleeve.Server.Infrastructure/sqlhydra-sqlite.toml`; output is
`Generated/AccountSchema.fs`. Check generated changes into Git. Ordinary builds
compile the checked-in file and do not need a live database or installed codegen
tool. The TOML file configures the generator only; runtime configuration is TOML.

Pinned packages: Microsoft.Data.Sqlite **10.0.12**, Migrondi.Core **1.3.0**,
SqlHydra.Query and SqlHydra.Cli **5.0.0**. The CLI version was verified by actual
NuGet restore; some NuGet web search results still show the older CLI 4.1.0.

## Schema versions

Schema 1 (`1790467200000_accounts.sql`) creates `accounts` and `profiles`.

Schema v2 (`1790553600000_saved_auth.sql`) moves optional password credentials out of accounts and adds
`account_identities(provider, subject, account_id)` for verified provider mappings.
`auth_tokens` stores SHA-256 hashes, account IDs, kind (0 saved login / 1 reset code),
and absolute UTC expiry. Raw bearer tokens are never persisted in SQLite. The reset
transaction consumes its code, replaces the password hash and revokes all account tokens.
The account service also invalidates in-memory tickets and notifies the game runtime.
Do not bypass that service by modifying live authentication rows from an admin UI.

Schema 3 (`1790640000000_ground_marks.sql`) adds `ground_marks`: notes (kind 1) and death
marks (kind 2) with the author (`author_id -> profiles`, `ON DELETE CASCADE`), the published
character name, the text, the placement (`plugin_name`, `local_form_id`, `x`, `y`, `z`,
`heading`) and `created_at`. The server issues the IDs; `AUTOINCREMENT` keeps the high-water
mark, so IDs never repeat after deletions and restarts. `SqliteGroundMarkStore` owns the SQL.
Schema 4 (`1790726400000_ground_mark_pseudonym.sql`) adds `author_pseudonym`: a mark placed
under a pseudonym keeps it; NULL shows the author's current profile.

Schema v5 (`1790812800000_admin.sql`) adds the web admin panel (docs/AdminPanelRu.md).
Times are Unix milliseconds (UTC); secrets are stored only as SHA-256 hashes.

| Table | Contents |
|---|---|
| `admin_accounts(id, username UNIQUE, password_hash, created_at)` | Administrators, separate from player accounts; same PasswordHasher |
| `admin_sessions(token_hash PK, admin_id, created_at, expires_at)` | Panel cookies; a new password deletes all of an administrator's rows |
| `admin_api_tokens(token_hash PK, admin_id, label, created_at)` | REST bearer tokens with a 1..64 character label |
| `player_roles(player_id PK -> profiles, role, granted_by, granted_at)` | 0 player, 1 moderator; read with the profile at login/resume |
| `admin_audit(id, admin_id, action, target, details, at)` | One line per panel mutation; indexed by `admin_id` and `at` |

`SqliteAdminStore` owns the SQL of these tables and runs in the admin service's bounded
workers; the account service reads `player_roles` with the profile and writes audit lines of
sanctions and moderator actions through `SqliteAdminStore.audit`. Every panel mutation writes
its audit line in the same transaction or right after the owning agent reports success.
One-time setup/reset codes live only in the service's memory.
The DOWN section drops the five tables and returns `user_version` to 4.

Schema v6 (`1790899200000_display_names.sql`) adds `display_name_changes(id, player_id ->
profiles, old_name, new_name, changed_by -> admin_accounts NULL, at)`: every display name
change, by the player (`changed_by` NULL) or by an administrator. The account service writes
it in the same transaction as the new name and reads the latest own change to enforce
`[Identity] DisplayNameChangeIntervalMinutes`. Only the newest
`[Authentication.Service] DisplayNameHistory` (20) rows per player are kept; older ones are
deleted in the same transaction. DOWN drops it and returns to version 5.

Schema 7 (`1790985600000_ground_mark_game_date.sql`) adds the in-game date of a mark at
placement: `game_era`, `game_year`, `game_month`, `game_day`, `game_day_of_week`, `game_hour`,
`game_minute`, each range-checked and set together. Older marks keep NULL and have no date;
order and lifetime still use `created_at`.

Schema 8 (`1791072000000_sanctions.sql`) adds `sanctions`: mutes (kind 0) and
bans (kind 1) with a required reason, the issuing administrator or moderator,
`issued_at`, an optional `expires_at` and `lifted_at` (Unix milliseconds). A row
is in force while it is not lifted and not expired; issuing lifts the one of its
kind in force. `SqliteSanctionStore` owns the SQL; the rules are the domain's.

Schema 9 (`1791158400000_moderator_audit.sql`) lets an audit line name a moderator
acting in the game: `admin_audit.admin_id` becomes nullable and `moderator_id`
references `profiles(player_id)` with `ON DELETE SET NULL`; a CHECK forbids both. A
moderator's line keeps its text once the profile is gone. `SqliteAdminStore.audit` is
the one writer of audit lines: panel actions, sanction actions of both issuers (in the
sanction's transaction) and content a moderator removed. DOWN drops the moderator lines
(schema 8 has no such actor) and returns to version 8.

Schema 10 (`1791244800000_registration.sql`) adds `server_settings`: settings the panel
and the console change at run time, one row per key (the registration mode). Schema 11
(`1791331200000_addresses.sql`) adds `sign_in_addresses` and `address_bans` (IP ranges);
schema 12 (`1791417600000_devices.sql`) adds `player_devices` and `device_bans`
(docs/AuthenticationRu.md).

Schema 13 (`1791504000000_guilds.sql`) adds guilds (docs/GuildsRu.md): `guilds` with a
unique lower-case `name_key` (names are unique regardless of case; AUTOINCREMENT never
reuses an ID, so a guild's chat channel never names another guild), `guild_members` with
the role (0 member, 1 officer, 2 master) and an optional guild mute, and `guild_invites`
with their expiry. Members and invitations go with their guild and with the player's
profile (`ON DELETE CASCADE`). `SqliteGuildStore` owns the SQL; one sequential writer of
the guild owner applies the changes.

Schema 14 (`1791590400000_name_colors.sql`) adds `profiles.name_color`: the color of the
player's name in chat, 0xRRGGBB (docs/ModerationAndNamesRu.md). Every existing profile gets
a random one of the 16 colors new accounts get theirs from (`NameColor.palette`); the
account store picks one for each new profile. Readability is checked when a player chooses
a color, not when a profile loads. DOWN drops the column. This is the schema version the
server supports (`SqliteDatabase.SchemaVersion`).

## Verification

```powershell
dotnet run --project tests/Dreamsleeve.Server.Tests -c Release -- --filter-test-list SQLite
```

The tests use isolated temporary databases. Accounts: restart persistence, canonical
username conflicts, concurrent registration, full rollback on a profile insert failure,
password hash compare-and-swap, cancellation, unsupported/damaged schema rejection and ID
non-reuse after deletion. The admin, sanction and ground mark stores: migrations 5..9 over
schema 4 and their rollback, sessions, tokens, roles, search, audit, sanction terms and
issuers, marks with their pseudonym and game date across restarts and account deletion.


## Stored representation and failure boundaries

Each read projection checks the actual SQLite INTEGER/TEXT/BLOB representation
before provider getters can coerce it. Signed identifiers must stay positive,
integer-to-Int32 values must fit their width, and Unix milliseconds must fit
DateTimeOffset's supported range. Numeric REAL projections also permit SQLite
INTEGER values. Domain constructors continue to own enum and value validation;
nullable fields and genuinely missing rows retain their documented absence.
Credential expiry is projected and checked before the expiry decision: saved credentials
keep INTEGER seconds; panel sessions keep supported INTEGER milliseconds. Valid
expired tokens retain their existing invalid-credential/absence outcomes. An audit
line with both administrator and moderator issuers is corruption; both deleted
issuers remain legitimate absence.
The account, administrator and ground-mark read projections inspect raw columns
before materialization; generated inserts and transaction ordering are unchanged.

A unit of work owns its connection, query context and transaction. Expected
SqliteException values retain their original cause in AccountStoreError.Failed;
owned cancellation is Canceled. Unexpected work faults leave this dependency
adapter after deterministic disposal/rollback. Authentication and admin workers
supervise a fresh isolated request: they log the original fault, return
unavailable, and never retry the work. Their actor-owned state changes only from
completed results, so this boundary does not resume partially mutated actor state.
Guild and ground-mark writers instead terminate and let the runtime reconstruct
its state from storage when a write fails.

Startup adapts filesystem/path failures only around those dependency calls, and
Migrondi's documented source/setup/application failures only around migration
execution. An aggregate containing an unexpected cause escapes to the startup
lifetime. Failure still prevents listeners from starting; the schema remains 14.
The SQLite boundaries suite uses temporary owned databases to check coercion,
corrupt stored values, provider locking/open failures, rollback and resource
release on an original unexpected fault, cancellation and startup failures.
