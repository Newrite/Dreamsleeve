#nowarn "104" // Unknown enum numbers are guarded; FS0025 still checks every named case.

namespace Dreamsleeve.Server.Core

open System
open Google.Protobuf
open Dreamsleeve.Server.Domain

[<RequireQualifiedAccess>]
module internal PlayerCodec =
    let private decodeLocation (limits: PlayerInputLimits) (source: Dreamsleeve.Protocol.Chat.PlayerLocation) =
        if isNull source then
            Ok ValueNone
        elif isNull source.Location || isNull source.Location.LocationId
             || isNull source.Position || isNull source.Rotation then
            Error(ProtocolCodecFailure.InvalidPayload "location")
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
                let sampled =
                    PlayerLocation.create location position rotation
                    |> PlayerLocation.withSampleTime source.SampledAtUs

                Ok(ValueSome sampled)
            | Error error, _, _, _, _
            | _, Error error, _, _, _
            | _, _, Error error, _, _
            | _, _, _, Error error, _
            | _, _, _, _, Error error -> Error(ProtocolCodecFailure.InvalidDomain error)

    let private decodeActorValue (limits: PlayerInputLimits) (source: Dreamsleeve.Protocol.Chat.ActorValueEntry) =
        let state =
            match source.ValueCase with
            | Dreamsleeve.Protocol.Chat.ActorValueEntry.ValueOneofCase.Scalar ->
                ActorValueState.scalar source.Scalar |> Result.mapError ProtocolCodecFailure.InvalidDomain
            | Dreamsleeve.Protocol.Chat.ActorValueEntry.ValueOneofCase.Resource ->
                ActorValueState.resource source.Resource.Current source.Resource.Maximum
                |> Result.mapError ProtocolCodecFailure.InvalidDomain
            | Dreamsleeve.Protocol.Chat.ActorValueEntry.ValueOneofCase.None ->
                Error(ProtocolCodecFailure.InvalidPayload "actor_value.value")
            | unknown when not (Enum.IsDefined unknown) ->
                Error(ProtocolCodecFailure.InvalidPayload "actor_value.value")

        match ActorValueKey.create limits.ActorValueKey source.Key,
              ActorValueName.create limits.ActorValueName source.DisplayName, state with
        | Ok key, Ok name, Ok reading -> Ok(key, ActorValueInfo.create name reading)
        | Error error, _, _ | _, Error error, _ -> Error(ProtocolCodecFailure.InvalidDomain error)
        | _, _, Error error -> Error error

    let private decodeActorValues (limits: PlayerInputLimits) (source: Collections.RepeatedField<Dreamsleeve.Protocol.Chat.ActorValueEntry>) =
        let rec collect index entries =
            if index = source.Count then
                Ok entries
            else
                match decodeActorValue limits source[index] with
                | Error error -> Error error
                | Ok(key, _) when Map.containsKey key entries ->
                    Error(ProtocolCodecFailure.InvalidPayload "actor_values.duplicate_key")
                | Ok(key, value) -> collect (index + 1) (Map.add key value entries)

        if source.Count > limits.MaxActorValues then
            Error(ProtocolCodecFailure.InvalidPayload "actor_values.count")
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
        | unknown when not (Enum.IsDefined unknown) -> Error(ProtocolCodecFailure.InvalidPayload "activity.kind")

    let private decodeLockDifficulty = function
        | Dreamsleeve.Protocol.Chat.LockDifficulty.Unknown -> Ok(LockDifficulty.Unknown)
        | Dreamsleeve.Protocol.Chat.LockDifficulty.Unlocked -> Ok(LockDifficulty.Unlocked)
        | Dreamsleeve.Protocol.Chat.LockDifficulty.VeryEasy -> Ok(LockDifficulty.VeryEasy)
        | Dreamsleeve.Protocol.Chat.LockDifficulty.Easy -> Ok(LockDifficulty.Easy)
        | Dreamsleeve.Protocol.Chat.LockDifficulty.Average -> Ok(LockDifficulty.Average)
        | Dreamsleeve.Protocol.Chat.LockDifficulty.Hard -> Ok(LockDifficulty.Hard)
        | Dreamsleeve.Protocol.Chat.LockDifficulty.VeryHard -> Ok(LockDifficulty.VeryHard)
        | Dreamsleeve.Protocol.Chat.LockDifficulty.RequiresKey -> Ok(LockDifficulty.RequiresKey)
        | unknown when not (Enum.IsDefined unknown) -> Error(ProtocolCodecFailure.InvalidPayload "activity.lock_difficulty")

    let private decodeNamedForm (limits: PlayerInputLimits) (source: Dreamsleeve.Protocol.Chat.NamedForm) =
        if isNull source then
            Ok ValueNone
        elif isNull source.Form then
            Error(ProtocolCodecFailure.InvalidPayload "race.form")
        else
            match PluginName.create limits.PluginName source.Form.PluginName,
                  LocalFormId.create source.Form.LocalFormId with
            | Ok plugin, Ok localId ->
                NamedForm.create limits.DetailsText (FormKey.create plugin localId) source.Name
                |> Result.map ValueSome
                |> Result.mapError ProtocolCodecFailure.InvalidDomain
            | Error error, _ | _, Error error -> Error(ProtocolCodecFailure.InvalidDomain error)

    let private decodeActivity (limits: PlayerInputLimits) (source: Dreamsleeve.Protocol.Chat.PlayerActivity) =
        if isNull source then
            Error(ProtocolCodecFailure.InvalidPayload "details.activity")
        else
            let target = if source.HasTargetName then ValueSome source.TargetName else ValueNone
            let menu = if source.HasMenuKey then ValueSome source.MenuKey else ValueNone

            match decodeActivityKind source.Kind, decodeLockDifficulty source.LockDifficulty with
            | Ok kind, Ok difficulty ->
                PlayerActivity.create limits.DetailsText limits.ActivityKey kind target difficulty menu
                |> Result.mapError ProtocolCodecFailure.InvalidDomain
            | Error error, _ | _, Error error -> Error error

    let private decodePlace (limits: PlayerInputLimits) (source: Dreamsleeve.Protocol.Chat.PlaceDescription) =
        if isNull source then
            Ok ValueNone
        else
            PlaceDescription.create limits.DetailsText limits.ActivityKey
                source.WorldspaceName source.LocationName source.NearbyMarkerName source.MarkerKind source.IsInterior
            |> Result.map ValueSome
            |> Result.mapError ProtocolCodecFailure.InvalidDomain

    let private decodeGameStartedAt (source: Dreamsleeve.Protocol.Chat.PlayerDetails) =
        if not source.HasGameStartedAtUnixMs then
            Ok ValueNone
        elif source.GameStartedAtUnixMs < -62135596800000L || source.GameStartedAtUnixMs > 253402300799999L then
            Error(ProtocolCodecFailure.InvalidPayload "details.game_started_at_unix_ms")
        else
            Ok(ValueSome(DateTimeOffset.FromUnixTimeMilliseconds source.GameStartedAtUnixMs))

    let private decodeDetails (limits: PlayerInputLimits) (source: Dreamsleeve.Protocol.Chat.PlayerDetails) =
        let level = if source.HasLevel then ValueSome source.Level else ValueNone

        match decodeNamedForm limits source.Race, decodeActivity limits source.Activity,
              decodePlace limits source.Place, decodeGameStartedAt source with
        | Ok race, Ok activity, Ok place, Ok startedAt ->
            Ok (PlayerDetails.create race level activity place startedAt)
        | Error error, _, _, _ | _, Error error, _, _
        | _, _, Error error, _ | _, _, _, Error error -> Error error

    let decodeUpdate (limits: PlayerInputLimits) (source: Dreamsleeve.Protocol.Chat.UpdatePlayer) =
        match source.ActionCase with
        | Dreamsleeve.Protocol.Chat.UpdatePlayer.ActionOneofCase.BeginCharacter ->
            CharacterName.create limits.CharacterName source.BeginCharacter.Name
            |> Result.map PlayerUpdate.BeginCharacter
            |> Result.mapError ProtocolCodecFailure.InvalidDomain
        | Dreamsleeve.Protocol.Chat.UpdatePlayer.ActionOneofCase.RenameCharacter ->
            CharacterName.create limits.CharacterName source.RenameCharacter.Name
            |> Result.map PlayerUpdate.RenameCharacter
            |> Result.mapError ProtocolCodecFailure.InvalidDomain
        | Dreamsleeve.Protocol.Chat.UpdatePlayer.ActionOneofCase.SetLocation ->
            if source.SetLocation.ContextRevision = 0UL then
                Error(ProtocolCodecFailure.InvalidPayload "context_revision")
            else
                decodeLocation limits source.SetLocation.Location
                |> Result.map (fun location -> PlayerUpdate.SetLocation(source.SetLocation.ContextRevision, location))
        | Dreamsleeve.Protocol.Chat.UpdatePlayer.ActionOneofCase.SetActorValues ->
            decodeActorValues limits source.SetActorValues.Values |> Result.map PlayerUpdate.SetActorValues
        | Dreamsleeve.Protocol.Chat.UpdatePlayer.ActionOneofCase.LeaveGame -> Ok PlayerUpdate.LeaveGame
        | Dreamsleeve.Protocol.Chat.UpdatePlayer.ActionOneofCase.SetDetails ->
            decodeDetails limits source.SetDetails |> Result.map PlayerUpdate.SetDetails
        | Dreamsleeve.Protocol.Chat.UpdatePlayer.ActionOneofCase.None ->
            Error(ProtocolCodecFailure.InvalidPayload "update_player.action")
        | unknown when not (Enum.IsDefined unknown) ->
            Error(ProtocolCodecFailure.InvalidPayload "update_player.action")

    let decodeMovement (source: Dreamsleeve.Protocol.Chat.MovementSample) =
        if isNull source || source.ContextRevision = 0UL || source.Sequence = 0UL then
            Error(ProtocolCodecFailure.InvalidPayload "movement_sample")
        elif isNull source.Pose || isNull source.Pose.Position || isNull source.Pose.Rotation then
            Error(ProtocolCodecFailure.InvalidPayload "pose")
        else
            let point, angles = source.Pose.Position, source.Pose.Rotation
            match Position.create point.X point.Y point.Z, Rotation.create angles.X angles.Y angles.Z with
            | Ok position, Ok rotation ->
                Ok { ContextRevision = source.ContextRevision; Sequence = source.Sequence
                     Pose = { Position = position; Rotation = rotation; SampledAtUs = source.Pose.SampledAtUs } }
            | Error error, _ | _, Error error -> Error(ProtocolCodecFailure.InvalidDomain error)

    let profile (value: PlayerData) =
        Dreamsleeve.Protocol.Chat.PlayerProfile(
            PlayerId = PlayerId.value value.PlayerId,
            Username = Username.value value.Username,
            DisplayName = DisplayName.value value.DisplayName)

    let location (value: PlayerLocation) =
        let place = value.Location
        let key = place.LocationId

        Dreamsleeve.Protocol.Chat.PlayerLocation(
            SampledAtUs = value.SampledAtUs,
            Location = Dreamsleeve.Protocol.Chat.Location(
                LocationId = Dreamsleeve.Protocol.Chat.FormKey(
                    PluginName = PluginName.value key.PluginName, LocalFormId = LocalFormId.value key.LocalFormId),
                LocationName = LocationName.value place.LocationName),
            Position = Dreamsleeve.Protocol.Chat.Position(
                X = WorldUnit.value value.Position.X, Y = WorldUnit.value value.Position.Y, Z = WorldUnit.value value.Position.Z),
            Rotation = Dreamsleeve.Protocol.Chat.Rotation(
                X = Radian.value value.Rotation.X, Y = Radian.value value.Rotation.Y, Z = Radian.value value.Rotation.Z))

    let actorValue (key: ActorValueKey, value: ActorValueInfo) =
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

    let details (value: PlayerDetails) =
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

    let player (value: PlayerSnapshot) =
        let result = Dreamsleeve.Protocol.Chat.PlayerInfo(
            Profile = profile value.Data,
            ViewRevision = value.ViewRevision, MovementSequence = value.MovementSequence, CharacterGeneration = value.CharacterGeneration,
            CharacterNameWithheld = value.CharacterNameWithheld,
            Details = details value.Details)

        value.CharacterName |> ValueOption.iter (fun name -> result.CharacterName <- CharacterName.value name)
        value.Location |> ValueOption.iter (fun place -> result.Location <- location place)
        result.ActorValues.AddRange(value.ActorValues |> Map.toSeq |> Seq.map actorValue)

        result

    let metadataChanged playerId values metadata =
        let changed = Dreamsleeve.Protocol.Chat.PlayerMetadataChanged(PlayerId = PlayerId.value playerId)
        values |> ValueOption.iter (fun entries ->
            let replacement = Dreamsleeve.Protocol.Chat.ActorValues()
            entries |> Map.toSeq |> Seq.map actorValue |> replacement.Values.AddRange
            changed.ActorValues <- replacement)
        metadata |> ValueOption.iter (fun value -> changed.Details <- details value)
        changed

    let pose (value: MovementPose) =
        Dreamsleeve.Protocol.Chat.MovementPose(
            Position = Dreamsleeve.Protocol.Chat.Position(
                X = WorldUnit.value value.Position.X, Y = WorldUnit.value value.Position.Y, Z = WorldUnit.value value.Position.Z),
            Rotation = Dreamsleeve.Protocol.Chat.Rotation(
                X = Radian.value value.Rotation.X, Y = Radian.value value.Rotation.Y, Z = Radian.value value.Rotation.Z),
            SampledAtUs = value.SampledAtUs)

    let moved (value: MovementChange) =
        Dreamsleeve.Protocol.Chat.PlayerMoved(PlayerId = PlayerId.value value.PlayerId,
            ViewRevision = value.ViewRevision, Sequence = value.Sequence, Pose = pose value.Pose)

    let visibility (value: VisibilityChange) =
        let result = Dreamsleeve.Protocol.Chat.PlayerVisibilityChanged(
            PlayerId = PlayerId.value value.PlayerId, ViewRevision = value.ViewRevision, Sequence = value.Sequence)
        value.Location |> ValueOption.iter (fun current -> result.Location <- location current)
        result
