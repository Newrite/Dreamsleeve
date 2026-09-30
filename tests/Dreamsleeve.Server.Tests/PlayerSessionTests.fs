module Dreamsleeve.Server.Tests.PlayerSessionTests

open System
open System.Threading.Channels
open Dreamsleeve.Agent
open Dreamsleeve.Server.Core
open Dreamsleeve.Server.Domain
open Expecto
open AgentTests
open BackgroundTests

let private ok = function Ok value -> value | Error error -> failwithf "%A" error
let private playerSnapshot profile = Player.snapshot (Player.create profile)
let private globalId = ChatChannelKind.channelId ChatChannelKind.Global
let private systemId = ChatChannelKind.channelId ChatChannelKind.System
let private options = { ServerRuntimeOptions.defaults.Player with MaxPendingChat = 1; MaxBootstrapEvents = 4; MaxPendingOutput = 16 }
let private collect (output: Channel<'T>) (_: AgentContext<'T>) value = task {
    check (output.Writer.TryWrite value) "Test output closed."
}
let private receive (output: Channel<'T>) = output.Reader.ReadAsync().AsTask().WaitAsync guard
let private deliver (address: ReliableAgentRef<'T>) value = task {
    let! result = address.PostAsync value
    equal AgentDeliveryResult.Posted result
}
let private post (player: Agent<PlayerSessionMessage>) message = deliver (player.Ref.TryReliable().Value) message
let private read (player: Agent<PlayerSessionMessage>) = task {
    let! result = player.TryAskAsync PlayerSessionMessage.Read |> awaitResult
    match result with
    | AgentAskResult.Replied value -> return value
    | other -> return failwithf "%A" other
}

type private Fixture = {
    Request: SessionOpenRequest
    Player: Agent<PlayerSessionMessage>
    Authentication: Channel<SessionAuthenticationRequest>
    Chat: Channel<ChatRoomCommand>
    System: Channel<ChatRoomCommand>
    Presence: Channel<PresenceCommand>
    Marks: Channel<GroundMarkCommand>
    Host: Channel<SessionHostCommand>
}

let private rules =
    Moderation.create { Words = ["badword"]; Substrings = []; Exceptions = [] }
    |> Moderation.withFlags { Words = ["flagword"]; Substrings = []; Exceptions = [] }

let private withIdentityPlayer moderation announcements identity hideIdentity settings (createPresence: Channel<PresenceCommand> -> Agent<PresenceCommand>) run = task {
    let queries = Channel.CreateUnbounded<SessionAuthenticationRequest>()
    let chatCommands = Channel.CreateUnbounded<ChatRoomCommand>()
    let systemCommands = Channel.CreateUnbounded<ChatRoomCommand>()
    let presenceCommands = Channel.CreateUnbounded<PresenceCommand>()
    let markCommands = Channel.CreateUnbounded<GroundMarkCommand>()
    let hostCommands = Channel.CreateUnbounded<SessionHostCommand>()
    use authentication = Agent.Start(AgentOptions.create "authentication", collect queries)
    use chat = Agent.Start(AgentOptions.create "chat", collect chatCommands)
    use system = Agent.Start(AgentOptions.create "system", collect systemCommands)
    use presence = createPresence presenceCommands
    use marks = Agent.Start(AgentOptions.create "marks", collect markCommands)
    use host = Agent.Start(AgentOptions.create "host", collect hostCommands)
    let request = {
        ConnectionId = Guid.NewGuid()
        RequestId = 1UL
        SessionTicket = String('a', 43)
        Hiding = hideIdentity
    }
    use player = PlayerSession.start settings 64 moderation announcements (GroundMarkOptions.rules GroundMarkOptions.defaults |> ok) identity
                     (authentication.Ref.TryReliable().Value) (chat.Ref.TryReliable().Value) (system.Ref.TryReliable().Value)
                     (presence.Ref.TryReliable().Value) (marks.Ref.TryReliable().Value) (host.Ref.TryReliable().Value) request |> ok
    let fixture = { Request = request; Player = player; Authentication = queries;
                    Chat = chatCommands; System = systemCommands; Presence = presenceCommands; Marks = markCommands; Host = hostCommands }
    do! run fixture
    if not player.Completion.IsCompleted then player.Abort()
    let! _ = terminal player.Completion
    authentication.Complete() |> ignore
    chat.Complete() |> ignore
    system.Complete() |> ignore
    presence.Complete() |> ignore
    marks.Complete() |> ignore
    host.Complete() |> ignore
    do! awaitUnit authentication.Completion
    do! awaitUnit chat.Completion
    do! awaitUnit system.Completion
    do! awaitUnit presence.Completion
    do! awaitUnit marks.Completion
    do! awaitUnit host.Completion
}

let private withConfiguredPlayer moderation announcements settings createPresence run =
    withIdentityPlayer moderation announcements IdentityOptions.defaults HiddenIdentity.Shown settings createPresence run

let private withModeratedPlayer moderation settings createPresence run =
    withConfiguredPlayer moderation AnnouncementOptions.defaults settings createPresence run

let private withPlayerUsingPresence settings createPresence run =
    withModeratedPlayer Moderation.empty settings createPresence run

let private withRules settings run =
    withModeratedPlayer rules settings (fun commands -> Agent.Start(AgentOptions.create "presence", collect commands)) run

let private withPlayer settings run =
    withPlayerUsingPresence settings (fun commands -> Agent.Start(AgentOptions.create "presence", collect commands)) run

let private resolve fixture = task {
    let! query = receive fixture.Authentication
    equal fixture.Request.SessionTicket query.Ticket
    let profile = PlayerData.create (PlayerId.create 42UL |> ok)
                      (Username.create 32 "player" |> ok) (DisplayName.create 64 "Player" |> ok)
    do! deliver query.ReplyTo { OperationId = query.OperationId; Result = Ok { Profile = profile; Role = PlayerRole.Player } }
    let! command = receive fixture.Host
    match command with
    | SessionHostCommand.Reserve(connectionId, reserved, hidden, reply) ->
        equal fixture.Request.ConnectionId connectionId
        equal profile reserved
        equal fixture.Request.Hiding hidden
        return profile, reply
    | other -> return failwithf "Expected Reserve: %A" other
}

let private snapshot (profile: PlayerData) = {
    ChannelId = globalId
    Kind = ChatChannelKind.Global
    Players = Set.singleton profile.PlayerId
    Messages = []
    HistoryCapacity = 4
}

// The system channel answers at once with an empty history; tests drive the global one.
let private joinsAs pseudonym fixture = task {
    let! profile, reply = resolve fixture
    do! deliver reply (IdentityAdmission.Reserved pseudonym)
    let! chatCommand = receive fixture.Chat
    let! systemCommand = receive fixture.System
    let! presenceCommand = receive fixture.Presence
    let! markCommand = receive fixture.Marks
    match chatCommand, systemCommand, presenceCommand, markCommand with
    | ChatRoomCommand.Join chat, ChatRoomCommand.Join system, PresenceCommand.Join presence, GroundMarkCommand.Join _ ->
        do! deliver system.Events (ChatRoomEvent.Joined { snapshot profile with ChannelId = systemId; Kind = ChatChannelKind.System })
        return profile, chat, presence
    | other -> return failwithf "Expected subscriptions: %A" other
}

let private joins fixture = joinsAs ValueNone fixture

let private ready fixture = task {
    let! profile, chat, presence = joins fixture
    do! deliver chat.Events (ChatRoomEvent.Joined(snapshot profile))
    do! deliver presence.Events (PresenceEvent.Snapshot [playerSnapshot profile])
    let! command = receive fixture.Host
    match command with
    | SessionHostCommand.Activate(connectionId, requestId, welcome) ->
        equal fixture.Request.ConnectionId connectionId
        equal fixture.Request.RequestId requestId
        equal profile.PlayerId welcome.SelfPlayerId
    | other -> failwithf "Expected Activate: %A" other
    return profile, chat, presence
}

let private applyUpdate fixture requestId command = task {
    do! post fixture.Player (PlayerSessionMessage.Update(requestId, command))
    let! accepted = receive fixture.Host
    equal (SessionHostCommand.Send(fixture.Request.ConnectionId, ServerResponse.PlayerUpdateAccepted requestId)) accepted
    let! change = receive fixture.Presence
    match change with
    | PresenceCommand.Update(connectionId, current) ->
        equal fixture.Request.ConnectionId connectionId
        return current
    | other -> return failwithf "Expected presence update: %A" other
}

let private rejectUpdate fixture requestId command = task {
    do! post fixture.Player (PlayerSessionMessage.Update(requestId, command))
    let! rejected = receive fixture.Host
    match rejected with
    | SessionHostCommand.Send(_, ServerResponse.RequestRejected(id, rejection)) ->
        equal requestId id
        equal RequestRejectionCode.InvalidRequest rejection.Code
    | other -> failwithf "Expected update refusal: %A" other
    equal 0 fixture.Presence.Reader.Count
}

let private publication profile id =
    ChatMessage.create (ChatMessageId.create id |> ok) globalId (PublicIdentity.Profile profile) ValueNone
        (ChatMessageText.create 2000 $"message {id}" |> ok) DateTimeOffset.UnixEpoch

let private finish fixture = task {
    let! chatCommand = receive fixture.Chat
    let! systemCommand = receive fixture.System
    let! presenceCommand = receive fixture.Presence
    // Position updates reach the mark owner before the detach; only the detach matters here.
    let mutable markCommand = GroundMarkCommand.Expire { DueTimestamp = 0L; QueuedTimestamp = 0L }
    while (match markCommand with GroundMarkCommand.Detach _ -> false | _ -> true) do
        let! next = receive fixture.Marks
        markCommand <- next
    match chatCommand, systemCommand, presenceCommand, markCommand with
    | ChatRoomCommand.Detach chat, ChatRoomCommand.Detach system, PresenceCommand.Detach presence, GroundMarkCommand.Detach marks ->
        do! deliver chat.ReplyTo fixture.Request.ConnectionId
        do! deliver system.ReplyTo fixture.Request.ConnectionId
        check (not fixture.Player.Completion.IsCompleted) "Session skipped presence cleanup."
        do! deliver presence.ReplyTo fixture.Request.ConnectionId
        check (not fixture.Player.Completion.IsCompleted) "Session skipped ground mark cleanup."
        do! deliver marks.ReplyTo fixture.Request.ConnectionId
        do! awaitUnit fixture.Player.Completion
    | other -> failwithf "Expected detach: %A" other
}

let private submitted fixture requestId text = task {
    do! post fixture.Player (PlayerSessionMessage.SendChat(requestId, globalId, ChatMessageText.create 2000 text |> ok))
    let! command = receive fixture.Chat
    match command with
    | ChatRoomCommand.Publish value -> return value
    | other -> return failwithf "Expected publication: %A" other
}

let private withAnnouncements announcements run =
    withConfiguredPlayer rules announcements options (fun commands -> Agent.Start(AgentOptions.create "presence", collect commands)) run

let private announcementRequest text signature : AnnouncementRequest = {
    ChannelId = systemId
    Text = ChatMessageText.create 500 text |> ok
    Kind = AnnouncementKind.Event
    Source = ClientAnnouncementSource.ThirdParty
    Signature = ValueSome(AnnouncementSignature.create 64 signature |> ok)
}

let private announcementRefused fixture requestId code field = task {
    let! refused = receive fixture.Host
    match refused with
    | SessionHostCommand.Send(_, ServerResponse.ChatRejected(id, rejection)) ->
        equal requestId id
        equal code rejection.Code
        equal field rejection.Field
    | other -> failwithf "Expected announcement refusal: %A" other
    equal 0 fixture.Chat.Reader.Count
    equal 0 fixture.System.Reader.Count
}

// Chat stays pending in these tests; the budget holds all of it.
let private withIdentity identity hide run =
    withIdentityPlayer Moderation.empty AnnouncementOptions.defaults identity hide { options with MaxPendingChat = 8 }
        (fun commands -> Agent.Start(AgentOptions.create "presence", collect commands)) run

let private strazh = Pseudonym.create "Страж" |> ok

// Presence answers with the copy the session handed it, as the real owner would.
let private readyAs pseudonym fixture = task {
    let! profile, chat, presence = joinsAs pseudonym fixture
    do! deliver chat.Events (ChatRoomEvent.Joined(snapshot profile))
    do! deliver presence.Events (PresenceEvent.Snapshot [presence.Snapshot])
    let! command = receive fixture.Host
    match command with
    | SessionHostCommand.Activate(_, _, welcome) -> return profile, chat, presence, welcome
    | other -> return failwithf "Expected Activate: %A" other
}

let private nextMarkPlacement fixture = task {
    let mutable found = None
    while found.IsNone do
        let! command = receive fixture.Marks
        match command with
        | GroundMarkCommand.Place submission -> found <- Some submission
        | _ -> ()
    return found.Value
}

let private switchIdentity fixture requestId hidden = task {
    do! post fixture.Player (PlayerSessionMessage.SetIdentityVisibility(requestId, hidden))
    let! command = receive fixture.Host
    match command with
    | SessionHostCommand.ChangeIdentity(connectionId, requested, reply) ->
        equal fixture.Request.ConnectionId connectionId
        equal hidden requested
        return reply
    | other -> return failwithf "Expected ChangeIdentity: %A" other
}

let private identityRefused fixture requestId code = task {
    let! command = receive fixture.Host
    match command with
    | SessionHostCommand.Send(_, ServerResponse.RequestRejected(id, rejection)) ->
        equal requestId id
        equal code rejection.Code
    | other -> failwithf "Expected identity refusal: %A" other
}

let private identityTests = [
    case "a hidden session sends only its pseudonym to others and keeps its own real profile" (fun () ->
        withIdentity IdentityOptions.defaults HiddenIdentity.Everywhere (fun fixture -> task {
            let! profile, _, presence, welcome = readyAs (ValueSome strazh) fixture
            equal (PublicIdentity.Pseudonymous(profile.PlayerId, strazh)) presence.Snapshot.Identity
            equal (ValueSome strazh) welcome.OwnPseudonym
            let self = welcome.Players |> List.find (fun player -> player.Identity.PlayerId = profile.PlayerId)
            equal (PublicIdentity.Profile profile) self.Identity

            let name = CharacterName.create 128 "Indoril" |> ok
            let! started = applyUpdate fixture 2UL (PlayerUpdate.BeginCharacter name)
            equal (PublicIdentity.Pseudonymous(profile.PlayerId, strazh)) started.Identity
            equal ValueNone started.CharacterName
            let! read = read fixture.Player
            equal ValueNone (ok read).CharacterName

            // The owner hears about itself through the copy meant for others.
            do! deliver presence.Events (PresenceEvent.Updated started)
            let! own = receive fixture.Host
            match own with
            | SessionHostCommand.Send(_, ServerResponse.PlayerUpdated value) ->
                equal (PublicIdentity.Profile profile) value.Identity
                equal (ValueSome name) value.CharacterName
            | other -> failwithf "Expected own update: %A" other
            // Another player's pseudonym passes unchanged.
            let other = PlayerSnapshot.withPseudonym strazh (playerSnapshot (PlayerData.create (PlayerId.create 9UL |> ok) (Username.create 32 "other" |> ok) (DisplayName.create 64 "Other" |> ok)))
            do! deliver presence.Events (PresenceEvent.Joined other)
            let! joined = receive fixture.Host
            equal (SessionHostCommand.Send(fixture.Request.ConnectionId, ServerResponse.PlayerJoined other)) joined

            let! message = submitted fixture 3UL "hello"
            equal (PublicIdentity.Pseudonymous(profile.PlayerId, strazh)) message.Author
            equal ValueNone message.CharacterName

            let location =
                PlayerLocation.create (Location.create (FormKey.create (PluginName.create 64 "Skyrim.esm" |> ok) (LocalFormId.create 60u |> ok))
                                           (LocationName.create 64 "Whiterun" |> ok)) Position.zero Rotation.zero
            let! _ = applyUpdate fixture 4UL (PlayerUpdate.SetLocation(1UL, ValueSome location))
            let placement = GroundMarkPlacement.create location.Location.LocationId Position.zero (Radian.create 0.0f |> ok)
            do! post fixture.Player (PlayerSessionMessage.PlaceGroundNote(5UL, GroundNoteText.create 200 "note" |> ok, placement))
            let! mark = nextMarkPlacement fixture
            equal (ValueSome strazh) mark.Pseudonym
            equal ValueNone mark.CharacterName
        }))

    case "switching is settled by the runtime's pseudonym, limited in frequency and idempotent" (fun () ->
        withIdentity { IdentityOptions.defaults with ToggleIntervalMs = 60000 } HiddenIdentity.Shown (fun fixture -> task {
            let! profile, _, _ = ready fixture
            let! shown = submitted fixture 2UL "before"
            equal (PublicIdentity.Profile profile) shown.Author
            let! reply = switchIdentity fixture 3UL HiddenIdentity.Everywhere
            // Until the runtime answers, messages still carry the real profile.
            let! pending = submitted fixture 4UL "while pending"
            equal (PublicIdentity.Profile profile) pending.Author
            do! deliver reply (ValueSome strazh)
            let! spread = receive fixture.Presence
            match spread with
            | PresenceCommand.Update(_, value) -> equal (PublicIdentity.Pseudonymous(profile.PlayerId, strazh)) value.Identity
            | other -> failwithf "Expected presence update: %A" other
            let! settled = receive fixture.Host
            equal (SessionHostCommand.Send(fixture.Request.ConnectionId, ServerResponse.IdentityVisibilityChanged(3UL, ValueSome strazh, HiddenIdentity.Everywhere))) settled
            let! hidden = submitted fixture 5UL "after"
            equal (PublicIdentity.Pseudonymous(profile.PlayerId, strazh)) hidden.Author

            // Asking for the current state is not a switch.
            do! post fixture.Player (PlayerSessionMessage.SetIdentityVisibility(6UL, HiddenIdentity.Everywhere))
            let! same = receive fixture.Host
            equal (SessionHostCommand.Send(fixture.Request.ConnectionId, ServerResponse.IdentityVisibilityChanged(6UL, ValueSome strazh, HiddenIdentity.Everywhere))) same
            // A real switch this soon is refused and nothing changes.
            do! post fixture.Player (PlayerSessionMessage.SetIdentityVisibility(7UL, HiddenIdentity.Shown))
            do! identityRefused fixture 7UL RequestRejectionCode.RateLimited
            equal 0 fixture.Presence.Reader.Count
            let! still = submitted fixture 8UL "still hidden"
            equal (PublicIdentity.Pseudonymous(profile.PlayerId, strazh)) still.Author
        }))

    case "showing again restores the real profile for later messages" (fun () ->
        withIdentity { IdentityOptions.defaults with ToggleIntervalMs = 0 } HiddenIdentity.Everywhere (fun fixture -> task {
            let! profile, _, _, _ = readyAs (ValueSome strazh) fixture
            let! reply = switchIdentity fixture 2UL HiddenIdentity.Shown
            do! deliver reply ValueNone
            let! spread = receive fixture.Presence
            match spread with
            | PresenceCommand.Update(_, value) -> equal (PublicIdentity.Profile profile) value.Identity
            | other -> failwithf "Expected presence update: %A" other
            let! settled = receive fixture.Host
            equal (SessionHostCommand.Send(fixture.Request.ConnectionId, ServerResponse.IdentityVisibilityChanged(2UL, ValueNone, HiddenIdentity.Shown))) settled
            let! shown = submitted fixture 3UL "shown"
            equal (PublicIdentity.Profile profile) shown.Author
            let! again = switchIdentity fixture 4UL HiddenIdentity.Everywhere
            do! deliver again (ValueSome (Pseudonym.numbered 2 strazh))
            let! _ = receive fixture.Presence
            let! _ = receive fixture.Host
            let! renamed = submitted fixture 5UL "hidden again"
            equal (PublicIdentity.Pseudonymous(profile.PlayerId, Pseudonym.numbered 2 strazh)) renamed.Author
        }))

    case "hidden everywhere except marks: presence and chat carry the pseudonym, new marks the real profile" (fun () ->
        withIdentity { IdentityOptions.defaults with ToggleIntervalMs = 0 } HiddenIdentity.ExceptGroundMarks (fun fixture -> task {
            let! profile, _, presence, welcome = readyAs (ValueSome strazh) fixture
            equal HiddenIdentity.ExceptGroundMarks welcome.Hiding
            equal (PublicIdentity.Pseudonymous(profile.PlayerId, strazh)) presence.Snapshot.Identity
            let name = CharacterName.create 128 "Indoril" |> ok
            let! started = applyUpdate fixture 2UL (PlayerUpdate.BeginCharacter name)
            equal ValueNone started.CharacterName
            let! message = submitted fixture 3UL "hello"
            equal (PublicIdentity.Pseudonymous(profile.PlayerId, strazh)) message.Author
            equal ValueNone message.CharacterName
            let location =
                PlayerLocation.create (Location.create (FormKey.create (PluginName.create 64 "Skyrim.esm" |> ok) (LocalFormId.create 60u |> ok))
                                           (LocationName.create 64 "Whiterun" |> ok)) Position.zero Rotation.zero
            let! _ = applyUpdate fixture 4UL (PlayerUpdate.SetLocation(1UL, ValueSome location))
            let placement = GroundMarkPlacement.create location.Location.LocationId Position.zero (Radian.create 0.0f |> ok)
            do! post fixture.Player (PlayerSessionMessage.PlaceGroundNote(5UL, GroundNoteText.create 200 "note" |> ok, placement))
            let! mark = nextMarkPlacement fixture
            equal ValueNone mark.Pseudonym
            equal (ValueSome name) mark.CharacterName
            // Extending the hiding to marks keeps the pseudonym the runtime returns.
            let! reply = switchIdentity fixture 6UL HiddenIdentity.Everywhere
            do! deliver reply (ValueSome strazh)
            let! _ = receive fixture.Presence
            let! settled = receive fixture.Host
            equal (SessionHostCommand.Send(fixture.Request.ConnectionId, ServerResponse.IdentityVisibilityChanged(6UL, ValueSome strazh, HiddenIdentity.Everywhere))) settled
            do! post fixture.Player (PlayerSessionMessage.PlaceGroundNote(7UL, GroundNoteText.create 200 "later" |> ok, placement))
            let! hidden = nextMarkPlacement fixture
            equal (ValueSome strazh) hidden.Pseudonym
            equal ValueNone hidden.CharacterName
        }))

    case "a server that refuses hidden names rejects the opening before the ticket and every switch" (fun () -> task {
        let refused = { IdentityOptions.defaults with AllowHiddenIdentity = false }
        do! withIdentity refused HiddenIdentity.Everywhere (fun fixture -> task {
            let! rejected = receive fixture.Host
            match rejected with
            | SessionHostCommand.Send(_, ServerResponse.RequestRejected(id, rejection)) ->
                equal fixture.Request.RequestId id
                equal RequestRejectionCode.HiddenIdentityNotAllowed rejection.Code
            | other -> failwithf "Expected refusal: %A" other
            let! closed = receive fixture.Host
            match closed with
            | SessionHostCommand.Close _ -> ()
            | other -> failwithf "Expected close: %A" other
            equal 0 fixture.Authentication.Reader.Count
        })
        do! withIdentity refused HiddenIdentity.Shown (fun fixture -> task {
            let! _ = ready fixture
            do! post fixture.Player (PlayerSessionMessage.SetIdentityVisibility(2UL, HiddenIdentity.Everywhere))
            do! identityRefused fixture 2UL RequestRejectionCode.HiddenIdentityNotAllowed
            do! post fixture.Player (PlayerSessionMessage.SetIdentityVisibility(3UL, HiddenIdentity.Shown))
            let! shown = receive fixture.Host
            equal (SessionHostCommand.Send(fixture.Request.ConnectionId, ServerResponse.IdentityVisibilityChanged(3UL, ValueNone, HiddenIdentity.Shown))) shown
        })
    })
]

let tests = testList "PlayerSession" ([
    case "a disabled announcement source is refused before the channel; the welcome lists admitted sources" (fun () ->
        let announcements = { AnnouncementOptions.defaults with ThirdParty = { Enabled = false } }
        withAnnouncements announcements (fun fixture -> task {
            let! profile, chat, presence = joins fixture
            do! deliver chat.Events (ChatRoomEvent.Joined(snapshot profile))
            do! deliver presence.Events (PresenceEvent.Snapshot [playerSnapshot profile])
            let! activation = receive fixture.Host
            match activation with
            | SessionHostCommand.Activate(_, _, welcome) -> equal [ClientAnnouncementSource.TrustedClient] welcome.AnnouncementSources
            | other -> failwithf "Expected Activate: %A" other
            do! post fixture.Player (PlayerSessionMessage.PostAnnouncement(2UL, announcementRequest "Игрок пал" "DeathMod"))
            do! announcementRefused fixture 2UL RequestRejectionCode.AnnouncementNotAllowed "source"
        }))

    case "announcement text and signature pass the word list; accepted ones carry their origin" (fun () ->
        withAnnouncements AnnouncementOptions.defaults (fun fixture -> task {
            let! _ = ready fixture
            do! post fixture.Player (PlayerSessionMessage.PostAnnouncement(2UL, announcementRequest "a badword" "DeathMod"))
            do! announcementRefused fixture 2UL RequestRejectionCode.TextNotAllowed "text"
            do! post fixture.Player (PlayerSessionMessage.PostAnnouncement(3UL, announcementRequest "Игрок пал" "Mod Badword"))
            do! announcementRefused fixture 3UL RequestRejectionCode.TextNotAllowed "source"
            do! post fixture.Player (PlayerSessionMessage.PostAnnouncement(4UL, announcementRequest "a flagword event" "DeathMod"))
            let! command = receive fixture.System
            match command with
            | ChatRoomCommand.Publish submission ->
                equal 4UL submission.RequestId
                equal [{ Start = 2; Length = 8 }] submission.Flagged
                match submission.Announcement with
                | ValueSome announcement ->
                    equal AnnouncementSource.ThirdParty announcement.Source
                    equal AnnouncementKind.Event announcement.Kind
                    equal (ValueSome "DeathMod") (announcement.Signature |> ValueOption.map AnnouncementSignature.value)
                | ValueNone -> failtest "The origin was lost."
            | other -> failwithf "Expected publication: %A" other
        }))

    case "chat never enters the system channel and announcements never leave it" (fun () ->
        withAnnouncements AnnouncementOptions.defaults (fun fixture -> task {
            let! _ = ready fixture
            do! post fixture.Player (PlayerSessionMessage.SendChat(2UL, systemId, ChatMessageText.create 2000 "hello" |> ok))
            do! announcementRefused fixture 2UL RequestRejectionCode.InvalidRequest ""
            let misrouted = { announcementRequest "Игрок пал" "DeathMod" with ChannelId = globalId }
            do! post fixture.Player (PlayerSessionMessage.PostAnnouncement(3UL, misrouted))
            do! announcementRefused fixture 3UL RequestRejectionCode.InvalidRequest "channel_id"
        }))

    case "word list refuses chat text before the channel sees it" (fun () ->
        withRules options (fun fixture -> task {
            let! _ = ready fixture
            do! post fixture.Player (PlayerSessionMessage.SendChat(2UL, globalId, ChatMessageText.create 2000 "you B@DW0RD!" |> ok))
            let! refused = receive fixture.Host
            match refused with
            | SessionHostCommand.Send(_, ServerResponse.ChatRejected(2UL, rejection)) ->
                equal RequestRejectionCode.TextNotAllowed rejection.Code
                equal "text" rejection.Field
            | other -> failwithf "Expected text refusal: %A" other
            equal 0 fixture.Chat.Reader.Count
            let! accepted = submitted fixture 3UL "badwordless flagword"
            equal "badwordless flagword" (ChatMessageText.value accepted.Text)
            equal (Moderation.normalize "badwordless flagword") accepted.Fingerprint
            // Flag-tier words pass with marked byte ranges instead of a refusal.
            equal [{ Start = 12; Length = 8 }] accepted.Flagged
        }))

    case "stored names failing current rules leave the session only as placeholders" (fun () ->
        withRules options (fun fixture -> task {
            let! query = receive fixture.Authentication
            let stored = PlayerData.create (PlayerId.create 42UL |> ok)
                             (Username.create 32 "bad.word" |> ok) (DisplayName.create 64 "Sir Badword" |> ok)
            do! deliver query.ReplyTo { OperationId = query.OperationId; Result = Ok { Profile = stored; Role = PlayerRole.Player } }
            let! reserve = receive fixture.Host
            let reply =
                match reserve with
                | SessionHostCommand.Reserve(_, reserved, _, reply) -> equal stored.PlayerId reserved.PlayerId; reply
                | other -> failwithf "Expected Reserve: %A" other
            do! deliver reply (IdentityAdmission.Reserved ValueNone)
            let! chatCommand = receive fixture.Chat
            let! presenceCommand = receive fixture.Presence
            match chatCommand, presenceCommand with
            | ChatRoomCommand.Join chat, PresenceCommand.Join presence ->
                let shown =
                    match presence.Snapshot.Identity with
                    | PublicIdentity.Profile profile -> profile
                    | PublicIdentity.Pseudonymous _ -> failwith "Expected a profile."
                for profile in [chat.Profile; shown] do
                    equal stored.PlayerId profile.PlayerId
                    equal "hidden.42" (Username.value profile.Username)
                    equal "Player 42" (DisplayName.value profile.DisplayName)
            | other -> failwithf "Expected subscriptions: %A" other
        }))

    case "a failing character name is withheld from presence and chat without being lost" (fun () ->
        withRules options (fun fixture -> task {
            let! _ = ready fixture
            let! hidden = applyUpdate fixture 2UL (PlayerUpdate.BeginCharacter(CharacterName.create 128 "Badword" |> ok))
            equal ValueNone hidden.CharacterName
            check hidden.CharacterNameWithheld "A withheld name is still an active character."
            let! first = submitted fixture 3UL "hello"
            equal ValueNone first.CharacterName
            let! state = read fixture.Player
            equal ValueNone (ok state).CharacterName

            let lydia = CharacterName.create 128 "Lydia" |> ok
            let! renamed = applyUpdate fixture 4UL (PlayerUpdate.RenameCharacter lydia)
            equal (ValueSome lydia) renamed.CharacterName
            check (not renamed.CharacterNameWithheld) "An allowed name is published again."
            do! deliver (fixture.Player.Ref.TryReliable().Value) (PlayerSessionMessage.ChatEvent(ChatRoomEvent.Rejected(3UL, { Code = RequestRejectionCode.RateLimited; Message = ""; Field = "" })))
            let! _ = receive fixture.Host
            let! second = submitted fixture 5UL "hello again"
            equal (ValueSome lydia) second.CharacterName
        }))

    case "bootstrap orders history before later chat and buffers independent presence changes" (fun () ->
        withPlayer options (fun fixture -> task {
            let! profile, chat, presence = joins fixture
            let message = publication profile 1UL
            do! deliver chat.Events (ChatRoomEvent.Joined(snapshot profile))
            do! deliver chat.Events (ChatRoomEvent.Published message)
            let! state = read fixture.Player
            equal (Error PlayerStateError.NotReady) state
            equal 0 fixture.Host.Reader.Count

            do! deliver presence.Events (PresenceEvent.Snapshot [playerSnapshot profile])
            let! first = receive fixture.Host
            match first with
            | SessionHostCommand.Activate(_, _, welcome) ->
                equal [ChatChannelKind.Global; ChatChannelKind.System] (welcome.Channels |> List.map _.Kind)
                check (welcome.Channels |> List.forall _.Messages.IsEmpty) "Later publications are not history."
            | other -> failwithf "Expected welcome first: %A" other
            let! second = receive fixture.Host
            equal (SessionHostCommand.Send(fixture.Request.ConnectionId, ServerResponse.ChatPublished message)) second
            do! deliver presence.Events (PresenceEvent.Left profile.PlayerId)
            let! third = receive fixture.Host
            equal (SessionHostCommand.Send(fixture.Request.ConnectionId, ServerResponse.PlayerLeft profile.PlayerId)) third
        }))

    case "presence snapshot and later delta remain ordered while chat snapshot is delayed" (fun () ->
        withPlayer options (fun fixture -> task {
            let! profile, chat, presence = joins fixture
            do! deliver presence.Events (PresenceEvent.Snapshot [playerSnapshot profile])
            do! deliver presence.Events (PresenceEvent.Left profile.PlayerId)
            do! deliver chat.Events (ChatRoomEvent.Joined(snapshot profile))
            let! welcome = receive fixture.Host
            match welcome with
            | SessionHostCommand.Activate(_, _, value) -> equal [playerSnapshot profile] value.Players
            | other -> failwithf "%A" other
            let! delta = receive fixture.Host
            equal (SessionHostCommand.Send(fixture.Request.ConnectionId, ServerResponse.PlayerLeft profile.PlayerId)) delta
        }))

    case "personal quota and rejection settle one request without closing the player" (fun () ->
        withPlayer options (fun fixture -> task {
            let! profile, chat, _ = ready fixture
            let text = ChatMessageText.create 2000 "hello" |> ok
            do! post fixture.Player (PlayerSessionMessage.SendChat(2UL, globalId, text))
            let! first = receive fixture.Chat
            match first with
            | ChatRoomCommand.Publish value -> equal 2UL value.RequestId
            | other -> failwithf "%A" other
            do! post fixture.Player (PlayerSessionMessage.SendChat(3UL, globalId, text))
            let! overloaded = receive fixture.Host
            match overloaded with
            | SessionHostCommand.Send(_, ServerResponse.ChatRejected(id, reason)) ->
                equal 3UL id
                equal RequestRejectionCode.Overloaded reason.Code
            | other -> failwithf "%A" other
            equal 0 fixture.Chat.Reader.Count

            let rejection = { Code = RequestRejectionCode.NotChannelMember; Message = "refused"; Field = "" }
            do! deliver chat.Events (ChatRoomEvent.Rejected(2UL, rejection))
            let! rejected = receive fixture.Host
            equal (SessionHostCommand.Send(fixture.Request.ConnectionId, ServerResponse.ChatRejected(2UL, rejection))) rejected
            do! post fixture.Player (PlayerSessionMessage.SendChat(4UL, globalId, text))
            let! next = receive fixture.Chat
            match next with
            | ChatRoomCommand.Publish value -> equal 4UL value.RequestId
            | other -> failwithf "%A" other
            let message = publication profile 1UL
            do! deliver chat.Events (ChatRoomEvent.Accepted(4UL, message))
            let! accepted = receive fixture.Host
            equal (SessionHostCommand.Send(fixture.Request.ConnectionId, ServerResponse.ChatAccepted(4UL, message))) accepted
            let! state = read fixture.Player
            equal (PublicIdentity.Profile profile) (ok state).Identity
        }))

    case "stop before snapshots detaches in the source command order and waits for both acknowledgements" (fun () ->
        withPlayer options (fun fixture -> task {
            let! profile, chat, presence = joins fixture
            do! post fixture.Player PlayerSessionMessage.Stop
            do! deliver chat.Events (ChatRoomEvent.Joined(snapshot profile))
            do! deliver presence.Events (PresenceEvent.Snapshot [playerSnapshot profile])
            let! state = read fixture.Player
            equal (Error PlayerStateError.Closed) state
            do! finish fixture
            equal 0 fixture.Host.Reader.Count
        }))

    case "bootstrap overflow closes only this session and completes its subscriptions" (fun () ->
        withPlayer { options with MaxBootstrapEvents = 1 } (fun fixture -> task {
            let! profile, chat, _ = joins fixture
            do! deliver chat.Events (ChatRoomEvent.Joined(snapshot profile))
            do! deliver chat.Events (ChatRoomEvent.Published(publication profile 1UL))
            do! deliver chat.Events (ChatRoomEvent.Published(publication profile 2UL))
            let! command = receive fixture.Host
            match command with
            | SessionHostCommand.Close(id, _) -> equal fixture.Request.ConnectionId id
            | other -> failwithf "%A" other
            do! finish fixture
            equal 0 fixture.Host.Reader.Count
        }))

    case "denied identity sends correlated refusal before close and never joins services" (fun () ->
        withPlayer options (fun fixture -> task {
            let! _, reply = resolve fixture
            do! deliver reply IdentityAdmission.AlreadyInUse
            let! rejected = receive fixture.Host
            match rejected with
            | SessionHostCommand.Send(_, ServerResponse.RequestRejected(id, reason)) ->
                equal fixture.Request.RequestId id
                equal RequestRejectionCode.SessionAlreadyOpen reason.Code
            | other -> failwithf "%A" other
            let! closed = receive fixture.Host
            match closed with
            | SessionHostCommand.Close(id, _) -> equal fixture.Request.ConnectionId id
            | other -> failwithf "%A" other
            let! _ = terminal fixture.Player.Completion
            equal 0 fixture.Chat.Reader.Count
            equal 0 fixture.Presence.Reader.Count
        }))

    case "profile resolution can stop without waiting for its reply" (fun () ->
        withPlayer options (fun fixture -> task {
            let! query = receive fixture.Authentication
            do! post fixture.Player PlayerSessionMessage.Stop
            let! _ = terminal fixture.Player.Completion
            let! late = query.ReplyTo.PostAsync { OperationId = query.OperationId; Result = Error SessionAuthenticationError.Unavailable }
            equal AgentDeliveryResult.Closed late
            equal 0 fixture.Host.Reader.Count
        }))

    case "player owns detached telemetry across character changes" (fun () ->
        withPlayer options (fun fixture -> task {
            let! profile, _, _ = ready fixture
            let name = CharacterName.create 128 "Nerevar" |> ok
            let key = ActorValueKey.create 128 "skyrim:health" |> ok
            let health value =
                ActorValueInfo.create (ActorValueName.create 64 "Health" |> ok)
                    (ActorValueState.resource value 100.0f |> ok)
            let form = FormKey.create (PluginName.create 255 "Skyrim.esm" |> ok) (LocalFormId.create 0x3Cu |> ok)
            let location = PlayerLocation.create
                               (Location.create form (LocationName.create 128 "Whiterun" |> ok))
                               (Position.create 1.0f 2.0f 3.0f |> ok) Rotation.zero
            let mutable nextRequestId = 2UL
            let update value = task {
                let requestId = nextRequestId
                nextRequestId <- nextRequestId + 1UL
                do! post fixture.Player (PlayerSessionMessage.Update(requestId, value))
                let! accepted = receive fixture.Host
                equal (SessionHostCommand.Send(fixture.Request.ConnectionId, ServerResponse.PlayerUpdateAccepted requestId)) accepted
                let! change = receive fixture.Presence
                match change with
                | PresenceCommand.Update(connectionId, current) ->
                    equal fixture.Request.ConnectionId connectionId
                    equal (PublicIdentity.Profile profile) current.Identity
                | other -> failwithf "Expected presence update: %A" other
            }

            do! update (PlayerUpdate.BeginCharacter name)
            do! update (PlayerUpdate.SetLocation(1UL, ValueSome location))
            do! update (PlayerUpdate.SetActorValues(Map.ofList [(key, health 80.0f)]))
            let! first = read fixture.Player
            let first = ok first
            equal (PublicIdentity.Profile profile) first.Identity
            equal (ValueSome name) first.CharacterName
            equal (ValueSome location) first.Location

            do! update (PlayerUpdate.SetActorValues(Map.ofList [(key, health 20.0f)]))
            let! second = read fixture.Player
            equal (health 20.0f) (ok second).ActorValues[key]
            equal (health 80.0f) first.ActorValues[key]

            // Loading another save with the same character name clears its old telemetry.
            do! update (PlayerUpdate.BeginCharacter name)
            let! fresh = read fixture.Player
            let fresh = ok fresh
            equal (first.CharacterGeneration + 1UL) fresh.CharacterGeneration
            equal (ValueSome name) fresh.CharacterName
            equal ValueNone fresh.Location
            equal Map.empty fresh.ActorValues
            do! update PlayerUpdate.LeaveGame
            let! cleared = read fixture.Player
            equal { fresh with CharacterName = ValueNone; CharacterGeneration = fresh.CharacterGeneration + 1UL } (ok cleared)
            equal 0 fixture.Host.Reader.Count
            do! post fixture.Player PlayerSessionMessage.Stop
            do! finish fixture
        }))

    case "movement samples need an accepted context and never produce acknowledgements" (fun () ->
        withPlayer options (fun fixture -> task {
            let! _, _, _ = ready fixture
            let name = CharacterName.create 128 "Nerevar" |> ok
            let form = FormKey.create (PluginName.create 255 "Skyrim.esm" |> ok) (LocalFormId.create 0x3Cu |> ok)
            let location = PlayerLocation.create (Location.create form (LocationName.create 128 "Whiterun" |> ok)) Position.zero Rotation.zero
            let pose = { Position = Position.create 10.0f 20.0f 30.0f |> ok; Rotation = Rotation.zero; SampledAtUs = 0UL }
            let sample context sequence = PlayerSessionMessage.SampleMovement { ContextRevision = context; Sequence = sequence; Pose = pose }
            do! post fixture.Player (sample 1UL 1UL)
            let! _ = read fixture.Player
            equal 0 fixture.Host.Reader.Count
            equal 0 fixture.Presence.Reader.Count
            let! _ = applyUpdate fixture 2UL (PlayerUpdate.BeginCharacter name)
            let! _ = applyUpdate fixture 3UL (PlayerUpdate.SetLocation(1UL, ValueSome location))
            do! post fixture.Player (sample 2UL 10UL) // Realtime overtook its reliable transition.
            do! post fixture.Player (sample 1UL 1UL)
            let! updated = receive fixture.Presence
            match updated with
            | PresenceCommand.Update(_, current) ->
                equal 1UL current.MovementSequence
                equal pose.Position current.Location.Value.Position
            | other -> failwithf "%A" other
            do! post fixture.Player (sample 1UL 1UL)
            do! post fixture.Player (sample 1UL 0UL)
            let! current = read fixture.Player
            equal 1UL (ok current).MovementSequence
            equal 0 fixture.Host.Reader.Count
            equal 0 fixture.Presence.Reader.Count
            let! _ = applyUpdate fixture 4UL (PlayerUpdate.BeginCharacter name)
            do! rejectUpdate fixture 5UL (PlayerUpdate.SetLocation(1UL, ValueSome location))
            do! post fixture.Player (sample 1UL 2UL)
            let! current = read fixture.Player
            equal ValueNone (ok current).Location
            let! _ = applyUpdate fixture 6UL (PlayerUpdate.SetLocation(2UL, ValueSome location))
            do! post fixture.Player (sample 1UL 99UL)
            let! current = read fixture.Player
            equal 0UL (ok current).MovementSequence
            equal 0 fixture.Host.Reader.Count
            equal 0 fixture.Presence.Reader.Count
        }))

    case "duplicate pending ID on another channel closes without settling the original as a refusal" (fun () ->
        withPlayer options (fun fixture -> task {
            let! _, _, _ = ready fixture
            let text = ChatMessageText.create 2000 "hello" |> ok
            do! post fixture.Player (PlayerSessionMessage.SendChat(2UL, globalId, text))
            let! published = receive fixture.Chat
            match published with
            | ChatRoomCommand.Publish value -> equal 2UL value.RequestId
            | other -> failwithf "%A" other
            let otherChannel = ChatChannelId.create 99UL |> ok
            do! post fixture.Player (PlayerSessionMessage.SendChat(2UL, otherChannel, text))
            let! command = receive fixture.Host
            match command with
            | SessionHostCommand.Close(id, _) -> equal fixture.Request.ConnectionId id
            | other -> failwithf "Duplicate must not create another correlated response: %A" other
            do! finish fixture
            equal 0 fixture.Host.Reader.Count
        }))

    case "duplicate Begin cannot issue a second authentication request" (fun () ->
        withPlayer options (fun fixture -> task {
            let! _ = receive fixture.Authentication
            do! post fixture.Player PlayerSessionMessage.Begin
            let! state = read fixture.Player
            equal (Error PlayerStateError.NotReady) state
            equal 0 fixture.Authentication.Reader.Count
        }))

    case "invalid or unavailable authentication rejects opening before any membership" (fun () -> task {
        for failure, expectedCode in [
            SessionAuthenticationError.InvalidTicket, RequestRejectionCode.AuthenticationFailed
            SessionAuthenticationError.Unavailable, RequestRejectionCode.Overloaded
        ] do
            do! withPlayer options (fun fixture -> task {
                let! query = receive fixture.Authentication
                do! deliver query.ReplyTo { OperationId = query.OperationId; Result = Error failure }
                let! rejected = receive fixture.Host
                match rejected with
                | SessionHostCommand.Send(connectionId, ServerResponse.RequestRejected(requestId, rejection)) ->
                    equal fixture.Request.ConnectionId connectionId
                    equal fixture.Request.RequestId requestId
                    equal expectedCode rejection.Code
                    check (not (rejection.Message.Contains fixture.Request.SessionTicket)) "Credential leaked in rejection."
                | other -> failwithf "Expected authentication rejection: %A" other
                let! command = receive fixture.Host
                match command with
                | SessionHostCommand.Close(connectionId, _) -> equal fixture.Request.ConnectionId connectionId
                | other -> failwithf "Expected closing after rejection: %A" other
                let! _ = terminal fixture.Player.Completion
                equal 0 fixture.Chat.Reader.Count
                equal 0 fixture.Presence.Reader.Count
            })
    })

    case "a closed authentication destination cannot leave opening waiting forever" (fun () -> task {
        let queries, chatCommands, presenceCommands, hostCommands =
            Channel.CreateUnbounded<SessionAuthenticationRequest>(), Channel.CreateUnbounded<ChatRoomCommand>(),
            Channel.CreateUnbounded<PresenceCommand>(), Channel.CreateUnbounded<SessionHostCommand>()
        use authentication = Agent.Start(AgentOptions.create "closed-authentication", collect queries)
        authentication.Complete() |> ignore
        do! awaitUnit authentication.Completion
        use chat = Agent.Start(AgentOptions.create "chat", collect chatCommands)
        use presence = Agent.Start(AgentOptions.create "presence", collect presenceCommands)
        use host = Agent.Start(AgentOptions.create "host", collect hostCommands)
        let request = {
            ConnectionId = Guid.NewGuid(); RequestId = 1UL
            SessionTicket = String('b', 43); Hiding = HiddenIdentity.Shown
        }
        use marks = Agent.Start(AgentOptions.create "marks", collect (Channel.CreateUnbounded<GroundMarkCommand>()))
        use player = PlayerSession.start options 64 Moderation.empty AnnouncementOptions.defaults (GroundMarkOptions.rules GroundMarkOptions.defaults |> ok)
                         IdentityOptions.defaults (authentication.Ref.TryReliable().Value) (chat.Ref.TryReliable().Value) (chat.Ref.TryReliable().Value)
                         (presence.Ref.TryReliable().Value) (marks.Ref.TryReliable().Value) (host.Ref.TryReliable().Value) request |> ok
        let! failure = terminal player.Completion
        check failure.IsSome "Closed dependency should terminate this session observably."
        equal 0 chatCommands.Reader.Count
        equal 0 presenceCommands.Reader.Count
    })

    case "telemetry validates character state and bounded full replacement without partial mutation" (fun () ->
        withPlayer options (fun fixture -> task {
            let! profile, _, presence = ready fixture
            let name = CharacterName.create 128 "Nerevar" |> ok
            let key = ActorValueKey.create 128 "skyrim:health" |> ok
            let health = ActorValueInfo.create (ActorValueName.create 64 "Health" |> ok) (ActorValueState.resource 20.0f 100.0f |> ok)
            do! rejectUpdate fixture 2UL (PlayerUpdate.SetActorValues( Map.ofList [(key, health)]))
            do! rejectUpdate fixture 3UL (PlayerUpdate.RenameCharacter name)
            let! beginning = applyUpdate fixture 4UL (PlayerUpdate.BeginCharacter name)
            let oversized = [for index in 0 .. 64 -> (ActorValueKey.create 128 $"test:value{index}" |> ok), health] |> Map.ofList
            do! rejectUpdate fixture 6UL (PlayerUpdate.SetActorValues( oversized))
            let! afterRejected = read fixture.Player
            equal beginning (ok afterRejected)

            let! populated = applyUpdate fixture 7UL (PlayerUpdate.SetActorValues( Map.ofList [(key, health)]))
            equal 1 populated.ActorValues.Count
            let! cleared = applyUpdate fixture 8UL (PlayerUpdate.SetActorValues( Map.empty))
            equal Map.empty cleared.ActorValues
            equal (PublicIdentity.Profile profile) cleared.Identity
            equal 0 fixture.Host.Reader.Count
            // Only the source's publication updates the author's outbound state.
            do! deliver presence.Events (PresenceEvent.Updated cleared)
            let! replicated = receive fixture.Host
            equal (SessionHostCommand.Send(fixture.Request.ConnectionId, ServerResponse.PlayerUpdated cleared)) replicated
        }))

    case "telemetry request ID cannot settle another pending chat command" (fun () ->
        withPlayer options (fun fixture -> task {
            let! _, _, _ = ready fixture
            let text = ChatMessageText.create 2000 "pending" |> ok
            do! post fixture.Player (PlayerSessionMessage.SendChat(2UL, globalId, text))
            let! _ = receive fixture.Chat
            do! post fixture.Player (PlayerSessionMessage.Update(2UL, PlayerUpdate.LeaveGame))
            let! closed = receive fixture.Host
            match closed with
            | SessionHostCommand.Close(id, _) -> equal fixture.Request.ConnectionId id
            | other -> failwithf "Expected collision close, no correlated refusal: %A" other
            do! finish fixture
        }))

    case "menu details are valid before character while race and level require one and reset with it" (fun () ->
        withPlayer options (fun fixture -> task {
            let! _, _, _ = ready fixture
            let activity = PlayerActivity.create 256 64 ActivityKind.Menu ValueNone LockDifficulty.Unknown (ValueSome "main_menu") |> ok
            let menu = PlayerDetails.create ValueNone ValueNone activity ValueNone (ValueSome DateTimeOffset.UnixEpoch)
            let leveled = PlayerDetails.create ValueNone (ValueSome 10u) activity ValueNone ValueNone
            let! initial = applyUpdate fixture 2UL (PlayerUpdate.SetDetails menu)
            equal ValueNone initial.CharacterName
            equal menu initial.Details
            do! rejectUpdate fixture 3UL (PlayerUpdate.SetDetails leveled)
            let name = CharacterName.create 128 "Nerevar" |> ok
            let! beginning = applyUpdate fixture 4UL (PlayerUpdate.BeginCharacter name)
            equal PlayerDetails.empty beginning.Details
            let! playing = applyUpdate fixture 5UL (PlayerUpdate.SetDetails leveled)
            equal leveled playing.Details
            let! leaving = applyUpdate fixture 6UL PlayerUpdate.LeaveGame
            equal PlayerDetails.empty leaving.Details
            equal ValueNone leaving.CharacterName
        }))

    case "a full presence outbox refuses new updates before changing session state" (fun () -> task {
        let entered, release = gate<unit>(), gate<unit>()
        let createPresence commands =
            let handle (context: AgentContext<PresenceCommand>) command = task {
                do! collect commands context command
                match command with
                | PresenceCommand.Update _ ->
                    entered.TrySetResult() |> ignore
                    do! release.Task.WaitAsync context.CancellationToken
                | PresenceCommand.Join _ | PresenceCommand.Detach _ | PresenceCommand.Flush _ -> ()
            }
            Agent.Start({ AgentOptions.create "blocked-presence" with Mailbox = AgentMailbox.boundedWait 1 }, handle)
        do! withPlayerUsingPresence { options with MaxPendingUpdates = 1 } createPresence (fun fixture -> task {
            let! _, _, _ = ready fixture
            let initial = CharacterName.create 128 "Original" |> ok
            let! _ = applyUpdate fixture 2UL (PlayerUpdate.BeginCharacter initial)
            do! awaitResult entered.Task
            let mutable expected = initial
            let mutable refused = 0
            for requestId in 3UL .. 12UL do
                let name = CharacterName.create 128 $"Character {requestId}" |> ok
                do! post fixture.Player (PlayerSessionMessage.Update(requestId, PlayerUpdate.RenameCharacter name))
                let! answer = receive fixture.Host
                match answer with
                | SessionHostCommand.Send(_, ServerResponse.PlayerUpdateAccepted id) ->
                    equal requestId id
                    expected <- name
                | SessionHostCommand.Send(_, ServerResponse.RequestRejected(id, rejection)) ->
                    equal requestId id
                    equal RequestRejectionCode.Overloaded rejection.Code
                    refused <- refused + 1
                | other -> failwithf "Unexpected admission result: %A" other
            check (refused > 0) "Blocked source accepted unlimited updates."
            let! current = read fixture.Player
            equal (ValueSome expected) (ok current).CharacterName
            release.SetResult()
        })
    })

] @ identityTests)
