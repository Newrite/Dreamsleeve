module Dreamsleeve.Server.Tests.CodecTests

open System
open Expecto
open Google.Protobuf
open Dreamsleeve.Server.Domain
open Dreamsleeve.Server.Core

let private config = ServerConfig.defaults

let private ok = function
    | Ok value -> value
    | Error error -> failtestf "Expected success, got %A" error

let private error = function
    | Error value -> value
    | Ok _ -> failtest "Expected error"

let private codec = ChatCodec.create config |> ok
let private configured settings = ChatCodec.create settings |> ok

let private pid raw = PlayerId.create raw |> ok
let private channel = ChatChannelId.create 1UL |> ok
let private profile = PlayerData.create (pid 7UL) (Username.create 32 "player" |> ok) (DisplayName.create 64 "Игрок" |> ok)
let private message =
    ChatMessage.create (ChatMessageId.create UInt64.MaxValue |> ok) channel profile
        (ChatMessageText.create 2000 "Привет\nworld" |> ok) (DateTimeOffset.FromUnixTimeMilliseconds(-1L))
let private parse bytes = Dreamsleeve.Protocol.Chat.ServerPacket.Parser.ParseFrom(bytes: byte array)
let private send requestId text =
    Dreamsleeve.Protocol.Chat.ClientPacket(
        ProtocolVersion = 1u, RequestId = requestId,
        SendChat = Dreamsleeve.Protocol.Chat.SendChat(ChannelId = 1UL, Text = text))
let private decode (packet: Dreamsleeve.Protocol.Chat.ClientPacket) =
    ChatCodec.decodeClient codec (packet.ToByteArray())
let private welcome = {
    SelfPlayerId = pid 7UL
    GlobalChannelId = channel
    Players = [profile]
    RecentMessages = [message]
}

let tests = testList "Dreamsleeve.Server.Codec" [
    testCase "open session canonicalizes names through domain factories" <| fun _ ->
        let packet = Dreamsleeve.Protocol.Chat.ClientPacket(
            ProtocolVersion = 1u, RequestId = 42UL,
            OpenSession = Dreamsleeve.Protocol.Chat.OpenSession(Username = " USER ", DisplayName = " e\u0301 "))
        let result = decode packet |> ok
        Expect.equal result.RequestId 42UL "request correlation"
        match result.Command with
        | ChatCommand.OpenSession(username, displayName) ->
            Expect.equal (Username.value username) "user" "canonical username"
            Expect.equal (DisplayName.value displayName) "é" "NFC display name"
        | _ -> failtest "Wrong command"

    testCase "send chat preserves text and full uint64 request IDs" <| fun _ ->
        let result = send UInt64.MaxValue "Привет\nworld" |> decode |> ok
        Expect.equal result.RequestId UInt64.MaxValue "no signed narrowing"
        match result.Command with
        | ChatCommand.SendChat(channelId, text) ->
            Expect.equal channelId channel "channel"
            Expect.equal (ChatMessageText.value text) "Привет\nworld" "original multiline text"
        | _ -> failtest "Wrong command"

    testCase "domain validation errors retain request ID without creating commands" <| fun _ ->
        let failure = send 9UL "   " |> decode |> error
        Expect.equal failure.RequestId (Some 9UL) "can reject the initiating request"
        match failure.Failure with
        | ChatCodecFailure.InvalidDomain(DomainError.InvalidText("ChatMessageText", TextError.Missing)) -> ()
        | _ -> failtestf "Unexpected error %A" failure
        let packet = send 10UL "ok"
        packet.SendChat.ChannelId <- 0UL
        match (decode packet |> error).Failure with
        | ChatCodecFailure.InvalidDomain(DomainError.InvalidId "ChatChannelId") -> ()
        | value -> failtestf "Unexpected error %A" value
        let shortLimits = { config with ChatInput = { config.ChatInput with MessageText = 1 } }
        Expect.isError (ChatCodec.decodeClient (configured shortLimits) ((send 11UL "long").ToByteArray())) "host-supplied limit"

    testCase "malformed oversized unsupported and empty envelopes fail at the boundary" <| fun _ ->
        for bytes in [ [||]; [| 0x5auy; 0xffuy |]; Array.zeroCreate (config.MaxPacketBytes + 1) ] do
            Expect.isError (ChatCodec.decodeClient codec bytes) "packet rejected"
        let packet = send 1UL "ok"
        packet.ProtocolVersion <- 2u
        Expect.equal (decode packet |> error).Failure (ChatCodecFailure.UnsupportedVersion 2u) "version"
        packet.ProtocolVersion <- 1u
        packet.RequestId <- 0UL
        Expect.equal (decode packet |> error).Failure (ChatCodecFailure.InvalidEnvelope "request_id") "nonzero correlation"
        packet.RequestId <- 1UL
        packet.ClearPayload()
        Expect.equal (decode packet |> error).Failure (ChatCodecFailure.InvalidPayload "payload") "no known payload"
        let futurePayload = [| 0x08uy; 0x01uy; 0x10uy; 0x01uy; 0x62uy; 0x00uy |]
        Expect.equal (ChatCodec.decodeClient codec futurePayload |> error).Failure
            (ChatCodecFailure.InvalidPayload "payload") "unknown oneof field is rejected"
        let additiveField = Array.append ((send 1UL "ok").ToByteArray()) [| 0x98uy; 0x06uy; 0x01uy |]
        Expect.isOk (ChatCodec.decodeClient codec additiveField) "unknown additive field preserves a known command"

    testCase "accepted chat differs only in recipient correlation and includes no history" <| fun _ ->
        let broadcast = ChatCodec.encodeServer codec (ChatResponse.ChatPublished message) |> ok |> parse
        let own = ChatCodec.encodeServer codec (ChatResponse.ChatAccepted(42UL, message)) |> ok |> parse
        Expect.isFalse broadcast.HasRequestId "others have no request ID"
        Expect.equal own.RequestId 42UL "author correlation"
        Expect.equal own.ChatPublished broadcast.ChatPublished "same accepted event"
        Expect.equal own.ChatPublished.Message.MessageId UInt64.MaxValue "server ID"
        Expect.equal own.ChatPublished.Message.SentAtUnixMs -1L "signed Unix milliseconds"
        Expect.equal own.ChatPublished.Message.Author.DisplayName "Игрок" "author profile"
        Expect.isNull own.SessionOpened "no history in normal publication"

    testCase "welcome requires unique online IDs self membership and ordered channel history" <| fun _ ->
        let encode value = ChatCodec.encodeServer codec (ChatResponse.SessionOpened(1UL, value))
        let packet = encode welcome |> ok |> parse
        Expect.equal packet.SessionOpened.Players.Count 1 "online"
        Expect.equal packet.SessionOpened.RecentMessages.Count 1 "initial retained history"
        Expect.isError (encode { welcome with SelfPlayerId = pid 8UL }) "self absent"
        Expect.isError (encode { welcome with Players = [profile; profile] }) "duplicate player"
        Expect.isError (encode { welcome with RecentMessages = [message; message] }) "duplicate/out-of-order message"
        Expect.isError (encode { welcome with Players = List.replicate (config.MaxInitialPlayers + 1) profile }) "count bound"
        Expect.isError (ChatCodec.encodeServer codec (ChatResponse.SessionOpened(0UL, welcome))) "zero correlation"
        Expect.isError (ChatCodec.encodeServer codec (ChatResponse.ChatAccepted(0UL, message))) "zero chat correlation"

    testCase "replies require an ID while presence notifications have no correlation" <| fun _ ->
        let rejection = { Code = RequestRejectionCode.UsernameTaken; Message = "Отказ"; Field = "text" }
        let packet = ChatCodec.encodeServer codec (ChatResponse.RequestRejected(9UL, rejection)) |> ok |> parse
        Expect.equal packet.RequestRejected.Code RequestRejectionCode.UsernameTaken "shared protobuf code"
        Expect.equal packet.RequestId 9UL "required correlation"
        Expect.isError (ChatCodec.encodeServer codec (ChatResponse.RequestRejected(0UL, rejection))) "zero ID"
        for response in [ChatResponse.PlayerJoined profile; ChatResponse.PlayerLeft (pid 7UL)] do
            let packet = ChatCodec.encodeServer codec response |> ok |> parse
            Expect.isFalse packet.HasRequestId "notifications cannot carry a request ID"

    testCase "configured byte limits apply at the exact boundary in both directions" <| fun _ ->
        let bytes = (send 1UL "hello").ToByteArray()
        let exact = { config with MaxPacketBytes = bytes.Length }
        Expect.isOk (ChatCodec.decodeClient (configured exact) bytes) "exact input limit"
        let small = { exact with MaxPacketBytes = bytes.Length - 1 }
        Expect.equal (ChatCodec.decodeClient (configured small) bytes |> error).Failure ChatCodecFailure.PacketTooLarge "one byte too large"
        let response = (ChatResponse.ChatPublished message)
        let encoded = ChatCodec.encodeServer codec response |> ok
        let exact = { config with MaxPacketBytes = encoded.Length }
        Expect.isOk (ChatCodec.encodeServer (configured exact) response) "exact output limit"
        let small = { exact with MaxPacketBytes = encoded.Length - 1 }
        Expect.equal (ChatCodec.encodeServer (configured small) response |> error).Failure ChatCodecFailure.PacketTooLarge "output above configured limit"

    testCase "bootstrap counts and empty initial history are configurable" <| fun _ ->
        let second = PlayerData.create (pid 8UL) profile.Username profile.DisplayName
        let response = ChatResponse.SessionOpened(1UL, { welcome with Players = [profile; second] })
        Expect.isOk (ChatCodec.encodeServer (configured { config with MaxInitialPlayers = 2 }) response) "two unique players"
        Expect.isError (ChatCodec.encodeServer (configured { config with MaxInitialPlayers = 1 }) response) "configured count"
        let noHistory = { config with MaxRecentMessages = 0 }
        Expect.isError (ChatCodec.encodeServer (configured noHistory) response) "history disabled"
        let response = ChatResponse.SessionOpened(1UL, { welcome with RecentMessages = [] })
        Expect.isOk (ChatCodec.encodeServer (configured noHistory) response) "empty history allowed"

    testCase "invalid config prevents codec creation" <| fun _ ->
        for invalid in [
            { config with MaxPacketBytes = 0 }
            { config with MaxWaitingData = config.MaxPacketBytes - 1 }
            { config with MaxInitialPlayers = 0 }
            { config with MaxRecentMessages = -1 }
            { config with ChatInput = { config.ChatInput with Username = 0 } }
            { config with ChatInput = { config.ChatInput with DisplayName = 0 } }
            { config with ChatInput = { config.ChatInput with MessageText = 0 } }
        ] do
            Expect.isError (ServerConfig.validate invalid) "startup validation"
            Expect.isError (ChatCodec.create invalid) "codec startup validation"

    testCase "one config sets yENet packet budgets and codec boundaries" <| fun _ ->
        Expect.equal (enet.ENET_API.enet_initialize()) 0 "initialize ENet"
        try
            use host = Enet.EnetHost.Create(Unchecked.defaultof<enet.ENetAddress>, 1un, 1un, 0u, 0u, Enet.EnetHostOption.Ipv4)
            let bytes = (send 1UL "hello").ToByteArray()
            let settings = { config with MaxPacketBytes = bytes.Length; MaxWaitingData = bytes.Length * 2 }
            ServerConfig.applyPacketLimits settings host |> ok
            Expect.equal host.MaximumPacketSize (unativeint bytes.Length) "actual host packet limit"
            Expect.equal host.MaximumWaitingData (unativeint (bytes.Length * 2)) "actual host waiting budget"
            Expect.isOk (ChatCodec.decodeClient (configured settings) bytes) "codec uses same config"
            Expect.isError (ServerConfig.applyPacketLimits { settings with MaxPacketBytes = 0 } host) "reject invalid config"
            Expect.equal host.MaximumPacketSize (unativeint bytes.Length) "no partial mutation"
        finally
            enet.ENET_API.enet_deinitialize()

    testCase "server emits only defined nonzero rejection codes" <| fun _ ->
        let encode code =
            ChatCodec.encodeServer codec
                (ChatResponse.RequestRejected(9UL, { Code = code; Message = "Rejected"; Field = "text" }))
        for code in Enum.GetValues<RequestRejectionCode>() do
            if code <> RequestRejectionCode.Unspecified then
                let packet = encode code |> ok |> parse
                Expect.equal packet.RequestRejected.Code code "generated enum survives serialization"
        for invalid in [RequestRejectionCode.Unspecified; enum<RequestRejectionCode> 0x7FFF0001; enum<RequestRejectionCode> -1] do
            Expect.equal (encode invalid |> error).Failure (ChatCodecFailure.InvalidPayload "code") "do not invent server codes"
]
