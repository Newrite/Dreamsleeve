namespace Dreamsleeve.Server.Infrastructure

open System
open System.Data.Common
open System.IO
open System.Net
open Dreamsleeve.Server.Domain
open Dreamsleeve.Server.Infrastructure.SqliteStatements

/// A player who signed in from an address in a range, as the panel lists them.
type AddressMatch = {
    Player: PlayerData
    Address: SignInAddress
}

/// Sign-in addresses and IP range bans (db/migrations/*_addresses.sql), run
/// by the account service's workers like the account store. Addresses are
/// stored as ClientAddress bytes; a ban writes and lifts with its audit line in
/// one transaction, like a sanction. Times are Unix milliseconds.
[<RequireQualifiedAccess>]
module SqliteAddressStore =
    /// The most rows one panel list shows.
    [<Literal>]
    let MaxListed = 200

    [<Literal>]
    let private BanColumns = "b.id, b.network, b.prefix, b.reason, b.issued_by, b.issued_at, b.expires_at"

    [<Literal>]
    let private InForce = "b.lifted_at IS NULL AND (b.expires_at IS NULL OR b.expires_at > @now)"

    let private invalidData () = Error(AccountStoreError.Failed(InvalidDataException "A stored address or address ban is invalid."))

    let private milliseconds (time: DateTimeOffset) = time.ToUnixTimeMilliseconds()

    let private time (reader: DbDataReader) index = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64 index)

    let private optional (reader: DbDataReader) index =
        if reader.IsDBNull index then ValueNone else ValueSome(reader.GetInt64 index)

    let private bytes (reader: DbDataReader) index = reader.GetFieldValue<byte array> index

    let private readAll (reader: DbDataReader) row =
        let rec next rows =
            if not (reader.Read()) then Ok(List.rev rows)
            else
                match row reader with
                | Ok value -> next (value :: rows)
                | Error error -> Error error
        next []

    // The columns of BanColumns, in their order.
    let private readBan (reader: DbDataReader) =
        let issuer =
            match optional reader 4 with
            | ValueSome admin -> AdminId.create admin |> Result.map ValueSome
            | ValueNone -> Ok ValueNone
        match AddressRange.ofStored (bytes reader 1) (int (reader.GetInt64 2)), SanctionReason.create (reader.GetString 3), issuer with
        | Some range, Ok reason, Ok issuedBy when reader.GetInt64 0 > 0L ->
            Ok { Id = reader.GetInt64 0; Range = range; Reason = reason; IssuedBy = issuedBy; IssuedAt = time reader 5
                 Expires = optional reader 6 |> ValueOption.map DateTimeOffset.FromUnixTimeMilliseconds }
        | _ -> invalidData ()

    // address, first_seen, last_seen, sign_ins from index on.
    let private readAddress (reader: DbDataReader) index =
        match ClientAddress.ofBytes (bytes reader index) with
        | Some address -> Ok { Address = address; FirstSeen = time reader (index + 1); LastSeen = time reader (index + 2); SignIns = reader.GetInt64(index + 3) }
        | None -> invalidData ()

    let private player (id: PlayerId) = box (int64 (PlayerId.value id))

    /// One more sign-in of the player from address at now. Rows not seen for
    /// keepDays go in the same transaction, so the history stays bounded.
    let record config (playerId: PlayerId) (address: IPAddress) (now: DateTimeOffset) (keepDays: int) token =
        SqliteAccountStore.withContext config token (fun context ->
            transaction context (fun () ->
                let at = box (milliseconds now)
                execute context "INSERT INTO sign_in_addresses(player_id, address, first_seen, last_seen, sign_ins) VALUES (@player, @address, @now, @now, 1) ON CONFLICT(player_id, address) DO UPDATE SET last_seen=excluded.last_seen, sign_ins=sign_ins + 1"
                    [ "@player", player playerId; "@address", box (ClientAddress.bytes address); "@now", at ] |> ignore
                execute context "DELETE FROM sign_in_addresses WHERE last_seen < @cutoff"
                    [ "@cutoff", box (milliseconds (now - TimeSpan.FromDays(float keepDays))) ] |> ignore
                Ok()))

    /// The addresses the player signed in from, latest first.
    let history config (playerId: PlayerId) token =
        SqliteAccountStore.withContext config token (fun context ->
            use statement = command context "SELECT address, first_seen, last_seen, sign_ins FROM sign_in_addresses WHERE player_id=@player ORDER BY last_seen DESC LIMIT @limit"
                                [ "@player", player playerId; "@limit", box MaxListed ]
            use reader = statement.ExecuteReader()
            readAll reader (fun reader -> readAddress reader 0))

    /// Players who signed in from the range, latest first: what a ban of it would also hit.
    let playersIn config (range: AddressRange) token =
        SqliteAccountStore.withContext config token (fun context ->
            let first, last = AddressRange.bounds range
            use statement =
                command context
                    "SELECT s.player_id, a.username, p.display_name, s.address, s.first_seen, s.last_seen, s.sign_ins FROM sign_in_addresses s JOIN profiles p ON p.player_id=s.player_id JOIN accounts a ON a.id=p.account_id WHERE s.address BETWEEN @first AND @last ORDER BY s.last_seen DESC LIMIT @limit"
                    [ "@first", box first; "@last", box last; "@limit", box MaxListed ]
            use reader = statement.ExecuteReader()
            readAll reader (fun reader ->
                match PlayerId.create (uint64 (reader.GetInt64 0)), Username.create Int32.MaxValue (reader.GetString 1),
                      DisplayName.create Int32.MaxValue (reader.GetString 2), readAddress reader 3 with
                | Ok id, Ok username, Ok name, Ok address -> Ok { Player = PlayerData.create id username name; Address = address }
                | _ -> invalidData ()))

    /// The bans in force at now, newest first.
    let active config (now: DateTimeOffset) token =
        SqliteAccountStore.withContext config token (fun context ->
            use statement = command context $"SELECT {BanColumns} FROM address_bans b WHERE {InForce} ORDER BY b.issued_at DESC"
                                [ "@now", box (milliseconds now) ]
            use reader = statement.ExecuteReader()
            readAll reader readBan)

    let private term (ban: AddressBan) =
        match ban.Expires with
        | ValueSome expires -> expires.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'")
        | ValueNone -> "lifted"

    /// Bans range at now for term; the audit line goes in the same transaction.
    let ban config (range: AddressRange) (reason: SanctionReason) (term': SanctionTerm) (admin: AdminId) (now: DateTimeOffset) token =
        // Stored in milliseconds: the ban returned is the one read back later.
        let now = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds now)
        SqliteAccountStore.withContext config token (fun context ->
            transaction context (fun () ->
                let expires = Sanction.expiry now term'
                let id =
                    scalar context
                        "INSERT INTO address_bans(network, prefix, reason, issued_by, issued_at, expires_at) VALUES (@network, @prefix, @reason, @admin, @now, @expires) RETURNING id"
                        [ "@network", box (AddressRange.network range); "@prefix", box (AddressRange.prefix range)
                          "@reason", box (SanctionReason.value reason); "@admin", box (AdminId.value admin); "@now", box (milliseconds now)
                          "@expires", (match expires with ValueSome value -> box (milliseconds value) | ValueNone -> box DBNull.Value) ]
                let ban = { Id = id :?> int64; Range = range; Reason = reason; IssuedBy = ValueSome admin; IssuedAt = now; Expires = expires }
                let record = AuditRecord.create AdminAction.BannedAddresses (AuditTarget.Range(AddressRange.key range)) $"until {term ban}: {SanctionReason.value reason}"
                SqliteAdminStore.audit context (AuditActor.Admin admin) record now
                Ok ban))

    /// Lifts the ban with this id if it is in force at now; None otherwise.
    let lift config (banId: int64) (admin: AdminId) (now: DateTimeOffset) token =
        SqliteAccountStore.withContext config token (fun context ->
            transaction context (fun () ->
                use statement = command context $"SELECT {BanColumns} FROM address_bans b WHERE b.id=@id AND {InForce}"
                                    [ "@id", box banId; "@now", box (milliseconds now) ]
                use reader = statement.ExecuteReader()
                match readAll reader readBan with
                | Error error -> Error error
                | Ok [] -> Ok None
                | Ok (ban :: _) ->
                    reader.Close()
                    execute context "UPDATE address_bans SET lifted_at=@now WHERE id=@id" [ "@id", box banId; "@now", box (milliseconds now) ] |> ignore
                    let record = AuditRecord.create AdminAction.LiftedAddressBan (AuditTarget.Range(AddressRange.key ban.Range)) ""
                    SqliteAdminStore.audit context (AuditActor.Admin admin) record now
                    Ok(Some ban)))
