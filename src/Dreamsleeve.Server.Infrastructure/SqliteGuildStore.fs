namespace Dreamsleeve.Server.Infrastructure

open System
open System.Data.Common
open System.IO
open Microsoft.Extensions.Logging
open Dreamsleeve.Agent
open Dreamsleeve.Server.Core
open Dreamsleeve.Server.Domain
open Dreamsleeve.Server.Infrastructure.SqliteStatements

/// Guilds, their members and invitations (db/migrations/*_guilds.sql). The
/// guild owner keeps them in memory; this store loads them once and its one
/// writer applies every change in order. Times are Unix milliseconds.
[<RequireQualifiedAccess>]
module SqliteGuildStore =
    type Loaded = {
        Guilds: StoredGuild list
        /// The stored profiles of every member and invited player, before moderation.
        Profiles: PlayerData list
        /// One above the highest ID storage ever issued (sqlite_sequence).
        NextId: uint64
    }

    let private milliseconds (time: DateTimeOffset) = time.ToUnixTimeMilliseconds()
    let private time (value: int64) = DateTimeOffset.FromUnixTimeMilliseconds value
    let private invalidData message = Error(AccountStoreError.Failed(InvalidDataException(message: string)))

    let private optional (reader: DbDataReader) index (read: int -> 'T) =
        if reader.IsDBNull index then ValueNone else ValueSome(read index)

    let private readAll (reader: DbDataReader) row =
        let rec next rows =
            if not (reader.Read()) then Ok(List.rev rows)
            else
                match row reader with
                | Ok value -> next (value :: rows)
                | Error error -> Error error
        next []

    let private memberOf (reader: DbDataReader) =
        let guild = GuildId.create (uint64 (reader.GetInt64 0))
        let player = PlayerId.create (uint64 (reader.GetInt64 1))
        let role = GuildRole.ofInt (reader.GetInt32 2)
        let mute =
            match optional reader 4 reader.GetString, optional reader 5 reader.GetInt64, optional reader 6 reader.GetInt64 with
            | ValueSome reason, ValueSome by, ValueSome at ->
                match SanctionReason.create reason, PlayerId.create (uint64 by) with
                | Ok reason, Ok by ->
                    Ok(ValueSome { Reason = reason; IssuedBy = by; IssuedAt = time at; Expires = optional reader 7 reader.GetInt64 |> ValueOption.map time })
                | _ -> Error()
            | ValueNone, ValueNone, ValueNone -> Ok ValueNone
            | _ -> Error()
        match guild, player, role, mute with
        | Ok guild, Ok player, ValueSome role, Ok mute -> Ok(guild, { Player = player; Role = role; JoinedAt = time (reader.GetInt64 3); Mute = mute })
        | _ -> invalidData "A stored guild member is invalid."

    let private inviteOf (reader: DbDataReader) =
        match GuildId.create (uint64 (reader.GetInt64 0)), PlayerId.create (uint64 (reader.GetInt64 1)), PlayerId.create (uint64 (reader.GetInt64 2)) with
        | Ok guild, Ok player, Ok by ->
            Ok { Guild = guild; Player = player; InvitedBy = by; CreatedAt = time (reader.GetInt64 3); Expires = time (reader.GetInt64 4) }
        | _ -> invalidData "A stored guild invitation is invalid."

    /// Every guild with its members and invitations, the profiles they name and
    /// the next ID. Stored names stay as they are, whatever the limits are now.
    let loadAll config token =
        SqliteAccountStore.withContext config token (fun context ->
            let rows sql row =
                use statement = command context sql []
                use reader = statement.ExecuteReader()
                readAll reader row
            let guilds =
                rows "SELECT id, name, created_at FROM guilds ORDER BY id" (fun reader ->
                    match GuildId.create (uint64 (reader.GetInt64 0)), GuildName.create 1 GuildOptions.MaxNameLength (reader.GetString 1) with
                    | Ok id, Ok name -> Ok(id, name, time (reader.GetInt64 2))
                    | _ -> invalidData "A stored guild is invalid.")
            let members =
                rows "SELECT guild_id, player_id, role, joined_at, mute_reason, muted_by, muted_at, muted_until FROM guild_members" memberOf
            let invites = rows "SELECT guild_id, player_id, invited_by, created_at, expires_at FROM guild_invites" inviteOf
            let profiles =
                rows "SELECT p.player_id, a.username, p.display_name, p.name_color FROM profiles p JOIN accounts a ON a.id=p.account_id WHERE p.player_id IN (SELECT player_id FROM guild_members UNION SELECT player_id FROM guild_invites)"
                    (fun reader ->
                        match PlayerId.create (uint64 (reader.GetInt64 0)), Username.create Int32.MaxValue (reader.GetString 1),
                              DisplayName.create Int32.MaxValue (reader.GetString 2), nameColor (reader.GetInt64 3) with
                        | Ok id, Ok username, Ok name, Ok color -> Ok(PlayerData.create id username name color)
                        | _ -> invalidData "A stored profile is invalid.")
            match guilds, members, invites, profiles with
            | Ok guilds, Ok members, Ok invites, Ok profiles ->
                let membersOf = members |> List.groupBy fst |> Map.ofList
                let invitesOf = invites |> List.groupBy _.Guild |> Map.ofList
                let stored =
                    guilds |> List.map (fun (id, name, createdAt) ->
                        { Id = id
                          Name = name
                          CreatedAt = createdAt
                          Members = membersOf |> Map.tryFind id |> Option.defaultValue [] |> List.map snd
                          Invites = invitesOf |> Map.tryFind id |> Option.defaultValue [] })
                // sqlite_sequence is not a schema table; the sequence outlives deleted rows.
                let highest =
                    scalar context "SELECT MAX(COALESCE((SELECT seq FROM sqlite_sequence WHERE name = 'guilds'), 0), COALESCE((SELECT MAX(id) FROM guilds), 0))" []
                    :?> int64
                if highest < 0L || uint64 highest >= GuildId.MaxValue - 1UL then invalidData "The guild ID sequence is exhausted."
                else Ok { Guilds = stored; Profiles = profiles; NextId = uint64 highest + 1UL }
            | Error error, _, _, _ | _, Error error, _, _ | _, _, Error error, _ | _, _, _, Error error -> Error error)

    let private guild (id: GuildId) = box (int64 (GuildId.value id))
    let private player (id: PlayerId) = box (int64 (PlayerId.value id))
    let private nullable (value: 'T voption) = match value with ValueSome value -> box value | ValueNone -> box DBNull.Value

    let private putMember context id (membership: GuildMember) =
        let mute = membership.Mute
        execute context
            "INSERT INTO guild_members(guild_id, player_id, role, joined_at, mute_reason, muted_by, muted_at, muted_until) VALUES (@guild, @player, @role, @joined, @reason, @by, @at, @until) ON CONFLICT(guild_id, player_id) DO UPDATE SET role=excluded.role, mute_reason=excluded.mute_reason, muted_by=excluded.muted_by, muted_at=excluded.muted_at, muted_until=excluded.muted_until"
            [ "@guild", guild id
              "@player", player membership.Player
              "@role", box (GuildRole.toInt membership.Role)
              "@joined", box (milliseconds membership.JoinedAt)
              "@reason", nullable (mute |> ValueOption.map (fun mute -> SanctionReason.value mute.Reason))
              "@by", nullable (mute |> ValueOption.map (fun mute -> int64 (PlayerId.value mute.IssuedBy)))
              "@at", nullable (mute |> ValueOption.map (fun mute -> milliseconds mute.IssuedAt))
              "@until", nullable (mute |> ValueOption.bind (fun mute -> mute.Expires |> ValueOption.map milliseconds)) ]
        |> ignore

    let private apply context (write: GuildWrite) =
        match write with
        | GuildWrite.Create(id, name, createdAt, master) ->
            transaction context (fun () ->
                execute context "INSERT INTO guilds(id, name, name_key, created_at) VALUES (@guild, @name, @key, @at)"
                    [ "@guild", guild id; "@name", box (GuildName.value name); "@key", box (GuildName.key name); "@at", box (milliseconds createdAt) ]
                |> ignore
                putMember context id master
                Ok())
        | GuildWrite.Delete id ->
            execute context "DELETE FROM guilds WHERE id=@guild" [ "@guild", guild id ] |> ignore
            Ok()
        | GuildWrite.PutMember(id, membership) ->
            putMember context id membership
            Ok()
        | GuildWrite.RemoveMember(id, playerId) ->
            execute context "DELETE FROM guild_members WHERE guild_id=@guild AND player_id=@player" [ "@guild", guild id; "@player", player playerId ] |> ignore
            Ok()
        | GuildWrite.PutInvite invite ->
            execute context
                "INSERT INTO guild_invites(guild_id, player_id, invited_by, created_at, expires_at) VALUES (@guild, @player, @by, @at, @expires) ON CONFLICT(guild_id, player_id) DO UPDATE SET invited_by=excluded.invited_by, created_at=excluded.created_at, expires_at=excluded.expires_at"
                [ "@guild", guild invite.Guild
                  "@player", player invite.Player
                  "@by", player invite.InvitedBy
                  "@at", box (milliseconds invite.CreatedAt)
                  "@expires", box (milliseconds invite.Expires) ]
            |> ignore
            Ok()
        | GuildWrite.RemoveInvite(id, playerId) ->
            execute context "DELETE FROM guild_invites WHERE guild_id=@guild AND player_id=@player" [ "@guild", guild id; "@player", player playerId ] |> ignore
            Ok()

    /// One change, in its own unit of work.
    let write config (change: GuildWrite) token = SqliteAccountStore.withContext config token (fun context -> apply context change)

    let private handle config (logger: ILogger) (context: AgentContext<GuildWrite>) (change: GuildWrite) = task {
        match write config change context.CancellationToken with
        | Ok() -> ()
        | Error error ->
            match error with
            | AccountStoreError.Failed exn -> logger.LogError(exn, "Guild storage write failed: {Write}", change)
            | AccountStoreError.UsernameTaken | AccountStoreError.InvalidCredential | AccountStoreError.Canceled ->
                logger.LogError("Guild storage write failed: {Error} ({Write})", error, change)
            // Memory went ahead of storage: the writer stops, the runtime fails
            // and the supervisor restarts the game from what storage holds.
            context.Abort()
    }

    /// One sequential writer keeps the order of a guild's changes. capacity is
    /// Guilds.MaxPendingWrites, checked with the configuration.
    let startWriter config (logger: ILogger) capacity =
        let options = { AgentOptions.create "guild-writer" with Mailbox = AgentMailbox.boundedWait capacity }
        Agent.Start(options, handle config logger)
