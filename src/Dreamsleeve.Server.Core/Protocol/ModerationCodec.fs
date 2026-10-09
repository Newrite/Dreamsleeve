#nowarn "104" // Unknown enum numbers are guarded; FS0025 still checks every named case.

namespace Dreamsleeve.Server.Core

open System
open Dreamsleeve.Server.Domain

type private WireRole = Dreamsleeve.Protocol.Chat.PlayerRole
type private WireSanction = Dreamsleeve.Protocol.Chat.SanctionKind

/// Moderation messages (moderation.proto). The domain factories check the
/// values; who may send them is the session's and the account service's.
[<RequireQualifiedAccess>]
module internal ModerationCodec =
    let role = function
        | PlayerRole.Player -> WireRole.Player
        | PlayerRole.Moderator -> WireRole.Moderator

    let kind = function
        | SanctionKind.Mute -> WireSanction.Mute
        | SanctionKind.Ban -> WireSanction.Ban

    let private decodeKind (value: WireSanction) =
        match value with
        | WireSanction.Mute -> Ok SanctionKind.Mute
        | WireSanction.Ban -> Ok SanctionKind.Ban
        | WireSanction.Unspecified -> Error(ProtocolCodecFailure.InvalidPayload "kind")
        | unknown when not (Enum.IsDefined unknown) -> Error(ProtocolCodecFailure.InvalidPayload "kind")

    let private player raw =
        PlayerId.create raw |> Result.mapError ProtocolCodecFailure.InvalidDomain

    let private reason raw =
        SanctionReason.create raw |> Result.mapError ProtocolCodecFailure.InvalidDomain

    let decodeSanction (source: Dreamsleeve.Protocol.Chat.SanctionPlayer) =
        let minutes =
            if not source.HasMinutes then
                Ok ValueNone
            elif source.Minutes > uint32 Int32.MaxValue then
                Error(ProtocolCodecFailure.InvalidPayload "minutes")
            else
                Ok(ValueSome(int source.Minutes))
        match player source.PlayerId, decodeKind source.Kind, minutes, reason source.Reason with
        | Ok target, Ok kind, Ok minutes, Ok reason ->
            SanctionTerm.create minutes
            |> Result.mapError ProtocolCodecFailure.InvalidDomain
            |> Result.map (fun term -> ClientCommand.SanctionPlayer(target, kind, term, reason, source.Devices))
        | Error error, _, _, _
        | _, Error error, _, _
        | _, _, Error error, _
        | _, _, _, Error error -> Error error

    let decodeLift (source: Dreamsleeve.Protocol.Chat.LiftSanction) =
        match player source.PlayerId, decodeKind source.Kind with
        | Ok target, Ok kind -> Ok(ClientCommand.LiftSanction(target, kind))
        | Error error, _ | _, Error error -> Error error

    let decodeKick (source: Dreamsleeve.Protocol.Chat.KickPlayer) =
        match player source.PlayerId, reason source.Reason with
        | Ok target, Ok reason -> Ok(ClientCommand.KickPlayer(target, reason))
        | Error error, _ | _, Error error -> Error error

    let decodeListMarks (source: Dreamsleeve.Protocol.Chat.ListPlayerMarks) =
        player source.PlayerId |> Result.map ClientCommand.ListPlayerMarks

    let decodeClearMarks (source: Dreamsleeve.Protocol.Chat.ClearPlayerMarks) =
        let kinds = [
            if source.Notes then GroundMarkKind.Note
            if source.Deaths then GroundMarkKind.Death
        ]

        if kinds.IsEmpty then
            Error(ProtocolCodecFailure.InvalidPayload "kinds")
        else
            player source.PlayerId
            |> Result.map (fun target -> ClientCommand.ClearPlayerMarks(target, kinds))

    let decodeDeleteMessage (source: Dreamsleeve.Protocol.Chat.DeleteChatMessage) =
        match ChatChannelId.create source.ChannelId, ChatMessageId.create source.MessageId with
        | Ok channel, Ok message -> Ok(ClientCommand.DeleteChatMessage(channel, message))
        | Error error, _ | _, Error error -> Error(ProtocolCodecFailure.InvalidDomain error)

    let entry (sanction: Sanction) =
        let result =
            Dreamsleeve.Protocol.Chat.SanctionEntry(
                PlayerId = PlayerId.value sanction.Target,
                Kind = kind sanction.Kind,
                Reason = SanctionReason.value sanction.Reason,
                IssuedAtUnixMs = sanction.IssuedAt.ToUnixTimeMilliseconds())

        sanction.Expires |> ValueOption.iter (fun expires -> result.UntilUnixMs <- expires.ToUnixTimeMilliseconds())
        result

    let list (sanctions: Sanction list) =
        let result = Dreamsleeve.Protocol.Chat.SanctionList()
        result.Sanctions.AddRange(sanctions |> Seq.map entry)
        result

    let marks (author: PlayerId) (records: GroundMarkRecord list) =
        let result = Dreamsleeve.Protocol.Chat.PlayerMarks(PlayerId = PlayerId.value author)
        result.Marks.AddRange(records |> Seq.map GroundMarkCodec.mark)
        result
