# SQLite account storage

The server persists accounts and profiles in SQLite. Registration creates both rows
in one transaction; login reads the existing profile. Account IDs and player IDs
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
when an ADO.NET method is named `Async`. The account service therefore executes
whole store operations in its bounded worker, outside the agent's mailbox.
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

## Verification

```powershell
dotnet run --project tests/Dreamsleeve.Server.Tests -- --filter "SQLite accounts"
```

The tests use isolated temporary databases. They cover restart persistence,
canonical username conflicts, concurrent registration, full rollback on a profile
insert failure, password hash compare-and-swap, cancellation, unsupported/damaged
schema rejection, and ID non-reuse after deletion.

Schema v2 moves optional password credentials out of accounts and adds
`account_identities(provider, subject, account_id)` for verified provider mappings.
`auth_tokens` stores SHA-256 hashes, account IDs, kind (0 saved login / 1 reset code),
and absolute UTC expiry. Raw bearer tokens are never persisted in SQLite. The reset
transaction consumes its code, replaces the password hash and revokes all account tokens.
The account service also invalidates in-memory tickets and notifies the game runtime.
Do not bypass that service by modifying live authentication rows from an admin UI.
