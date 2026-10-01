namespace Dreamsleeve.Server.Infrastructure

open System
open System.Data.Common
open System.IO
open Dreamsleeve.Server.Domain
open Dreamsleeve.Server.Infrastructure.SqliteStatements

/// What an order or a lift came to.
[<RequireQualifiedAccess>]
type SanctionOutcome =
    /// The sanction issued, or the one lifted.
    | Applied of Sanction
    | Refused of SanctionError

/// A sanction in force with the player it holds on, as the panel lists it.
type SanctionRecord = {
    Sanction: Sanction
    Target: PlayerData
}

/// Mutes, bans and kicks (db/migrations/*_sanctions.sql), run by the account
/// service's workers like the account store. The rules are the domain's: who
/// may act (PlayerRole.outranks), terms and expiry (Sanction). Every action
/// writes its audit line in the same transaction. Times are Unix milliseconds.
[<RequireQualifiedAccess>]
module SqliteSanctionStore =
    /// The most sanctions the panel lists at once.
    [<Literal>]
    let MaxListed = 500

    [<Literal>]
    let private Columns = "s.id, s.player_id, s.kind, s.reason, s.issued_by_admin, s.issued_by_player, s.issued_at, s.expires_at"

    /// Not lifted and not expired at @now.
    [<Literal>]
    let private InForce = "lifted_at IS NULL AND (expires_at IS NULL OR expires_at > @now)"

    let private invalidData () = Error(AccountStoreError.Failed(InvalidDataException "A stored sanction is invalid."))

    let private milliseconds (time: DateTimeOffset) = time.ToUnixTimeMilliseconds()

    let private player (id: PlayerId) = box (int64 (PlayerId.value id))

    let private optional (reader: DbDataReader) index =
        if reader.IsDBNull index then ValueNone else ValueSome(reader.GetInt64 index)

    let private issuer (reader: DbDataReader) =
        match optional reader 4, optional reader 5 with
        | ValueSome admin, _ -> AdminId.create admin |> Result.map (SanctionIssuer.Admin >> ValueSome)
        | ValueNone, ValueSome moderator -> PlayerId.create (uint64 moderator) |> Result.map (SanctionIssuer.Moderator >> ValueSome)
        | ValueNone, ValueNone -> Ok ValueNone

    // The columns of Columns, in their order.
    let private read (reader: DbDataReader) =
        match SanctionId.create (reader.GetInt64 0), PlayerId.create (uint64 (reader.GetInt64 1)), SanctionKind.ofInt (int (reader.GetInt64 2)),
              SanctionReason.create (reader.GetString 3), issuer reader with
        | Ok id, Ok target, ValueSome kind, Ok reason, Ok issuedBy ->
            Ok { Id = id; Target = target; Kind = kind; Scope = SanctionScope.Server; Reason = reason; IssuedBy = issuedBy
                 IssuedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64 6)
                 Expires = optional reader 7 |> ValueOption.map DateTimeOffset.FromUnixTimeMilliseconds }
        | _ -> invalidData ()

    let private readAll (reader: DbDataReader) row =
        let rec next rows =
            if not (reader.Read()) then Ok(List.rev rows)
            else
                match row reader with
                | Ok value -> next (value :: rows)
                | Error error -> Error error
        next []

    let private inForceOn context target now =
        use statement = command context $"SELECT {Columns} FROM sanctions s WHERE s.player_id=@player AND {InForce}"
                            [ "@player", player target; "@now", box (milliseconds now) ]
        use reader = statement.ExecuteReader()
        readAll reader read

    /// The sanctions in force on a player at now, at most one of each kind.
    let active config target now token =
        SqliteAccountStore.withContext config token (fun context -> inForceOn context target now)

    // The stored role of a registered player; absent when there is no such player.
    let private roleOf context (id: PlayerId) =
        match scalar context "SELECT COALESCE(r.role, 0) FROM profiles p LEFT JOIN player_roles r ON r.player_id=p.player_id WHERE p.player_id=@player"
                  [ "@player", player id ] with
        | :? int64 as value ->
            match PlayerRole.ofInt (int value) with
            | ValueSome role -> Ok(ValueSome role)
            | ValueNone -> invalidData ()
        | _ -> Ok ValueNone

    // An administrator acts on any registered player, a moderator only on one it outranks.
    let private authorize context issuer target =
        match roleOf context target with
        | Error error -> Error error
        | Ok ValueNone -> Ok(Error SanctionError.PlayerNotFound)
        | Ok(ValueSome targetRole) ->
            match issuer with
            | SanctionIssuer.Admin _ -> Ok(Ok())
            | SanctionIssuer.Moderator actor ->
                roleOf context actor
                |> Result.map (function
                    | ValueSome actorRole when PlayerRole.outranks actorRole targetRole -> Ok()
                    | ValueSome _ | ValueNone -> Error SanctionError.NotAllowed)

    let private authorized context issuer target action =
        match authorize context issuer target with
        | Error error -> Error error
        | Ok(Error refused) -> Ok(SanctionOutcome.Refused refused)
        | Ok(Ok()) -> action ()

    let private actor = function
        | SanctionIssuer.Admin id -> AuditActor.Admin id
        | SanctionIssuer.Moderator id -> AuditActor.Moderator id

    let private term (sanction: Sanction) =
        match sanction.Expires with
        | ValueSome expires -> expires.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'")
        | ValueNone -> "lifted"

    let private audit context issuer action (target: PlayerId) details now =
        SqliteAdminStore.audit context (actor issuer) (AuditRecord.create action (AuditTarget.Player target) details) now

    /// Issues order at now; the sanction of its kind in force is lifted first.
    let issue config (order: SanctionOrder) (now: DateTimeOffset) token =
        // Stored in milliseconds: the sanction returned is the one read back later.
        let now = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds now)
        SqliteAccountStore.withContext config token (fun context ->
            transaction context (fun () ->
                authorized context order.IssuedBy order.Target (fun () ->
                    let at = box (milliseconds now)
                    execute context $"UPDATE sanctions SET lifted_at=@now WHERE player_id=@player AND kind=@kind AND {InForce}"
                        [ "@player", player order.Target; "@kind", box (SanctionKind.toInt order.Kind); "@now", at ] |> ignore
                    let admin, moderator =
                        match order.IssuedBy with
                        | SanctionIssuer.Admin id -> box (AdminId.value id), box DBNull.Value
                        | SanctionIssuer.Moderator id -> box DBNull.Value, player id
                    let expires = Sanction.expiry now order.Term |> ValueOption.map milliseconds
                    let id =
                        scalar context
                            "INSERT INTO sanctions(player_id, kind, reason, issued_by_admin, issued_by_player, issued_at, expires_at) VALUES (@player, @kind, @reason, @admin, @moderator, @now, @expires) RETURNING id"
                            [ "@player", player order.Target; "@kind", box (SanctionKind.toInt order.Kind)
                              "@reason", box (SanctionReason.value order.Reason); "@admin", admin; "@moderator", moderator; "@now", at
                              "@expires", (match expires with ValueSome value -> box value | ValueNone -> box DBNull.Value) ]
                    match SanctionId.create (id :?> int64) with
                    | Ok id ->
                        let sanction = Sanction.issue id now order
                        // The devices the player signed in from (kept SignInHistoryDays) share the ban.
                        let devices =
                            if order.Kind = SanctionKind.Ban && order.Devices then
                                let count =
                                    execute context "INSERT INTO device_bans(sanction_id, device) SELECT @id, device FROM player_devices WHERE player_id=@player"
                                        [ "@id", box (SanctionId.value id); "@player", player order.Target ]
                                $", devices: {count}"
                            else ""
                        audit context order.IssuedBy AdminAction.SanctionedPlayer order.Target
                            $"{SanctionKind.key sanction.Kind} until {term sanction}{devices}: {SanctionReason.value sanction.Reason}" now
                        Ok(SanctionOutcome.Applied sanction)
                    | Error _ -> invalidData ())))

    /// The ban in force at now whose devices include device: a device ban
    /// holds exactly as long as the account ban it came with.
    let bannedDevice config (device: DeviceId) (now: DateTimeOffset) token =
        SqliteAccountStore.withContext config token (fun context ->
            use statement =
                command context
                    $"SELECT {Columns} FROM device_bans d JOIN sanctions s ON s.id=d.sanction_id WHERE d.device=@device AND {InForce} ORDER BY s.issued_at DESC LIMIT 1"
                    [ "@device", box (DeviceId.value device); "@now", box (milliseconds now) ]
            use reader = statement.ExecuteReader()
            readAll reader read |> Result.map (function [] -> ValueNone | ban :: _ -> ValueSome ban))

    /// Lifts the sanction of kind in force on target at now.
    let lift config target kind issuer now token =
        SqliteAccountStore.withContext config token (fun context ->
            transaction context (fun () ->
                authorized context issuer target (fun () ->
                    match inForceOn context target now with
                    | Error error -> Error error
                    | Ok sanctions ->
                        match Sanction.find kind now sanctions with
                        | ValueNone -> Ok(SanctionOutcome.Refused SanctionError.NotActive)
                        | ValueSome sanction ->
                            execute context "UPDATE sanctions SET lifted_at=@now WHERE id=@id"
                                [ "@id", box (SanctionId.value sanction.Id); "@now", box (milliseconds now) ] |> ignore
                            audit context issuer AdminAction.LiftedSanction target (SanctionKind.key kind) now
                            Ok(SanctionOutcome.Applied sanction))))

    /// Whether issuer may end the target's session now; the runtime does it.
    let kick config target (reason: SanctionReason) issuer now token =
        SqliteAccountStore.withContext config token (fun context ->
            transaction context (fun () ->
                match authorize context issuer target with
                | Error error -> Error error
                | Ok(Error refused) -> Ok(Error refused)
                | Ok(Ok()) ->
                    audit context issuer AdminAction.KickedPlayer target (SanctionReason.value reason) now
                    Ok(Ok())))

    /// The sanctions in force at now with their players, newest first.
    let listActive config now token =
        SqliteAccountStore.withContext config token (fun context ->
            use statement =
                command context
                    $"SELECT {Columns}, a.username, p.display_name FROM sanctions s JOIN profiles p ON p.player_id=s.player_id JOIN accounts a ON a.id=p.account_id WHERE {InForce} ORDER BY s.issued_at DESC LIMIT @limit"
                    [ "@now", box (milliseconds now); "@limit", box MaxListed ]
            use reader = statement.ExecuteReader()
            readAll reader (fun reader ->
                match read reader, Username.create Int32.MaxValue (reader.GetString 8), DisplayName.create Int32.MaxValue (reader.GetString 9) with
                | Ok sanction, Ok username, Ok name -> Ok { Sanction = sanction; Target = PlayerData.create sanction.Target username name }
                | _ -> invalidData ()))
