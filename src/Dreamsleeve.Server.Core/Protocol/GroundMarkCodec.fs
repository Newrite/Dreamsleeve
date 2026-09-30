namespace Dreamsleeve.Server.Core

open Dreamsleeve.Server.Domain

type private WireGroundKind = Dreamsleeve.Protocol.Chat.GroundMarkKind

[<RequireQualifiedAccess>]
module internal GroundMarkCodec =
    let private kind = function
        | GroundMarkKind.Note -> WireGroundKind.Note
        | GroundMarkKind.Death -> WireGroundKind.Death

    let private decodePlacement (limits: PlayerInputLimits) (source: Dreamsleeve.Protocol.Chat.GroundMarkPlacement) =
        if isNull source || isNull source.LocationId || isNull source.Position then
            Error(ProtocolCodecFailure.InvalidPayload "placement")
        else
            match PluginName.create limits.PluginName source.LocationId.PluginName,
                  LocalFormId.create source.LocationId.LocalFormId,
                  Position.create source.Position.X source.Position.Y source.Position.Z,
                  Radian.create source.Heading with
            | Ok plugin, Ok localId, Ok position, Ok heading ->
                Ok(GroundMarkPlacement.create (FormKey.create plugin localId) position heading)
            | Error error, _, _, _ | _, Error error, _, _ | _, _, Error error, _ | _, _, _, Error error ->
                Error(ProtocolCodecFailure.InvalidDomain error)

    // Required in both requests. A wire value above Int32.MaxValue turns
    // negative here and fails its range like any other.
    let private decodeGameDate (source: Dreamsleeve.Protocol.Chat.GameDate) =
        if isNull source then
            Error(ProtocolCodecFailure.InvalidPayload "game_date")
        else
            GameDate.create (int source.Era) (int source.Year) (int source.Month) (int source.Day) (int source.DayOfWeek)
                (int source.Hour) (int source.Minute)
            |> Result.mapError ProtocolCodecFailure.InvalidDomain

    let decodeNote (config: ServerConfig) (source: Dreamsleeve.Protocol.Chat.PlaceGroundNote) =
        decodePlacement config.PlayerInput source.Placement |> Result.bind (fun placement ->
        decodeGameDate source.GameDate |> Result.bind (fun date ->
        GroundNoteText.create config.ChatInput.GroundNoteText source.Text
        |> Result.mapError ProtocolCodecFailure.InvalidDomain
        |> Result.map (fun text -> ClientCommand.PlaceGroundNote(text, placement, date))))

    let decodeDeath (config: ServerConfig) (source: Dreamsleeve.Protocol.Chat.ReportDeath) =
        decodePlacement config.PlayerInput source.Placement |> Result.bind (fun placement ->
        decodeGameDate source.GameDate |> Result.bind (fun date ->
        DeathMarkText.create config.ChatInput.DeathMarkText source.Label
        |> Result.mapError ProtocolCodecFailure.InvalidDomain
        |> Result.map (fun label -> ClientCommand.ReportDeath(label, placement, date))))

    let decodeRemove (source: Dreamsleeve.Protocol.Chat.RemoveGroundMark) =
        GroundMarkId.create source.MarkId
        |> Result.mapError ProtocolCodecFailure.InvalidDomain
        |> Result.map ClientCommand.RemoveGroundMark

    let placement (value: GroundMarkPlacement) =
        Dreamsleeve.Protocol.Chat.GroundMarkPlacement(
            LocationId = Dreamsleeve.Protocol.Chat.FormKey(
                PluginName = PluginName.value value.LocationId.PluginName,
                LocalFormId = LocalFormId.value value.LocationId.LocalFormId),
            Position = Dreamsleeve.Protocol.Chat.Position(
                X = WorldUnit.value value.Position.X, Y = WorldUnit.value value.Position.Y, Z = WorldUnit.value value.Position.Z),
            Heading = Radian.value value.Heading)

    let gameDate (value: GameDate) =
        Dreamsleeve.Protocol.Chat.GameDate(
            Era = uint32 value.Era, Year = uint32 value.Year, Month = uint32 value.Month, Day = uint32 value.Day,
            DayOfWeek = uint32 value.DayOfWeek, Hour = uint32 value.Hour, Minute = uint32 value.Minute)

    let mark (record: GroundMarkRecord) =
        let value = record.Mark
        let result =
            Dreamsleeve.Protocol.Chat.GroundMark(
                MarkId = GroundMarkId.value value.Id,
                Author = PlayerCodec.profile record.Author,
                Kind = kind value.Kind,
                Text = value.Text,
                Placement = placement value.Placement,
                CreatedAtUnixMs = Core.toUnixMilliseconds value.CreatedAt)
        value.CharacterName |> ValueOption.iter (fun name -> result.CharacterName <- CharacterName.value name)
        value.GameDate |> ValueOption.iter (fun date -> result.GameDate <- gameDate date)
        for span in value.Flagged do
            result.Flagged.Add(Dreamsleeve.Protocol.Chat.TextSpan(Start = uint32 span.Start, Length = uint32 span.Length))
        result

    let changed (view: GroundMarkView) =
        let result = Dreamsleeve.Protocol.Chat.GroundMarksChanged(ViewRevision = view.ViewRevision, Clear = view.Clear)
        result.Added.AddRange(view.Added |> Seq.map mark)
        result.RemovedIds.AddRange(view.Removed |> Seq.map GroundMarkId.value)
        result

    let placed (record: GroundMarkRecord) (evicted: GroundMarkId voption) =
        Dreamsleeve.Protocol.Chat.GroundMarkPlaced(
            Mark = mark record,
            EvictedId = (evicted |> ValueOption.map GroundMarkId.value |> ValueOption.defaultValue 0UL))

    let removed (id: GroundMarkId) =
        Dreamsleeve.Protocol.Chat.GroundMarkRemoved(MarkId = GroundMarkId.value id)

    let own (records: GroundMarkRecord list) =
        let result = Dreamsleeve.Protocol.Chat.OwnGroundMarks()
        result.Marks.AddRange(records |> Seq.map mark)
        result

    /// One author for the whole list, each ID once.
    let validOwn (records: GroundMarkRecord list) =
        let ids = records |> List.map (fun record -> record.Mark.Id)
        (records |> List.forall (fun record -> record.Author.PlayerId = record.Mark.Author))
        && (records |> List.map (fun record -> record.Mark.Author) |> List.distinct |> List.length <= 1)
        && Set.count (Set.ofList ids) = ids.Length

    /// The record's author must be the mark's author; a view mentions each ID once.
    let validView (view: GroundMarkView) =
        let ids = view.Added |> List.map (fun record -> record.Mark.Id)
        view.ViewRevision <> 0UL
        && (view.Added |> List.forall (fun record -> record.Author.PlayerId = record.Mark.Author))
        && Set.count (Set.ofList ids) = ids.Length
        && Set.count (Set.ofList view.Removed) = view.Removed.Length
        && (view.Clear || not ids.IsEmpty || not view.Removed.IsEmpty)
