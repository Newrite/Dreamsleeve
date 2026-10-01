module Dreamsleeve.Server.Tests.CodecTests

open System
open Expecto
open Google.Protobuf
open Dreamsleeve.Server.Domain
open Dreamsleeve.Server.Core

let private movementBatch items =
    items |> List.map (fun (id, location) ->
        let pose = match location with ValueSome value -> MovementPose.ofLocation value | ValueNone -> { Position = Position.zero; Rotation = Rotation.zero; SampledAtUs = 0UL }
        ({ PlayerId = id; ViewRevision = 1UL; Sequence = 0UL; Pose = pose }: Dreamsleeve.Server.Domain.MovementChange)) |> List.toArray

let private config = ServerConfig.defaults

let private ok = function
    | Ok value -> value
    | Error error -> failtestf "Expected success, got %A" error

let private error = function
    | Error value -> value
    | Ok _ -> failtest "Expected error"

let private codec = ProtocolCodec.create config
let private configured settings = ProtocolCodec.create settings

let private pid raw = PlayerId.create raw |> ok
let private channel = ChatChannelId.create 1UL |> ok
let private profile = PlayerData.create (pid 7UL) (Username.create 32 "player" |> ok) (DisplayName.create 64 "Игрок" |> ok)
let private snapshot = Player.create profile |> Player.snapshot
let private healthKey = ActorValueKey.create 128 "skyrim:health" |> ok
let private healthName = ActorValueName.create 64 "Health" |> ok
let private health current maximum = ActorValueInfo.create healthName (ActorValueState.resource current maximum)
let private whiterun =
    PlayerLocation.create
        (Location.create (FormKey.create (PluginName.create 255 "Skyrim.esm" |> ok) (LocalFormId.create 0x3Cu |> ok)) (LocationName.create 128 "Whiterun" |> ok))
        (Position.create 1.0f 2.0f 3.0f |> ok) Rotation.zero

/// Numbers the readings of these players from one, as presence does for a recipient that knows no kind.
let private kindsOf (players: PlayerSnapshot list) : ActorValueKinds =
    let defined =
        players
        |> List.collect (fun player -> [ for KeyValue(key, info) in player.ActorValues -> key, info.DisplayName ])
        |> List.distinct
        |> List.mapi (fun index (key, name) -> ({ Id = uint64 index + 1UL; Key = key; DisplayName = name }: ActorValueKind))
    { Ids = defined |> List.map (fun kind -> struct (kind.Key, kind.DisplayName), kind.Id) |> Map.ofList; Defined = defined }

let private joined player = ServerResponse.PresenceChanged({ PresenceChange.empty with Joined = [ player ] }, kindsOf [ player ])
let private updated player = ServerResponse.PresenceChanged({ PresenceChange.empty with Updated = [ player ] }, kindsOf [ player ])
let private message =
    ChatMessage.create (ChatMessageId.create UInt64.MaxValue |> ok) channel (PublicIdentity.Profile profile) ValueNone
        (ChatMessageText.create 2000 "Привет\nworld" |> ok) (DateTimeOffset.FromUnixTimeMilliseconds(-1L))
let private parseMovement bytes = Dreamsleeve.Protocol.Chat.ServerMovementPacket.Parser.ParseFrom(bytes: byte array)
let private parse bytes = Dreamsleeve.Protocol.Chat.ServerPacket.Parser.ParseFrom(bytes: byte array)
let private send requestId text =
    Dreamsleeve.Protocol.Chat.ClientPacket(
        ProtocolVersion = ProtocolCodec.Version, RequestId = requestId,
        SendChat = Dreamsleeve.Protocol.Chat.SendChat(ChannelId = 1UL, Text = text))
let private decode (packet: Dreamsleeve.Protocol.Chat.ClientPacket) =
    ProtocolCodec.decodeClient codec (packet.ToByteArray())
let private systemChannel = { ChannelId = ChatChannelKind.channelId ChatChannelKind.System; Kind = ChatChannelKind.System; Messages = [] }
let private welcomeWith messages = {
    SelfPlayerId = pid 7UL
    Players = [snapshot]
    Kinds = ActorValueKinds.none
    Channels = [ { ChannelId = channel; Kind = ChatChannelKind.Global; Messages = messages }; systemChannel ]
    AnnouncementSources = [ClientAnnouncementSource.ThirdParty]
    OwnPseudonym = ValueNone
    Hiding = HiddenIdentity.Shown
    Mute = ValueNone
    Role = PlayerRole.Player
}
let private welcome = welcomeWith [message]

let private updatePacket action =
    Dreamsleeve.Protocol.Chat.ClientPacket(
        ProtocolVersion = ProtocolCodec.Version, RequestId = 91UL, UpdatePlayer = action)

let private update action = updatePacket action |> decode

let private locationPacket place =
    Dreamsleeve.Protocol.Chat.UpdatePlayer(SetLocation = Dreamsleeve.Protocol.Chat.SetPlayerLocation(ContextRevision = 1UL, Location = place))

let private valuesPacket entries =
    let values = Dreamsleeve.Protocol.Chat.ActorValues()
    values.Values.AddRange(entries: Dreamsleeve.Protocol.Chat.ActorValueEntry list)
    Dreamsleeve.Protocol.Chat.UpdatePlayer(SetActorValues = values)

let private wireLocation () =
    Dreamsleeve.Protocol.Chat.PlayerLocation(
        Location = Dreamsleeve.Protocol.Chat.Location(
            LocationId = Dreamsleeve.Protocol.Chat.FormKey(PluginName = "Skyrim.ESM", LocalFormId = 0x3Cu),
            LocationName = "Тамриэль"),
        Position = Dreamsleeve.Protocol.Chat.Position(), Rotation = Dreamsleeve.Protocol.Chat.Rotation())

let private scalarEntry key scalar =
    Dreamsleeve.Protocol.Chat.ActorValueEntry(Key = key, DisplayName = "", Scalar = scalar)

let private playerUpdate result =
    match (result |> ok).Command with
    | ClientCommand.UpdatePlayer value -> value
    | ClientCommand.OpenSession _ | ClientCommand.JoinAsGuest | ClientCommand.SendChat _ | ClientCommand.PostAnnouncement _
    | ClientCommand.PlaceGroundNote _ | ClientCommand.ReportDeath _ | ClientCommand.RemoveGroundMark _
    | ClientCommand.SetIdentityVisibility _ | ClientCommand.ChangeDisplayName _ | ClientCommand.SanctionPlayer _ | ClientCommand.LiftSanction _ | ClientCommand.KickPlayer _ | ClientCommand.ListSanctions
    | ClientCommand.ListPlayerMarks _ | ClientCommand.ClearPlayerMarks _ | ClientCommand.DeleteChatMessage _ -> failtest "Expected player update"

let private apply update = Player.create profile |> Player.applyUpdate update |> Player.snapshot

let tests = testList "Dreamsleeve.Server.Codec" [
    testCase "movement batches split exactly within configured packet limits" <| fun _ ->
        let movements = movementBatch [for id in 1UL .. 130UL -> pid id, ValueNone]
        let packet = Packets.single codec (ServerResponse.PlayersMoved movements) |> ok
        for limit in [32; 127; 128; packet.Length - 1; packet.Length] do
            let small = ProtocolCodec.create { ServerConfig.defaults with MaxPacketBytes = limit }
            let packets = ProtocolCodec.encode small Int32.MaxValue (ServerResponse.PlayersMoved movements) |> ok
            let decoded = packets |> List.collect (fun bytes ->
                Expect.isLessThanOrEqual bytes.Length limit "Application limit includes the whole envelope."
                let value = parseMovement bytes
                value.Movements.Players |> Seq.map _.PlayerId |> List.ofSeq)
            Expect.equal decoded [1UL .. 130UL] "Every entry exactly once and in order."
        let tiny = ProtocolCodec.create { ServerConfig.defaults with MaxPacketBytes = 1 }
        Expect.isError (ProtocolCodec.encode tiny Int32.MaxValue (ServerResponse.PlayersMoved movements)) "An unsplittable entry fails before any send."
        Expect.isError (Packets.single codec (ServerResponse.PlayersMoved (movementBatch []))) "Empty batch is invalid."

    testCase "default batching splits realtime below the transport fragmentation threshold" <| fun _ ->
        let movements = movementBatch [for id in 1UL .. 200UL -> pid id, ValueNone]
        let expected = Packets.single codec (ServerResponse.PlayersMoved movements) |> ok
        let packets = ProtocolCodec.encode codec 548 (ServerResponse.PlayersMoved movements) |> ok
        Expect.isGreaterThan expected.Length 548 "Fixture crosses the transport fragmentation threshold."
        Expect.isGreaterThan packets.Length 1 "Realtime splits at MTU even without an explicit target."
        for bytes in packets do Expect.isLessThanOrEqual bytes.Length 548 "No reliable fragmentation fallback."

    testCase "movement target is clamped by negotiated transport and application budgets" <| fun _ ->
        let movements = movementBatch [for id in 1UL .. 130UL -> pid id, ValueNone]
        for target, transport, application in [64, 128, 1024; 128, 64, 1024; 128, 1024, 64] do
            let configured = configured { config with MovementPacketTargetBytes = target; MaxPacketBytes = application }
            let packets = ProtocolCodec.encode configured transport (ServerResponse.PlayersMoved movements) |> ok
            let budget = min target (min transport application)
            let ids = packets |> List.collect (fun bytes ->
                Expect.isLessThanOrEqual bytes.Length budget "Whole protobuf envelope fits."
                (parseMovement bytes).Movements.Players |> Seq.map _.PlayerId |> Seq.toList)
            Expect.isGreaterThan packets.Length 1 "Fixture requires splitting."
            Expect.equal ids [1UL .. 130UL] "Splitting preserves every entry and order."
        Expect.isError (ProtocolCodec.encode codec 0 (ServerResponse.PlayersMoved movements)) "Unavailable peer cannot encode movement."

    testCase "indivisible realtime never falls back to fragmentation" <| fun _ ->
        let single = movementBatch [pid UInt64.MaxValue, ValueNone]
        let size = (Packets.single codec (ServerResponse.PlayersMoved single) |> ok).Length
        Expect.isOk (ProtocolCodec.encode codec size (ServerResponse.PlayersMoved single)) "Exact MTU boundary fits."
        Expect.isError (ProtocolCodec.encode codec (size - 1) (ServerResponse.PlayersMoved single)) "Oversized entry rejects the entire batch."
        let strict = configured { config with MaxPacketBytes = size - 1 }
        Expect.isError (ProtocolCodec.encode strict 548 (ServerResponse.PlayersMoved single)) "Application cap also applies."

    testCase "source movement timestamp survives domain and every replication shape" <| fun _ ->
        for stamp in [0UL; 123456789UL; UInt64.MaxValue] do
            let wire = wireLocation()
            wire.SampledAtUs <- stamp
            let state = locationPacket wire |> update |> playerUpdate |> apply
            let location = state.Location |> ValueOption.get
            Expect.equal location.SampledAtUs stamp "No clock conversion on the server."

            let moved = Packets.single codec (ServerResponse.PlayersMoved (movementBatch [pid 7UL, state.Location])) |> ok |> parseMovement
            Expect.equal moved.Movements.Players[0].Pose.SampledAtUs stamp "Compact movement retains time."
            let full = Packets.single codec (joined state) |> ok |> parse
            Expect.equal full.PresenceChanged.Joined[0].Location.SampledAtUs stamp "Snapshots retain the same measurement."
            let baseline: VisibilityChange = { PlayerId = pid 7UL; ViewRevision = 1UL; Sequence = 0UL; Pose = ValueSome (MovementPose.ofLocation location) }
            let change = { PresenceChange.empty with Space = ValueSome location.Location; Visibility = [ baseline ] }
            let visible = Packets.single codec (ServerResponse.PresenceChanged(change, ActorValueKinds.none)) |> ok |> parse
            Expect.equal visible.PresenceChanged.Visibility[0].Pose.SampledAtUs stamp "A visibility baseline retains it too."

    testCase "metadata patches carry removed kinds, set readings, replaced details and cleared components" <| fun _ ->
        let stamina = ActorValueKey.create 128 "skyrim:stamina" |> ok
        let staminaName = ActorValueName.create 64 "Stamina" |> ok
        let kinds: ActorValueKinds = {
            Ids = Map.ofList [ struct (healthKey, healthName), 4UL; struct (stamina, staminaName), 9UL ]
            Defined = [ { Id = 9UL; Key = stamina; DisplayName = staminaName } ]
        }
        let activity = PlayerActivity.create 256 64 ActivityKind.Combat (ValueSome "Mudcrab") LockDifficulty.Unknown ValueNone |> ok
        let values: ActorValuesPatch = {
            Removed = [ struct (healthKey, healthName) ]
            Set = [ stamina, ActorValueInfo.create staminaName (ActorValueState.resource -3 120) ]
        }
        let details: DetailsPatch = {
            Race = ValueSome ValueNone; Level = ValueSome (ValueSome 12u); Activity = ValueSome activity
            Place = ValueNone; GameStartedAt = ValueSome ValueNone
        }
        let patch: MetadataPatch = { PlayerId = pid 7UL; ActorValues = ValueSome values; Details = ValueSome details }
        let encode patch kinds =
            let packet = Packets.single codec (ServerResponse.PresenceChanged({ PresenceChange.empty with Metadata = [ patch ] }, kinds)) |> ok |> parse
            packet.PresenceChanged
        let packet = encode patch kinds
        let kind = packet.ActorValueKinds |> Seq.exactlyOne
        Expect.equal (kind.Id, kind.Key, kind.DisplayName) (9UL, "skyrim:stamina", "Stamina") "the new kind is defined with its first use"
        let wire = packet.Metadata |> Seq.exactlyOne
        Expect.equal wire.PlayerId 7UL "patched player"
        Expect.sequenceEqual wire.RemovedActorValues [ 4UL ] "removed by number"
        let set = wire.ActorValues |> Seq.exactlyOne
        Expect.equal (set.Kind, set.Resource.Current, set.Resource.Maximum) (9UL, -3, 120) "set by number in whole points"
        Expect.equal wire.Details.Level 12u "replaced level"
        Expect.equal wire.Details.Activity.TargetName "Mudcrab" "replaced activity"
        Expect.isNull wire.Details.Race "a cleared component is not sent"
        Expect.isNull wire.Details.Place "an unchanged component is not sent"
        Expect.sequenceEqual wire.ClearedDetails
            [ Dreamsleeve.Protocol.Chat.PlayerDetailsField.Race; Dreamsleeve.Protocol.Chat.PlayerDetailsField.GameStartedAt ] "cleared components are listed"

        let valuesOnly = encode { patch with Details = ValueNone } kinds
        Expect.isNull valuesOnly.Metadata[0].Details "absent details are unchanged"
        Expect.isEmpty valuesOnly.Metadata[0].ClearedDetails "and nothing is cleared"
        let clearing = { details with Race = ValueNone; Level = ValueSome ValueNone; Activity = ValueNone; GameStartedAt = ValueNone }
        let clearOnly = encode { patch with ActorValues = ValueNone; Details = ValueSome clearing } ActorValueKinds.none
        Expect.isNull clearOnly.Metadata[0].Details "a pure clearing sends no details"
        Expect.sequenceEqual clearOnly.Metadata[0].ClearedDetails [ Dreamsleeve.Protocol.Chat.PlayerDetailsField.Level ] "only the level clears"
        Expect.isEmpty clearOnly.Metadata[0].RemovedActorValues "absent values are unchanged"
        Expect.isEmpty clearOnly.Metadata[0].ActorValues "absent values set nothing"

    testCase "a presence change defines new kinds and carries every part in its own field" <| fun _ ->
        let other = PlayerData.create (pid 8UL) profile.Username (DisplayName.create 64 "Other" |> ok) |> Player.create |> Player.snapshot
        let entering = { other with ActorValues = Map.ofList [ healthKey, health 75 100 ] }
        let renamed = { snapshot with ActorValues = Map.ofList [ healthKey, health 30 100 ] }
        let activity = PlayerActivity.create 256 64 ActivityKind.Sneaking ValueNone LockDifficulty.Unknown ValueNone |> ok
        let details: DetailsPatch = { Race = ValueNone; Level = ValueNone; Activity = ValueSome activity; Place = ValueNone; GameStartedAt = ValueNone }
        let change: PresenceChange = {
            Joined = [ entering ]
            Updated = [ renamed ]
            Metadata = [ { PlayerId = pid 9UL; ActorValues = ValueNone; Details = ValueSome details } ]
            Space = ValueSome whiterun.Location
            Visibility = [
                { PlayerId = pid 8UL; ViewRevision = 3UL; Sequence = 5UL; Pose = ValueSome (MovementPose.ofLocation whiterun) }
                { PlayerId = pid 10UL; ViewRevision = 4UL; Sequence = 0UL; Pose = ValueNone }
            ]
            Left = [ pid 11UL ]
        }
        let encoded = Packets.single codec (ServerResponse.PresenceChanged(change, kindsOf [ entering; renamed ])) |> ok |> parse
        Expect.isFalse encoded.HasRequestId "a presence change is a notification"
        let packet = encoded.PresenceChanged
        Expect.equal (packet.ActorValueKinds |> Seq.map (fun kind -> kind.Id, kind.Key, kind.DisplayName) |> List.ofSeq)
            [ 1UL, "skyrim:health", "Health" ] "one kind for both players' readings"
        let reading (player: Dreamsleeve.Protocol.Chat.PlayerInfo) =
            let value = player.ActorValues |> Seq.exactlyOne
            player.Profile.PlayerId, value.Kind, value.Resource.Current
        Expect.equal (reading packet.Joined[0]) (8UL, 1UL, 75) "joined player with numbered readings"
        Expect.equal (reading packet.Updated[0]) (7UL, 1UL, 30) "updated player with numbered readings"
        Expect.equal (packet.Metadata[0].PlayerId, packet.Metadata[0].Details.Activity.Kind)
            (9UL, Dreamsleeve.Protocol.Chat.ActivityKind.Sneaking) "metadata patch"
        Expect.equal (packet.Space.LocationId.LocalFormId, packet.Space.LocationName) (0x3Cu, "Whiterun") "the recipient's place"
        let baseline, clear = packet.Visibility[0], packet.Visibility[1]
        Expect.equal (baseline.PlayerId, baseline.ViewRevision, baseline.Sequence, baseline.Pose.Position.X) (8UL, 3UL, 5UL, 1.0f) "a baseline carries a pose"
        Expect.equal (clear.PlayerId, clear.ViewRevision) (10UL, 4UL) "a clear advances the view"
        Expect.isNull clear.Pose "a clear has no pose"
        Expect.sequenceEqual packet.Left [ 11UL ] "left players"

    testCase "presence changes refuse unnumbered readings, empty parts and a place that disagrees with the poses" <| fun _ ->
        let refused (change: PresenceChange) kinds message =
            Expect.equal (Packets.single codec (ServerResponse.PresenceChanged(change, kinds)) |> error).Failure
                (ProtocolCodecFailure.InvalidPayload "presence_changed") message
        let healthy = { snapshot with ActorValues = Map.ofList [ healthKey, health 50 100 ] }
        let kinds = kindsOf [ healthy ]
        let pose = MovementPose.ofLocation whiterun
        let view revision pose : VisibilityChange = { PlayerId = pid 8UL; ViewRevision = revision; Sequence = 0UL; Pose = pose }
        let values patch : MetadataPatch = { PlayerId = pid 7UL; ActorValues = ValueSome patch; Details = ValueNone }
        refused PresenceChange.empty ActorValueKinds.none "an empty change is not sent"
        refused { PresenceChange.empty with Joined = [ healthy ] } ActorValueKinds.none "a joined reading needs a number"
        refused { PresenceChange.empty with Updated = [ healthy ] } ActorValueKinds.none "an updated reading needs a number"
        refused { PresenceChange.empty with Metadata = [ values { Removed = []; Set = [ healthKey, health 50 100 ] } ] } ActorValueKinds.none "a set reading needs a number"
        refused { PresenceChange.empty with Metadata = [ values { Removed = [ struct (healthKey, healthName) ]; Set = [] } ] } ActorValueKinds.none "a removed kind needs a number"
        refused { PresenceChange.empty with Metadata = [ { PlayerId = pid 7UL; ActorValues = ValueNone; Details = ValueNone } ] } ActorValueKinds.none "a patch changes something"
        refused { PresenceChange.empty with Visibility = [ view 1UL (ValueSome pose) ] } ActorValueKinds.none "a baseline needs the place"
        refused { PresenceChange.empty with Space = ValueSome whiterun.Location; Visibility = [ view 1UL ValueNone ] } ActorValueKinds.none "clears alone have no place"
        refused { PresenceChange.empty with Space = ValueSome whiterun.Location; Left = [ pid 8UL ] } ActorValueKinds.none "a place without any visibility"
        refused { PresenceChange.empty with Visibility = [ view 0UL ValueNone ] } ActorValueKinds.none "a view always has a revision"
        refused { PresenceChange.empty with Joined = [ healthy ] } { kinds with Defined = [ { kinds.Defined.Head with Id = 0UL } ] } "a defined kind is never zero"
        Expect.isOk (Packets.single codec (ServerResponse.PresenceChanged({ PresenceChange.empty with Joined = [ healthy ] }, kinds))) "numbered readings pass"
        Expect.isOk (Packets.single codec (ServerResponse.PresenceChanged({ PresenceChange.empty with Joined = [ healthy ] }, { kinds with Defined = [] })))
            "a kind the recipient was told earlier needs no definition"
        let visible = { PresenceChange.empty with Space = ValueSome whiterun.Location; Visibility = [ view 1UL (ValueSome pose); view 2UL ValueNone ] }
        Expect.isOk (Packets.single codec (ServerResponse.PresenceChanged(visible, ActorValueKinds.none))) "a place with a pose passes next to a clear"


    testCase "open session carries only an opaque ticket without changing it" <| fun _ ->
        let ticket = String('a', 41) + "-_"
        let packet = Dreamsleeve.Protocol.Chat.ClientPacket(
            ProtocolVersion = ProtocolCodec.Version, RequestId = 42UL,
            OpenSession = Dreamsleeve.Protocol.Chat.OpenSession(SessionTicket = ticket))
        let result = decode packet |> ok
        Expect.equal result.RequestId 42UL "request correlation"
        match result.Command with
        | ClientCommand.OpenSession(actual, hidden) ->
            Expect.equal actual ticket "credential preserved exactly"
            Expect.equal hidden HiddenIdentity.Shown "names are shown unless the client asks otherwise"
        | ClientCommand.JoinAsGuest | ClientCommand.SendChat _ | ClientCommand.UpdatePlayer _ | ClientCommand.PostAnnouncement _
        | ClientCommand.PlaceGroundNote _ | ClientCommand.ReportDeath _ | ClientCommand.RemoveGroundMark _
        | ClientCommand.SetIdentityVisibility _ | ClientCommand.ChangeDisplayName _ | ClientCommand.SanctionPlayer _ | ClientCommand.LiftSanction _
        | ClientCommand.KickPlayer _ | ClientCommand.ListSanctions | ClientCommand.ListPlayerMarks _ | ClientCommand.ClearPlayerMarks _
        | ClientCommand.DeleteChatMessage _ -> failtest "Wrong command"

        for invalid in [ ""; String('a', 42); String('a', 44); String('a', 42) + " "; String('a', 42) + "é" ] do
            packet.OpenSession.SessionTicket <- invalid
            Expect.equal (decode packet |> error).Failure (ProtocolCodecFailure.InvalidPayload "session_ticket") "bounded base64url ticket"

    testCase "a guest joins with an empty payload on the control lane" <| fun _ ->
        let packet = Dreamsleeve.Protocol.Chat.ClientPacket(
            ProtocolVersion = ProtocolCodec.Version, RequestId = 7UL, JoinAsGuest = Dreamsleeve.Protocol.Chat.JoinAsGuest())
        let result = decode packet |> ok
        Expect.equal result.Command ClientCommand.JoinAsGuest "guest command"
        Expect.equal (ProtocolCodec.requestLane result) DeliveryLane.Control "control lane"

    testCase "a mute and the end of a session reach the player with the reason and when they end" <| fun _ ->
        let now = DateTimeOffset.FromUnixTimeMilliseconds 1_000_000L
        let issue id kind term =
            Sanction.issue (SanctionId.create id |> ok) now
                { Target = pid 7UL; Kind = kind; Term = term; Reason = SanctionReason.create " Флуд " |> ok
                  IssuedBy = SanctionIssuer.Admin(AdminId.create 1L |> ok) }
        let packet response = Packets.single codec response |> ok |> Dreamsleeve.Protocol.Chat.ServerPacket.Parser.ParseFrom
        let mute = issue 1L SanctionKind.Mute (SanctionTerm.For(TimeSpan.FromMinutes 15.))
        let muted = (packet (ServerResponse.MuteChanged(ValueSome mute))).MuteChanged.Mute
        Expect.equal muted.Reason "Флуд" "the trimmed reason"
        Expect.equal muted.UntilUnixMs 1_900_000L "fifteen minutes later"
        Expect.isNull (packet (ServerResponse.MuteChanged ValueNone)).MuteChanged.Mute "a lift carries no mute"
        let welcome = (packet (ServerResponse.SessionOpened(1UL, { welcomeWith [] with Mute = ValueSome mute }))).SessionOpened
        Expect.equal welcome.Mute.Reason "Флуд" "the welcome carries the mute"
        Expect.isNull (packet (ServerResponse.SessionOpened(1UL, welcomeWith []))).SessionOpened.Mute "not muted"
        let banned = (packet (ServerResponse.SessionEnded(SessionEnd.Banned(issue 2L SanctionKind.Ban SanctionTerm.UntilLifted)))).SessionEnded
        Expect.equal banned.Reason Dreamsleeve.Protocol.Chat.SessionEndReason.Banned "banned"
        Expect.isFalse banned.HasUntilUnixMs "until lifted"
        let kicked = (packet (ServerResponse.SessionEnded(SessionEnd.Kicked(SanctionReason.create "Остынь" |> ok)))).SessionEnded
        Expect.equal (kicked.Reason, kicked.Text) (Dreamsleeve.Protocol.Chat.SessionEndReason.Kicked, "Остынь") "kicked with the reason"
        let revoked = (packet (ServerResponse.SessionEnded SessionEnd.AccessRevoked)).SessionEnded
        Expect.equal (revoked.Reason, revoked.Text) (Dreamsleeve.Protocol.Chat.SessionEndReason.AccessRevoked, "") "revoked without words"

    testCase "send chat preserves text and full uint64 request IDs" <| fun _ ->
        let result = send UInt64.MaxValue "Привет\nworld" |> decode |> ok
        Expect.equal result.RequestId UInt64.MaxValue "no signed narrowing"
        match result.Command with
        | ClientCommand.SendChat(channelId, text) ->
            Expect.equal channelId channel "channel"
            Expect.equal (ChatMessageText.value text) "Привет\nworld" "original multiline text"
        | _ -> failtest "Wrong command"

    testCase "domain validation errors retain request ID without creating commands" <| fun _ ->
        let failure = send 9UL "   " |> decode |> error
        Expect.equal failure.RequestId (Some 9UL) "can reject the initiating request"
        match failure.Failure with
        | ProtocolCodecFailure.InvalidDomain(DomainError.InvalidText("ChatMessageText", TextError.Missing)) -> ()
        | _ -> failtestf "Unexpected error %A" failure
        let packet = send 10UL "ok"
        packet.SendChat.ChannelId <- 0UL
        match (decode packet |> error).Failure with
        | ProtocolCodecFailure.InvalidDomain(DomainError.InvalidId "ChatChannelId") -> ()
        | value -> failtestf "Unexpected error %A" value
        let shortLimits = { config with ChatInput = { config.ChatInput with MessageText = 1 } }
        Expect.isError (ProtocolCodec.decodeClient (configured shortLimits) ((send 11UL "long").ToByteArray())) "host-supplied limit"

    testCase "malformed oversized unsupported and empty envelopes fail at the boundary" <| fun _ ->
        for bytes in [ [||]; [| 0x5auy; 0xffuy |]; Array.zeroCreate (config.MaxPacketBytes + 1) ] do
            Expect.isError (ProtocolCodec.decodeClient codec bytes) "packet rejected"
        let packet = send 1UL "ok"
        packet.ProtocolVersion <- 1u
        Expect.equal (decode packet |> error).Failure (ProtocolCodecFailure.UnsupportedVersion 1u) "version"
        packet.ProtocolVersion <- ProtocolCodec.Version
        packet.RequestId <- 0UL
        Expect.equal (decode packet |> error).Failure (ProtocolCodecFailure.InvalidEnvelope "request_id") "nonzero correlation"
        packet.RequestId <- 1UL
        packet.ClearPayload()
        Expect.equal (decode packet |> error).Failure (ProtocolCodecFailure.InvalidPayload "payload") "no known payload"
        let futurePayload = [| 0x08uy; byte ProtocolCodec.Version; 0x10uy; 0x01uy; 0x9auy; 0x06uy; 0x00uy |]
        Expect.equal (ProtocolCodec.decodeClient codec futurePayload |> error).Failure
            (ProtocolCodecFailure.InvalidPayload "payload") "unknown oneof field is rejected"
        let additiveField = Array.append ((send 1UL "ok").ToByteArray()) [| 0x98uy; 0x06uy; 0x01uy |]
        Expect.isOk (ProtocolCodec.decodeClient codec additiveField) "unknown additive field preserves a known command"

    testCase "accepted chat differs only in recipient correlation and includes no history" <| fun _ ->
        let broadcast = Packets.single codec (ServerResponse.ChatPublished message) |> ok |> parse
        let own = Packets.single codec (ServerResponse.ChatAccepted(42UL, message)) |> ok |> parse
        Expect.isFalse broadcast.HasRequestId "others have no request ID"
        Expect.equal own.RequestId 42UL "author correlation"
        Expect.equal own.ChatPublished broadcast.ChatPublished "same accepted event"
        Expect.equal own.ChatPublished.Message.MessageId UInt64.MaxValue "server ID"
        Expect.equal own.ChatPublished.Message.SentAtUnixMs -1L "signed Unix milliseconds"
        Expect.equal own.ChatPublished.Message.Author.DisplayName "Игрок" "author profile"
        Expect.isNull own.SessionOpened "no history in normal publication"

    testCase "character snapshots and withheld names are encoded without leaking the game name" <| fun _ ->
        let plain = Packets.single codec (ServerResponse.ChatPublished message) |> ok |> parse
        Expect.isFalse plain.ChatPublished.Message.HasCharacterName "no snapshot outside a character"
        let lydia = CharacterName.create 128 "Lydia" |> ok
        let named = ChatMessage.create (ChatMessageId.create 5UL |> ok) channel (PublicIdentity.Profile profile) (ValueSome lydia)
                        (ChatMessageText.create 2000 "hi" |> ok) DateTimeOffset.UnixEpoch
        let packet = Packets.single codec (ServerResponse.ChatPublished named) |> ok |> parse
        Expect.equal packet.ChatPublished.Message.CharacterName "Lydia" "snapshot at sending"
        let marked = Packets.single codec (ServerResponse.ChatPublished(ChatMessage.withFlagged [{ Start = 1; Length = 2 }] named)) |> ok |> parse
        Expect.equal marked.ChatPublished.Message.Flagged.Count 1 "flag ranges"
        Expect.equal (marked.ChatPublished.Message.Flagged[0].Start, marked.ChatPublished.Message.Flagged[0].Length) (1u, 2u) "byte range"
        let withheld = { snapshot with CharacterName = ValueNone; CharacterNameWithheld = true }
        let player = (Packets.single codec (joined withheld) |> ok |> parse).PresenceChanged.Joined[0]
        Expect.isTrue player.CharacterNameWithheld "withheld flag"
        Expect.isFalse player.HasCharacterName "withheld name absent"
        let tooLong = send 3UL (String('x', config.ChatInput.MessageText + 1))
        match (ProtocolCodec.decodeClient codec (tooLong.ToByteArray()) |> error).Failure with
        | ProtocolCodecFailure.InvalidDomain(DomainError.InvalidText("ChatMessageText", TextError.TooLong maximum)) ->
            Expect.equal maximum config.ChatInput.MessageText "configured maximum length"
        | other -> failtestf "Expected length failure: %A" other

    testCase "welcome requires unique online IDs self membership and ordered channel history" <| fun _ ->
        let encode value = Packets.single codec (ServerResponse.SessionOpened(1UL, value))
        let packet = encode welcome |> ok |> parse
        Expect.equal packet.SessionOpened.ServerName config.ServerName "configured server name"
        let named = configured { config with ServerName = "Голоса Тамриэля" }
        let namedPacket = Packets.single named (ServerResponse.SessionOpened(1UL, welcome)) |> ok |> parse
        Expect.equal namedPacket.SessionOpened.ServerName "Голоса Тамриэля" "name comes from this server configuration"
        Expect.equal packet.SessionOpened.Players.Count 1 "online"
        Expect.equal packet.SessionOpened.Channels.Count 2 "global and system channels"
        Expect.equal packet.SessionOpened.Channels[0].Kind Dreamsleeve.Protocol.Chat.ChatChannelKind.Global "kind"
        Expect.equal packet.SessionOpened.Channels[0].RecentMessages.Count 1 "initial retained history"
        Expect.equal packet.SessionOpened.Channels[1].Kind Dreamsleeve.Protocol.Chat.ChatChannelKind.System "system kind"
        Expect.isError (encode { welcome with SelfPlayerId = pid 8UL }) "self absent"
        Expect.isError (encode { welcome with Players = [snapshot; snapshot] }) "duplicate player"
        Expect.isError (encode (welcomeWith [message; message])) "duplicate/out-of-order message"
        Expect.isError (encode { welcome with Channels = [ systemChannel; systemChannel ] }) "duplicate channel"
        Expect.isError (encode { welcome with Channels = [ { systemChannel with Messages = [message] } ] }) "chat in the system channel"
        Expect.isError (encode { welcome with Players = List.replicate (config.MaxInitialPlayers + 1) snapshot }) "count bound"
        Expect.isError (Packets.single codec (ServerResponse.SessionOpened(0UL, welcome))) "zero correlation"
        Expect.isError (Packets.single codec (ServerResponse.ChatAccepted(0UL, message))) "zero chat correlation"

    testCase "a welcome defines every kind its players use and numbers their readings" <| fun _ ->
        let healthy = { snapshot with ActorValues = Map.ofList [ healthKey, health -10 100 ] }
        let packet = Packets.single codec (ServerResponse.SessionOpened(1UL, { welcome with Players = [ healthy ]; Kinds = kindsOf [ healthy ] })) |> ok |> parse
        let kind = packet.SessionOpened.ActorValueKinds |> Seq.exactlyOne
        Expect.equal (kind.Id, kind.Key, kind.DisplayName) (1UL, "skyrim:health", "Health") "the kind travels once, with its strings"
        let reading = packet.SessionOpened.Players[0].ActorValues |> Seq.exactlyOne
        Expect.equal (reading.Kind, reading.Resource.Current, reading.Resource.Maximum) (1UL, -10, 100) "the reading names its kind by number"
        Expect.equal (Packets.single codec (ServerResponse.SessionOpened(1UL, { welcome with Players = [ healthy ] })) |> error).Failure
            (ProtocolCodecFailure.InvalidPayload "session_opened") "a reading without a number is refused"

    testCase "replies require an ID while presence notifications have no correlation" <| fun _ ->
        let rejection = { Code = RequestRejectionCode.AuthenticationFailed; Message = "Отказ"; Field = "text" }
        let packet = Packets.single codec (ServerResponse.RequestRejected(9UL, rejection)) |> ok |> parse
        Expect.equal packet.RequestRejected.Code RequestRejectionCode.AuthenticationFailed "shared protobuf code"
        Expect.equal packet.RequestId 9UL "required correlation"
        Expect.isError (Packets.single codec (ServerResponse.RequestRejected(0UL, rejection))) "zero ID"
        for response in [joined snapshot; ServerResponse.PresenceChanged({ PresenceChange.empty with Left = [ pid 7UL ] }, ActorValueKinds.none)] do
            let packet = Packets.single codec response |> ok |> parse
            Expect.isFalse packet.HasRequestId "notifications cannot carry a request ID"

    testCase "configured byte limits apply at the exact boundary in both directions" <| fun _ ->
        let bytes = (send 1UL "hello").ToByteArray()
        let exact = { config with MaxPacketBytes = bytes.Length }
        Expect.isOk (ProtocolCodec.decodeClient (configured exact) bytes) "exact input limit"
        let small = { exact with MaxPacketBytes = bytes.Length - 1 }
        Expect.equal (ProtocolCodec.decodeClient (configured small) bytes |> error).Failure ProtocolCodecFailure.PacketTooLarge "one byte too large"
        let response = (ServerResponse.ChatPublished message)
        let encoded = Packets.single codec response |> ok
        let exact = { config with MaxPacketBytes = encoded.Length }
        Expect.isOk (Packets.single (configured exact) response) "exact output limit"
        let small = { exact with MaxPacketBytes = encoded.Length - 1 }
        Expect.equal (Packets.single (configured small) response |> error).Failure ProtocolCodecFailure.PacketTooLarge "output above configured limit"

    testCase "bootstrap counts and empty initial history are configurable" <| fun _ ->
        let second = PlayerData.create (pid 8UL) profile.Username profile.DisplayName |> Player.create |> Player.snapshot
        let response = ServerResponse.SessionOpened(1UL, { welcome with Players = [snapshot; second] })
        Expect.isOk (Packets.single (configured { config with MaxInitialPlayers = 2 }) response) "two unique players"
        Expect.isError (Packets.single (configured { config with MaxInitialPlayers = 1 }) response) "configured count"
        let noHistory = { config with MaxRecentMessages = 0 }
        Expect.isError (Packets.single (configured noHistory) response) "history disabled"
        let response = ServerResponse.SessionOpened(1UL, welcomeWith [])
        Expect.isOk (Packets.single (configured noHistory) response) "empty history allowed"

    testCase "invalid config prevents codec creation" <| fun _ ->
        for invalid in [
            { config with MaxPacketBytes = 0 }
            { config with MaxWaitingData = config.MaxPacketBytes - 1 }
            { config with MaxInitialPlayers = 0 }
            { config with MaxRecentMessages = -1 }
            { config with ChatInput = { config.ChatInput with Username = 0 } }
            { config with ChatInput = { config.ChatInput with DisplayName = 0 } }
            { config with ChatInput = { config.ChatInput with MessageText = 0 } }
            { config with PlayerInput = { config.PlayerInput with CharacterName = 0 } }
            { config with PlayerInput = { config.PlayerInput with PluginName = 0 } }
            { config with PlayerInput = { config.PlayerInput with LocationName = 0 } }
            { config with PlayerInput = { config.PlayerInput with ActorValueKey = 0 } }
            { config with PlayerInput = { config.PlayerInput with ActorValueName = 0 } }
            { config with PlayerInput = { config.PlayerInput with MaxActorValues = 0 } }
            { config with PlayerInput = { config.PlayerInput with DetailsText = 0 } }
            { config with PlayerInput = { config.PlayerInput with ActivityKey = 0 } }
        ] do
            Expect.isError (ServerConfig.validate invalid) "startup validation"

    testCase "one config sets yENet packet budgets and codec boundaries" <| fun _ ->
        Expect.equal (enet.ENET_API.enet_initialize()) 0 "initialize ENet"
        try
            use host = Enet.EnetHost.Create(Unchecked.defaultof<enet.ENetAddress>, 1un, 1un, 0u, 0u, Enet.EnetHostOption.Ipv4)
            let bytes = (send 1UL "hello").ToByteArray()
            let settings = { config with MaxPacketBytes = bytes.Length; MaxWaitingData = bytes.Length * 2 }
            ServerConfig.applyPacketLimits settings host |> ok
            Expect.equal host.MaximumPacketSize (unativeint bytes.Length) "actual host packet limit"
            Expect.equal host.MaximumWaitingData (unativeint (bytes.Length * 2)) "actual host waiting budget"
            Expect.isOk (ProtocolCodec.decodeClient (configured settings) bytes) "codec uses same config"
            Expect.isError (ServerConfig.validate { settings with MaxPacketBytes = 0 }) "the settings check refuses it before any host"
        finally
            enet.ENET_API.enet_deinitialize()

    testCase "server emits only defined nonzero rejection codes" <| fun _ ->
        let encode code =
            Packets.single codec
                (ServerResponse.RequestRejected(9UL, { Code = code; Message = "Rejected"; Field = "text" }))
        for code in Enum.GetValues<RequestRejectionCode>() do
            if code <> RequestRejectionCode.Unspecified then
                let packet = encode code |> ok |> parse
                Expect.equal packet.RequestRejected.Code code "generated enum survives serialization"
        for invalid in [RequestRejectionCode.Unspecified; enum<RequestRejectionCode> 0x7FFF0001; enum<RequestRejectionCode> -1] do
            Expect.equal (encode invalid |> error).Failure (ProtocolCodecFailure.InvalidPayload "code") "do not invent server codes"
    testCase "player lifecycle commands preserve character names and require an action" <| fun _ ->
        let name = "  Nerevar  "
        let beginAction = Dreamsleeve.Protocol.Chat.UpdatePlayer(BeginCharacter = Dreamsleeve.Protocol.Chat.BeginCharacter(Name = name))
        let renameAction = Dreamsleeve.Protocol.Chat.UpdatePlayer(RenameCharacter = Dreamsleeve.Protocol.Chat.RenameCharacter(Name = name))
        let expected = CharacterName.create 128 name |> ok
        Expect.equal (update beginAction |> playerUpdate) (PlayerUpdate.BeginCharacter expected) "name is an observation"
        Expect.equal (update renameAction |> playerUpdate) (PlayerUpdate.RenameCharacter expected) "rename has its own command"
        let leave = Dreamsleeve.Protocol.Chat.UpdatePlayer(LeaveGame = Dreamsleeve.Protocol.Chat.LeaveGame())
        Expect.equal (update leave |> playerUpdate) PlayerUpdate.LeaveGame "leave is explicit"
        Expect.equal (update (Dreamsleeve.Protocol.Chat.UpdatePlayer()) |> error).Failure
            (ProtocolCodecFailure.InvalidPayload "update_player.action") "missing oneof rejected"
        beginAction.BeginCharacter.Name <- " "
        Expect.isError (update beginAction) "character name is required"

    testCase "zero scalar differs from unset and whole-point resources are not clamped" <| fun _ ->
        let zero = scalarEntry "Skyrim:SpeedMult" 0.0f
        let resource key name current maximum =
            Dreamsleeve.Protocol.Chat.ActorValueEntry(
                Key = key, DisplayName = name,
                Resource = Dreamsleeve.Protocol.Chat.ResourceActorValue(Current = current, Maximum = maximum))
        let decoded = valuesPacket [zero; resource "skyrim:health" "Health" 120 100; resource "skyrim:stamina" "Stamina" -40 100] |> update |> playerUpdate |> apply
        Expect.equal decoded.Location ValueNone "absent location is unknown"
        let observe key =
            ActorValueState.fold (fun scalar -> Choice1Of2 (ActorValue.value scalar)) (fun current maximum -> Choice2Of2 (current, maximum))
                decoded.ActorValues[ActorValueKey.create 128 key |> ok].State
        Expect.equal (observe "skyrim:speedmult") (Choice1Of2 0.0f) "zero survived its oneof presence and has no maximum"
        Expect.equal (observe "skyrim:health") (Choice2Of2 (120, 100)) "above maximum preserved"
        Expect.equal (observe "skyrim:stamina") (Choice2Of2 (-40, 100)) "negative current preserved"
        zero.ClearValue()
        Expect.equal (valuesPacket [zero] |> update |> error).Failure
            (ProtocolCodecFailure.InvalidPayload "actor_value.value") "unset is not scalar zero"

    testCase "present zero coordinates are distinct from an unknown location" <| fun _ ->
        let decoded = locationPacket (wireLocation()) |> update |> playerUpdate |> apply
        let place = decoded.Location |> ValueOption.get
        Expect.equal place.Position Position.zero "all-zero coordinates are valid"
        Expect.equal place.Rotation Rotation.zero "all-zero radians are valid"
        Expect.equal (PluginName.value place.Location.LocationId.PluginName) "skyrim.esm" "canonical identity"
        let noLocation = locationPacket null |> update |> playerUpdate |> apply
        Expect.equal noLocation.Location ValueNone "unknown is represented by presence"
        for field in [0; 1; 2; 3] do
            let broken = wireLocation()
            match field with
            | 0 -> broken.Location <- null
            | 1 -> broken.Location.LocationId <- null
            | 2 -> broken.Position <- null
            | _ -> broken.Rotation <- null
            Expect.equal (locationPacket broken |> update |> error).Failure
                (ProtocolCodecFailure.InvalidPayload "location") "partial location rejected"

    testCase "nonfinite telemetry rejects the complete sample and preserves request correlation" <| fun _ ->
        for bad in [Single.NaN; Single.PositiveInfinity; Single.NegativeInfinity] do
            for index in 0..5 do
                let place = wireLocation()
                match index with
                | 0 -> place.Position.X <- bad
                | 1 -> place.Position.Y <- bad
                | 2 -> place.Position.Z <- bad
                | 3 -> place.Rotation.X <- bad
                | 4 -> place.Rotation.Y <- bad
                | _ -> place.Rotation.Z <- bad
                let failure = locationPacket place |> update |> error
                Expect.equal failure.RequestId (Some 91UL) "caller can reject without applying the sample"
                match failure.Failure with
                | ProtocolCodecFailure.InvalidDomain(DomainError.NonFiniteNumber _) -> ()
                | value -> failtestf "Unexpected failure %A" value
            Expect.isError (valuesPacket [scalarEntry "skyrim:health" bad] |> update) "a scalar reading must be finite"

    testCase "resources travel as whole signed points from the client to observers" <| fun _ ->
        for current, maximum in [ -250, 100; 0, 0; 150, 100; Int32.MinValue, Int32.MaxValue ] do
            let entry = Dreamsleeve.Protocol.Chat.ActorValueEntry(
                Key = "skyrim:health", DisplayName = "Health",
                Resource = Dreamsleeve.Protocol.Chat.ResourceActorValue(Current = current, Maximum = maximum))
            let state = valuesPacket [entry] |> update |> playerUpdate |> apply
            let reading = (Packets.single codec (joined state) |> ok |> parse).PresenceChanged.Joined[0].ActorValues |> Seq.exactlyOne
            Expect.equal (reading.Resource.Current, reading.Resource.Maximum) (current, maximum) "no rounding, clamping or sign loss"

    testCase "sample admission checks count before accepting unique canonical keys" <| fun _ ->
        let first = scalarEntry "Skyrim:Health" 1.0f
        let duplicate = scalarEntry "skyrim:health" 2.0f
        Expect.equal (valuesPacket [first; duplicate] |> update |> error).Failure
            (ProtocolCodecFailure.InvalidPayload "actor_values.duplicate_key") "case does not create another stat"
        let one = configured {config with PlayerInput = {config.PlayerInput with MaxActorValues = 1}}
        let request entries = valuesPacket entries |> updatePacket |> fun packet -> packet.ToByteArray()
        Expect.isOk (ProtocolCodec.decodeClient one (request [first])) "exact configured count"
        Expect.equal (ProtocolCodec.decodeClient one (request [first; scalarEntry "avg:health" 1.0f]) |> error).Failure
            (ProtocolCodecFailure.InvalidPayload "actor_values.count") "cannot grow state past the configured count"

    testCase "configured telemetry text limits apply before domain commands are created" <| fun _ ->
        let decodeWith limits action =
            let parser = configured {config with PlayerInput = limits}
            ProtocolCodec.decodeClient parser ((updatePacket action).ToByteArray())

        let limits = config.PlayerInput
        let shortName = {limits with CharacterName = 1}
        let beginName name = Dreamsleeve.Protocol.Chat.UpdatePlayer(BeginCharacter = Dreamsleeve.Protocol.Chat.BeginCharacter(Name = name))
        Expect.isOk (decodeWith shortName (beginName "Ж")) "exact scalar count"
        Expect.isError (decodeWith shortName (beginName "Жа")) "character limit"

        let place = wireLocation()
        Expect.isError (decodeWith {limits with PluginName = 2} (locationPacket place)) "plugin key limit"
        Expect.isError (decodeWith {limits with LocationName = 2} (locationPacket place)) "place label limit"

        let entry = scalarEntry "skyrim:health" 0.0f
        entry.DisplayName <- "Health"
        Expect.isError (decodeWith {limits with ActorValueKey = 2} (valuesPacket [entry])) "actor key limit"
        Expect.isError (decodeWith {limits with ActorValueName = 2} (valuesPacket [entry])) "actor label limit"

        let description = Dreamsleeve.Protocol.Chat.PlayerDetails(Activity = Dreamsleeve.Protocol.Chat.PlayerActivity(
            Kind = Dreamsleeve.Protocol.Chat.ActivityKind.Talking, TargetName = "Nerevar"))
        Expect.isError (decodeWith {limits with DetailsText = 2} (Dreamsleeve.Protocol.Chat.UpdatePlayer(SetDetails = description))) "detail label limit"
        description.Activity <- Dreamsleeve.Protocol.Chat.PlayerActivity(Kind = Dreamsleeve.Protocol.Chat.ActivityKind.Menu, MenuKey = "inventorymenu")
        Expect.isError (decodeWith {limits with ActivityKey = 2} (Dreamsleeve.Protocol.Chat.UpdatePlayer(SetDetails = description))) "activity key limit"

    testCase "full player replication includes generation and all telemetry while moved omits other state" <| fun _ ->
        let started = Player.create profile |> Player.beginCharacter (CharacterName.create 128 "Nerevar" |> ok)
        let sampled = valuesPacket [scalarEntry "skyrim:health" 0.0f] |> update |> playerUpdate
        let state = started |> Player.applyUpdate sampled |> Player.snapshot
        let entered = Packets.single codec (joined state) |> ok |> parse
        let changed = Packets.single codec (updated state) |> ok |> parse
        let boot = Packets.single codec (ServerResponse.SessionOpened(1UL, {welcome with Players = [state]; Kinds = kindsOf [state]})) |> ok |> parse
        let player = changed.PresenceChanged.Updated[0]
        Expect.equal entered.PresenceChanged.Joined[0] player "same complete state"
        Expect.equal boot.SessionOpened.Players[0] player "bootstrap agrees with replication"
        Expect.sequenceEqual boot.SessionOpened.ActorValueKinds changed.PresenceChanged.ActorValueKinds "and defines the same kinds"
        Expect.equal player.CharacterGeneration 1UL "save generation retained"
        Expect.equal player.CharacterName "Nerevar" "name retained"
        Expect.equal player.ActorValues[0].ValueCase Dreamsleeve.Protocol.Chat.ActorValue.ValueOneofCase.Scalar "zero is a present scalar"
        Expect.equal player.ActorValues[0].Kind 1UL "the reading names its kind"
        Expect.isFalse changed.HasRequestId "periodic replication is a notification"

        let ack = Packets.single codec (ServerResponse.PlayerUpdateAccepted 91UL) |> ok |> parse
        Expect.equal ack.RequestId 91UL "admitted command has its own acknowledgement"
        Expect.isNotNull ack.PlayerUpdateAccepted "empty but present acknowledgement"
        Expect.isError (Packets.single codec (ServerResponse.PlayerUpdateAccepted 0UL)) "zero acknowledgement ID rejected"

        let moved = Packets.single codec (ServerResponse.PlayersMoved (movementBatch [pid 7UL, ValueNone])) |> ok |> parseMovement
        Expect.equal moved.Movements.Players[0].PlayerId 7UL "Realtime identity."
        Expect.equal moved.Movements.Players[0].ViewRevision 1UL "Visibility revision."
        let cleared: VisibilityChange = { PlayerId = pid 7UL; ViewRevision = 2UL; Sequence = 0UL; Pose = ValueNone }
        let clear = Packets.single codec (ServerResponse.PresenceChanged({ PresenceChange.empty with Visibility = [ cleared ] }, ActorValueKinds.none)) |> ok |> parse
        Expect.isNull clear.PresenceChanged.Visibility[0].Pose "Visibility clears are reliable control."
        Expect.isNull clear.PresenceChanged.Space "A clear needs no place."

    testCase "rich details roundtrip preserves optional zero time and descriptive places" <| fun _ ->
        let source = Dreamsleeve.Protocol.Chat.PlayerDetails(
            Race = Dreamsleeve.Protocol.Chat.NamedForm(
                Form = Dreamsleeve.Protocol.Chat.FormKey(PluginName = "Skyrim.ESM", LocalFormId = 0x13749u), Name = "  Breton  "),
            Level = UInt32.MaxValue,
            Activity = Dreamsleeve.Protocol.Chat.PlayerActivity(Kind = Dreamsleeve.Protocol.Chat.ActivityKind.Lockpicking,
                TargetName = "Chest", LockDifficulty = Dreamsleeve.Protocol.Chat.LockDifficulty.RequiresKey),
            Place = Dreamsleeve.Protocol.Chat.PlaceDescription(WorldspaceName = "", LocationName = "Whiterun",
                NearbyMarkerName = "", MarkerKind = "CITY", IsInterior = true),
            GameStartedAtUnixMs = 0L)
        let state = update (Dreamsleeve.Protocol.Chat.UpdatePlayer(SetDetails = source)) |> playerUpdate |> apply
        let encoded = Packets.single codec (updated state) |> ok |> parse

        let actual = encoded.PresenceChanged.Updated[0].Details
        Expect.equal actual.Race.Form.PluginName "skyrim.esm" "race key is canonical"
        Expect.equal actual.Race.Name "  Breton  " "display label preserved"
        Expect.equal actual.Level UInt32.MaxValue "level is not clamped to vanilla gameplay"
        Expect.equal actual.Activity source.Activity "typed activity retained"
        Expect.equal actual.Place.MarkerKind "city" "machine key canonicalized"
        Expect.isTrue actual.Place.IsInterior "interior observation retained"
        Expect.isTrue actual.HasGameStartedAtUnixMs "epoch is present, not unknown"
        Expect.equal actual.GameStartedAtUnixMs 0L "epoch preserved"

        source.ClearGameStartedAtUnixMs()
        let state = update (Dreamsleeve.Protocol.Chat.UpdatePlayer(SetDetails = source)) |> playerUpdate |> apply
        Expect.equal state.Details.GameStartedAt ValueNone "absence remains optional"

    testCase "all defined activity and lock enums map explicitly in both directions" <| fun _ ->
        for kind in Enum.GetValues<Dreamsleeve.Protocol.Chat.ActivityKind>() do
            let activity = Dreamsleeve.Protocol.Chat.PlayerActivity(Kind = kind)
            if kind = Dreamsleeve.Protocol.Chat.ActivityKind.Menu then activity.MenuKey <- "InventoryMenu"
            let source = Dreamsleeve.Protocol.Chat.PlayerDetails(Activity = activity)
            let state = update (Dreamsleeve.Protocol.Chat.UpdatePlayer(SetDetails = source)) |> playerUpdate |> apply
            let encoded = Packets.single codec (updated state) |> ok |> parse
            Expect.equal encoded.PresenceChanged.Updated[0].Details.Activity.Kind kind "activity mapping"
        for difficulty in Enum.GetValues<Dreamsleeve.Protocol.Chat.LockDifficulty>() do
            let source = Dreamsleeve.Protocol.Chat.PlayerDetails(Activity = Dreamsleeve.Protocol.Chat.PlayerActivity(
                Kind = Dreamsleeve.Protocol.Chat.ActivityKind.Lockpicking, LockDifficulty = difficulty))
            let state = update (Dreamsleeve.Protocol.Chat.UpdatePlayer(SetDetails = source)) |> playerUpdate |> apply
            let encoded = Packets.single codec (updated state) |> ok |> parse
            Expect.equal encoded.PresenceChanged.Updated[0].Details.Activity.LockDifficulty difficulty "difficulty mapping"

    testCase "zero and absent level survive client command and server replication" <| fun _ ->
        for level in [ValueNone; ValueSome 0u; ValueSome UInt32.MaxValue] do
            let source = Dreamsleeve.Protocol.Chat.PlayerDetails(Activity = Dreamsleeve.Protocol.Chat.PlayerActivity())
            level |> ValueOption.iter (fun value -> source.Level <- value)
            let state = update (Dreamsleeve.Protocol.Chat.UpdatePlayer(SetDetails = source)) |> playerUpdate |> apply
            Expect.equal state.Details.Level level "Client-reported level is preserved."
            let encoded = Packets.single codec (updated state) |> ok |> parse
            let actual = encoded.PresenceChanged.Updated[0].Details
            Expect.equal actual.HasLevel level.IsSome "Presence survives replication."
            level |> ValueOption.iter (fun value -> Expect.equal actual.Level value "Level survives replication.")

    testCase "malformed details and undefined enums never enter the domain" <| fun _ ->
        let valid () = Dreamsleeve.Protocol.Chat.PlayerDetails(Activity = Dreamsleeve.Protocol.Chat.PlayerActivity())
        let rejected source = Expect.isError (update (Dreamsleeve.Protocol.Chat.UpdatePlayer(SetDetails = source))) "invalid details"
        rejected (Dreamsleeve.Protocol.Chat.PlayerDetails())
        let race = valid()
        race.Race <- Dreamsleeve.Protocol.Chat.NamedForm()
        rejected race
        let unknownKind = valid()
        unknownKind.Activity.Kind <- enum<Dreamsleeve.Protocol.Chat.ActivityKind> 999
        rejected unknownKind
        let unknownLock = valid()
        unknownLock.Activity.LockDifficulty <- enum<Dreamsleeve.Protocol.Chat.LockDifficulty> -1
        rejected unknownLock
        for outside in [-62135596800001L; 253402300800000L] do
            let timestamp = valid()
            timestamp.GameStartedAtUnixMs <- outside
            rejected timestamp
        for boundary in [-62135596800000L; 253402300799999L] do
            let timestamp = valid()
            timestamp.GameStartedAtUnixMs <- boundary
            Expect.isOk (update (Dreamsleeve.Protocol.Chat.UpdatePlayer(SetDetails = timestamp))) "DateTimeOffset boundary is valid"
    testCase "realtime decoding validates independent context and sequence without request correlation" <| fun _ ->
        let sample () = Dreamsleeve.Protocol.Chat.MovementSample(ContextRevision = 3UL, Sequence = 9UL,
            Pose = Dreamsleeve.Protocol.Chat.MovementPose(Position = Dreamsleeve.Protocol.Chat.Position(),
                Rotation = Dreamsleeve.Protocol.Chat.Rotation(), SampledAtUs = UInt64.MaxValue))
        let packet = Dreamsleeve.Protocol.Chat.ClientMovementPacket(ProtocolVersion = ProtocolCodec.Version, Sample = sample())
        let decode () = ProtocolCodec.decodeMovement codec (packet.ToByteArray())
        let actual = decode() |> ok
        Expect.equal actual.ContextRevision 3UL "Coordinate context retained."
        Expect.equal actual.Sequence 9UL "No request ID is involved."
        Expect.equal actual.Pose.SampledAtUs UInt64.MaxValue "Clock retained without signed narrowing."
        for malformed in [0; 1; 2; 3; 4] do
            packet.Sample <- sample()
            match malformed with
            | 0 -> packet.Sample.ContextRevision <- 0UL
            | 1 -> packet.Sample.Sequence <- 0UL
            | 2 -> packet.Sample.Pose <- null
            | 3 -> packet.Sample.Pose.Position <- null
            | _ -> packet.Sample.Pose.Position.X <- Single.NaN
            let rejected = decode() |> error
            Expect.equal rejected.RequestId None "Malformed telemetry cannot create a command response."
        packet.Sample <- null
        Expect.isError (decode()) "Missing sample rejected."
        packet.Sample <- sample()
        packet.ProtocolVersion <- ProtocolCodec.Version - 1u
        Expect.isError (decode()) "Old protocol rejected."
        for bytes in [null; [||]; [|0xffuy|]; Array.zeroCreate (config.MaxPacketBytes + 1)] do
            Expect.isError (ProtocolCodec.decodeMovement codec bytes) "Boundary rejects malformed or oversized telemetry."

    testCase "delivery lanes isolate chat control and realtime" <| fun _ ->
        Expect.equal (send 1UL "hello" |> decode |> ok |> ProtocolCodec.requestLane) DeliveryLane.Chat "Chat request."
        let location = locationPacket (wireLocation()) |> update |> ok
        Expect.equal (ProtocolCodec.requestLane location) DeliveryLane.Control "Location is reliable control."
        let rejection = { Code = RequestRejectionCode.Overloaded; Message = "busy"; Field = "" }
        for response in [ServerResponse.ChatPublished message; ServerResponse.ChatAccepted(1UL, message); ServerResponse.ChatRejected(1UL, rejection)] do
            Expect.equal (ProtocolCodec.delivery response).Lane DeliveryLane.Chat "Chat response remains on chat channel."
        for response in [joined snapshot; ServerResponse.PlayerUpdateAccepted 1UL; ServerResponse.RequestRejected(1UL, rejection)] do
            Expect.equal (ProtocolCodec.delivery response).Lane DeliveryLane.Control "Lifecycle and command replies."
        Expect.equal (ProtocolCodec.delivery (ServerResponse.PlayersMoved (movementBatch [pid 7UL, ValueNone]))).Lane DeliveryLane.Realtime "Movement envelope is independent."
    testCase "a pseudonymous identity leaves with no username or character and a flag" <| fun _ ->
        let pseudonym = Pseudonym.create "Страж" |> ok |> Pseudonym.numbered 2
        let character = Player.create profile |> Player.beginCharacter (CharacterName.create 128 "Lydia" |> ok)
        let hidden = PlayerSnapshot.withPseudonym pseudonym (Player.snapshot character)
        let entered = Packets.single codec (joined hidden) |> ok
        let player = (parse entered).PresenceChanged.Joined[0]
        Expect.isTrue player.Profile.Pseudonymous "flagged"
        Expect.equal player.Profile.Username "" "no username"
        Expect.equal player.Profile.DisplayName "Страж 2" "pseudonym as display name"
        Expect.equal player.Profile.PlayerId 7UL "public PlayerId"
        Expect.isFalse player.HasCharacterName "no character name"
        for secret in [ "player"; "Игрок"; "Lydia" ] do
            let bytes = Text.Encoding.UTF8.GetBytes secret
            Expect.isFalse (Seq.windowed bytes.Length entered |> Seq.exists (fun window -> window = bytes)) $"no {secret} in the packet"
        let shown = Packets.single codec (joined snapshot) |> ok |> parse
        Expect.isFalse shown.PresenceChanged.Joined[0].Profile.Pseudonymous "a shown profile is not flagged"

    testCase "hidden identity travels in OpenSession, SetIdentityVisibility and its settlement" <| fun _ ->
        let opening = Dreamsleeve.Protocol.Chat.ClientPacket(
            ProtocolVersion = ProtocolCodec.Version, RequestId = 3UL,
            OpenSession = Dreamsleeve.Protocol.Chat.OpenSession(SessionTicket = String('a', 43),
                                                                HiddenIdentity = Dreamsleeve.Protocol.Chat.HiddenIdentity.ExceptGroundMarks))
        match (decode opening |> ok).Command with
        | ClientCommand.OpenSession(_, hidden) -> Expect.equal hidden HiddenIdentity.ExceptGroundMarks "hidden from the first packet"
        | other -> failtestf "%A" other
        opening.OpenSession.HiddenIdentity <- enum<Dreamsleeve.Protocol.Chat.HiddenIdentity> 7
        Expect.equal (decode opening |> error).Failure (ProtocolCodecFailure.InvalidPayload "hidden_identity") "unknown choice refused"
        let switch = Dreamsleeve.Protocol.Chat.ClientPacket(
            ProtocolVersion = ProtocolCodec.Version, RequestId = 4UL,
            SetIdentityVisibility = Dreamsleeve.Protocol.Chat.SetIdentityVisibility(Hidden = Dreamsleeve.Protocol.Chat.HiddenIdentity.Everywhere))
        let request = decode switch |> ok
        Expect.equal request.Command (ClientCommand.SetIdentityVisibility HiddenIdentity.Everywhere) "switch command"
        Expect.equal (ProtocolCodec.requestLane request) DeliveryLane.Control "control lane"
        let pseudonym = Pseudonym.create "Страж" |> ok
        let settled =
            Packets.single codec (ServerResponse.IdentityVisibilityChanged(4UL, ValueSome pseudonym, HiddenIdentity.ExceptGroundMarks))
            |> ok |> parse
        Expect.equal settled.RequestId 4UL "correlated"
        Expect.equal settled.IdentityVisibilityChanged.Pseudonym "Страж" "pseudonym for the owner"
        Expect.equal settled.IdentityVisibilityChanged.Hidden Dreamsleeve.Protocol.Chat.HiddenIdentity.ExceptGroundMarks "the applied choice"
        let shown = Packets.single codec (ServerResponse.IdentityVisibilityChanged(5UL, ValueNone, HiddenIdentity.Shown)) |> ok |> parse
        Expect.isFalse shown.IdentityVisibilityChanged.HasPseudonym "absent while shown"
        let welcomed =
            Packets.single codec (ServerResponse.SessionOpened(1UL, { welcome with OwnPseudonym = ValueSome pseudonym; Hiding = HiddenIdentity.Everywhere }))
            |> ok |> parse
        Expect.equal welcomed.SessionOpened.HiddenIdentity Dreamsleeve.Protocol.Chat.HiddenIdentity.Everywhere "the owner learns where it is hidden"
        let selfHidden = { welcome with Players = [ PlayerSnapshot.withPseudonym pseudonym snapshot ] }
        Expect.isError (Packets.single codec (ServerResponse.SessionOpened(1UL, selfHidden))) "the self entry keeps the real profile"
        Expect.equal welcomed.SessionOpened.OwnPseudonym "Страж" "the owner learns its pseudonym at opening"
        Expect.isFalse (parse (Packets.single codec (ServerResponse.SessionOpened(1UL, welcome)) |> ok)).SessionOpened.HasOwnPseudonym "absent when shown"
    testCase "moderator requests decode through the domain and their answers carry the correlation" <| fun _ ->
        let packet (fill: Dreamsleeve.Protocol.Chat.ClientPacket -> unit) =
            let value = Dreamsleeve.Protocol.Chat.ClientPacket(ProtocolVersion = ProtocolCodec.Version, RequestId = 5UL)
            fill value
            decode value
        let command result = (result |> ok).Command
        let sanction minutes reason =
            let value = Dreamsleeve.Protocol.Chat.SanctionPlayer(PlayerId = 9UL, Kind = Dreamsleeve.Protocol.Chat.SanctionKind.Mute, Reason = reason)
            minutes |> Option.iter (fun minutes -> value.Minutes <- minutes)
            packet (fun p -> p.SanctionPlayer <- value)
        match command (sanction (Some 15u) " Флуд ") with
        | ClientCommand.SanctionPlayer(target, SanctionKind.Mute, term, reason) ->
            Expect.equal (target, term, SanctionReason.value reason) (pid 9UL, SanctionTerm.For(TimeSpan.FromMinutes 15.), "Флуд") "a term and a trimmed reason"
        | other -> failtestf "%A" other
        match command (sanction None "Флуд") with
        | ClientCommand.SanctionPlayer(_, _, SanctionTerm.UntilLifted, _) -> ()
        | other -> failtestf "no minutes is until lifted: %A" other
        Expect.isError (sanction (Some 0u) "Флуд") "a term is at least a minute"
        Expect.isError (sanction (Some 15u) "   ") "a reason is required"
        Expect.isError (packet (fun p -> p.SanctionPlayer <- Dreamsleeve.Protocol.Chat.SanctionPlayer(PlayerId = 9UL, Reason = "x"))) "a kind is required"
        Expect.isError (packet (fun p -> p.KickPlayer <- Dreamsleeve.Protocol.Chat.KickPlayer(PlayerId = 0UL, Reason = "x"))) "a player is required"
        match command (packet (fun p -> p.ClearPlayerMarks <- Dreamsleeve.Protocol.Chat.ClearPlayerMarks(PlayerId = 9UL, Deaths = true))) with
        | ClientCommand.ClearPlayerMarks(_, kinds) -> Expect.equal kinds [ GroundMarkKind.Death ] "only deaths"
        | other -> failtestf "%A" other
        Expect.isError (packet (fun p -> p.ClearPlayerMarks <- Dreamsleeve.Protocol.Chat.ClearPlayerMarks(PlayerId = 9UL))) "a clearing names a kind"
        let delete = packet (fun p -> p.DeleteChatMessage <- Dreamsleeve.Protocol.Chat.DeleteChatMessage(ChannelId = 1UL, MessageId = 3UL)) |> ok
        Expect.equal (ProtocolCodec.requestLane delete) DeliveryLane.Chat "a removal settles on the chat lane"
        let lists = packet (fun p -> p.ListSanctions <- Dreamsleeve.Protocol.Chat.ListSanctions()) |> ok
        Expect.equal (lists.Command, ProtocolCodec.requestLane lists) (ClientCommand.ListSanctions, DeliveryLane.Control) "a list settles on control"
        let encoded response = Packets.single codec response |> ok |> parse
        let removed = encoded (ServerResponse.ChatMessageRemoved(ValueNone, channel, message.MessageId))
        Expect.equal (removed.RequestId, removed.ChatMessageRemoved.MessageId) (0UL, UInt64.MaxValue) "another member's copy is a notification"
        Expect.equal (ProtocolCodec.delivery (ServerResponse.ChatMessageRemoved(ValueSome 5UL, channel, message.MessageId))).Lane DeliveryLane.Chat "chat lane"
        let issued =
            Sanction.issue (SanctionId.create 1L |> ok) (DateTimeOffset.FromUnixTimeMilliseconds 1_000L)
                { Target = pid 9UL; Kind = SanctionKind.Ban; Term = SanctionTerm.UntilLifted; Reason = SanctionReason.create "Читы" |> ok
                  IssuedBy = SanctionIssuer.Moderator(pid 7UL) }
        let listed = (encoded (ServerResponse.SanctionList(6UL, [ issued ]))).SanctionList.Sanctions |> Seq.exactlyOne
        Expect.equal (listed.PlayerId, listed.Kind, listed.Reason, listed.IssuedAtUnixMs, listed.HasUntilUnixMs)
                     (9UL, Dreamsleeve.Protocol.Chat.SanctionKind.Ban, "Читы", 1_000L, false) "the entry names the player, not the issuer"
        Expect.equal (encoded (ServerResponse.RoleChanged PlayerRole.Moderator)).RoleChanged.Role Dreamsleeve.Protocol.Chat.PlayerRole.Moderator "role"
        Expect.equal (encoded (ServerResponse.SessionOpened(1UL, { welcome with Role = PlayerRole.Moderator }))).SessionOpened.Role
                     Dreamsleeve.Protocol.Chat.PlayerRole.Moderator "the welcome carries the role"
        Expect.isError (Packets.single codec (ServerResponse.PlayerKicked(0UL, pid 9UL))) "an answer needs its request"

    testCase "a display name change decodes through the domain limit and its answer carries the correlation" <| fun _ ->
        let change name =
            Dreamsleeve.Protocol.Chat.ClientPacket(
                ProtocolVersion = ProtocolCodec.Version, RequestId = 77UL,
                ChangeDisplayName = Dreamsleeve.Protocol.Chat.ChangeDisplayName(DisplayName = name)) |> decode
        match (change "  Новое́ Имя " |> ok).Command with
        | ClientCommand.ChangeDisplayName name -> Expect.equal (DisplayName.value name) ("Новое́ Имя".Normalize()) "trimmed and NFC"
        | other -> failtestf "%A" other
        Expect.equal (ProtocolCodec.requestLane (change "Name" |> ok)) DeliveryLane.Control "control lane"
        for invalid, expected in [ String('x', config.ChatInput.DisplayName + 1), TextError.TooLong config.ChatInput.DisplayName
                                   "   ", TextError.Missing
                                   "line\nbreak", TextError.InvalidCharacters ] do
            match (change invalid |> error) with
            | { RequestId = Some 77UL; Failure = ProtocolCodecFailure.InvalidDomain(DomainError.InvalidText("DisplayName", actual)) } ->
                Expect.equal actual expected "domain refusal keeps the correlation"
            | other -> failtestf "%A" other
        let name = DisplayName.create 64 "Новое Имя" |> ok
        let packet = Packets.single codec (ServerResponse.DisplayNameChanged(78UL, name)) |> ok |> parse
        Expect.equal packet.RequestId 78UL "correlated"
        Expect.equal packet.DisplayNameChanged.DisplayName "Новое Имя" "stored name"
        Expect.equal (ProtocolCodec.delivery (ServerResponse.DisplayNameChanged(78UL, name))).Lane DeliveryLane.Control "control lane"
        Expect.isError (Packets.single codec (ServerResponse.DisplayNameChanged(0UL, name))) "zero correlation is invalid"
]
