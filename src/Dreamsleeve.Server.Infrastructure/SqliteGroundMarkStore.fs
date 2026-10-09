namespace Dreamsleeve.Server.Infrastructure

open System
open System.IO
open Microsoft.Extensions.Logging
open SqlHydra.Query
open Dreamsleeve.Agent
open Dreamsleeve.Server.Core
open Dreamsleeve.Server.Domain
open Dreamsleeve.Server.Infrastructure.AccountSchema

/// Every stored mark with its author's current profile, plus the next ID to issue.
type LoadedGroundMarks = {
    Marks: StoredGroundMark list
    /// One above the highest ID storage ever issued (sqlite_sequence), so a
    /// deleted top mark cannot lend its ID to a new one after a restart.
    NextId: uint64
}

/// Synchronous units of work over the ground_marks table, on the same SqlHydra
/// context as accounts. The mark owner never calls them: loading runs at
/// startup before the runtime, writes go through the writer agent of startWriter.
[<RequireQualifiedAccess>]
module SqliteGroundMarkStore =
    let private invalidData message =
        Error(AccountStoreError.Failed(InvalidDataException message))

    let private kindNumber = function
        | GroundMarkKind.Note -> 1L
        | GroundMarkKind.Death -> 2L

    // All seven columns or none: marks stored before the game date was kept
    // have none. The domain factory checks the ranges again.
    let private gameDate (mark: main.ground_marks) =
        let small (value: int64) = if value < 0L || value > int64 Int32.MaxValue then -1 else int value
        match mark.game_era, mark.game_year, mark.game_month, mark.game_day, mark.game_day_of_week, mark.game_hour, mark.game_minute with
        | None, None, None, None, None, None, None -> Ok ValueNone
        | Some era, Some year, Some month, Some day, Some weekday, Some hour, Some minute ->
            GameDate.create (small era) (small year) (small month) (small day) (small weekday) (small hour) (small minute)
            |> Result.map ValueSome
        | _ -> Error(DomainError.InvalidGameDate "partial")

    // Stored rows were validated when written; limits here only guard against a
    // damaged file, so they are the widest the domain accepts.
    let private toRecord (mark: main.ground_marks) (profile: main.profiles) (account: main.accounts) : Result<StoredGroundMark, AccountStoreError> =
        if mark.id <= 0L || mark.author_id <= 0L || mark.local_form_id <= 0L || mark.local_form_id > int64 UInt32.MaxValue then
            invalidData "A stored ground mark has an identifier outside its range."
        else
            let body =
                match mark.kind with
                | 1L -> GroundNoteText.create Int32.MaxValue mark.text |> Result.map GroundMarkBody.Note
                | 2L -> DeathMarkText.create Int32.MaxValue mark.text |> Result.map GroundMarkBody.Death
                | _ -> Error(DomainError.InvalidLimit("kind", 0))
            let characterName =
                match mark.character_name with
                | None -> Ok ValueNone
                | Some name -> CharacterName.create Int32.MaxValue name |> Result.map ValueSome
            let pseudonym =
                match mark.author_pseudonym with
                | None -> Ok ValueNone
                | Some name -> Pseudonym.restore name |> Result.map ValueSome
            match pseudonym, gameDate mark with
            | Error _, _ -> invalidData "A stored ground mark has an invalid author pseudonym."
            | _, Error _ -> invalidData "A stored ground mark has an invalid game date."
            | Ok pseudonym, Ok gameDate ->
            match GroundMarkId.create (uint64 mark.id), PlayerId.create (uint64 mark.author_id), body, characterName,
                  PluginName.create Int32.MaxValue mark.plugin_name, LocalFormId.create (uint32 mark.local_form_id),
                  Position.create (float32 mark.x) (float32 mark.y) (float32 mark.z), Radian.create (float32 mark.heading),
                  Username.create Int32.MaxValue account.username, DisplayName.create Int32.MaxValue profile.display_name,
                  SqliteStatements.nameColor profile.name_color with
            | Ok markId, Ok author, Ok body, Ok characterName, Ok plugin, Ok localId, Ok position, Ok heading, Ok username, Ok displayName, Ok color
                when PluginName.value plugin = mark.plugin_name && DisplayName.value displayName = profile.display_name ->
                let placement = GroundMarkPlacement.create (FormKey.create plugin localId) position heading
                let createdAt =
                    try Ok (DateTimeOffset.FromUnixTimeMilliseconds mark.created_at)
                    with :? ArgumentOutOfRangeException -> Error ()
                match createdAt with
                | Error () -> invalidData "A stored ground mark has a creation time outside the supported range."
                | Ok createdAt ->
                    Ok { Mark =
                            GroundMark.create markId author body placement createdAt
                            |> GroundMark.withCharacterName characterName
                            |> GroundMark.withPseudonym pseudonym
                            |> GroundMark.withGameDate gameDate
                         Author = PlayerData.create author username displayName color }
            | _ -> invalidData "A stored ground mark or its author profile is invalid."

    let private MarkProjection =
        [| SqliteStored.Column.PositiveInteger
           SqliteStored.Column.PositiveInteger
           (SqliteStored.Column.Nullable SqliteStored.Column.Text)
           SqliteStored.Column.Integer
           SqliteStored.Column.Text
           SqliteStored.Column.Text
           SqliteStored.Column.Integer
           SqliteStored.Column.Number
           SqliteStored.Column.Number
           SqliteStored.Column.Number
           SqliteStored.Column.Number
           SqliteStored.Column.UnixMilliseconds
           (SqliteStored.Column.Nullable SqliteStored.Column.Text)
           (SqliteStored.Column.Nullable SqliteStored.Column.Integer)
           (SqliteStored.Column.Nullable SqliteStored.Column.Integer)
           (SqliteStored.Column.Nullable SqliteStored.Column.Integer)
           (SqliteStored.Column.Nullable SqliteStored.Column.Integer)
           (SqliteStored.Column.Nullable SqliteStored.Column.Integer)
           (SqliteStored.Column.Nullable SqliteStored.Column.Integer)
           (SqliteStored.Column.Nullable SqliteStored.Column.Integer)
           SqliteStored.Column.PositiveInteger
           SqliteStored.Column.PositiveInteger
           SqliteStored.Column.Text
           SqliteStored.Column.Integer
           SqliteStored.Column.PositiveInteger
           SqliteStored.Column.Text |]

    /// All marks, ascending by ID, with the storage high-water mark. Expiry is
    /// the owner's rule and is applied after loading.
    let loadAll config token =
        SqliteAccountStore.withContext config token (fun context ->
            use statement = SqliteStatements.command context
                                "SELECT m.id,m.author_id,m.character_name,m.kind,m.text,m.plugin_name,m.local_form_id,m.x,m.y,m.z,m.heading,m.created_at,m.author_pseudonym,m.game_era,m.game_year,m.game_month,m.game_day,m.game_day_of_week,m.game_hour,m.game_minute,p.player_id,p.account_id,p.display_name,p.name_color,a.id,a.username FROM ground_marks m JOIN profiles p ON m.author_id=p.player_id JOIN accounts a ON p.account_id=a.id ORDER BY m.id"
                                []
            use reader = statement.ExecuteReader()
            let optional index read = if reader.IsDBNull index then None else Some(read index)
            let rec next records =
                if not (reader.Read()) then Ok records
                else
                    let decoded = SqliteAccountStore.storedRow reader 0 MarkProjection (fun () ->
                        let mark: main.ground_marks = {
                            id = reader.GetInt64 0; author_id = reader.GetInt64 1; character_name = optional 2 reader.GetString
                            kind = reader.GetInt64 3; text = reader.GetString 4; plugin_name = reader.GetString 5; local_form_id = reader.GetInt64 6
                            x = reader.GetDouble 7; y = reader.GetDouble 8; z = reader.GetDouble 9; heading = reader.GetDouble 10
                            created_at = reader.GetInt64 11; author_pseudonym = optional 12 reader.GetString
                            game_era = optional 13 reader.GetInt64; game_year = optional 14 reader.GetInt64; game_month = optional 15 reader.GetInt64
                            game_day = optional 16 reader.GetInt64; game_day_of_week = optional 17 reader.GetInt64
                            game_hour = optional 18 reader.GetInt64; game_minute = optional 19 reader.GetInt64 }
                        let profile: main.profiles = { player_id = reader.GetInt64 20; account_id = reader.GetInt64 21
                                                       display_name = reader.GetString 22; name_color = reader.GetInt64 23 }
                        let account: main.accounts = { id = reader.GetInt64 24; username = reader.GetString 25 }
                        toRecord mark profile account)
                    match decoded with
                    | Ok record -> next (record :: records)
                    | Error error -> Error error
            let records = next []
            reader.Close()
            match records with
            | Error error -> Error error
            | Ok reversed ->
                // sqlite_sequence is not a schema table; the sequence outlives deleted rows.
                use sequence = context.Connection.CreateCommand()
                sequence.CommandText <-
                    "SELECT MAX(COALESCE((SELECT seq FROM sqlite_sequence WHERE name = 'ground_marks'), 0), COALESCE((SELECT MAX(id) FROM ground_marks), 0))"
                match sequence.ExecuteScalar() with
                | :? int64 as highest when highest >= 0L && highest < Int64.MaxValue ->
                    Ok { Marks = List.rev reversed; NextId = uint64 highest + 1UL }
                | _ -> invalidData "The ground mark ID sequence is invalid or exhausted.")

    let private insertInto (context: QueryContext) (mark: GroundMark) =
        let id = GroundMarkId.value mark.Id
        let author = PlayerId.value mark.Author
        if id > uint64 Int64.MaxValue || author > uint64 Int64.MaxValue then
            invalidData "Ground mark identifiers must fit the positive Int64 range of SQLite."
        else
            let date (part: GameDate -> int) = mark.GameDate |> ValueOption.map (part >> int64) |> ValueOption.toOption
            let row: main.ground_marks = {
                id = int64 id
                author_id = int64 author
                character_name = mark.CharacterName |> ValueOption.map CharacterName.value |> ValueOption.toOption
                kind = kindNumber mark.Kind
                text = mark.Text
                plugin_name = PluginName.value mark.Placement.LocationId.PluginName
                local_form_id = int64 (LocalFormId.value mark.Placement.LocationId.LocalFormId)
                x = float (WorldUnit.value mark.Placement.Position.X)
                y = float (WorldUnit.value mark.Placement.Position.Y)
                z = float (WorldUnit.value mark.Placement.Position.Z)
                heading = float (Radian.value mark.Placement.Heading)
                created_at = Core.toUnixMilliseconds mark.CreatedAt
                author_pseudonym = mark.Pseudonym |> ValueOption.map Pseudonym.value |> ValueOption.toOption
                game_era = date (fun value -> value.Era)
                game_year = date (fun value -> value.Year)
                game_month = date (fun value -> value.Month)
                game_day = date (fun value -> value.Day)
                game_day_of_week = date (fun value -> value.DayOfWeek)
                game_hour = date (fun value -> value.Hour)
                game_minute = date (fun value -> value.Minute)
            }
            let query = insert {
                for stored in main.ground_marks do
                entity row
            }
            context.Insert query |> ignore
            Ok ()

    let insert config mark token =
        SqliteAccountStore.withContext config token (fun context -> insertInto context mark)

    let delete config (ids: GroundMarkId list) token =
        if ids.IsEmpty then Ok ()
        else
            SqliteAccountStore.withContext config token (fun context ->
                let values = ids |> List.map (fun id -> int64 (GroundMarkId.value id))
                let query = delete {
                    for stored in main.ground_marks do
                    where (isIn stored.id values)
                }
                context.Delete query |> ignore
                Ok ())

    /// A replacement is one admitted write; a failed insert rolls back the eviction.
    let replace config evicted mark token =
        SqliteAccountStore.withContext config token (fun context ->
            SqliteStatements.transaction context (fun () ->
                SqliteStatements.execute context "DELETE FROM ground_marks WHERE id=@id"
                    [ "@id", box (int64 (GroundMarkId.value evicted)) ] |> ignore
                insertInto context mark))

    let private write config (logger: ILogger) (context: ReliableAgentContext<GroundMarkWrite>) (request: GroundMarkWrite) = task {
        let result =
            match request with
            | GroundMarkWrite.Insert mark -> insert config mark context.CancellationToken
            | GroundMarkWrite.Replace(evicted, mark) -> replace config evicted mark context.CancellationToken
            | GroundMarkWrite.Delete ids -> delete config ids context.CancellationToken
        match result with
        | Ok () -> ()
        | Error (AccountStoreError.Failed error) -> logger.LogError(error, "Ground mark storage write failed: {Write}", request)
        | Error error -> logger.LogError("Ground mark storage write failed: {Error}", error)
    }

    /// One sequential writer keeps order between an eviction and the insert
    /// that caused it. A failed write is logged; memory stays authoritative
    /// for the running server and the next successful write is unaffected.
    /// capacity is GroundMarks.MaxPendingWrites, checked with the configuration.
    let tryPrepareWriter config (logger: ILogger) capacity =
        let options = { AgentOptions.create "ground-mark-writer" with Mailbox = AgentMailbox.boundedWait capacity }
        Agent.TryPrepareReliable(options, write config logger)

    let startWriter config logger capacity =
        tryPrepareWriter config logger capacity |> Result.map (fun plan -> plan.Start())
