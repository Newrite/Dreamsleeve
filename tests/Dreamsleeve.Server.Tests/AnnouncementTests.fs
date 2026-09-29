module Dreamsleeve.Server.Tests.AnnouncementTests

open System
open System.IO
open Expecto
open Google.Protobuf
open Dreamsleeve.Server
open Dreamsleeve.Server.Domain
open Dreamsleeve.Server.Core

let private ok = function
    | Ok value -> value
    | Error error -> failtestf "Expected success, got %A" error

let private error = function
    | Error value -> value
    | Ok _ -> failtest "Expected error"

let private config = ServerConfig.defaults
let private codec = ProtocolCodec.create config |> ok
let private pid raw = PlayerId.create raw |> ok
let private channel = ChatChannelKind.channelId ChatChannelKind.System
let private profile = PlayerData.create (pid 7UL) (Username.create 32 "player" |> ok) (DisplayName.create 64 "Игрок" |> ok)

type private WireKind = Dreamsleeve.Protocol.Chat.AnnouncementKind
type private WireClientSource = Dreamsleeve.Protocol.Chat.ClientAnnouncementSource

let private post text kind source signature : Result<ClientRequest, ProtocolCodecError> =
    Dreamsleeve.Protocol.Chat.ClientPacket(
        ProtocolVersion = ProtocolCodec.Version, RequestId = 5UL,
        PostAnnouncement =
            Dreamsleeve.Protocol.Chat.PostAnnouncement(
                ChannelId = ChatChannelId.value channel, Text = text, Kind = kind, Source = source, Signature = signature))
    |> fun packet -> ProtocolCodec.decodeClient codec (packet.ToByteArray())

let private request (result: Result<ClientRequest, ProtocolCodecError>) =
    match (ok result).Command with
    | ClientCommand.PostAnnouncement value -> value
    | ClientCommand.OpenSession _ | ClientCommand.SendChat _ | ClientCommand.UpdatePlayer _ -> failtest "Expected announcement"

let private withFile (text: string) action =
    let path = Path.Combine(Path.GetTempPath(), sprintf "dreamsleeve-announcements-%O.toml" (Guid.NewGuid()))
    try
        File.WriteAllText(path, text)
        action path
    finally
        File.Delete path

let private load text =
    withFile text (fun path ->
        match Configuration.parse [|"--config"; path|] with
        | Ok (LaunchCommand.Run settings) -> Ok settings
        | Ok other -> failtestf "Unexpected launch %A" other
        | Error failure -> Error failure)

let private example () =
    let rec find (directory: DirectoryInfo) =
        let candidate = Path.Combine(directory.FullName, "src", "Dreamsleeve.Server", "server.example.toml")
        if File.Exists candidate then candidate
        elif isNull directory.Parent then failtest "server.example.toml not found"
        else find directory.Parent
    File.ReadAllText(find (DirectoryInfo AppContext.BaseDirectory))

let private text value = ChatMessageText.create 2000 value |> ok

let tests = testList "Announcements" [
    testCase "signature keeps the label as sent and refuses blank, multiline and long labels" <| fun _ ->
        Expect.equal (AnnouncementSignature.create 64 " Мод «Смерти» " |> ok |> AnnouncementSignature.value) " Мод «Смерти» " "kept exactly"
        for invalid in [""; "   "; "two\nlines"; "tab\there"; "bell\u0007"; "sep "] do
            Expect.isError (AnnouncementSignature.create 64 invalid) $"invalid label {invalid}"
        Expect.equal (AnnouncementSignature.create 3 "abcd") (Error(DomainError.InvalidText("AnnouncementSignature", TextError.TooLong 3))) "length in scalars"

    testCase "channel kinds own their IDs and decide what a channel carries" <| fun _ ->
        let globalId, systemId = ChatChannelKind.channelId ChatChannelKind.Global, ChatChannelKind.channelId ChatChannelKind.System
        Expect.notEqual globalId systemId "one ID per server-wide kind"
        Expect.equal (ChatChannelKind.tryOfChannelId systemId) (Some ChatChannelKind.System) "ID maps back to its kind"
        Expect.equal (ChatChannelKind.tryOfChannelId (ChatChannelId.create 99UL |> ok)) None "unknown channel"
        let globalChat = Chat.create ChatChannelKind.Global 4 |> ok
        Chat.join profile.PlayerId globalChat |> ignore
        let announcement =
            ChatMessage.create (ChatMessageId.create 1UL |> ok) globalId profile ValueNone (text "event") DateTimeOffset.UnixEpoch
            |> ChatMessage.withAnnouncement (Announcement.fromClient ClientAnnouncementSource.ThirdParty AnnouncementKind.Event ValueNone)
        Expect.equal (Chat.append announcement globalChat) (Error DomainError.ChannelMismatch) "no announcements in a global channel"
        let systemChat = Chat.create ChatChannelKind.System 4 |> ok
        Chat.join profile.PlayerId systemChat |> ignore
        let chat = ChatMessage.create (ChatMessageId.create 1UL |> ok) systemId profile ValueNone (text "chat") DateTimeOffset.UnixEpoch
        Expect.equal (Chat.append chat systemChat) (Error DomainError.ChannelMismatch) "no chat in the system channel"

    testCase "server announcements have no author and need no membership; client ones do" <| fun _ ->
        let chat = Chat.create ChatChannelKind.System 4 |> ok
        let server = ChatMessage.serverAnnouncement (ChatMessageId.create 1UL |> ok) channel AnnouncementKind.Periodic (text "notice") DateTimeOffset.UnixEpoch
        Expect.equal server.Author ValueNone "the system is not a player"
        Chat.append server chat |> ok
        let client =
            ChatMessage.create (ChatMessageId.create 2UL |> ok) channel profile ValueNone (text "event") DateTimeOffset.UnixEpoch
            |> ChatMessage.withAnnouncement (Announcement.fromClient ClientAnnouncementSource.ThirdParty AnnouncementKind.Event ValueNone)
        Expect.equal (Chat.append client chat) (Error(DomainError.NotChatMember profile.PlayerId)) "client origin is a member action"
        Chat.join profile.PlayerId chat |> ignore
        Chat.append client chat |> ok
        let page = Chat.historyAfter ValueNone 10 chat |> ok
        Expect.equal (page.Messages |> List.map _.Announcement) [ValueSome(Announcement.server AnnouncementKind.Periodic); client.Announcement] "stored with markers"
        Expect.isFalse (Announcement.clientMayRequest AnnouncementKind.Admin) "admin is server only"
        Expect.isFalse (Announcement.clientMayRequest AnnouncementKind.Periodic) "periodic is server only"

    testCase "post announcement decodes on the chat lane and keeps the signature" <| fun _ ->
        let decoded = post "Игрок пал" WireKind.Event WireClientSource.ThirdParty "DeathMod"
        let value = request decoded
        Expect.equal value.Source ClientAnnouncementSource.ThirdParty "requested origin"
        Expect.equal value.Kind AnnouncementKind.Event "kind"
        Expect.equal (value.Signature |> ValueOption.map AnnouncementSignature.value) (ValueSome "DeathMod") "signature"
        Expect.equal value.ChannelId channel "target channel"
        Expect.equal (ProtocolCodec.requestLane (ok decoded: ClientRequest)) DeliveryLane.Chat "same lane as chat"
        let trusted = post "hello" WireKind.Announcement WireClientSource.TrustedClient "" |> request
        Expect.equal trusted.Signature ValueNone "trusted client may omit the label"

    testCase "server-only kinds, unknown origins and missing third-party labels are refused" <| fun _ ->
        for kind in [WireKind.Admin; WireKind.Periodic; WireKind.Unspecified; enum<WireKind> 42] do
            Expect.equal (post "x" kind WireClientSource.ThirdParty "Mod" |> error).Failure (ProtocolCodecFailure.InvalidPayload "kind") $"kind {kind}"
        for source in [WireClientSource.Unspecified; enum<WireClientSource> 7] do
            Expect.equal (post "x" WireKind.Event source "Mod" |> error).Failure (ProtocolCodecFailure.InvalidPayload "source") $"source {source}"
        let missing = post "x" WireKind.Event WireClientSource.ThirdParty "" |> error
        Expect.equal missing.Failure (ProtocolCodecFailure.InvalidDomain(DomainError.InvalidText("AnnouncementSignature", TextError.Missing))) "label required"
        Expect.equal missing.RequestId (Some 5UL) "refusal stays correlated"
        let long = post (String('x', config.ChatInput.AnnouncementText + 1)) WireKind.Event WireClientSource.ThirdParty "Mod" |> error
        Expect.equal long.Failure (ProtocolCodecFailure.InvalidDomain(DomainError.InvalidText("AnnouncementText", TextError.TooLong config.ChatInput.AnnouncementText))) "own text limit"
        let label = post "x" WireKind.Event WireClientSource.ThirdParty (String('m', config.ChatInput.AnnouncementSignature + 1)) |> error
        Expect.equal label.Failure (ProtocolCodecFailure.InvalidDomain(DomainError.InvalidText("AnnouncementSignature", TextError.TooLong config.ChatInput.AnnouncementSignature))) "label limit"

    testCase "announcements travel on ChatMessage and the welcome announces the policy" <| fun _ ->
        let message =
            ChatMessage.create (ChatMessageId.create 3UL |> ok) channel profile ValueNone (text "event") DateTimeOffset.UnixEpoch
            |> ChatMessage.withAnnouncement (Announcement.fromClient ClientAnnouncementSource.ThirdParty AnnouncementKind.Event
                                                 (ValueSome(AnnouncementSignature.create 64 "DeathMod" |> ok)))
        let wire = ProtocolCodec.encodeServer codec (ServerResponse.ChatPublished message) |> ok |> Dreamsleeve.Protocol.Chat.ServerPacket.Parser.ParseFrom
        let announcement = wire.ChatPublished.Message.Announcement
        Expect.equal announcement.Source Dreamsleeve.Protocol.Chat.AnnouncementSource.ThirdParty "origin"
        Expect.equal announcement.Kind WireKind.Event "kind"
        Expect.equal announcement.Signature "DeathMod" "label"
        Expect.equal wire.ChatPublished.Message.Author.PlayerId 7UL "the posting player"
        let server = ChatMessage.serverAnnouncement (ChatMessageId.create 4UL |> ok) channel AnnouncementKind.Admin (text "notice") DateTimeOffset.UnixEpoch
        let serverWire = ProtocolCodec.encodeServer codec (ServerResponse.ChatPublished server) |> ok |> Dreamsleeve.Protocol.Chat.ServerPacket.Parser.ParseFrom
        Expect.isNull serverWire.ChatPublished.Message.Author "no fictitious player"
        Expect.equal serverWire.ChatPublished.Message.Announcement.Source Dreamsleeve.Protocol.Chat.AnnouncementSource.Server "server origin"

        let welcome = {
            SelfPlayerId = pid 7UL; Players = [Player.create profile |> Player.snapshot]
            Channels = [ { ChannelId = channel; Kind = ChatChannelKind.System; Messages = [server] } ]
            AnnouncementSources = [ClientAnnouncementSource.TrustedClient]
        }
        let opened = ProtocolCodec.encodeServer codec (ServerResponse.SessionOpened(1UL, welcome)) |> ok |> Dreamsleeve.Protocol.Chat.ServerPacket.Parser.ParseFrom
        let policy = opened.SessionOpened.Announcements
        Expect.sequenceEqual policy.AllowedSources [WireClientSource.TrustedClient] "admitted origins"
        Expect.equal policy.MaxTextLength (uint32 config.ChatInput.AnnouncementText) "text limit"
        Expect.equal policy.MaxSignatureLength (uint32 config.ChatInput.AnnouncementSignature) "label limit"

    testCase "admission follows the per-source switches" <| fun _ ->
        let requestFrom source : AnnouncementRequest = { ChannelId = channel; Text = text "x"; Kind = AnnouncementKind.Event; Source = source; Signature = ValueNone }
        let options = { AnnouncementOptions.defaults with ThirdParty = { Enabled = false } }
        Expect.equal (AnnouncementOptions.allowedSources AnnouncementOptions.defaults)
            [ClientAnnouncementSource.TrustedClient; ClientAnnouncementSource.ThirdParty] "all by default"
        Expect.equal (AnnouncementOptions.allowedSources options) [ClientAnnouncementSource.TrustedClient] "third party off"
        Expect.isOk (AnnouncementOptions.admit options (requestFrom ClientAnnouncementSource.TrustedClient)) "trusted admitted"
        let refusal = AnnouncementOptions.admit options (requestFrom ClientAnnouncementSource.ThirdParty) |> error
        Expect.equal refusal.Code RequestRejectionCode.AnnouncementNotAllowed "own refusal code"
        Expect.equal refusal.Field "source" "field"

    testCase "schedule publishes once-off and periodic entries without replaying missed periods" <| fun _ ->
        let entry kind delay interval = { Text = $"{kind} text"; Kind = kind; DelaySeconds = delay; IntervalSeconds = interval }
        let resolved =
            AnnouncementOptions.resolve config.ChatInput
                { AnnouncementOptions.defaults with Scheduled = [entry "Admin" 0 0; entry "periodic" 5 10] }
            |> ok
        Expect.equal (resolved |> List.map (fun (value, _) -> value.Kind)) [AnnouncementKind.Admin; AnnouncementKind.Periodic] "kinds, case-insensitive"
        let schedule = AnnouncementSchedule.create 1000L resolved
        let kinds due = due |> List.map (fun (value: ServerAnnouncement) -> value.Kind)
        Expect.equal (kinds (AnnouncementSchedule.due 1000L schedule)) [AnnouncementKind.Admin] "once at start"
        Expect.isEmpty (AnnouncementSchedule.due 5999L schedule) "periodic waits for its delay"
        Expect.equal (kinds (AnnouncementSchedule.due 6000L schedule)) [AnnouncementKind.Periodic] "first period"
        Expect.equal (kinds (AnnouncementSchedule.due 60000L schedule)) [AnnouncementKind.Periodic] "one publication after a stall"
        Expect.isEmpty (AnnouncementSchedule.due 69999L schedule) "next period counts from the publication"
        Expect.equal (kinds (AnnouncementSchedule.due 70000L schedule)) [AnnouncementKind.Periodic] "periodic continues"
        for invalid in [entry "Chat" 0 0; entry "Admin" -1 0; entry "Admin" 0 5; { entry "Admin" 0 0 with Text = " " }] do
            Expect.isError (AnnouncementOptions.resolve config.ChatInput { AnnouncementOptions.defaults with Scheduled = [invalid] }) $"invalid {invalid}"

    testCase "old configuration keeps every source; the section and schedule load from TOML" <| fun _ ->
        let settings = load "[Server]\nPort = 9000\n" |> ok
        Expect.equal settings.Announcements AnnouncementOptions.defaults "missing section keeps defaults"
        let source = String.concat "\n" [
            "[Announcements]"
            "RateBurst = 1"
            "[Announcements.ThirdParty]"
            "Enabled = false"
            "[[Announcements.Scheduled]]"
            "Text = 'Добро пожаловать'"
            "Kind = 'Periodic'"
            "IntervalSeconds = 600"
            "[[Announcements.Scheduled]]"
            "Text = 'Рестарт в полночь'"
            "" ]
        let loaded = (load source |> ok).Announcements
        Expect.equal loaded.RateBurst 1 "rate"
        Expect.isTrue loaded.TrustedClient.Enabled "unset source keeps default"
        Expect.isFalse loaded.ThirdParty.Enabled "switched off"
        Expect.equal loaded.Scheduled [
            { Text = "Добро пожаловать"; Kind = "Periodic"; DelaySeconds = 0; IntervalSeconds = 600 }
            { Text = "Рестарт в полночь"; Kind = "Announcement"; DelaySeconds = 0; IntervalSeconds = 0 } ] "table array with defaults"
        for invalid in ["[Announcements]\nTypo = 1\n"
                        "[[Announcements.Scheduled]]\nText = 'x'\nColor = 'red'\n"; "[Announcements]\nScheduled = 'x'\n"
                        "[Announcements.ThirdParty]\nEnabled = 'yes'\n"; "[Server.ChatInput]\nAnnouncementSignature = 129\n"] do
            Expect.isError (load invalid) $"invalid: {invalid}"

    testCase "bundled server example loads with every client source admitted" <| fun _ ->
        let settings = load (example ()) |> ok
        Expect.equal settings.Announcements AnnouncementOptions.defaults "documented defaults"
        Expect.equal settings.Server.ChatInput config.ChatInput "documented limits"
        // The commented sample entry: its header and the four keys below it.
        let lines = (example ()).Replace("\r\n", "\n").Split('\n')
        let first = lines |> Array.findIndex (fun line -> line = "# [[Announcements.Scheduled]]")
        let uncommented =
            lines |> Array.mapi (fun index line -> if index >= first && index <= first + 4 then line.Substring 2 else line)
            |> String.concat "\n"
        let scheduled = (load uncommented |> ok).Announcements.Scheduled
        Expect.equal scheduled.Length 1 "the commented sample is valid"
]
