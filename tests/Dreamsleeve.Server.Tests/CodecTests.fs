module Dreamsleeve.Server.Tests.CodecTests

open System
open Expecto
open Google.Protobuf
open Dreamsleeve.Server.Domain
open Dreamsleeve.Server.Core

let private movementBatch items =
    items |> List.map (fun (id, location) -> ({ PlayerId = id; Location = location }: Dreamsleeve.Server.Domain.MovementChange)) |> List.toArray

let private config = ServerConfig.defaults

let private ok = function
    | Ok value -> value
    | Error error -> failtestf "Expected success, got %A" error

let private error = function
    | Error value -> value
    | Ok _ -> failtest "Expected error"

let private codec = ProtocolCodec.create config |> ok
let private configured settings = ProtocolCodec.create settings |> ok

let private pid raw = PlayerId.create raw |> ok
let private channel = ChatChannelId.create 1UL |> ok
let private profile = PlayerData.create (pid 7UL) (Username.create 32 "player" |> ok) (DisplayName.create 64 "Игрок" |> ok)
let private snapshot = Player.create profile |> Player.snapshot
let private message =
    ChatMessage.create (ChatMessageId.create UInt64.MaxValue |> ok) channel profile
        (ChatMessageText.create 2000 "Привет\nworld" |> ok) (DateTimeOffset.FromUnixTimeMilliseconds(-1L))
let private parse bytes = Dreamsleeve.Protocol.Chat.ServerPacket.Parser.ParseFrom(bytes: byte array)
let private send requestId text =
    Dreamsleeve.Protocol.Chat.ClientPacket(
        ProtocolVersion = ProtocolCodec.Version, RequestId = requestId,
        SendChat = Dreamsleeve.Protocol.Chat.SendChat(ChannelId = 1UL, Text = text))
let private decode (packet: Dreamsleeve.Protocol.Chat.ClientPacket) =
    ProtocolCodec.decodeClient codec (packet.ToByteArray())
let private welcome = {
    SelfPlayerId = pid 7UL
    GlobalChannelId = channel
    Players = [snapshot]
    RecentMessages = [message]
}

let private updatePacket action =
    Dreamsleeve.Protocol.Chat.ClientPacket(
        ProtocolVersion = ProtocolCodec.Version, RequestId = 91UL, UpdatePlayer = action)

let private update action = updatePacket action |> decode

let private movementPacket place =
    Dreamsleeve.Protocol.Chat.UpdatePlayer(SampleMovement = Dreamsleeve.Protocol.Chat.SampleMovement(Location = place))

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
    | ClientCommand.OpenSession _ | ClientCommand.SendChat _ -> failtest "Expected player update"

let private apply update = Player.create profile |> Player.applyUpdate update |> Player.snapshot

let tests = testList "Dreamsleeve.Server.Codec" [
    testCase "movement batches split exactly within configured packet limits" <| fun _ ->
        let movements = movementBatch [for id in 1UL .. 130UL -> pid id, ValueNone]
        let packet = ProtocolCodec.encodeServer codec (ServerResponse.PlayersMoved movements) |> ok
        for limit in [12; 127; 128; packet.Length - 1; packet.Length] do
            let small = ProtocolCodec.create { ServerConfig.defaults with MaxPacketBytes = limit } |> ok
            let packets = ProtocolCodec.encodeMovementPackets small Int32.MaxValue movements |> ok
            let decoded = packets |> List.collect (fun bytes ->
                Expect.isLessThanOrEqual bytes.Length limit "Application limit includes the whole envelope."
                let value = parse bytes
                Expect.isFalse value.HasRequestId "Notification batch."
                value.PlayersMoved.Players |> Seq.map _.PlayerId |> List.ofSeq)
            Expect.equal decoded [1UL .. 130UL] "Every entry exactly once and in order."
        let tiny = ProtocolCodec.create { ServerConfig.defaults with MaxPacketBytes = 1 } |> ok
        Expect.isError (ProtocolCodec.encodeMovementPackets tiny Int32.MaxValue movements) "An unsplittable entry fails before any send."
        Expect.isError (ProtocolCodec.encodeServer codec (ServerResponse.PlayersMoved (movementBatch []))) "Empty batch is invalid."

    testCase "default batching does not amplify packet count to avoid transport fragmentation" <| fun _ ->
        let movements = movementBatch [for id in 1UL .. 200UL -> pid id, ValueNone]
        let expected = ProtocolCodec.encodeServer codec (ServerResponse.PlayersMoved movements) |> ok
        let packets = ProtocolCodec.encodeMovementPackets codec 548 movements |> ok
        Expect.isGreaterThan expected.Length 548 "Fixture crosses the transport fragmentation threshold."
        Expect.equal packets [expected] "Default keeps one valid reliable packet; ENet owns fragmentation."

    testCase "movement target is clamped by negotiated transport and application budgets" <| fun _ ->
        let movements = movementBatch [for id in 1UL .. 130UL -> pid id, ValueNone]
        for target, transport, application in [64, 128, 1024; 128, 64, 1024; 128, 1024, 64] do
            let configured = configured { config with MovementPacketTargetBytes = target; MaxPacketBytes = application }
            let packets = ProtocolCodec.encodeMovementPackets configured transport movements |> ok
            let budget = min target (min transport application)
            let ids = packets |> List.collect (fun bytes ->
                Expect.isLessThanOrEqual bytes.Length budget "Whole protobuf envelope fits."
                (parse bytes).PlayersMoved.Players |> Seq.map _.PlayerId |> Seq.toList)
            Expect.isGreaterThan packets.Length 1 "Fixture requires splitting."
            Expect.equal ids [1UL .. 130UL] "Splitting preserves every entry and order."
        Expect.isError (ProtocolCodec.encodeMovementPackets codec 0 movements) "Unavailable peer cannot encode movement."

    testCase "an indivisible movement can fragment alone but cannot bypass application limit" <| fun _ ->
        let wire = wireLocation ()
        wire.Location.LocationId.PluginName <- String.replicate 240 "a" + ".esm"
        wire.Location.LocationName <- String.replicate 128 "界"
        let location = wire |> movementPacket |> update |> playerUpdate |> apply |> _.Location
        let single = movementBatch [pid 2UL, location]
        let singleSize = (ProtocolCodec.encodeServer codec (ServerResponse.PlayersMoved single) |> ok).Length
        let movements = movementBatch [pid 1UL, ValueNone; pid 2UL, location; pid 3UL, ValueNone]
        let configured = configured { config with MovementPacketTargetBytes = 1200 }
        let packets = ProtocolCodec.encodeMovementPackets configured 548 movements |> ok
        Expect.equal (packets |> List.map (fun bytes -> (parse bytes).PlayersMoved.Players.Count)) [1; 1; 1] "Fallback is isolated."
        Expect.isGreaterThan singleSize 548 "Long labels exceed the negotiated payload budget."
        Expect.equal packets[1].Length singleSize "Large entry is retained intact."
        let strict = ProtocolCodec.create { config with MaxPacketBytes = singleSize - 1 } |> ok
        Expect.isError (ProtocolCodec.encodeMovementPackets strict 548 movements) "No partial result when any entry exceeds the strict cap."

    testCase "source movement timestamp survives domain and both replication shapes" <| fun _ ->
        for stamp in [0UL; 123456789UL; UInt64.MaxValue] do
            let wire = wireLocation()
            wire.SampledAtUs <- stamp
            let state = movementPacket wire |> update |> playerUpdate |> apply
            let location = state.Location |> ValueOption.get
            Expect.equal location.SampledAtUs stamp "No clock conversion on the server."

            let moved = ProtocolCodec.encodeServer codec (ServerResponse.PlayersMoved (movementBatch [pid 7UL, state.Location])) |> ok |> parse
            Expect.equal moved.PlayersMoved.Players[0].Location.SampledAtUs stamp "Compact movement retains time."
            let full = ProtocolCodec.encodeServer codec (ServerResponse.PlayerUpdated state) |> ok |> parse
            Expect.equal full.PlayerUpdated.Player.Location.SampledAtUs stamp "Snapshots retain the same measurement."

    testCase "metadata replication encodes omitted and empty components independently" <| fun _ ->
        let packet = ProtocolCodec.encodeServer codec (ServerResponse.PlayerMetadataChanged(pid 7UL, ValueSome Map.empty, ValueNone)) |> ok |> parse
        Expect.isNotNull packet.PlayerMetadataChanged.ActorValues "Present empty means clear."
        Expect.isNull packet.PlayerMetadataChanged.Details "Absent details means preserve."
        let packet = ProtocolCodec.encodeServer codec (ServerResponse.PlayerMetadataChanged(pid 7UL, ValueNone, ValueSome PlayerDetails.empty)) |> ok |> parse
        Expect.isNull packet.PlayerMetadataChanged.ActorValues "Absent values mean preserve."
        Expect.isNotNull packet.PlayerMetadataChanged.Details "Details reset is explicit."
        Expect.isError (ProtocolCodec.encodeServer codec (ServerResponse.PlayerMetadataChanged(pid 7UL, ValueNone, ValueNone))) "Empty patch is not an update."


    testCase "open session carries only an opaque ticket without changing it" <| fun _ ->
        let ticket = String('a', 41) + "-_"
        let packet = Dreamsleeve.Protocol.Chat.ClientPacket(
            ProtocolVersion = ProtocolCodec.Version, RequestId = 42UL,
            OpenSession = Dreamsleeve.Protocol.Chat.OpenSession(SessionTicket = ticket))
        let result = decode packet |> ok
        Expect.equal result.RequestId 42UL "request correlation"
        match result.Command with
        | ClientCommand.OpenSession actual -> Expect.equal actual ticket "credential preserved exactly"
        | ClientCommand.SendChat _ | ClientCommand.UpdatePlayer _ -> failtest "Wrong command"

        for invalid in [ ""; String('a', 42); String('a', 44); String('a', 42) + " "; String('a', 42) + "é" ] do
            packet.OpenSession.SessionTicket <- invalid
            Expect.equal (decode packet |> error).Failure (ProtocolCodecFailure.InvalidPayload "session_ticket") "bounded base64url ticket"

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
        let broadcast = ProtocolCodec.encodeServer codec (ServerResponse.ChatPublished message) |> ok |> parse
        let own = ProtocolCodec.encodeServer codec (ServerResponse.ChatAccepted(42UL, message)) |> ok |> parse
        Expect.isFalse broadcast.HasRequestId "others have no request ID"
        Expect.equal own.RequestId 42UL "author correlation"
        Expect.equal own.ChatPublished broadcast.ChatPublished "same accepted event"
        Expect.equal own.ChatPublished.Message.MessageId UInt64.MaxValue "server ID"
        Expect.equal own.ChatPublished.Message.SentAtUnixMs -1L "signed Unix milliseconds"
        Expect.equal own.ChatPublished.Message.Author.DisplayName "Игрок" "author profile"
        Expect.isNull own.SessionOpened "no history in normal publication"

    testCase "welcome requires unique online IDs self membership and ordered channel history" <| fun _ ->
        let encode value = ProtocolCodec.encodeServer codec (ServerResponse.SessionOpened(1UL, value))
        let packet = encode welcome |> ok |> parse
        Expect.equal packet.SessionOpened.Players.Count 1 "online"
        Expect.equal packet.SessionOpened.RecentMessages.Count 1 "initial retained history"
        Expect.isError (encode { welcome with SelfPlayerId = pid 8UL }) "self absent"
        Expect.isError (encode { welcome with Players = [snapshot; snapshot] }) "duplicate player"
        Expect.isError (encode { welcome with RecentMessages = [message; message] }) "duplicate/out-of-order message"
        Expect.isError (encode { welcome with Players = List.replicate (config.MaxInitialPlayers + 1) snapshot }) "count bound"
        Expect.isError (ProtocolCodec.encodeServer codec (ServerResponse.SessionOpened(0UL, welcome))) "zero correlation"
        Expect.isError (ProtocolCodec.encodeServer codec (ServerResponse.ChatAccepted(0UL, message))) "zero chat correlation"

    testCase "replies require an ID while presence notifications have no correlation" <| fun _ ->
        let rejection = { Code = RequestRejectionCode.AuthenticationFailed; Message = "Отказ"; Field = "text" }
        let packet = ProtocolCodec.encodeServer codec (ServerResponse.RequestRejected(9UL, rejection)) |> ok |> parse
        Expect.equal packet.RequestRejected.Code RequestRejectionCode.AuthenticationFailed "shared protobuf code"
        Expect.equal packet.RequestId 9UL "required correlation"
        Expect.isError (ProtocolCodec.encodeServer codec (ServerResponse.RequestRejected(0UL, rejection))) "zero ID"
        for response in [ServerResponse.PlayerJoined snapshot; ServerResponse.PlayerLeft (pid 7UL)] do
            let packet = ProtocolCodec.encodeServer codec response |> ok |> parse
            Expect.isFalse packet.HasRequestId "notifications cannot carry a request ID"

    testCase "configured byte limits apply at the exact boundary in both directions" <| fun _ ->
        let bytes = (send 1UL "hello").ToByteArray()
        let exact = { config with MaxPacketBytes = bytes.Length }
        Expect.isOk (ProtocolCodec.decodeClient (configured exact) bytes) "exact input limit"
        let small = { exact with MaxPacketBytes = bytes.Length - 1 }
        Expect.equal (ProtocolCodec.decodeClient (configured small) bytes |> error).Failure ProtocolCodecFailure.PacketTooLarge "one byte too large"
        let response = (ServerResponse.ChatPublished message)
        let encoded = ProtocolCodec.encodeServer codec response |> ok
        let exact = { config with MaxPacketBytes = encoded.Length }
        Expect.isOk (ProtocolCodec.encodeServer (configured exact) response) "exact output limit"
        let small = { exact with MaxPacketBytes = encoded.Length - 1 }
        Expect.equal (ProtocolCodec.encodeServer (configured small) response |> error).Failure ProtocolCodecFailure.PacketTooLarge "output above configured limit"

    testCase "bootstrap counts and empty initial history are configurable" <| fun _ ->
        let second = PlayerData.create (pid 8UL) profile.Username profile.DisplayName |> Player.create |> Player.snapshot
        let response = ServerResponse.SessionOpened(1UL, { welcome with Players = [snapshot; second] })
        Expect.isOk (ProtocolCodec.encodeServer (configured { config with MaxInitialPlayers = 2 }) response) "two unique players"
        Expect.isError (ProtocolCodec.encodeServer (configured { config with MaxInitialPlayers = 1 }) response) "configured count"
        let noHistory = { config with MaxRecentMessages = 0 }
        Expect.isError (ProtocolCodec.encodeServer (configured noHistory) response) "history disabled"
        let response = ServerResponse.SessionOpened(1UL, { welcome with RecentMessages = [] })
        Expect.isOk (ProtocolCodec.encodeServer (configured noHistory) response) "empty history allowed"

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
            Expect.isError (ProtocolCodec.create invalid) "codec startup validation"

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
            Expect.isError (ServerConfig.applyPacketLimits { settings with MaxPacketBytes = 0 } host) "reject invalid config"
            Expect.equal host.MaximumPacketSize (unativeint bytes.Length) "no partial mutation"
        finally
            enet.ENET_API.enet_deinitialize()

    testCase "server emits only defined nonzero rejection codes" <| fun _ ->
        let encode code =
            ProtocolCodec.encodeServer codec
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

    testCase "zero scalar differs from unset and resource values are not clamped" <| fun _ ->
        let zero = scalarEntry "Skyrim:SpeedMult" 0.0f
        let resource = Dreamsleeve.Protocol.Chat.ActorValueEntry(
            Key = "skyrim:health", DisplayName = "Health",
            Resource = Dreamsleeve.Protocol.Chat.ResourceActorValue(Current = 120.0f, Maximum = 100.0f))
        let decoded = valuesPacket [zero; resource] |> update |> playerUpdate |> apply
        Expect.equal decoded.Location ValueNone "absent location is unknown"
        let speed = decoded.ActorValues[ActorValueKey.create 128 "skyrim:speedmult" |> ok].State
        Expect.equal (ActorValueState.current speed |> ActorValue.value) 0.0f "zero survived its oneof presence"
        Expect.equal (ActorValueState.tryMaximum speed) ValueNone "scalar has no maximum"
        let health = decoded.ActorValues[ActorValueKey.create 128 "skyrim:health" |> ok].State
        Expect.equal (ActorValueState.current health |> ActorValue.value) 120.0f "above maximum preserved"
        Expect.equal (ActorValueState.tryMaximum health |> ValueOption.map ActorValue.value) (ValueSome 100.0f) "maximum preserved"
        zero.ClearValue()
        Expect.equal (valuesPacket [zero] |> update |> error).Failure
            (ProtocolCodecFailure.InvalidPayload "actor_value.value") "unset is not scalar zero"

    testCase "present zero coordinates are distinct from an unknown location" <| fun _ ->
        let decoded = movementPacket (wireLocation()) |> update |> playerUpdate |> apply
        let place = decoded.Location |> ValueOption.get
        Expect.equal place.Position Position.zero "all-zero coordinates are valid"
        Expect.equal place.Rotation Rotation.zero "all-zero radians are valid"
        Expect.equal (PluginName.value place.Location.LocationId.PluginName) "skyrim.esm" "canonical identity"
        let noLocation = movementPacket null |> update |> playerUpdate |> apply
        Expect.equal noLocation.Location ValueNone "unknown is represented by presence"
        for field in [0; 1; 2; 3] do
            let broken = wireLocation()
            match field with
            | 0 -> broken.Location <- null
            | 1 -> broken.Location.LocationId <- null
            | 2 -> broken.Position <- null
            | _ -> broken.Rotation <- null
            Expect.equal (movementPacket broken |> update |> error).Failure
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
                let failure = movementPacket place |> update |> error
                Expect.equal failure.RequestId (Some 91UL) "caller can reject without applying the sample"
                match failure.Failure with
                | ProtocolCodecFailure.InvalidDomain(DomainError.NonFiniteNumber _) -> ()
                | value -> failtestf "Unexpected failure %A" value
            for entry in [
                scalarEntry "skyrim:health" bad
                Dreamsleeve.Protocol.Chat.ActorValueEntry(Key = "skyrim:health", Resource = Dreamsleeve.Protocol.Chat.ResourceActorValue(Current = bad))
                Dreamsleeve.Protocol.Chat.ActorValueEntry(Key = "skyrim:health", Resource = Dreamsleeve.Protocol.Chat.ResourceActorValue(Maximum = bad))
            ] do
                Expect.isError (valuesPacket [entry] |> update) "every actor value component must be finite"

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
        Expect.isError (decodeWith {limits with PluginName = 2} (movementPacket place)) "plugin key limit"
        Expect.isError (decodeWith {limits with LocationName = 2} (movementPacket place)) "place label limit"

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
        let joined = ProtocolCodec.encodeServer codec (ServerResponse.PlayerJoined state) |> ok |> parse
        let changed = ProtocolCodec.encodeServer codec (ServerResponse.PlayerUpdated state) |> ok |> parse
        let boot = ProtocolCodec.encodeServer codec (ServerResponse.SessionOpened(1UL, {welcome with Players = [state]})) |> ok |> parse
        Expect.equal joined.PlayerJoined.Player changed.PlayerUpdated.Player "same complete state"
        Expect.equal boot.SessionOpened.Players[0] joined.PlayerJoined.Player "bootstrap agrees with replication"
        Expect.equal changed.PlayerUpdated.Player.CharacterGeneration 1UL "save generation retained"
        Expect.equal changed.PlayerUpdated.Player.CharacterName "Nerevar" "name retained"
        Expect.equal changed.PlayerUpdated.Player.ActorValues[0].ValueCase Dreamsleeve.Protocol.Chat.ActorValueEntry.ValueOneofCase.Scalar "zero is a present scalar"
        Expect.isFalse changed.HasRequestId "periodic replication is a notification"

        let ack = ProtocolCodec.encodeServer codec (ServerResponse.PlayerUpdateAccepted 91UL) |> ok |> parse
        Expect.equal ack.RequestId 91UL "admitted command has its own acknowledgement"
        Expect.isNotNull ack.PlayerUpdateAccepted "empty but present acknowledgement"
        Expect.isError (ProtocolCodec.encodeServer codec (ServerResponse.PlayerUpdateAccepted 0UL)) "zero acknowledgement ID rejected"

        for place in [state.Location; ValueNone] do
            let moved = ProtocolCodec.encodeServer codec (ServerResponse.PlayersMoved (movementBatch [pid 7UL, place])) |> ok |> parse
            Expect.isFalse moved.HasRequestId "movement has no command correlation"
            Expect.equal moved.PlayersMoved.Players[0].PlayerId 7UL "identity retained"
            Expect.equal (isNull moved.PlayersMoved.Players[0].Location) place.IsNone "unknown location survives"
            Expect.isNull moved.PlayerUpdated "movement does not resend actor values"

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
        let encoded = ProtocolCodec.encodeServer codec (ServerResponse.PlayerUpdated state) |> ok |> parse

        let actual = encoded.PlayerUpdated.Player.Details
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
            let encoded = ProtocolCodec.encodeServer codec (ServerResponse.PlayerUpdated state) |> ok |> parse
            Expect.equal encoded.PlayerUpdated.Player.Details.Activity.Kind kind "activity mapping"
        for difficulty in Enum.GetValues<Dreamsleeve.Protocol.Chat.LockDifficulty>() do
            let source = Dreamsleeve.Protocol.Chat.PlayerDetails(Activity = Dreamsleeve.Protocol.Chat.PlayerActivity(
                Kind = Dreamsleeve.Protocol.Chat.ActivityKind.Lockpicking, LockDifficulty = difficulty))
            let state = update (Dreamsleeve.Protocol.Chat.UpdatePlayer(SetDetails = source)) |> playerUpdate |> apply
            let encoded = ProtocolCodec.encodeServer codec (ServerResponse.PlayerUpdated state) |> ok |> parse
            Expect.equal encoded.PlayerUpdated.Player.Details.Activity.LockDifficulty difficulty "difficulty mapping"

    testCase "zero and absent level survive client command and server replication" <| fun _ ->
        for level in [ValueNone; ValueSome 0u; ValueSome UInt32.MaxValue] do
            let source = Dreamsleeve.Protocol.Chat.PlayerDetails(Activity = Dreamsleeve.Protocol.Chat.PlayerActivity())
            level |> ValueOption.iter (fun value -> source.Level <- value)
            let state = update (Dreamsleeve.Protocol.Chat.UpdatePlayer(SetDetails = source)) |> playerUpdate |> apply
            Expect.equal state.Details.Level level "Client-reported level is preserved."
            let encoded = ProtocolCodec.encodeServer codec (ServerResponse.PlayerUpdated state) |> ok |> parse
            let actual = encoded.PlayerUpdated.Player.Details
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
]
