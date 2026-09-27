namespace Dreamsleeve.Server.Infrastructure

open System
open System.IO
open System.Threading
open Microsoft.Data.Sqlite
open SqlHydra.Query
open Dreamsleeve.Server.Domain
open Dreamsleeve.Server.Infrastructure.AccountSchema

type StoredAccount = {
    AccountId: int64
    Profile: PlayerData
    PasswordHash: string
}

[<RequireQualifiedAccess>]
type AccountStoreError =
    | UsernameTaken
    | Canceled
    | Failed of exn

/// Synchronous units of work. The caller admits them to a bounded worker outside
/// its agent handler; SQLite's async methods do not provide asynchronous I/O.
[<RequireQualifiedAccess>]
module SqliteAccountStore =
    let initialize config =
        SqliteDatabase.initialize config (Path.Combine(AppContext.BaseDirectory, "db", "migrations"))

    let private invalidData message =
        Error(AccountStoreError.Failed(InvalidDataException message))

    let private withContext config (token: CancellationToken) action =
        if token.IsCancellationRequested then
            Error AccountStoreError.Canceled
        else
            try
                use connection = new SqliteConnection(SqliteDatabase.connectionString config SqliteOpenMode.ReadWrite)
                connection.Open()
                use context = new QueryContext(connection, SqliteEmitter())

                if token.IsCancellationRequested then Error AccountStoreError.Canceled
                else action context
            with error ->
                Error(AccountStoreError.Failed error)

    let private toProfile username (row: main.profiles) =
        if row.player_id <= 0L || row.account_id <= 0L then
            invalidData "The stored account/profile identifier is outside the positive Int64 range."
        else
            match PlayerId.create (uint64 row.player_id), DisplayName.create Int32.MaxValue row.display_name with
            | Ok playerId, Ok displayName when DisplayName.value displayName = row.display_name ->
                Ok(PlayerData.create playerId username displayName)
            | _ -> invalidData "The stored profile contains an invalid identifier or display name."

    let find config (username: Username) token =
        withContext config token (fun context ->
            let name = Username.value username
            let query = select {
                for account in main.accounts do
                join profile in main.profiles on (account.id = profile.account_id)
                where (account.username = name)
                select (account, profile)
            }

            match context.SelectOne query with
            | None -> Ok None
            | Some (account, profile) ->
                toProfile username profile
                |> Result.map (fun data -> Some { AccountId = account.id; Profile = data; PasswordHash = account.password_hash }))

    let private insertAccount (context: QueryContext) username passwordHash =
        let row: main.accounts = { id = 0L; username = Username.value username; password_hash = passwordHash }
        let query = insert {
            for account in main.accounts do
            entity row
            getId account.id
        }

        try
            context.Insert query |> Ok
        with :? SqliteException as error when error.SqliteExtendedErrorCode = 2067 ->
            Error AccountStoreError.UsernameTaken

    let create config (username: Username) (displayName: DisplayName) passwordHash (token: CancellationToken) =
        withContext config token (fun context ->
            use transaction = context.Connection.BeginTransaction()
            context.Transaction <- Some transaction

            match insertAccount context username passwordHash with
            | Error error -> Error error
            | Ok accountId ->
                let row: main.profiles = {
                    player_id = 0L
                    account_id = accountId
                    display_name = DisplayName.value displayName
                }
                let query = insert {
                    for profile in main.profiles do
                    entity row
                    getId profile.player_id
                }
                let playerId = context.Insert query

                match toProfile username { row with player_id = playerId } with
                | Error error -> Error error
                | Ok profile ->
                    if token.IsCancellationRequested then
                        Error AccountStoreError.Canceled
                    else
                        transaction.Commit()
                        Ok profile)

    /// A concurrent password change wins over a stale rehash. A missed compare
    /// is deliberately a successful no-op; it must never overwrite the new hash.
    let rehash config (username: Username) expectedHash replacementHash token =
        withContext config token (fun context ->
            let name = Username.value username
            let query = update {
                for account in main.accounts do
                set account.password_hash replacementHash
                where (account.username = name && account.password_hash = expectedHash)
            }
            context.Update query |> ignore
            Ok ())
