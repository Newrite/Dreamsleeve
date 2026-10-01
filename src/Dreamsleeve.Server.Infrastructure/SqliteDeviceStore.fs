namespace Dreamsleeve.Server.Infrastructure

open System
open System.Data.Common
open System.IO
open Dreamsleeve.Server.Domain
open Dreamsleeve.Server.Infrastructure.SqliteStatements

/// The devices players signed in from (db/migrations/*_devices.sql), run by
/// the account service's workers. Their bans are sanctions: see
/// SqliteSanctionStore.issue and bannedDevice. Times are Unix milliseconds.
[<RequireQualifiedAccess>]
module SqliteDeviceStore =
    /// The most devices one panel list shows.
    [<Literal>]
    let MaxListed = 100

    let private milliseconds (time: DateTimeOffset) = time.ToUnixTimeMilliseconds()

    let private player (id: PlayerId) = box (int64 (PlayerId.value id))

    /// One more sign-in of the player from device at now. Rows not seen for
    /// keepDays go in the same transaction.
    let record config (playerId: PlayerId) (device: DeviceId) (now: DateTimeOffset) (keepDays: int) token =
        SqliteAccountStore.withContext config token (fun context ->
            transaction context (fun () ->
                execute context "INSERT INTO player_devices(player_id, device, first_seen, last_seen, sign_ins) VALUES (@player, @device, @now, @now, 1) ON CONFLICT(player_id, device) DO UPDATE SET last_seen=excluded.last_seen, sign_ins=sign_ins + 1"
                    [ "@player", player playerId; "@device", box (DeviceId.value device); "@now", box (milliseconds now) ] |> ignore
                execute context "DELETE FROM player_devices WHERE last_seen < @cutoff"
                    [ "@cutoff", box (milliseconds (now - TimeSpan.FromDays(float keepDays))) ] |> ignore
                Ok()))

    /// The devices the player signed in from, latest first.
    let history config (playerId: PlayerId) token =
        SqliteAccountStore.withContext config token (fun context ->
            use statement = command context "SELECT device, first_seen, last_seen, sign_ins FROM player_devices WHERE player_id=@player ORDER BY last_seen DESC LIMIT @limit"
                                [ "@player", player playerId; "@limit", box MaxListed ]
            use reader = statement.ExecuteReader()
            let rec next rows =
                if not (reader.Read()) then Ok(List.rev rows)
                else
                    match DeviceId.create (reader.GetString 0) with
                    | Ok device ->
                        let entry = { Device = device; FirstSeen = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64 1)
                                      LastSeen = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64 2); SignIns = reader.GetInt64 3 }
                        next (entry :: rows)
                    | Error _ -> Error(AccountStoreError.Failed(InvalidDataException "A stored device is invalid."))
            next [])
