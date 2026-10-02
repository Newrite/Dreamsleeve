namespace Dreamsleeve.Server.Core

open System
open Dreamsleeve.Server.Domain

/// The game part of the configuration ([Server], [Runtime], [Identity],
/// [Announcements], [GroundMarks], [Guilds]) checked once, with what follows from it.
/// Only GameSettings.create makes a value, so the runtime and the owners it
/// starts never check these settings again.
type GameSettings =
    private {
        server: ServerConfig
        runtime: ServerRuntimeOptions
        identity: IdentityOptions
        announcements: AnnouncementOptions
        groundMarks: GroundMarkOptions
        guilds: GuildOptions
        codec: ProtocolCodec
        schedule: (ServerAnnouncement * ScheduledAnnouncement) list
        groundMarkRules: GroundMarkRules
        guildLimits: GuildLimits
    }

    member this.Server = this.server
    member this.Runtime = this.runtime
    member this.Identity = this.identity
    member this.Announcements = this.announcements
    member this.GroundMarks = this.groundMarks
    member this.Codec = this.codec
    /// The server's own announcements from [[Announcements.Scheduled]].
    member this.Schedule = this.schedule
    member this.GroundMarkRules = this.groundMarkRules
    member this.Guilds = this.guilds
    member this.GuildLimits = this.guildLimits

[<RequireQualifiedAccess>]
module GameSettings =
    /// Sources that acknowledge the cleanup of every session: chat, system
    /// channel, presence, ground marks and guilds.
    [<Literal>]
    let CleanupSources = 5

    // Ordinary capacity plus control reserve of one mailbox must fit Int32.
    let private mailbox section capacity reserve = [
        if int64 capacity + int64 reserve > int64 Int32.MaxValue then $"{section} MailboxCapacity and ControlReserve overflow."
    ]

    let private runtimeErrors (server: ServerConfig) (options: ServerRuntimeOptions) (announcements: AnnouncementOptions) = [
        let positive = [
            "Runtime.MaxSessions", options.MaxSessions; "Runtime.MailboxCapacity", options.MailboxCapacity
            "Runtime.ControlReserve", options.ControlReserve; "Runtime.OpenTimeoutMs", options.OpenTimeoutMs
            "Runtime.ShutdownTimeoutMs", options.ShutdownTimeoutMs; "Runtime.PollIntervalMs", options.PollIntervalMs
            "Runtime.Player.MailboxCapacity", options.Player.MailboxCapacity; "Runtime.Player.ControlReserve", options.Player.ControlReserve
            "Runtime.Player.MaxPendingChat", options.Player.MaxPendingChat; "Runtime.Player.MaxPendingUpdates", options.Player.MaxPendingUpdates
            "Runtime.Player.MaxBootstrapEvents", options.Player.MaxBootstrapEvents; "Runtime.Player.MaxPendingOutput", options.Player.MaxPendingOutput
            "Runtime.Chat.MailboxCapacity", options.Chat.MailboxCapacity; "Runtime.Chat.ControlReserve", options.Chat.ControlReserve
            "Runtime.Chat.HistoryCapacity", options.Chat.HistoryCapacity; "Runtime.Chat.MaxControlDeliveries", options.Chat.MaxControlDeliveries
            "Runtime.Presence.MailboxCapacity", options.Presence.MailboxCapacity; "Runtime.Presence.ControlReserve", options.Presence.ControlReserve
            "Runtime.Presence.MaxControlDeliveries", options.Presence.MaxControlDeliveries
            "Runtime.Presence.ReplicationIntervalMs", options.Presence.ReplicationIntervalMs
            "Announcements.HistoryCapacity", announcements.HistoryCapacity
        ]
        for name, value in positive do
            if value < 1 then $"{name} must be positive."
        yield! mailbox "Runtime" options.MailboxCapacity options.ControlReserve
        yield! mailbox "Runtime.Player" options.Player.MailboxCapacity options.Player.ControlReserve
        yield! mailbox "Runtime.Chat" options.Chat.MailboxCapacity options.Chat.ControlReserve
        yield! mailbox "Runtime.Presence" options.Presence.MailboxCapacity options.Presence.ControlReserve
        let pending = [ options.Player.MaxPendingChat; options.Player.MaxPendingUpdates; options.Player.MaxPendingOutput ]
        if pending |> List.exists (fun value -> value > Int32.MaxValue - PlayerSessionOptions.OutboxReserve) then
            "Runtime.Player pending limits plus the outbox reserve overflow."
        if not (Single.IsFinite options.Presence.VisibilityDistance) || options.Presence.VisibilityDistance < 0.0f then
            "Runtime.Presence.VisibilityDistance must be finite and non-negative."
        yield! RateLimitOptions.validate "Runtime.Chat.Rate" options.Chat.Rate
        yield! RateLimitOptions.validate "Announcements.Rate" announcements.Rate
        if int64 options.ControlReserve < int64 CleanupSources * int64 options.MaxSessions + int64 CleanupSources then
            $"Runtime.ControlReserve must allow {CleanupSources} * MaxSessions + {CleanupSources} cleanup acknowledgements."
        if options.MaxSessions > server.PeerLimit then "Runtime.MaxSessions cannot exceed Server.PeerLimit."
        if options.MaxSessions > server.MaxInitialPlayers then "Server.MaxInitialPlayers must include every admitted session."
        if options.Chat.HistoryCapacity > server.MaxRecentMessages || announcements.HistoryCapacity > server.MaxRecentMessages then
            "Server.MaxRecentMessages must include the retained chat and announcement history."
        if int64 server.ServiceTimeoutMs + int64 options.PollIntervalMs > int64 (min options.OpenTimeoutMs options.ShutdownTimeoutMs) then
            "Server.ServiceTimeoutMs plus Runtime.PollIntervalMs must fit the runtime deadlines."
    ]

    let create server runtime identity announcements groundMarks (guilds: GuildOptions) : Result<GameSettings, string list> =
        let errors = [
            match ServerConfig.validate server with Ok _ -> () | Error errors -> yield! errors
            yield! runtimeErrors server runtime announcements
            yield! IdentityOptions.validate identity
            yield! GroundMarkOptions.validate groundMarks
            yield! GuildOptions.validate guilds
            if guilds.HistoryCapacity > server.MaxRecentMessages then "Server.MaxRecentMessages must include the retained guild chat history."
        ]
        let schedule = AnnouncementOptions.resolve server.ChatInput announcements
        let rules = if errors.IsEmpty then GroundMarkOptions.rules groundMarks |> Result.mapError (fun error -> [ sprintf "GroundMarks: %A" error ]) else Error []
        let limits = if errors.IsEmpty then GuildOptions.rules guilds |> Result.mapError (fun error -> [ sprintf "Guilds: %A" error ]) else Error []
        match errors, schedule, rules, limits with
        | [], Ok schedule, Ok rules, Ok limits ->
            Ok { server = server; runtime = runtime; identity = identity; announcements = announcements; groundMarks = groundMarks
                 guilds = guilds; codec = ProtocolCodec.create server; schedule = schedule; groundMarkRules = rules; guildLimits = limits }
        | errors, schedule, rules, limits ->
            let scheduleErrors = match schedule with Error errors -> errors | Ok _ -> []
            let rulesErrors = match rules with Error errors -> errors | Ok _ -> []
            let limitErrors = match limits with Error errors -> errors | Ok _ -> []
            Error (errors @ scheduleErrors @ rulesErrors @ limitErrors)
