// Unnamed enum values are handled by an Enum.IsDefined guard below.
// Keep FS0025 enabled: every named case must still be listed explicitly.
#nowarn "104"

namespace Dreamsleeve.Server.Core

open System
open Google.Protobuf
open Dreamsleeve.Server.Domain

[<RequireQualifiedAccess>]
type ChatCodecFailure =
    | EmptyPacket
    | PacketTooLarge
    | MalformedPacket
    | UnsupportedVersion of uint32
    | InvalidEnvelope of string
    | InvalidPayload of string
    | InvalidDomain of DomainError

type ChatCodecError = {
    RequestId: uint64 option
    Failure: ChatCodecFailure
}

[<RequireQualifiedAccess>]
type ChatCommand =
    | OpenSession of sessionTicket: string
    | SendChat of ChatChannelId * ChatMessageText
    | UpdatePlayer of PlayerUpdate

type ChatRequest = {
    RequestId: uint64
    Command: ChatCommand
}

type ChatSessionOpened = {
    SelfPlayerId: PlayerId
    GlobalChannelId: ChatChannelId
    Players: PlayerSnapshot list
    RecentMessages: ChatMessage list
}

type RequestRejectionCode = Dreamsleeve.Protocol.Chat.RequestRejectionCode

type ChatRequestRejected = {
    Code: RequestRejectionCode
    Message: string
    Field: string
}

[<RequireQualifiedAccess>]
type ChatResponse =
    | SessionOpened of requestId: uint64 * session: ChatSessionOpened
    | ChatAccepted of requestId: uint64 * message: ChatMessage
    | ChatPublished of ChatMessage
    | RequestRejected of requestId: uint64 * rejection: ChatRequestRejected
    | PlayerJoined of PlayerSnapshot
    | PlayerLeft of PlayerId
    | PlayerUpdated of PlayerSnapshot
    | PlayerMoved of PlayerId * PlayerLocation voption
    | PlayerUpdateAccepted of requestId: uint64

/// Validated settings, held unchanged for the network owner's lifetime.
type ChatCodec = private { Config: ServerConfig }

[<RequireQualifiedAccess>]
module ChatCodec =
    [<Literal>]
    let Version = 3u

    let private fail requestId failure = Error { RequestId = requestId; Failure = failure }

    let create (config: ServerConfig) : Result<ChatCodec, string list> =
        match ServerConfig.protocolErrors config with
        | [] -> Ok { Config = config }
        | errors -> Error errors

    let private decodeLocation (limits: PlayerInputLimits) (source: Dreamsleeve.Protocol.Chat.PlayerLocation) =
        if isNull source then
            Ok ValueNone
        elif isNull source.Location || isNull source.Location.LocationId
             || isNull source.Position || isNull source.Rotation then
            Error(ChatCodecFailure.InvalidPayload "location")
        else
            let key = source.Location.LocationId
            let point = source.Position
            let angles = source.Rotation

            match PluginName.create limits.PluginName key.PluginName,
                  LocalFormId.create key.LocalFormId,
                  LocationName.create limits.LocationName source.Location.LocationName,
                  Position.create point.X point.Y point.Z,
                  Rotation.create angles.X angles.Y angles.Z with
            | Ok plugin, Ok localId, Ok name, Ok position, Ok rotation ->
                let location = Location.create (FormKey.create plugin localId) name
                Ok(ValueSome(PlayerLocation.create location position rotation))
            | Error error, _, _, _, _
            | _, Error error, _, _, _
            | _, _, Error error, _, _
            | _, _, _, Error error, _
            | _, _, _, _, Error error -> Error(ChatCodecFailure.InvalidDomain error)

    let private decodeActorValue (limits: PlayerInputLimits) (source: Dreamsleeve.Protocol.Chat.ActorValueEntry) =
        let state =
            match source.ValueCase with
            | Dreamsleeve.Protocol.Chat.ActorValueEntry.ValueOneofCase.Scalar ->
                ActorValueState.scalar source.Scalar |> Result.mapError ChatCodecFailure.InvalidDomain
            | Dreamsleeve.Protocol.Chat.ActorValueEntry.ValueOneofCase.Resource ->
                ActorValueState.resource source.Resource.Current source.Resource.Maximum
                |> Result.mapError ChatCodecFailure.InvalidDomain
            | Dreamsleeve.Protocol.Chat.ActorValueEntry.ValueOneofCase.None ->
                Error(ChatCodecFailure.InvalidPayload "actor_value.value")
            | unknown when not (Enum.IsDefined unknown) ->
                Error(ChatCodecFailure.InvalidPayload "actor_value.value")

        match ActorValueKey.create limits.ActorValueKey source.Key,
              ActorValueName.create limits.ActorValueName source.DisplayName, state with
        | Ok key, Ok name, Ok reading -> Ok(key, ActorValueInfo.create name reading)
        | Error error, _, _ | _, Error error, _ -> Error(ChatCodecFailure.InvalidDomain error)
        | _, _, Error error -> Error error

    let private decodeActorValues (limits: PlayerInputLimits) (source: Collections.RepeatedField<Dreamsleeve.Protocol.Chat.ActorValueEntry>) =
        let rec collect index entries =
            if index = source.Count then
                Ok entries
            else
                match decodeActorValue limits source[index] with
                | Error error -> Error error
                | Ok(key, _) when Map.containsKey key entries ->
                    Error(ChatCodecFailure.InvalidPayload "actor_values.duplicate_key")
                | Ok(key, value) -> collect (index + 1) (Map.add key value entries)

        if source.Count > limits.MaxActorValues then
            Error(ChatCodecFailure.InvalidPayload "actor_values.count")
        else
            collect 0 Map.empty

    let private decodeActivityKind = function
        | Dreamsleeve.Protocol.Chat.ActivityKind.Unknown -> Ok(ActivityKind.Unknown)
        | Dreamsleeve.Protocol.Chat.ActivityKind.Exploring -> Ok(ActivityKind.Exploring)
        | Dreamsleeve.Protocol.Chat.ActivityKind.Combat -> Ok(ActivityKind.Combat)
        | Dreamsleeve.Protocol.Chat.ActivityKind.Talking -> Ok(ActivityKind.Talking)
        | Dreamsleeve.Protocol.Chat.ActivityKind.Bartering -> Ok(ActivityKind.Bartering)
        | Dreamsleeve.Protocol.Chat.ActivityKind.Training -> Ok(ActivityKind.Training)
        | Dreamsleeve.Protocol.Chat.ActivityKind.Reading -> Ok(ActivityKind.Reading)
        | Dreamsleeve.Protocol.Chat.ActivityKind.Lockpicking -> Ok(ActivityKind.Lockpicking)
        | Dreamsleeve.Protocol.Chat.ActivityKind.Crafting -> Ok(ActivityKind.Crafting)
        | Dreamsleeve.Protocol.Chat.ActivityKind.UsingObject -> Ok(ActivityKind.UsingObject)
        | Dreamsleeve.Protocol.Chat.ActivityKind.Riding -> Ok(ActivityKind.Riding)
        | Dreamsleeve.Protocol.Chat.ActivityKind.Sneaking -> Ok(ActivityKind.Sneaking)
        | Dreamsleeve.Protocol.Chat.ActivityKind.Swimming -> Ok(ActivityKind.Swimming)
        | Dreamsleeve.Protocol.Chat.ActivityKind.Flying -> Ok(ActivityKind.Flying)
        | Dreamsleeve.Protocol.Chat.ActivityKind.Dead -> Ok(ActivityKind.Dead)
        | Dreamsleeve.Protocol.Chat.ActivityKind.Ragdoll -> Ok(ActivityKind.Ragdoll)
        | Dreamsleeve.Protocol.Chat.ActivityKind.Menu -> Ok(ActivityKind.Menu)
        | Dreamsleeve.Protocol.Chat.ActivityKind.NewGame -> Ok(ActivityKind.NewGame)
        | Dreamsleeve.Protocol.Chat.ActivityKind.Loading -> Ok(ActivityKind.Loading)
        | unknown when not (Enum.IsDefined unknown) -> Error(ChatCodecFailure.InvalidPayload "activity.kind")

    let private decodeLockDifficulty = function
        | Dreamsleeve.Protocol.Chat.LockDifficulty.Unknown -> Ok(LockDifficulty.Unknown)
        | Dreamsleeve.Protocol.Chat.LockDifficulty.Unlocked -> Ok(LockDifficulty.Unlocked)
        | Dreamsleeve.Protocol.Chat.LockDifficulty.VeryEasy -> Ok(LockDifficulty.VeryEasy)
        | Dreamsleeve.Protocol.Chat.LockDifficulty.Easy -> Ok(LockDifficulty.Easy)
        | Dreamsleeve.Protocol.Chat.LockDifficulty.Average -> Ok(LockDifficulty.Average)
        | Dreamsleeve.Protocol.Chat.LockDifficulty.Hard -> Ok(LockDifficulty.Hard)
        | Dreamsleeve.Protocol.Chat.LockDifficulty.VeryHard -> Ok(LockDifficulty.VeryHard)
        | Dreamsleeve.Protocol.Chat.LockDifficulty.RequiresKey -> Ok(LockDifficulty.RequiresKey)
        | unknown when not (Enum.IsDefined unknown) -> Error(ChatCodecFailure.InvalidPayload "activity.lock_difficulty")

    let private decodeNamedForm (limits: PlayerInputLimits) (source: Dreamsleeve.Protocol.Chat.NamedForm) =
        if isNull source then
            Ok ValueNone
        elif isNull source.Form then
            Error(ChatCodecFailure.InvalidPayload "race.form")
        else
            match PluginName.create limits.PluginName source.Form.PluginName,
                  LocalFormId.create source.Form.LocalFormId with
            | Ok plugin, Ok localId ->
                NamedForm.create limits.DetailsText (FormKey.create plugin localId) source.Name
                |> Result.map ValueSome
                |> Result.mapError ChatCodecFailure.InvalidDomain
            | Error error, _ | _, Error error -> Error(ChatCodecFailure.InvalidDomain error)

    let private decodeActivity (limits: PlayerInputLimits) (source: Dreamsleeve.Protocol.Chat.PlayerActivity) =
        if isNull source then
            Error(ChatCodecFailure.InvalidPayload "details.activity")
        else
            let target = if source.HasTargetName then ValueSome source.TargetName else ValueNone
            let menu = if source.HasMenuKey then ValueSome source.MenuKey else ValueNone

            match decodeActivityKind source.Kind, decodeLockDifficulty source.LockDifficulty with
            | Ok kind, Ok difficulty ->
                PlayerActivity.create limits.DetailsText limits.ActivityKey kind target difficulty menu
                |> Result.mapError ChatCodecFailure.InvalidDomain
            | Error error, _ | _, Error error -> Error error

    let private decodePlace (limits: PlayerInputLimits) (source: Dreamsleeve.Protocol.Chat.PlaceDescription) =
        if isNull source then
            Ok ValueNone
        else
            PlaceDescription.create limits.DetailsText limits.ActivityKey
                source.WorldspaceName source.LocationName source.NearbyMarkerName source.MarkerKind source.IsInterior
            |> Result.map ValueSome
            |> Result.mapError ChatCodecFailure.InvalidDomain

    let private decodeGameStartedAt (source: Dreamsleeve.Protocol.Chat.PlayerDetails) =
        if not source.HasGameStartedAtUnixMs then
            Ok ValueNone
        elif source.GameStartedAtUnixMs < -62135596800000L || source.GameStartedAtUnixMs > 253402300799999L then
            Error(ChatCodecFailure.InvalidPayload "details.game_started_at_unix_ms")
        else
            Ok(ValueSome(DateTimeOffset.FromUnixTimeMilliseconds source.GameStartedAtUnixMs))

    let private decodeDetails (limits: PlayerInputLimits) (source: Dreamsleeve.Protocol.Chat.PlayerDetails) =
        let level = if source.HasLevel then ValueSome source.Level else ValueNone

        match decodeNamedForm limits source.Race, decodeActivity limits source.Activity,
              decodePlace limits source.Place, decodeGameStartedAt source with
        | Ok race, Ok activity, Ok place, Ok startedAt ->
            PlayerDetails.create race level activity place startedAt
            |> Result.mapError ChatCodecFailure.InvalidDomain
        | Error error, _, _, _ | _, Error error, _, _
        | _, _, Error error, _ | _, _, _, Error error -> Error error

    let private decodeUpdate (limits: PlayerInputLimits) (source: Dreamsleeve.Protocol.Chat.UpdatePlayer) =
        match source.ActionCase with
        | Dreamsleeve.Protocol.Chat.UpdatePlayer.ActionOneofCase.BeginCharacter ->
            CharacterName.create limits.CharacterName source.BeginCharacter.Name
            |> Result.map PlayerUpdate.BeginCharacter
            |> Result.mapError ChatCodecFailure.InvalidDomain
        | Dreamsleeve.Protocol.Chat.UpdatePlayer.ActionOneofCase.RenameCharacter ->
            CharacterName.create limits.CharacterName source.RenameCharacter.Name
            |> Result.map PlayerUpdate.RenameCharacter
            |> Result.mapError ChatCodecFailure.InvalidDomain
        | Dreamsleeve.Protocol.Chat.UpdatePlayer.ActionOneofCase.SamplePlayerState ->
            let sample = source.SamplePlayerState

            match decodeLocation limits sample.Location, decodeActorValues limits sample.ActorValues with
            | Ok location, Ok values -> Ok(PlayerUpdate.Sample(location, values))
            | Error error, _ | _, Error error -> Error error
        | Dreamsleeve.Protocol.Chat.UpdatePlayer.ActionOneofCase.LeaveGame -> Ok PlayerUpdate.LeaveGame
        | Dreamsleeve.Protocol.Chat.UpdatePlayer.ActionOneofCase.SetDetails ->
            decodeDetails limits source.SetDetails |> Result.map PlayerUpdate.SetDetails
        | Dreamsleeve.Protocol.Chat.UpdatePlayer.ActionOneofCase.None ->
            Error(ChatCodecFailure.InvalidPayload "update_player.action")
        | unknown when not (Enum.IsDefined unknown) ->
            Error(ChatCodecFailure.InvalidPayload "update_player.action")

    let private decodePayload (config: ServerConfig) (packet: Dreamsleeve.Protocol.Chat.ClientPacket) =
        let requestId = Some packet.RequestId
        let domainError error = { RequestId = requestId; Failure = ChatCodecFailure.InvalidDomain error }

        match packet.PayloadCase with
        | Dreamsleeve.Protocol.Chat.ClientPacket.PayloadOneofCase.OpenSession ->
            let source = packet.OpenSession

            let ticket = source.SessionTicket
            if isNull ticket || ticket.Length <> 43
               || ticket |> Seq.exists (fun ch -> not (Char.IsAsciiLetterOrDigit ch || ch = '-' || ch = '_')) then
                fail requestId (ChatCodecFailure.InvalidPayload "session_ticket")
            else
                Ok { RequestId = packet.RequestId; Command = ChatCommand.OpenSession ticket }

        | Dreamsleeve.Protocol.Chat.ClientPacket.PayloadOneofCase.SendChat ->
            let source = packet.SendChat

            match ChatChannelId.create source.ChannelId, ChatMessageText.create config.ChatInput.MessageText source.Text with
            | Ok channel, Ok text ->
                Ok { RequestId = packet.RequestId; Command = ChatCommand.SendChat(channel, text) }
            | Error error, _ | _, Error error -> Error(domainError error)

        | Dreamsleeve.Protocol.Chat.ClientPacket.PayloadOneofCase.UpdatePlayer ->
            decodeUpdate config.PlayerInput packet.UpdatePlayer
            |> Result.map (fun update -> { RequestId = packet.RequestId; Command = ChatCommand.UpdatePlayer update })
            |> Result.mapError (fun failure -> { RequestId = requestId; Failure = failure })

        | Dreamsleeve.Protocol.Chat.ClientPacket.PayloadOneofCase.None ->
            fail requestId (ChatCodecFailure.InvalidPayload "payload")
        | unknown when not (Enum.IsDefined unknown) ->
            fail requestId (ChatCodecFailure.InvalidPayload "payload")

    // Parse/validate before entering any stateful owner. Domain failures retain
    // correlation so the runtime can send a rejection without applying anything.
    let decodeClient (codec: ChatCodec) (bytes: byte array) : Result<ChatRequest, ChatCodecError> =
        let config = codec.Config

        if isNull bytes || bytes.Length = 0 then
            fail None ChatCodecFailure.EmptyPacket
        elif bytes.Length > config.MaxPacketBytes then
            fail None ChatCodecFailure.PacketTooLarge
        else
            try
                let packet = Dreamsleeve.Protocol.Chat.ClientPacket.Parser.ParseFrom(bytes)
                let requestId = if packet.RequestId = 0UL then None else Some packet.RequestId

                if packet.ProtocolVersion <> Version then
                    fail requestId (ChatCodecFailure.UnsupportedVersion packet.ProtocolVersion)
                elif packet.RequestId = 0UL then
                    fail None (ChatCodecFailure.InvalidEnvelope "request_id")
                else
                    decodePayload config packet
            with :? InvalidProtocolBufferException -> fail None ChatCodecFailure.MalformedPacket

    let private profile (value: PlayerData) =
        Dreamsleeve.Protocol.Chat.PlayerProfile(
            PlayerId = PlayerId.value value.PlayerId,
            Username = Username.value value.Username,
            DisplayName = DisplayName.value value.DisplayName)

    let private location (value: PlayerLocation) =
        let place = value.Location
        let key = place.LocationId

        Dreamsleeve.Protocol.Chat.PlayerLocation(
            Location = Dreamsleeve.Protocol.Chat.Location(
                LocationId = Dreamsleeve.Protocol.Chat.FormKey(
                    PluginName = PluginName.value key.PluginName, LocalFormId = LocalFormId.value key.LocalFormId),
                LocationName = LocationName.value place.LocationName),
            Position = Dreamsleeve.Protocol.Chat.Position(
                X = WorldUnit.value value.Position.X, Y = WorldUnit.value value.Position.Y, Z = WorldUnit.value value.Position.Z),
            Rotation = Dreamsleeve.Protocol.Chat.Rotation(
                X = Radian.value value.Rotation.X, Y = Radian.value value.Rotation.Y, Z = Radian.value value.Rotation.Z))

    let private actorValue (key: ActorValueKey, value: ActorValueInfo) =
        let entry = Dreamsleeve.Protocol.Chat.ActorValueEntry(
            Key = ActorValueKey.value key, DisplayName = ActorValueName.value value.DisplayName)

        ActorValueState.fold
            (fun scalar -> entry.Scalar <- ActorValue.value scalar)
            (fun current maximum ->
                entry.Resource <- Dreamsleeve.Protocol.Chat.ResourceActorValue(
                    Current = ActorValue.value current, Maximum = ActorValue.value maximum))
            value.State

        entry

    let private encodeActivityKind = function
        | ActivityKind.Unknown -> Dreamsleeve.Protocol.Chat.ActivityKind.Unknown
        | ActivityKind.Exploring -> Dreamsleeve.Protocol.Chat.ActivityKind.Exploring
        | ActivityKind.Combat -> Dreamsleeve.Protocol.Chat.ActivityKind.Combat
        | ActivityKind.Talking -> Dreamsleeve.Protocol.Chat.ActivityKind.Talking
        | ActivityKind.Bartering -> Dreamsleeve.Protocol.Chat.ActivityKind.Bartering
        | ActivityKind.Training -> Dreamsleeve.Protocol.Chat.ActivityKind.Training
        | ActivityKind.Reading -> Dreamsleeve.Protocol.Chat.ActivityKind.Reading
        | ActivityKind.Lockpicking -> Dreamsleeve.Protocol.Chat.ActivityKind.Lockpicking
        | ActivityKind.Crafting -> Dreamsleeve.Protocol.Chat.ActivityKind.Crafting
        | ActivityKind.UsingObject -> Dreamsleeve.Protocol.Chat.ActivityKind.UsingObject
        | ActivityKind.Riding -> Dreamsleeve.Protocol.Chat.ActivityKind.Riding
        | ActivityKind.Sneaking -> Dreamsleeve.Protocol.Chat.ActivityKind.Sneaking
        | ActivityKind.Swimming -> Dreamsleeve.Protocol.Chat.ActivityKind.Swimming
        | ActivityKind.Flying -> Dreamsleeve.Protocol.Chat.ActivityKind.Flying
        | ActivityKind.Dead -> Dreamsleeve.Protocol.Chat.ActivityKind.Dead
        | ActivityKind.Ragdoll -> Dreamsleeve.Protocol.Chat.ActivityKind.Ragdoll
        | ActivityKind.Menu -> Dreamsleeve.Protocol.Chat.ActivityKind.Menu
        | ActivityKind.NewGame -> Dreamsleeve.Protocol.Chat.ActivityKind.NewGame
        | ActivityKind.Loading -> Dreamsleeve.Protocol.Chat.ActivityKind.Loading

    let private encodeLockDifficulty = function
        | LockDifficulty.Unknown -> Dreamsleeve.Protocol.Chat.LockDifficulty.Unknown
        | LockDifficulty.Unlocked -> Dreamsleeve.Protocol.Chat.LockDifficulty.Unlocked
        | LockDifficulty.VeryEasy -> Dreamsleeve.Protocol.Chat.LockDifficulty.VeryEasy
        | LockDifficulty.Easy -> Dreamsleeve.Protocol.Chat.LockDifficulty.Easy
        | LockDifficulty.Average -> Dreamsleeve.Protocol.Chat.LockDifficulty.Average
        | LockDifficulty.Hard -> Dreamsleeve.Protocol.Chat.LockDifficulty.Hard
        | LockDifficulty.VeryHard -> Dreamsleeve.Protocol.Chat.LockDifficulty.VeryHard
        | LockDifficulty.RequiresKey -> Dreamsleeve.Protocol.Chat.LockDifficulty.RequiresKey

    let private details (value: PlayerDetails) =
        let activity = Dreamsleeve.Protocol.Chat.PlayerActivity(
            Kind = encodeActivityKind value.Activity.Kind,
            LockDifficulty = encodeLockDifficulty value.Activity.LockDifficulty)

        value.Activity.TargetName |> ValueOption.iter (fun target -> activity.TargetName <- target)
        value.Activity.MenuKey |> ValueOption.iter (fun menu -> activity.MenuKey <- menu)

        let result = Dreamsleeve.Protocol.Chat.PlayerDetails(Activity = activity)

        value.Race |> ValueOption.iter (fun race ->
            result.Race <- Dreamsleeve.Protocol.Chat.NamedForm(
                Form = Dreamsleeve.Protocol.Chat.FormKey(
                    PluginName = PluginName.value race.Form.PluginName,
                    LocalFormId = LocalFormId.value race.Form.LocalFormId),
                Name = race.Name))
        value.Level |> ValueOption.iter (fun level -> result.Level <- level)
        value.Place |> ValueOption.iter (fun place ->
            result.Place <- Dreamsleeve.Protocol.Chat.PlaceDescription(
                WorldspaceName = place.WorldspaceName, LocationName = place.LocationName,
                NearbyMarkerName = place.NearbyMarkerName, MarkerKind = place.MarkerKind,
                IsInterior = place.IsInterior))
        value.GameStartedAt |> ValueOption.iter (fun started -> result.GameStartedAtUnixMs <- started.ToUnixTimeMilliseconds())

        result

    let private player (value: PlayerSnapshot) =
        let result = Dreamsleeve.Protocol.Chat.PlayerInfo(
            Profile = profile value.Data, CharacterGeneration = value.CharacterGeneration,
            Details = details value.Details)

        value.CharacterName |> ValueOption.iter (fun name -> result.CharacterName <- CharacterName.value name)
        value.Location |> ValueOption.iter (fun place -> result.Location <- location place)
        result.ActorValues.AddRange(value.ActorValues |> Map.toSeq |> Seq.map actorValue)

        result

    let private message (value: ChatMessage) =
        Dreamsleeve.Protocol.Chat.ChatMessage(
            MessageId = ChatMessageId.value value.MessageId,
            ChannelId = ChatChannelId.value value.ChannelId,
            Author = profile value.Author,
            Text = ChatMessageText.value value.MessageText,
            SentAtUnixMs = Core.toUnixMilliseconds value.SentAt)

    // Domain factories establish scalar invariants; only cross-field consistency
    // and configured collection budgets remain to check here.
    let private validWelcome (config: ServerConfig) (value: ChatSessionOpened) =
        let ids = value.Players |> List.map (fun player -> player.Data.PlayerId)

        let ordered =
            value.RecentMessages |> List.pairwise
            |> List.forall (fun (left, right) -> left.MessageId < right.MessageId)

        value.Players.Length <= config.MaxInitialPlayers && value.RecentMessages.Length <= config.MaxRecentMessages
        && Set.count (Set.ofList ids) = ids.Length && List.contains value.SelfPlayerId ids
        && ordered && (value.RecentMessages |> List.forall (fun msg -> msg.ChannelId = value.GlobalChannelId))

    // Optional only at the wire/error boundary; response cases carry their own
    // required correlation, while notifications cannot be given a request ID.
    let private responseRequestId = function
        | ChatResponse.SessionOpened(requestId, _)
        | ChatResponse.ChatAccepted(requestId, _)
        | ChatResponse.PlayerUpdateAccepted requestId
        | ChatResponse.RequestRejected(requestId, _) -> Some requestId
        | ChatResponse.ChatPublished _
        | ChatResponse.PlayerJoined _
        | ChatResponse.PlayerUpdated _
        | ChatResponse.PlayerMoved _
        | ChatResponse.PlayerLeft _ -> None

    let private validateResponse config response =
        if responseRequestId response = Some 0UL then
            Some(ChatCodecFailure.InvalidEnvelope "request_id")
        else
            match response with
            | ChatResponse.SessionOpened(_, value) ->
                if validWelcome config value then None
                else Some(ChatCodecFailure.InvalidPayload "session_opened")

            | ChatResponse.RequestRejected(_, value) ->
                if value.Code = RequestRejectionCode.Unspecified || not (Enum.IsDefined value.Code) then
                    Some(ChatCodecFailure.InvalidPayload "code")
                elif isNull value.Message || isNull value.Field then
                    Some(ChatCodecFailure.InvalidPayload "rejection")
                else None

            | ChatResponse.ChatAccepted _
            | ChatResponse.ChatPublished _
            | ChatResponse.PlayerJoined _
            | ChatResponse.PlayerUpdated _
            | ChatResponse.PlayerMoved _
            | ChatResponse.PlayerUpdateAccepted _
            | ChatResponse.PlayerLeft _ -> None

    // Inputs are validated domain values. The owner decides IDs, times, recipient
    // correlation, membership and session ordering; the codec does none of these.
    let encodeServer (codec: ChatCodec) (response: ChatResponse) : Result<byte array, ChatCodecError> =
        let config = codec.Config
        let requestId = responseRequestId response

        match validateResponse config response with
        | Some failure -> fail requestId failure
        | None ->
            let packet = Dreamsleeve.Protocol.Chat.ServerPacket(ProtocolVersion = Version)
            requestId |> Option.iter (fun id -> packet.RequestId <- id)

            match response with
            | ChatResponse.SessionOpened(_, value) ->
                let welcome = Dreamsleeve.Protocol.Chat.SessionOpened(
                    SelfPlayerId = PlayerId.value value.SelfPlayerId,
                    GlobalChannelId = ChatChannelId.value value.GlobalChannelId)

                welcome.Players.AddRange(value.Players |> Seq.map player)
                welcome.RecentMessages.AddRange(value.RecentMessages |> Seq.map message)

                packet.SessionOpened <- welcome

            | ChatResponse.ChatAccepted(_, value)
            | ChatResponse.ChatPublished value ->
                packet.ChatPublished <- Dreamsleeve.Protocol.Chat.ChatPublished(Message = message value)
            | ChatResponse.PlayerJoined value ->
                packet.PlayerJoined <- Dreamsleeve.Protocol.Chat.PlayerJoined(Player = player value)
            | ChatResponse.PlayerUpdated value ->
                packet.PlayerUpdated <- Dreamsleeve.Protocol.Chat.PlayerUpdated(Player = player value)
            | ChatResponse.PlayerMoved(playerId, place) ->
                let moved = Dreamsleeve.Protocol.Chat.PlayerMoved(PlayerId = PlayerId.value playerId)
                place |> ValueOption.iter (fun value -> moved.Location <- location value)
                packet.PlayerMoved <- moved
            | ChatResponse.PlayerUpdateAccepted _ ->
                packet.PlayerUpdateAccepted <- Dreamsleeve.Protocol.Chat.PlayerUpdateAccepted()
            | ChatResponse.PlayerLeft value ->
                packet.PlayerLeft <- Dreamsleeve.Protocol.Chat.PlayerLeft(PlayerId = PlayerId.value value)

            | ChatResponse.RequestRejected(_, value) ->
                packet.RequestRejected <- Dreamsleeve.Protocol.Chat.RequestRejected(Code = value.Code, Message = value.Message, Field = value.Field)

            if packet.CalculateSize() > config.MaxPacketBytes then
                fail requestId ChatCodecFailure.PacketTooLarge
            else
                Ok(packet.ToByteArray())
