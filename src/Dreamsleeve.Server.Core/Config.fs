namespace Dreamsleeve.Server.Core

open System.Net
open System.Net.Sockets

type ChatInputLimits = {
    Username: int
    DisplayName: int
    MessageText: int
    /// Client announcement text, Unicode scalar values.
    AnnouncementText: int
    /// Self-declared mod label of a client announcement, Unicode scalar values.
    AnnouncementSignature: int
    /// A note written on the ground, Unicode scalar values; the chat text rules apply.
    GroundNoteText: int
    /// Death mark label (killer or cause), one line, Unicode scalar values; may be empty.
    DeathMarkText: int
}

type PlayerInputLimits = {
    CharacterName: int
    PluginName: int
    LocationName: int
    ActorValueKey: int
    ActorValueName: int
    MaxActorValues: int
    DetailsText: int
    ActivityKey: int
}

/// Limits apply to handoff memory and our admission pass, not to the duration of ENet Service.
type TransportWorkerOptions = {
    QueueCapacity: int
    QueueBytes: int
    SendCommandsPerPass: int
    SendBytesPerPass: int
    WorkBudgetMs: int
    IdleWaitMs: int
}

/// Supplied before starting the network owner; fixed for the host/session lifetime.
type ServerConfig =
    {
        ServerName: string
        BindAddress: IPAddress
        Port: uint16
        PeerLimit: int
        ChannelLimit: int
        ServiceTimeoutMs: uint32
        EventBudget: int
        Worker: TransportWorkerOptions
        MaxPacketBytes: int
        /// Zero uses the negotiated MTU; positive values can lower the realtime payload target.
        MovementPacketTargetBytes: int
        ReceiveBufferBytes: int
        SendBufferBytes: int
        MaxWaitingData: int
        MaxOutgoingPacketsPerPeer: int
        MaxOutgoingBytesPerPeer: int
        MaxOutgoingPackets: int
        MaxOutgoingBytes: int
        MaxInitialPlayers: int
        MaxRecentMessages: int
        ChatInput: ChatInputLimits
        PlayerInput: PlayerInputLimits
    }

[<RequireQualifiedAccess>]
module ServerConfig =
    /// IPv4 must match the native ENet client. Checksums and compression remain disabled.
    let defaults =
        {
            ServerName = "Dreamsleeve"
            BindAddress = IPAddress.Loopback
            Port = 8778us
            PeerLimit = 32
            ChannelLimit = 3
            ServiceTimeoutMs = 0u
            EventBudget = 64
            Worker = { QueueCapacity = 65536; QueueBytes = 16 * 1024 * 1024
                       SendCommandsPerPass = 2048; SendBytesPerPass = 4 * 1024 * 1024
                       WorkBudgetMs = 2; IdleWaitMs = 1 }
            MaxPacketBytes = 1024 * 1024
            MovementPacketTargetBytes = 0
            ReceiveBufferBytes = 256 * 1024
            SendBufferBytes = 256 * 1024
            MaxWaitingData = 32 * 1024 * 1024
            MaxOutgoingPacketsPerPeer = 256
            MaxOutgoingBytesPerPeer = 4 * 1024 * 1024
            MaxOutgoingPackets = 4096
            MaxOutgoingBytes = 32 * 1024 * 1024
            MaxInitialPlayers = 4096
            MaxRecentMessages = 512
            ChatInput = { Username = 32; DisplayName = 64; MessageText = 2000; AnnouncementText = 500; AnnouncementSignature = 64
                          GroundNoteText = 200; DeathMarkText = 64 }
            PlayerInput = {
                CharacterName = 128; PluginName = 260; LocationName = 256
                ActorValueKey = 128; ActorValueName = 128; MaxActorValues = 64
                DetailsText = 256; ActivityKey = 64
            }
        }

    let private protocolErrors (config: ServerConfig) =
        [
            if System.String.IsNullOrWhiteSpace config.ServerName || config.ServerName.Length > 128
               || config.ServerName |> Seq.exists System.Char.IsControl then
                "ServerName must contain 1–128 characters without control characters."
            if config.MovementPacketTargetBytes < 0 then "MovementPacketTargetBytes must be nonnegative."
            if config.MaxPacketBytes < 1 then "MaxPacketBytes must be positive."
            if config.MaxWaitingData < config.MaxPacketBytes then
                "MaxWaitingData must allow at least one maximum-size packet."
            if config.MaxInitialPlayers < 1 then "MaxInitialPlayers must be positive."
            if config.MaxRecentMessages < 0 then "MaxRecentMessages must be nonnegative."
            if config.ChatInput.Username < 1 then "ChatInput.Username must be positive."
            if config.ChatInput.DisplayName < 1 then "ChatInput.DisplayName must be positive."
            if config.ChatInput.MessageText < 1 then "ChatInput.MessageText must be positive."
            if config.ChatInput.AnnouncementText < 1 then "ChatInput.AnnouncementText must be positive."
            if config.ChatInput.AnnouncementSignature < 1 || config.ChatInput.AnnouncementSignature > 128 then
                "ChatInput.AnnouncementSignature must be between 1 and 128."
            if config.ChatInput.GroundNoteText < 1 then "ChatInput.GroundNoteText must be positive."
            if config.ChatInput.DeathMarkText < 1 || config.ChatInput.DeathMarkText > 128 then
                "ChatInput.DeathMarkText must be between 1 and 128."
            if config.PlayerInput.CharacterName < 1 then "PlayerInput.CharacterName must be positive."
            if config.PlayerInput.PluginName < 1 then "PlayerInput.PluginName must be positive."
            if config.PlayerInput.LocationName < 1 then "PlayerInput.LocationName must be positive."
            if config.PlayerInput.ActorValueKey < 1 then "PlayerInput.ActorValueKey must be positive."
            if config.PlayerInput.ActorValueName < 1 then "PlayerInput.ActorValueName must be positive."
            if config.PlayerInput.MaxActorValues < 1 then "PlayerInput.MaxActorValues must be positive."
            if config.PlayerInput.DetailsText < 1 then "PlayerInput.DetailsText must be positive."
            if config.PlayerInput.ActivityKey < 1 then "PlayerInput.ActivityKey must be positive."
        ]

    /// ENet refuses more peers than this.
    [<Literal>]
    let MaxPeerLimit = 4095

    /// ENet channels: at least the three delivery lanes, at most what one byte addresses.
    [<Literal>]
    let MinChannelLimit = 3

    [<Literal>]
    let MaxChannelLimit = 255

    [<Literal>]
    let MinIdleWaitMs = 1

    [<Literal>]
    let MaxIdleWaitMs = 10

    /// Checked once, by GameSettings.create, before ENet is initialized.
    let validate config =
        let errors =
            [
                if isNull config.BindAddress || config.BindAddress.AddressFamily <> AddressFamily.InterNetwork then
                    "BindAddress must be an IPv4 address."
                if config.Port = 0us then
                    "Port must be between 1 and 65535."
                if config.PeerLimit < 1 || config.PeerLimit > MaxPeerLimit then
                    $"PeerLimit must be between 1 and {MaxPeerLimit}."
                if config.ChannelLimit < MinChannelLimit || config.ChannelLimit > MaxChannelLimit then
                    $"ChannelLimit must be between {MinChannelLimit} and {MaxChannelLimit}."
                if config.ReceiveBufferBytes < 1 || config.SendBufferBytes < 1 then
                    "UDP socket buffer sizes must be positive."
                if config.ServiceTimeoutMs <> 0u then "ServiceTimeoutMs must be zero; the owner uses an interruptible idle wait."
                if config.Worker.QueueCapacity < 1 || int64 config.Worker.QueueCapacity + 2L * int64 config.PeerLimit + 2L > int64 System.Int32.MaxValue then
                    "Worker.QueueCapacity must be positive and leave room for lifecycle reserve."
                if config.Worker.QueueBytes < config.MaxPacketBytes then "Worker.QueueBytes must allow a maximum-size packet."
                if config.Worker.SendCommandsPerPass < 1 || config.Worker.SendBytesPerPass < 1 || config.Worker.WorkBudgetMs < 1 then
                    "Worker admission budgets must be positive."
                if config.Worker.IdleWaitMs < MinIdleWaitMs || config.Worker.IdleWaitMs > MaxIdleWaitMs then
                    $"Worker.IdleWaitMs must be between {MinIdleWaitMs} and {MaxIdleWaitMs}."
                if config.EventBudget < 1 then
                    "EventBudget must be positive."
                if config.MaxOutgoingPacketsPerPeer < 1 then
                    "MaxOutgoingPacketsPerPeer must be positive."
                if config.MaxOutgoingPackets < config.MaxOutgoingPacketsPerPeer then
                    "MaxOutgoingPackets must allow at least one peer budget."
                if config.MaxOutgoingBytesPerPeer < config.MaxPacketBytes then
                    "MaxOutgoingBytesPerPeer must allow at least one maximum-size packet."
                if config.MaxOutgoingBytes < config.MaxOutgoingBytesPerPeer then
                    "MaxOutgoingBytes must allow at least one peer budget."
                yield! protocolErrors config
            ]

        if List.isEmpty errors then Ok config else Error errors

    /// Apply before serving any peers; the codec uses the same settings.
    let applyPacketLimits config (host: Enet.EnetHost) =
        if not host.IsCreated then Error ["ENet host must be created."]
        else
            host.SetMaximumPacketSize(unativeint config.MaxPacketBytes)
            host.SetMaximumWaitingData(unativeint config.MaxWaitingData)
            Ok ()
