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
                  Username.create Int32.MaxValue account.username, DisplayName.create Int32.MaxValue profile.display_name with
            | Ok markId, Ok author, Ok body, Ok characterName, Ok plugin, Ok localId, Ok position, Ok heading, Ok username, Ok displayName
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
                         Author = PlayerData.create author username displayName }
            | _ -> invalidData "A stored ground mark or its author profile is invalid."

    /// All marks, ascending by ID, with the storage high-water mark. Expiry is
    /// the owner's rule and is applied after loading.
    let loadAll config token =
        SqliteAccountStore.withContext config token (fun context ->
            let query = select {
                for mark in main.ground_marks do
                join profile in main.profiles on (mark.author_id = profile.player_id)
                join account in main.accounts on (profile.account_id = account.id)
                orderBy mark.id
                select (mark, profile, account)
            }
            let rows = context.Select query |> List.ofSeq
            let records =
                rows |> List.fold (fun state (mark, profile, account) ->
                    match state with
                    | Error error -> Error error
                    | Ok records ->
                        match toRecord mark profile account with
                        | Ok record -> Ok (record :: records)
                        | Error error -> Error error) (Ok [])
            match records with
            | Error error -> Error error
            | Ok reversed ->
                // sqlite_sequence is not a schema table; the sequence outlives deleted rows.
                use sequence = context.Connection.CreateCommand()
                sequence.CommandText <-
                    "SELECT MAX(COALESCE((SELECT seq FROM sqlite_sequence WHERE name = 'ground_marks'), 0), COALESCE((SELECT MAX(id) FROM ground_marks), 0))"
                let highest = sequence.ExecuteScalar() :?> int64
                if highest < 0L || highest = Int64.MaxValue then invalidData "The ground mark ID sequence is exhausted."
                else Ok { Marks = List.rev reversed; NextId = uint64 highest + 1UL })

    let insert config (mark: GroundMark) token =
        let id = GroundMarkId.value mark.Id
        let author = PlayerId.value mark.Author
        if id > uint64 Int64.MaxValue || author > uint64 Int64.MaxValue then
            invalidData "Ground mark identifiers must fit the positive Int64 range of SQLite."
        else
            let date (part: GameDate -> int) = mark.GameDate |> ValueOption.map (part >> int64) |> ValueOption.toOption
            SqliteAccountStore.withContext config token (fun context ->
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
                Ok ())

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

    let private write config (logger: ILogger) (context: AgentContext<GroundMarkWrite>) (request: GroundMarkWrite) = task {
        let result =
            match request with
            | GroundMarkWrite.Insert mark -> insert config mark context.CancellationToken
            | GroundMarkWrite.Delete ids -> delete config ids context.CancellationToken
        match result with
        | Ok () -> ()
        | Error (AccountStoreError.Failed error) -> logger.LogError(error, "Ground mark storage write failed: {Write}", request)
        | Error error -> logger.LogError("Ground mark storage write failed: {Error}", error)
    }

    /// One sequential writer keeps order between an eviction and the insert
    /// that caused it. A failed write is logged; memory stays authoritative
    /// for the running server and the next successful write is unaffected.
    let startWriter config (logger: ILogger) capacity =
        if capacity < 1 then Error "Ground mark writer capacity must be positive."
        else
            let options = { AgentOptions.create "ground-mark-writer" with Mailbox = AgentMailbox.boundedWait capacity }
            Ok (Agent.Start(options, write config logger))
