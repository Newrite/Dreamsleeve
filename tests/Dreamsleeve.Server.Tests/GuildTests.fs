module Dreamsleeve.Server.Tests.GuildTests

open System
open System.Threading
open System.Threading.Channels
open Expecto
open Google.Protobuf
open Microsoft.Extensions.Logging.Abstractions
open Dreamsleeve.Agent
open Dreamsleeve.Server.Core
open Dreamsleeve.Server.Domain
open Dreamsleeve.Server.Infrastructure
open AgentTests
open BackgroundTests

let private ok = function Ok value -> value | Error error -> failtestf "Expected success, got %A" error
let private pid raw = PlayerId.create raw |> ok
let private gid raw = GuildId.create raw |> ok
let private profile number =
    PlayerData.create (pid number) (Username.create 32 $"player{number}" |> ok) (DisplayName.create 64 $"Player {number}" |> ok) NameColor.unknown
let private text value = ChatMessageText.create 2000 value |> ok
let private reason = SanctionReason.create "Флуд" |> ok
let private token = CancellationToken.None

let private collect (output: Channel<'T>) (_: AgentContext<'T>) value = task {
    check (output.Writer.TryWrite value) "Test output closed."
}
let private receive (output: Channel<'T>) = output.Reader.ReadAsync().AsTask().WaitAsync guard
let private post (agent: Agent<'T>) value = task {
    let! result = agent.PostAsync value
    equal AgentPostResult.Posted result
}
let private stop (agent: Agent<'T>) = task {
    agent.Complete() |> ignore
    do! awaitUnit agent.Completion
}

let private rules = Moderation.create { Words = [ "badword" ]; Substrings = []; Exceptions = [] }
let private options = { GuildOptions.defaults with MaxMembers = 3; InviteCheckIntervalMs = 3600000; HistoryCapacity = 5 }
let private chatRate = { Burst = 100; RefillMs = 1000; DuplicateWindowMs = 0 }

type private Member = {
    Subscription: Subscription<GuildEvent>
    Events: Channel<GuildEvent>
    /// Replies to this member's chat requests.
    Chat: Channel<ChatRoomEvent>
    ChatReplies: ReliableAgentRef<ChatRoomEvent>
}

type private Fixture = {
    Guilds: Agent<GuildCommand>
    Writes: Channel<GuildWrite>
    Alice: Member
    Bob: Member
    Carol: Member
    Acknowledgments: Channel<Guid>
    Cleanup: ReliableAgentRef<Guid>
}

let private withGuildsUsing (persistence: Agent<GuildWrite> -> GuildPersistence) run = task {
    let writes, hostEvents, acknowledgments = Channel.CreateUnbounded<GuildWrite>(), Channel.CreateUnbounded<SessionHostCommand>(), Channel.CreateUnbounded<Guid>()
    use writer = Agent.Start(AgentOptions.create "guild-writer", collect writes)
    use host = Agent.Start(AgentOptions.create "host", collect hostEvents)
    use cleanup = Agent.Start(AgentOptions.create "cleanup", collect acknowledgments)
    let limits = GuildOptions.rules options |> ok
    use guilds = GuildsAgent.start options limits rules chatRate (persistence writer) (host.Ref.TryReliable().Value) NullLogger.Instance |> ok
    let mutable agents = []
    let participant number =
        let events, chat = Channel.CreateUnbounded<GuildEvent>(), Channel.CreateUnbounded<ChatRoomEvent>()
        let eventAgent = Agent.Start(AgentOptions.create $"member-{number}", collect events)
        let chatAgent = Agent.Start(AgentOptions.create $"chat-{number}", collect chat)
        agents <- (eventAgent :> IDisposable) :: (chatAgent :> IDisposable) :: agents
        { Subscription = { ConnectionId = Guid.NewGuid(); Profile = profile number; Events = eventAgent.Ref.TryReliable().Value }
          Events = events; Chat = chat; ChatReplies = chatAgent.Ref.TryReliable().Value }
    let fixture = {
        Guilds = guilds; Writes = writes; Alice = participant 1UL; Bob = participant 2UL; Carol = participant 3UL
        Acknowledgments = acknowledgments; Cleanup = cleanup.Ref.TryReliable().Value
    }
    try
        do! run fixture
        do! stop guilds
        do! stop writer
    finally
        for agent in agents do agent.Dispose()
}

let private empty (writer: Agent<GuildWrite>) =
    { Loaded = []; Profiles = []; NextId = 1UL; Writer = writer.Ref.TryReliable().Value; WriterStopped = writer.Completion }

let private withGuilds run = withGuildsUsing empty run

let private join fixture (who: Member) = task {
    do! post fixture.Guilds (GuildCommand.Join who.Subscription)
    let! snapshot = receive who.Events
    match snapshot with
    | GuildEvent.Snapshot state -> return state
    | other -> return failtestf "Expected the guild snapshot, got %A" other
}

let private act fixture (who: Member) requestId action =
    post fixture.Guilds (GuildCommand.Act { ConnectionId = who.Subscription.ConnectionId; RequestId = requestId; Action = action })

let private expectDone (who: Member) requestId = task {
    let! event = receive who.Events
    match event with
    | GuildEvent.Done(id, guild) when id = requestId -> return guild
    | other -> return failtestf "Expected Done %d, got %A" requestId other
}

let private expectRefused (who: Member) requestId code = task {
    let! event = receive who.Events
    match event with
    | GuildEvent.Refused(id, rejection) when id = requestId ->
        equal code rejection.Code
        return rejection
    | other -> return failtestf "Expected a refusal of %d, got %A" requestId other
}

let private expectChange (who: Member) = task {
    let! event = receive who.Events
    match event with
    | GuildEvent.Changed change -> return change
    | other -> return failtestf "Expected a guild change, got %A" other
}

/// Alice creates "Стражи"; Bob joins it. Returns the guild.
let private founded fixture = task {
    let! _ = join fixture fixture.Alice
    let! _ = join fixture fixture.Bob
    do! act fixture fixture.Alice 1UL (GuildAction.Create "Стражи")
    let! added = expectChange fixture.Alice
    let! guild = expectDone fixture.Alice 1UL
    match added with
    | GuildChange.Added view ->
        equal "Стражи" (GuildName.value view.Name)
        equal (ChatChannels.ofGuild guild) view.ChannelId
        equal [ pid 1UL, GuildRole.Master ] (view.Members |> List.map (fun item -> item.Profile.PlayerId, item.Membership.Role))
    | other -> failtestf "Expected the new guild, got %A" other
    do! act fixture fixture.Alice 2UL (GuildAction.Invite(guild, pid 2UL))
    let! invited = expectChange fixture.Bob
    match invited with
    | GuildChange.Invited view ->
        equal guild view.Invite.Guild
        equal (pid 1UL) view.Invite.InvitedBy
    | other -> failtestf "Expected an invitation, got %A" other
    let! _ = expectDone fixture.Alice 2UL
    do! act fixture fixture.Bob 3UL (GuildAction.Answer(guild, true))
    let! removed = expectChange fixture.Bob
    equal (GuildChange.InviteRemoved guild) removed
    let! joined = expectChange fixture.Bob
    match joined with
    | GuildChange.Added view -> equal 2 view.Members.Length
    | other -> failtestf "Expected the joined guild, got %A" other
    let! _ = expectDone fixture.Bob 3UL
    let! arrival = expectChange fixture.Alice
    match arrival with
    | GuildChange.MemberChanged(id, view) ->
        equal guild id
        equal (pid 2UL) view.Profile.PlayerId
        check view.Online "Bob is online"
    | other -> failtestf "Expected Bob's arrival, got %A" other
    return guild
}

let private submission (who: Member) requestId body : ChatSubmission =
    { ConnectionId = who.Subscription.ConnectionId; RequestId = requestId; Author = PublicIdentity.Profile who.Subscription.Profile
      Text = text body; CharacterName = ValueNone; Fingerprint = body; Flagged = []; Announcement = ValueNone; ReplyTo = who.ChatReplies }

let private agentTests = testSequenced <| testList "Guild owner" [
    case "creating, inviting and joining reach exactly who should hear, and every change is written" (fun () ->
        withGuilds (fun fixture -> task {
            let! guild = founded fixture
            let! create = receive fixture.Writes
            match create with
            | GuildWrite.Create(id, name, _, master) ->
                equal guild id
                equal "Стражи" (GuildName.value name)
                equal GuildRole.Master master.Role
            | other -> failtestf "Expected the creation, got %A" other
            let! invite = receive fixture.Writes
            check (match invite with GuildWrite.PutInvite value -> value.Player = pid 2UL | _ -> false) "the invitation is written"
            let! removed = receive fixture.Writes
            equal (GuildWrite.RemoveInvite(guild, pid 2UL)) removed
            let! joined = receive fixture.Writes
            check (match joined with GuildWrite.PutMember(id, membership) -> id = guild && membership.Player = pid 2UL | _ -> false) "Bob is written"
            // Carol's own snapshot shows nothing of a guild she is not in.
            let! carol = join fixture fixture.Carol
            check carol.Guilds.IsEmpty "a stranger sees no guilds"
        }))

    case "a name has letters, digits and spaces, passes the word list and is unique in any case" (fun () ->
        withGuilds (fun fixture -> task {
            let! _ = founded fixture
            let! _ = join fixture fixture.Carol
            do! act fixture fixture.Carol 10UL (GuildAction.Create "Два-слова")
            let! invalid = expectRefused fixture.Carol 10UL RequestRejectionCode.InvalidRequest
            equal "name" invalid.Field
            do! act fixture fixture.Carol 11UL (GuildAction.Create "badword")
            let! _ = expectRefused fixture.Carol 11UL RequestRejectionCode.TextNotAllowed
            do! act fixture fixture.Carol 12UL (GuildAction.Create "СТРАЖИ")
            let! _ = expectRefused fixture.Carol 12UL RequestRejectionCode.GuildNameTaken
            ()
        }))

    case "only an online player is invited and the guild limit refuses the next one" (fun () ->
        withGuilds (fun fixture -> task {
            let! guild = founded fixture
            do! act fixture fixture.Alice 20UL (GuildAction.Invite(guild, pid 3UL))
            let! offline = expectRefused fixture.Alice 20UL RequestRejectionCode.TargetNotFound
            equal "player_id" offline.Field
            let! _ = join fixture fixture.Carol
            do! act fixture fixture.Bob 21UL (GuildAction.Invite(guild, pid 3UL))
            let! _ = expectRefused fixture.Bob 21UL RequestRejectionCode.NotPermitted
            do! act fixture fixture.Alice 22UL (GuildAction.Invite(guild, pid 3UL))
            let! _ = expectChange fixture.Carol
            let! _ = expectDone fixture.Alice 22UL
            do! act fixture fixture.Carol 23UL (GuildAction.Answer(guild, true))
            let! _ = expectChange fixture.Carol
            let! _ = expectChange fixture.Carol
            let! _ = expectDone fixture.Carol 23UL
            let! arrival = expectChange fixture.Alice
            check (match arrival with GuildChange.MemberChanged(_, view) -> view.Profile.PlayerId = pid 3UL | _ -> false) "Carol arrived"
            do! act fixture fixture.Alice 24UL (GuildAction.Invite(guild, pid 2UL))
            let! _ = expectRefused fixture.Alice 24UL RequestRejectionCode.GuildAlreadyMember
            ()
        }))

    case "guild chat reaches the members only; a guild mute and a stranger are refused" (fun () ->
        withGuilds (fun fixture -> task {
            let! guild = founded fixture
            do! post fixture.Guilds (GuildCommand.Publish(guild, submission fixture.Alice 30UL "Привет"))
            let! accepted = receive fixture.Alice.Chat
            let message =
                match accepted with
                | ChatRoomEvent.Accepted(30UL, message) -> message
                | other -> failtestf "Expected acceptance, got %A" other
            equal (ChatChannels.ofGuild guild) message.ChannelId
            let! published = receive fixture.Bob.Events
            equal (GuildEvent.Chat(ChatRoomEvent.Published message)) published
            let! _ = join fixture fixture.Carol
            do! post fixture.Guilds (GuildCommand.Publish(guild, submission fixture.Carol 31UL "Я тоже"))
            let! stranger = receive fixture.Carol.Chat
            check (match stranger with ChatRoomEvent.Rejected(31UL, rejection) -> rejection.Code = RequestRejectionCode.NotChannelMember | _ -> false) "a stranger is refused"
            do! act fixture fixture.Alice 32UL (GuildAction.Mute(guild, pid 2UL, SanctionTerm.UntilLifted, reason))
            let! muted = expectChange fixture.Bob
            check (match muted with GuildChange.MemberChanged(_, view) -> view.Membership.Mute.IsSome | _ -> false) "Bob sees his guild mute"
            let! _ = expectChange fixture.Alice
            let! _ = expectDone fixture.Alice 32UL
            do! post fixture.Guilds (GuildCommand.Publish(guild, submission fixture.Bob 33UL "Ответ"))
            let! refused = receive fixture.Bob.Chat
            check (match refused with ChatRoomEvent.Rejected(33UL, rejection) -> rejection.Code = RequestRejectionCode.Muted | _ -> false) "a muted member reads only"
            // A snapshot after a reconnect carries the retained history.
            let! state = join fixture fixture.Alice
            equal [ message ] (state.Guilds |> List.collect _.Messages)
        }))

    case "the master removes a member's message, a member removes nothing" (fun () ->
        withGuilds (fun fixture -> task {
            let! guild = founded fixture
            do! post fixture.Guilds (GuildCommand.Publish(guild, submission fixture.Bob 40UL "Спам"))
            let! accepted = receive fixture.Bob.Chat
            let bobMessage = match accepted with ChatRoomEvent.Accepted(_, message) -> message | other -> failtestf "%A" other
            let! _ = receive fixture.Alice.Events
            do! post fixture.Guilds (GuildCommand.Publish(guild, submission fixture.Alice 41UL "Правила"))
            let! accepted = receive fixture.Alice.Chat
            let aliceMessage = match accepted with ChatRoomEvent.Accepted(_, message) -> message | other -> failtestf "%A" other
            let! _ = receive fixture.Bob.Events
            let removal (who: Member) requestId (message: ChatMessage) : ChatRemoval =
                { ConnectionId = who.Subscription.ConnectionId; RequestId = requestId; MessageId = message.MessageId; ReplyTo = who.ChatReplies }
            do! post fixture.Guilds (GuildCommand.Remove(guild, removal fixture.Bob 42UL aliceMessage))
            let! denied = receive fixture.Bob.Chat
            check (match denied with ChatRoomEvent.Rejected(42UL, rejection) -> rejection.Code = RequestRejectionCode.NotPermitted | _ -> false) "a member removes nothing"
            do! post fixture.Guilds (GuildCommand.Remove(guild, removal fixture.Alice 43UL bobMessage))
            let! removed = receive fixture.Alice.Chat
            equal (ChatRoomEvent.Removed(ValueSome 43UL, bobMessage)) removed
            let! notice = receive fixture.Bob.Events
            equal (GuildEvent.Chat(ChatRoomEvent.Removed(ValueNone, bobMessage))) notice
        }))

    case "guildmates see a member go offline; leaving, exclusion and disbanding reach the right players" (fun () ->
        withGuilds (fun fixture -> task {
            let! guild = founded fixture
            do! post fixture.Guilds (GuildCommand.Detach { ConnectionId = fixture.Bob.Subscription.ConnectionId; ReplyTo = fixture.Cleanup })
            let! acknowledged = receive fixture.Acknowledgments
            equal fixture.Bob.Subscription.ConnectionId acknowledged
            let! offline = expectChange fixture.Alice
            check (match offline with GuildChange.MemberChanged(_, view) -> not view.Online | _ -> false) "Bob is offline"
            let! state = join fixture fixture.Bob
            equal [ guild ] (state.Guilds |> List.map _.Guild)
            let! _ = expectChange fixture.Alice
            do! act fixture fixture.Alice 50UL (GuildAction.Leave guild)
            let! _ = expectRefused fixture.Alice 50UL RequestRejectionCode.GuildMasterStays
            do! act fixture fixture.Alice 51UL (GuildAction.Exclude(guild, pid 2UL))
            let! excluded = expectChange fixture.Bob
            equal (GuildChange.Removed(guild, GuildRemoval.Excluded)) excluded
            let! told = expectChange fixture.Alice
            equal (GuildChange.MemberRemoved(guild, pid 2UL, GuildRemoval.Excluded)) told
            let! _ = expectDone fixture.Alice 51UL
            do! act fixture fixture.Alice 52UL (GuildAction.Invite(guild, pid 2UL))
            let! _ = expectChange fixture.Bob
            let! _ = expectDone fixture.Alice 52UL
            do! act fixture fixture.Alice 53UL (GuildAction.Disband guild)
            let! disbanded = expectChange fixture.Alice
            equal (GuildChange.Removed(guild, GuildRemoval.Disbanded)) disbanded
            let! _ = expectDone fixture.Alice 53UL
            let! invitation = expectChange fixture.Bob
            equal (GuildChange.InviteRemoved guild) invitation
            // The name is free again.
            do! act fixture fixture.Bob 54UL (GuildAction.Create "стражи")
            let! _ = expectChange fixture.Bob
            let! next = expectDone fixture.Bob 54UL
            check (next <> guild) "a new guild never takes an old ID"
        }))

    case "handing the master's role over makes the old master an officer for everyone" (fun () ->
        withGuilds (fun fixture -> task {
            let! guild = founded fixture
            do! act fixture fixture.Alice 60UL (GuildAction.Transfer(guild, pid 2UL))
            let roles = Collections.Generic.Dictionary<PlayerId, GuildRole>()
            for _ in 1 .. 2 do
                match! expectChange fixture.Bob with
                | GuildChange.MemberChanged(_, view) -> roles[view.Profile.PlayerId] <- view.Membership.Role
                | other -> failtestf "%A" other
            equal GuildRole.Officer roles[pid 1UL]
            equal GuildRole.Master roles[pid 2UL]
            let! _ = expectChange fixture.Alice
            let! _ = expectChange fixture.Alice
            let! _ = expectDone fixture.Alice 60UL
            do! act fixture fixture.Alice 61UL (GuildAction.Leave guild)
            let! _ = expectChange fixture.Alice
            let! _ = expectDone fixture.Alice 61UL
            ()
        }))

    case "the panel finds guilds, opens one, appoints a master and dissolves a guild" (fun () ->
        withGuilds (fun fixture -> task {
            let! guild = founded fixture
            let! page = fixture.Guilds.TryAskAsync(fun reply -> GuildCommand.Admin(GuildAdminCommand.Search("стр", 1), reply)) |> awaitReply
            match page with
            | GuildAdminResult.Page page ->
                equal 1 page.Total
                equal (ValueSome(pid 1UL)) (page.Guilds.Head.Master |> ValueOption.map _.PlayerId)
            | other -> failtestf "%A" other
            let! card = fixture.Guilds.TryAskAsync(fun reply -> GuildCommand.Admin(GuildAdminCommand.Card guild, reply)) |> awaitReply
            check (match card with GuildAdminResult.Card(ValueSome card) -> card.Members.Length = 2 | _ -> false) "the card shows both members"
            let! appointed = fixture.Guilds.TryAskAsync(fun reply -> GuildCommand.Admin(GuildAdminCommand.Appoint(guild, pid 2UL), reply)) |> awaitReply
            match appointed with
            | GuildAdminResult.Appointed card ->
                equal (ValueSome(pid 2UL)) (card.Summary.Master |> ValueOption.map _.PlayerId)
            | other -> failtestf "%A" other
            let! _ = expectChange fixture.Bob
            let! _ = expectChange fixture.Bob
            let! _ = expectChange fixture.Alice
            let! _ = expectChange fixture.Alice
            let! mine = fixture.Guilds.TryAskAsync(fun reply -> GuildCommand.Admin(GuildAdminCommand.PlayerGuilds(pid 1UL), reply)) |> awaitReply
            check (match mine with GuildAdminResult.PlayerGuilds [ (_, GuildRole.Officer) ] -> true | _ -> false) "Alice is an officer now"
            let! dissolved = fixture.Guilds.TryAskAsync(fun reply -> GuildCommand.Admin(GuildAdminCommand.Dissolve guild, reply)) |> awaitReply
            check (match dissolved with GuildAdminResult.Dissolved gone -> gone.Members.Length = 2 | _ -> false) "dissolved"
            let! gone = expectChange fixture.Bob
            equal (GuildChange.Removed(guild, GuildRemoval.Disbanded)) gone
        }))

    case "stored guilds come back with their members, profiles and the next ID" (fun () ->
        let stored = {
            Id = gid 4UL; Name = GuildName.create 1 64 "Вороны" |> ok; CreatedAt = DateTimeOffset.UnixEpoch; Invites = []
            Members = [ { Player = pid 2UL; Role = GuildRole.Master; JoinedAt = DateTimeOffset.UnixEpoch; Mute = ValueNone } ]
        }
        let loaded (writer: Agent<GuildWrite>) =
            { empty writer with Loaded = [ stored ]; Profiles = [ profile 2UL ]; NextId = 9UL }
        withGuildsUsing loaded (fun fixture -> task {
            let! alice = join fixture fixture.Alice
            check alice.Guilds.IsEmpty "not hers"
            let! bob = join fixture fixture.Bob
            match bob.Guilds with
            | [ guild ] ->
                equal (gid 4UL) guild.Guild
                equal "Player 2" (DisplayName.value guild.Members.Head.Profile.DisplayName)
            | other -> failtestf "%A" other
            do! act fixture fixture.Alice 70UL (GuildAction.Create "Совы")
            let! _ = expectChange fixture.Alice
            let! created = expectDone fixture.Alice 70UL
            equal (gid 9UL) created
        }))
]

let private storeTests = testSequenced <| testList "SQLite guilds" [
    testCase "every change lands and loads back; disbanding takes members and invitations; IDs never return" (fun () ->
        use database = new SqliteAccountStoreTests.Database()
        SqliteAccountStore.initialize database.Config |> ok
        let register name =
            SqliteAccountStore.create database.Config (Username.create 32 name |> ok) (DisplayName.create 64 $"Display {name}" |> ok) "hash" token |> ok
        let alice, bob, carol = register "alice", register "bob", register "carol"
        let at = DateTimeOffset.FromUnixTimeMilliseconds 1700000000000L
        let write change = SqliteGuildStore.write database.Config change token |> ok
        let master = { Player = alice.PlayerId; Role = GuildRole.Master; JoinedAt = at; Mute = ValueNone }
        write (GuildWrite.Create(gid 1UL, GuildName.create 3 24 "Стражи" |> ok, at, master))
        let muted = { Player = bob.PlayerId; Role = GuildRole.Officer; JoinedAt = at
                      Mute = ValueSome { Reason = reason; IssuedBy = alice.PlayerId; IssuedAt = at; Expires = ValueSome(at.AddMinutes 5.0) } }
        write (GuildWrite.PutMember(gid 1UL, muted))
        write (GuildWrite.PutInvite { Guild = gid 1UL; Player = carol.PlayerId; InvitedBy = alice.PlayerId; CreatedAt = at; Expires = at.AddDays 7.0 })
        let loaded = SqliteGuildStore.loadAll database.Config token |> ok
        match loaded.Guilds with
        | [ guild ] ->
            equal "Стражи" (GuildName.value guild.Name)
            equal [ master; muted ] (guild.Members |> List.sortBy (fun item -> PlayerId.value item.Player))
            equal [ carol.PlayerId ] (guild.Invites |> List.map _.Player)
        | other -> failtestf "%A" other
        equal 3 loaded.Profiles.Length
        equal 2UL loaded.NextId
        Expect.throws (fun () -> database.Execute "INSERT INTO guilds(name, name_key, created_at) VALUES ('СТРАЖИ', 'стражи', 0)") "names are unique in any case"
        write (GuildWrite.PutMember(gid 1UL, { muted with Mute = ValueNone; Role = GuildRole.Member }))
        write (GuildWrite.RemoveInvite(gid 1UL, carol.PlayerId))
        let changed = SqliteGuildStore.loadAll database.Config token |> ok
        equal [ ValueNone ] (changed.Guilds.Head.Members |> List.filter (fun item -> item.Player = bob.PlayerId) |> List.map _.Mute)
        check changed.Guilds.Head.Invites.IsEmpty "the invitation is gone"
        write (GuildWrite.Delete(gid 1UL))
        let gone = SqliteGuildStore.loadAll database.Config token |> ok
        check gone.Guilds.IsEmpty "the guild is gone"
        equal 0L (database.Scalar "SELECT count(*) FROM guild_members")
        equal 2UL gone.NextId)
]

let private codecTests = testList "Guild codec" [
    testCase "guild commands decode to their actions; the master's role is only handed over" <| fun _ ->
        let codec = (Settings.defaults).Codec
        let decode (command: Dreamsleeve.Protocol.Chat.GuildCommand) =
            let packet = Dreamsleeve.Protocol.Chat.ClientPacket(ProtocolVersion = ProtocolCodec.Version, RequestId = 5UL, GuildCommand = command)
            ProtocolCodec.decodeClient codec (packet.ToByteArray())
        let action (result: Result<ClientRequest, ProtocolCodecError>) =
            match result with
            | Ok request ->
                match request.Command with
                | ClientCommand.Guild action -> action
                | other -> failtestf "%A" other
            | Error error -> failtestf "%A" error
        equal (GuildAction.Create "Стражи") (decode (Dreamsleeve.Protocol.Chat.GuildCommand(Create = Dreamsleeve.Protocol.Chat.CreateGuild(Name = "Стражи"))) |> action)
        let mute = Dreamsleeve.Protocol.Chat.MuteGuildMember(GuildId = 4UL, PlayerId = 7UL, Minutes = 30u, Reason = "Флуд")
        equal (GuildAction.Mute(gid 4UL, pid 7UL, SanctionTerm.For(TimeSpan.FromMinutes 30.0), reason))
              (decode (Dreamsleeve.Protocol.Chat.GuildCommand(Mute = mute)) |> action)
        let master = Dreamsleeve.Protocol.Chat.SetGuildRole(GuildId = 4UL, PlayerId = 7UL, Role = Dreamsleeve.Protocol.Chat.GuildRole.Master)
        match decode (Dreamsleeve.Protocol.Chat.GuildCommand(SetRole = master)) with
        | Error failure -> equal "role" (ProtocolCodec.rejection failure.Failure).Field
        | Ok request -> failtestf "%A" request
        match decode (Dreamsleeve.Protocol.Chat.GuildCommand(Leave = Dreamsleeve.Protocol.Chat.LeaveGuild(GuildId = 0UL))) with
        | Error failure -> equal "guild_id" (ProtocolCodec.rejection failure.Failure).Field
        | Ok request -> failtestf "%A" request
        equal DeliveryLane.Control (ProtocolCodec.requestLane { RequestId = 5UL; Command = ClientCommand.Guild(GuildAction.Leave(gid 4UL)) })

    testCase "a guild snapshot carries real names, roles, the guild mute and the channel" <| fun _ ->
        let codec = (Settings.defaults).Codec
        let limits = GuildOptions.rules GuildOptions.defaults |> ok
        let mute = { Reason = reason; IssuedBy = pid 1UL; IssuedAt = DateTimeOffset.UnixEpoch; Expires = ValueNone }
        let view = {
            Guild = gid 4UL; Name = GuildName.create 1 64 "Вороны" |> ok; ChannelId = ChatChannels.ofGuild (gid 4UL); CreatedAt = DateTimeOffset.UnixEpoch
            Members = [ { Membership = { Player = pid 2UL; Role = GuildRole.Officer; JoinedAt = DateTimeOffset.UnixEpoch; Mute = ValueSome mute }
                          Profile = profile 2UL; Online = true } ]
            Messages = []
        }
        let encoded = ProtocolCodec.encode codec Int32.MaxValue (ServerResponse.GuildsSnapshot { Guilds = [ view ]; Invites = []; Limits = limits }) |> ok
        let packet = Dreamsleeve.Protocol.Chat.ServerPacket.Parser.ParseFrom(List.exactlyOne encoded)
        let guild = packet.GuildsSnapshot.Guilds[0]
        equal (ChatChannelId.value (ChatChannels.ofGuild (gid 4UL))) guild.ChannelId
        equal "Player 2" guild.Members[0].Profile.DisplayName
        check (not guild.Members[0].Profile.Pseudonymous) "guildmates see the real names"
        equal Dreamsleeve.Protocol.Chat.GuildRole.Officer guild.Members[0].Role
        equal "Флуд" guild.Members[0].Mute.Reason
        equal 3u packet.GuildsSnapshot.Limits.MaxGuildsPerPlayer
        equal 64u packet.GuildsSnapshot.Limits.MaxMembers
        let wrong = { view with ChannelId = ChatChannels.globalId }
        check (ProtocolCodec.encode codec Int32.MaxValue (ServerResponse.GuildsSnapshot { Guilds = [ wrong ]; Invites = []; Limits = limits }) |> Result.isError)
            "a guild's history belongs to its own channel"

    testCase "a guild's chat travels with its guild changes on the control lane; the global chat keeps its own" <| fun _ ->
        let message channel =
            ChatMessage.create (ChatMessageId.create 1UL |> ok) channel (PublicIdentity.Profile(profile 2UL)) ValueNone (text "привет") DateTimeOffset.UnixEpoch
        let lane response = (ProtocolCodec.delivery response).Lane
        let guild = message (ChatChannels.ofGuild (gid 4UL))
        let open' = message ChatChannels.globalId
        for response in [ ServerResponse.ChatPublished guild; ServerResponse.ChatAccepted(5UL, guild)
                          ServerResponse.ChatMessageRemoved(ValueSome 5UL, guild.ChannelId, guild.MessageId)
                          ServerResponse.ChatMessageRemoved(ValueNone, guild.ChannelId, guild.MessageId)
                          ServerResponse.GuildChanged(GuildChange.InviteRemoved(gid 4UL)) ] do
            equal DeliveryLane.Control (lane response)
        for response in [ ServerResponse.ChatPublished open'; ServerResponse.ChatAccepted(5UL, open')
                          ServerResponse.ChatMessageRemoved(ValueNone, open'.ChannelId, open'.MessageId) ] do
            equal DeliveryLane.Chat (lane response)
        // A refusal changes nothing and answers on the lane the request came by.
        equal DeliveryLane.Chat (lane (ServerResponse.ChatRejected(5UL, { Code = RequestRejectionCode.NotChannelMember; Message = ""; Field = "" })))
]

let tests = testList "Guilds" [ agentTests; storeTests; codecTests ]
