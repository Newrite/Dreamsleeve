namespace Dreamsleeve.Server.Infrastructure

open System
open System.Data.Common
open System.IO
open Microsoft.Extensions.Logging
open Dreamsleeve.Agent
open Dreamsleeve.Server.Core
open Dreamsleeve.Server.Domain
open Dreamsleeve.Server.Infrastructure.SqliteStatements

/// Guilds, their members, custom roles and invitations (db/migrations/*_guilds.sql,
/// *_guild_titles.sql). The guild owner keeps them in memory; this store loads
/// them once and its one writer applies every change in order. Times are Unix
/// milliseconds.
[<RequireQualifiedAccess>]
module SqliteGuildStore =
    type Loaded = {
        Guilds: StoredGuild list
        /// The stored profiles of every member and invited player, before moderation.
        Profiles: PlayerData list
        /// One above the highest ID storage ever issued (sqlite_sequence).
        NextId: uint64
        /// The same for custom roles.
        NextTitleId: uint64
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

    let private MemberProjection =
        [| SqliteStored.Column.PositiveInteger
           SqliteStored.Column.PositiveInteger
           SqliteStored.Column.Int32
           SqliteStored.Column.UnixMilliseconds
           (SqliteStored.Column.Nullable SqliteStored.Column.Text)
           (SqliteStored.Column.Nullable SqliteStored.Column.PositiveInteger)
           (SqliteStored.Column.Nullable SqliteStored.Column.UnixMilliseconds)
           (SqliteStored.Column.Nullable SqliteStored.Column.UnixMilliseconds)
           (SqliteStored.Column.Nullable SqliteStored.Column.PositiveInteger) |]

    let private InviteProjection =
        [| SqliteStored.Column.PositiveInteger
           SqliteStored.Column.PositiveInteger
           SqliteStored.Column.PositiveInteger
           SqliteStored.Column.UnixMilliseconds
           SqliteStored.Column.UnixMilliseconds |]

    let private GuildProjection =
        [|
            SqliteStored.Column.PositiveInteger
            SqliteStored.Column.Text
            SqliteStored.Column.UnixMilliseconds
            (SqliteStored.Column.Nullable SqliteStored.Column.Text)
            SqliteStored.Column.Integer
        |]

    let private JoinRequestProjection =
        [|
            SqliteStored.Column.PositiveInteger
            SqliteStored.Column.PositiveInteger
            SqliteStored.Column.UnixMilliseconds
            SqliteStored.Column.UnixMilliseconds
        |]

    let private TitleProjection =
        [|
            SqliteStored.Column.PositiveInteger
            SqliteStored.Column.PositiveInteger
            SqliteStored.Column.Text
        |]

    let private ProfileProjection =
        [|
            SqliteStored.Column.PositiveInteger
            SqliteStored.Column.Text
            SqliteStored.Column.Text
            SqliteStored.Column.Integer
        |]

    let private memberOf (reader: DbDataReader) =
        SqliteAccountStore.storedRow reader 0 MemberProjection (fun () ->
            let guild = GuildId.create (uint64 (reader.GetInt64 0))
            let player = PlayerId.create (uint64 (reader.GetInt64 1))
            let role = GuildRole.ofInt (reader.GetInt32 2)
            let title =
                match optional reader 8 reader.GetInt64 with
                | ValueSome raw -> GuildTitleId.create (uint64 raw) |> Result.map ValueSome |> Result.mapError ignore
                | ValueNone -> Ok ValueNone
            let mute =
                match optional reader 4 reader.GetString, optional reader 5 reader.GetInt64, optional reader 6 reader.GetInt64 with
                | ValueSome reason, ValueSome by, ValueSome at ->
                    match SanctionReason.create reason, PlayerId.create (uint64 by) with
                    | Ok reason, Ok by ->
                        Ok(
                            ValueSome {
                                Reason = reason
                                IssuedBy = by
                                IssuedAt = time at
                                Expires = optional reader 7 reader.GetInt64 |> ValueOption.map time
                            })
                    | _ -> Error()
                | ValueNone, ValueNone, ValueNone -> Ok ValueNone
                | _ -> Error()

            match guild, player, role, mute, title with
            | Ok guild, Ok player, ValueSome role, Ok mute, Ok title ->
                Ok(guild, {
                    Player = player
                    Role = role
                    JoinedAt = time (reader.GetInt64 3)
                    Mute = mute
                    Title = title
                })
            | _ -> invalidData "A stored guild member is invalid.")

    let private inviteOf (reader: DbDataReader) =
        SqliteAccountStore.storedRow reader 0 InviteProjection (fun () ->
            match GuildId.create (uint64 (reader.GetInt64 0)), PlayerId.create (uint64 (reader.GetInt64 1)), PlayerId.create (uint64 (reader.GetInt64 2)) with
            | Ok guild, Ok player, Ok by ->
                Ok {
                    Guild = guild
                    Player = player
                    InvitedBy = by
                    CreatedAt = time (reader.GetInt64 3)
                    Expires = time (reader.GetInt64 4)
                }
            | _ -> invalidData "A stored guild invitation is invalid.")

    let private requestOf (reader: DbDataReader) =
        SqliteAccountStore.storedRow reader 0 JoinRequestProjection (fun () ->
            match GuildId.create (uint64 (reader.GetInt64 0)), PlayerId.create (uint64 (reader.GetInt64 1)) with
            | Ok guild, Ok player ->
                Ok {
                    Guild = guild
                    Player = player
                    CreatedAt = time (reader.GetInt64 2)
                    Expires = time (reader.GetInt64 3)
                }
            | _ -> invalidData "A stored guild join request is invalid.")

    let private titleOf (reader: DbDataReader) =
        SqliteAccountStore.storedRow reader 0 TitleProjection (fun () ->
            match GuildTitleId.create (uint64 (reader.GetInt64 0)), GuildId.create (uint64 (reader.GetInt64 1)),
                  GuildTitleName.create 1 GuildOptions.MaxNameLength (reader.GetString 2) with
            | Ok id, Ok guild, Ok name ->
                Ok(guild, {
                    Id = id
                    Name = name
                })
            | _ -> invalidData "A stored guild custom role is invalid.")

    // One above the highest ID the table's AUTOINCREMENT issued; sqlite_sequence
    // is not a schema table, and the sequence outlives deleted rows.
    let private nextId context (table: string) =
        match scalar context $"SELECT MAX(COALESCE((SELECT seq FROM sqlite_sequence WHERE name = '{table}'), 0), COALESCE((SELECT MAX(id) FROM {table}), 0))" [] with
        | :? int64 as highest when highest >= 0L -> Ok(uint64 highest + 1UL)
        | _ -> Error()

    // A member holds a custom role of their own guild only.
    let private titlesHeld (members: (GuildId * GuildMember) list) (titles: (GuildId * GuildTitle) list) =
        let owners = titles |> List.map (fun (guild, title) -> title.Id, guild) |> Map.ofList
        members |> List.forall (fun (guild, membership) ->
            match membership.Title with
            | ValueSome title -> Map.tryFind title owners = Some guild
            | ValueNone -> true)

    /// Every guild with its members, custom roles and invitations, the profiles
    /// they name and the next IDs. Stored names stay as they are, whatever the
    /// limits are now.
    let loadAll config token =
        SqliteAccountStore.withContext config token (fun context ->
            let rows sql row =
                use statement = command context sql []
                use reader = statement.ExecuteReader()
                readAll reader row
            let guilds =
                rows "SELECT id, name, created_at, info, listed FROM guilds ORDER BY id" (fun reader ->
                    SqliteAccountStore.storedRow reader 0 GuildProjection (fun () ->
                        let info =
                            match optional reader 3 reader.GetString with
                            | ValueSome text -> GuildInfo.create GuildOptions.MaxInfoLength text |> Result.map ValueSome
                            | ValueNone -> Ok ValueNone

                        match GuildId.create (uint64 (reader.GetInt64 0)), GuildName.create 1 GuildOptions.MaxNameLength (reader.GetString 1), info with
                        | Ok id, Ok name, Ok info -> Ok(id, name, time (reader.GetInt64 2), info, reader.GetInt64 4 <> 0L)
                        | _ -> invalidData "A stored guild is invalid."))
            let members =
                rows "SELECT guild_id, player_id, role, joined_at, mute_reason, muted_by, muted_at, muted_until, title_id FROM guild_members" memberOf
            let invites = rows "SELECT guild_id, player_id, invited_by, created_at, expires_at FROM guild_invites" inviteOf
            let titles = rows "SELECT id, guild_id, name FROM guild_titles" titleOf
            let requests = rows "SELECT guild_id, player_id, created_at, expires_at FROM guild_join_requests" requestOf
            let profiles =
                rows "SELECT p.player_id, a.username, p.display_name, p.name_color FROM profiles p JOIN accounts a ON a.id=p.account_id WHERE p.player_id IN (SELECT player_id FROM guild_members UNION SELECT player_id FROM guild_invites UNION SELECT player_id FROM guild_join_requests)"
                    (fun reader ->
                        SqliteAccountStore.storedRow reader 0 ProfileProjection (fun () ->
                            match PlayerId.create (uint64 (reader.GetInt64 0)), Username.create Int32.MaxValue (reader.GetString 1),
                                  DisplayName.create Int32.MaxValue (reader.GetString 2), nameColor (reader.GetInt64 3) with
                            | Ok id, Ok username, Ok name, Ok color -> Ok(PlayerData.create id username name color)
                            | _ -> invalidData "A stored profile is invalid."))

            match guilds, members, invites, profiles, titles, requests with
            | Ok guilds, Ok members, Ok invites, Ok profiles, Ok titles, Ok requests ->
                let membersOf = members |> List.groupBy fst |> Map.ofList
                let invitesOf = invites |> List.groupBy _.Guild |> Map.ofList
                let titlesOf = titles |> List.groupBy fst |> Map.ofList
                let requestsOf = requests |> List.groupBy _.Guild |> Map.ofList
                let stored =
                    guilds |> List.map (fun (id, name, createdAt, info, listed) -> {
                        Id = id
                        Name = name
                        CreatedAt = createdAt
                        Info = info
                        Listed = listed
                        JoinRequests = requestsOf |> Map.tryFind id |> Option.defaultValue []
                        Members = membersOf |> Map.tryFind id |> Option.defaultValue [] |> List.map snd
                        Invites = invitesOf |> Map.tryFind id |> Option.defaultValue []
                        Titles = titlesOf |> Map.tryFind id |> Option.defaultValue [] |> List.map snd
                    })

                match nextId context "guilds", nextId context "guild_titles" with
                | Ok next, _ when next >= GuildId.MaxValue -> invalidData "The guild ID sequence is exhausted."
                | Ok _, Ok nextTitle when nextTitle > uint64 Int64.MaxValue -> invalidData "The guild custom role ID sequence is exhausted."
                | Ok _, Ok _ when not (titlesHeld members titles) -> invalidData "A guild member holds a custom role of another guild."
                | Ok next, Ok nextTitle ->
                    Ok {
                        Guilds = stored
                        Profiles = profiles
                        NextId = next
                        NextTitleId = nextTitle
                    }
                | Error(), _ | _, Error() -> invalidData "A guild ID sequence is invalid."
            | Error error, _, _, _, _, _ | _, Error error, _, _, _, _ | _, _, Error error, _, _, _
            | _, _, _, Error error, _, _ | _, _, _, _, Error error, _ | _, _, _, _, _, Error error -> Error error)

    let private guild (id: GuildId) = box (int64 (GuildId.value id))
    let private player (id: PlayerId) = box (int64 (PlayerId.value id))
    let private nullable (value: 'T voption) =
        match value with
        | ValueSome value -> box value
        | ValueNone -> box DBNull.Value

    let private title (id: GuildTitleId) = box (int64 (GuildTitleId.value id))

    let private putMember context id (membership: GuildMember) =
        let mute = membership.Mute
        execute context
            "INSERT INTO guild_members(guild_id, player_id, role, joined_at, mute_reason, muted_by, muted_at, muted_until, title_id) VALUES (@guild, @player, @role, @joined, @reason, @by, @at, @until, @title) ON CONFLICT(guild_id, player_id) DO UPDATE SET role=excluded.role, mute_reason=excluded.mute_reason, muted_by=excluded.muted_by, muted_at=excluded.muted_at, muted_until=excluded.muted_until, title_id=excluded.title_id"
            [ "@guild", guild id
              "@player", player membership.Player
              "@role", box (GuildRole.toInt membership.Role)
              "@joined", box (milliseconds membership.JoinedAt)
              "@reason", nullable (mute |> ValueOption.map (fun mute -> SanctionReason.value mute.Reason))
              "@by", nullable (mute |> ValueOption.map (fun mute -> int64 (PlayerId.value mute.IssuedBy)))
              "@at", nullable (mute |> ValueOption.map (fun mute -> milliseconds mute.IssuedAt))
              "@until", nullable (mute |> ValueOption.bind (fun mute -> mute.Expires |> ValueOption.map milliseconds))
              "@title", nullable (membership.Title |> ValueOption.map (fun id -> int64 (GuildTitleId.value id))) ]
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
        | GuildWrite.PutTitle(id, created) ->
            execute context
                "INSERT INTO guild_titles(id, guild_id, name, name_key) VALUES (@title, @guild, @name, @key) ON CONFLICT(id) DO UPDATE SET name=excluded.name, name_key=excluded.name_key"
                [ "@title", title created.Id
                  "@guild", guild id
                  "@name", box (GuildTitleName.value created.Name)
                  "@key", box (GuildTitleName.key created.Name) ]
            |> ignore
            Ok()
        | GuildWrite.PutListed(id, listed) ->
            execute context "UPDATE guilds SET listed=@listed WHERE id=@guild" [ "@guild", guild id; "@listed", box (if listed then 1L else 0L) ]
            |> ignore
            Ok()
        | GuildWrite.PutJoinRequest request ->
            execute context
                "INSERT INTO guild_join_requests(guild_id, player_id, created_at, expires_at) VALUES (@guild, @player, @at, @expires) ON CONFLICT(guild_id, player_id) DO UPDATE SET created_at=excluded.created_at, expires_at=excluded.expires_at"
                [ "@guild", guild request.Guild
                  "@player", player request.Player
                  "@at", box (milliseconds request.CreatedAt)
                  "@expires", box (milliseconds request.Expires) ]
            |> ignore
            Ok()
        | GuildWrite.RemoveJoinRequest(id, playerId) ->
            execute context "DELETE FROM guild_join_requests WHERE guild_id=@guild AND player_id=@player" [ "@guild", guild id; "@player", player playerId ]
            |> ignore
            Ok()
        | GuildWrite.PutInfo(id, info) ->
            execute context "UPDATE guilds SET info=@info WHERE id=@guild"
                [ "@guild", guild id; "@info", nullable (info |> ValueOption.map GuildInfo.value) ]
            |> ignore
            Ok()
        // The holders lose the role with it: title_id has no foreign key.
        | GuildWrite.RemoveTitle id ->
            transaction context (fun () ->
                execute context "UPDATE guild_members SET title_id=NULL WHERE title_id=@title" [ "@title", title id ] |> ignore
                execute context "DELETE FROM guild_titles WHERE id=@title" [ "@title", title id ] |> ignore
                Ok())

        | GuildWrite.Join(id, membership) ->
            transaction context (fun () ->
                let row = [ "@guild", guild id; "@player", player membership.Player ]
                execute context "DELETE FROM guild_invites WHERE guild_id=@guild AND player_id=@player" row |> ignore
                execute context "DELETE FROM guild_join_requests WHERE guild_id=@guild AND player_id=@player" row |> ignore
                putMember context id membership
                Ok())
        | GuildWrite.TransferMaster(id, previous, master) ->
            transaction context (fun () ->
                previous |> ValueOption.iter (putMember context id)
                putMember context id master
                Ok())

    /// One change, in its own unit of work.
    let write config (change: GuildWrite) token = SqliteAccountStore.withContext config token (fun context -> apply context change)

    let private handle config (logger: ILogger) (context: ReliableAgentContext<GuildWrite>) (change: GuildWrite) = task {
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
    let tryPrepareWriter config (logger: ILogger) capacity =
        let options = {
            AgentOptions.create "guild-writer" with
                Mailbox = AgentMailbox.boundedWait capacity
        }
        Agent.TryPrepareReliable(options, handle config logger)

    let startWriter config logger capacity =
        tryPrepareWriter config logger capacity |> Result.map (fun plan -> plan.Start())
