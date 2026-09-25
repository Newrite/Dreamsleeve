namespace Dreamsleeve.Server.Core

open System.Net
open System.Net.Sockets

/// Settings for the first, binary ENet echo server. No application protocol is involved.
type ServerConfig =
    {
        BindAddress: IPAddress
        Port: uint16
        PeerLimit: int
        ChannelLimit: int
        ServiceTimeoutMs: uint32
        EventBudget: int
        MaxPacketBytes: int
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
            MaxPacketBytes = 64 * 1024
            ShutdownTimeoutMs = 1500u
        }

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
                if config.ServiceTimeoutMs = 0u || config.ServiceTimeoutMs > 1000u then
                    "ServiceTimeoutMs must be between 1 and 1000."
                if config.EventBudget < 1 then
                    "EventBudget must be positive."
                if config.MaxPacketBytes < 1 then
                    "MaxPacketBytes must be positive."
                if config.ShutdownTimeoutMs > 60000u then
                    "ShutdownTimeoutMs must not exceed 60000; zero forces an immediate local reset."
            ]

        if List.isEmpty errors then Ok config else Error errors
