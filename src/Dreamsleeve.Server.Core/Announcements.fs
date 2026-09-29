namespace Dreamsleeve.Server.Core

open System
open Dreamsleeve.Server.Domain

/// Admission rules of one client-requested origin. Later rules (allowed
/// signatures, per-origin limits, minimum trust) are added as fields here.
type ClientAnnouncementRules = {
    Enabled: bool
}

/// One configured server announcement. IntervalSeconds = 0 publishes it once.
type ScheduledAnnouncement = {
    Text: string
    /// Announcement, Event, Admin or Periodic.
    Kind: string
    /// First publication after the server starts.
    DelaySeconds: int
    IntervalSeconds: int
}

/// [Announcements]: the system channel, client admission and the
/// server's own schedule. Read at startup like the rest of the configuration;
/// there is no hot reload.
type AnnouncementOptions = {
    /// Retained announcements; also the tail sent at session opening.
    HistoryCapacity: int
    /// Client announcements an account may post at once; chat has its own limit.
    RateBurst: int
    /// One more client announcement per this interval, up to RateBurst.
    RateRefillMs: int
    /// The same normalized announcement text is refused within this window; 0 disables it.
    DuplicateWindowMs: int
    TrustedClient: ClientAnnouncementRules
    ThirdParty: ClientAnnouncementRules
    Scheduled: ScheduledAnnouncement list
}

[<RequireQualifiedAccess>]
module AnnouncementOptions =
    let defaults = {
        HistoryCapacity = 128
        RateBurst = 3
        RateRefillMs = 20000
        DuplicateWindowMs = 300000
        TrustedClient = { Enabled = true }
        ThirdParty = { Enabled = true }
        Scheduled = []
    }

    /// Mailbox limits follow the chat channel; history and admission are the channel's own.
    let channelOptions (chat: ChatRoomOptions) options =
        { chat with
            HistoryCapacity = options.HistoryCapacity
            RateBurst = options.RateBurst
            RateRefillMs = options.RateRefillMs
            DuplicateWindowMs = options.DuplicateWindowMs }

    let private rules options = function
        | ClientAnnouncementSource.TrustedClient -> options.TrustedClient
        | ClientAnnouncementSource.ThirdParty -> options.ThirdParty

    /// Origins announced to clients in the welcome, so they can refuse early.
    let allowedSources options =
        [ ClientAnnouncementSource.TrustedClient; ClientAnnouncementSource.ThirdParty ]
        |> List.filter (fun source -> (rules options source).Enabled)

    /// The single admission decision for a requested origin. Rate, repetition
    /// and word lists are applied afterwards by their own owners.
    let admit options (request: AnnouncementRequest) : Result<unit, RequestRejection> =
        if (rules options request.Source).Enabled then Ok ()
        else Error { Code = RequestRejectionCode.AnnouncementNotAllowed; Message = "This server does not accept announcements from this source."; Field = "source" }

    let parseKind (text: string) =
        match (if isNull text then "" else text.Trim().ToLowerInvariant()) with
        | "announcement" -> Some AnnouncementKind.Announcement
        | "event" -> Some AnnouncementKind.Event
        | "admin" -> Some AnnouncementKind.Admin
        | "periodic" -> Some AnnouncementKind.Periodic
        | _ -> None

    /// Resolves the schedule once, at runtime start. Server text has the chat
    /// limit; the channel limits are checked by the channel owner itself.
    let resolve (limits: ChatInputLimits) options : Result<(ServerAnnouncement * ScheduledAnnouncement) list, string list> =
        if isNull (box options) || isNull (box options.TrustedClient) || isNull (box options.ThirdParty) || isNull (box options.Scheduled) then
            Error ["Announcements sections cannot be null."]
        else
            let entries =
                options.Scheduled |> List.mapi (fun index entry ->
                    let name = $"Announcements.Scheduled[{index}]"
                    match ChatMessageText.create limits.MessageText entry.Text, parseKind entry.Kind with
                    | Error _, _ -> Error $"{name}.Text must be nonempty text of at most {limits.MessageText} characters."
                    | _, None -> Error $"{name}.Kind must be Announcement, Event, Admin or Periodic."
                    | Ok _, Some _ when entry.DelaySeconds < 0 || entry.IntervalSeconds < 0 ->
                        Error $"{name} delay and interval must be nonnegative."
                    | Ok _, Some _ when entry.IntervalSeconds > 0 && entry.IntervalSeconds < 10 ->
                        Error $"{name}.IntervalSeconds must be 0 (once) or at least 10."
                    | Ok text, Some kind -> Ok({ Text = text; Kind = kind }, entry))
            let errors = entries |> List.choose (function Error error -> Some error | Ok _ -> None)
            if errors.IsEmpty then Ok(entries |> List.choose (function Ok entry -> Some entry | Error _ -> None))
            else Error errors

/// Owned by the runtime. Due times are milliseconds of Environment.TickCount64.
type AnnouncementSchedule = private {
    Items: ServerAnnouncement array
    Intervals: int64 array
    Next: int64 array
}

[<RequireQualifiedAccess>]
module AnnouncementSchedule =
    let create startMs (entries: (ServerAnnouncement * ScheduledAnnouncement) list) =
        let entries = List.toArray entries
        {
            Items = entries |> Array.map fst
            Intervals = entries |> Array.map (fun (_, entry) -> int64 entry.IntervalSeconds * 1000L)
            Next = entries |> Array.map (fun (_, entry) -> startMs + int64 entry.DelaySeconds * 1000L)
        }

    /// Announcements due at nowMs, in configuration order. A periodic entry
    /// moves to nowMs + interval: after a stall it is published once, not
    /// replayed for every missed period. A one-off entry retires.
    let due nowMs (schedule: AnnouncementSchedule) =
        [
            for index in 0 .. schedule.Items.Length - 1 do
                if schedule.Next[index] <> Int64.MaxValue && nowMs >= schedule.Next[index] then
                    yield schedule.Items[index]
                    schedule.Next[index] <-
                        if schedule.Intervals[index] > 0L then nowMs + schedule.Intervals[index] else Int64.MaxValue
        ]
