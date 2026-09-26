namespace Dreamsleeve.Server.Core

open System.Net
open System.Net.Sockets

type ChatInputLimits = {
    Username: int
    DisplayName: int
    MessageText: int
}

/// Supplied before starting the network owner; fixed for the host/session lifetime.
type ServerConfig =
    {
        BindAddress: IPAddress
        Port: uint16
        PeerLimit: int
        ChannelLimit: int
        ServiceTimeoutMs: uint32
        EventBudget: int
        MaxPacketBytes: int
        MaxWaitingData: int
        MaxInitialPlayers: int
        MaxRecentMessages: int
        ChatInput: ChatInputLimits
        ShutdownTimeoutMs: uint32
    }

[<RequireQualifiedAccess>]
module ServerConfig =
    /// IPv4 must match the native ENet client. Checksums and compression remain disabled.
    let defaults =
        {
            BindAddress = IPAddress.Loopback
            Port = 8778us
            PeerLimit = 32
            ChannelLimit = 2
            ServiceTimeoutMs = 10u
            EventBudget = 64
            MaxPacketBytes = 1024 * 1024
            MaxWaitingData = 32 * 1024 * 1024
            MaxInitialPlayers = 4096
            MaxRecentMessages = 512
            ChatInput = { Username = 32; DisplayName = 64; MessageText = 2000 }
            ShutdownTimeoutMs = 1500u
        }

    // Used at codec creation and host startup; no per-packet config validation.
    let protocolErrors (config: ServerConfig) =
        [
            if config.MaxPacketBytes < 1 then "MaxPacketBytes must be positive."
            if config.MaxWaitingData < config.MaxPacketBytes then
                "MaxWaitingData must allow at least one maximum-size packet."
            if config.MaxInitialPlayers < 1 then "MaxInitialPlayers must be positive."
            if config.MaxRecentMessages < 0 then "MaxRecentMessages must be nonnegative."
            if config.ChatInput.Username < 1 then "ChatInput.Username must be positive."
            if config.ChatInput.DisplayName < 1 then "ChatInput.DisplayName must be positive."
            if config.ChatInput.MessageText < 1 then "ChatInput.MessageText must be positive."
        ]

    /// Validate before initializing ENet or allocating its host.
    let validate config =
        let errors =
            [
                if isNull config.BindAddress || config.BindAddress.AddressFamily <> AddressFamily.InterNetwork then
                    "BindAddress must be an IPv4 address."
                if config.Port = 0us then
                    "Port must be between 1 and 65535."
                if config.PeerLimit < 1 || config.PeerLimit > 4095 then
                    "PeerLimit must be between 1 and 4095."
                if config.ChannelLimit < 1 || config.ChannelLimit > 255 then
                    "ChannelLimit must be between 1 and 255."
                if config.EventBudget < 1 then
                    "EventBudget must be positive."
                yield! protocolErrors config
            ]

        if List.isEmpty errors then Ok config else Error errors

    /// Apply before serving any peers. Create ChatCodec from the same config before serving peers.
    let applyPacketLimits config (host: Enet.EnetHost) =
        match validate config with
        | Error errors -> Error errors
        | Ok _ when not host.IsCreated -> Error ["ENet host must be created."]
        | Ok settings ->
            host.SetMaximumPacketSize(unativeint settings.MaxPacketBytes)
            host.SetMaximumWaitingData(unativeint settings.MaxWaitingData)
            Ok ()
