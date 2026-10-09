namespace Dreamsleeve.Server.Core

open System
open Dreamsleeve.Server.Domain

/// One delivery policy shared by the handoff and native adapter.
[<RequireQualifiedAccess>]
type LaneReliability =
    | Reliable
    | Sequenced
    | SequencedFragmented

[<RequireQualifiedAccess>]
module LanePolicy =
    let reliability = function
        | DeliveryLane.Control | DeliveryLane.Chat | DeliveryLane.Models -> LaneReliability.Reliable
        | DeliveryLane.Realtime -> LaneReliability.Sequenced
        | DeliveryLane.Poses -> LaneReliability.SequencedFragmented
    let reliable lane = reliability lane = LaneReliability.Reliable
    let bulk lane = lane = DeliveryLane.Models
    // Queue/native byte quotas use the control reserve for small model notices.
    // Their wire lane and reliability remain Models/reliable.
    let admissionLane (packet: TransportPacket) =
        match packet.Schedule with
        | PacketSchedule.ModelNotice _ -> DeliveryLane.Control
        | _ -> packet.Lane


type PhantomOptions = {
    Enabled: bool
    CameraCulling: bool
    StoragePath: string
    DiskBytes: int64
    RamBytes: int64
    CacheEntries: int
    CacheTtlSeconds: int

    Limits: PhantomAssetLimits
    Maximum: int
    Distance: float32
    MaxSources: int
    MaxSubscribers: int

    MaxTransfers: int
    TransfersPerPlayer: int
    ChunkBytes: int
    TransferTimeoutMs: int

    PublishCooldownMs: int
    PoseIntervalMs: int
    PoseTimeoutMs: int
    ReplicationIntervalMs: int

    ModelBytesPerSecond: int
    PlayerModelBytesPerSecond: int
    PoseBytesPerSecond: int
    TotalPoseBytesPerSecond: int
    CommandsPerSecond: int
    HttpRequestsPerMinute: int
    MaxPoseFanoutPerTick: int
}

[<RequireQualifiedAccess>]
module PhantomOptions =
    let defaults = {
        Enabled = true
        CameraCulling = true
        StoragePath = "phantoms"
        DiskBytes = 4L * 1024L * 1024L * 1024L
        RamBytes = 64L * 1024L * 1024L
        CacheEntries = 1024
        CacheTtlSeconds = 86400

        Limits = {
            CompressedBytes = 64 * 1024 * 1024
            RawBytes = 128 * 1024 * 1024
            Channels = 4096
            PoseBytes = 128 * 1024
            RawPoseBytes = 256 * 1024
        }
        Maximum = 4
        Distance = 4096.0f
        MaxSources = 512
        MaxSubscribers = 64

        MaxTransfers = 64
        TransfersPerPlayer = 2
        ChunkBytes = 16384
        TransferTimeoutMs = 30000

        PublishCooldownMs = 1000
        PoseIntervalMs = 100
        PoseTimeoutMs = 1000
        ReplicationIntervalMs = 100

        ModelBytesPerSecond = 5 * 1024 * 1024
        PlayerModelBytesPerSecond = 5 * 1024 * 1024
        PoseBytesPerSecond = 2 * 1024 * 1024
        TotalPoseBytesPerSecond = 128 * 1024 * 1024
        CommandsPerSecond = 128
        HttpRequestsPerMinute = 128
        MaxPoseFanoutPerTick = 2048
    }
    let validate (options: PhantomOptions) = [
        if String.IsNullOrWhiteSpace options.StoragePath then "Phantoms.StoragePath must be set."
        if options.DiskBytes < int64 options.Limits.CompressedBytes || options.RamBytes < 0L then "Phantoms cache quotas are invalid."
        for name, value in [ "CacheEntries", options.CacheEntries; "CacheTtlSeconds", options.CacheTtlSeconds;
                            "CompressedBytes", options.Limits.CompressedBytes; "RawBytes", options.Limits.RawBytes;
                            "Channels", options.Limits.Channels; "PoseBytes", options.Limits.PoseBytes;
                            "RawPoseBytes", options.Limits.RawPoseBytes;
                            "Maximum", options.Maximum; "MaxSources", options.MaxSources; "MaxSubscribers", options.MaxSubscribers;
                            "MaxTransfers", options.MaxTransfers; "TransfersPerPlayer", options.TransfersPerPlayer;
                            "ChunkBytes", options.ChunkBytes; "TransferTimeoutMs", options.TransferTimeoutMs;
                            "PoseIntervalMs", options.PoseIntervalMs; "PoseTimeoutMs", options.PoseTimeoutMs;
                            "ReplicationIntervalMs", options.ReplicationIntervalMs; "ModelBytesPerSecond", options.ModelBytesPerSecond;
                            "PlayerModelBytesPerSecond", options.PlayerModelBytesPerSecond; "PoseBytesPerSecond", options.PoseBytesPerSecond;
                            "TotalPoseBytesPerSecond", options.TotalPoseBytesPerSecond;
                            "CommandsPerSecond", options.CommandsPerSecond; "HttpRequestsPerMinute", options.HttpRequestsPerMinute; "MaxPoseFanoutPerTick", options.MaxPoseFanoutPerTick ] do
            if value < 1 then $"Phantoms.{name} must be positive."
        if options.PublishCooldownMs < 0 || options.PublishCooldownMs > 60000 then "Phantoms.PublishCooldownMs must be between zero and 60000."
        // File I/O buffer size, independent of TCP flight or actor service interval.
        if options.ChunkBytes < 4096 || options.ChunkBytes > 1024 * 1024 then "Phantoms.ChunkBytes must be between 4096 and 1048576."
        if options.Maximum > 64 || options.MaxSources > ServerConfig.MaxPeerLimit || options.MaxSubscribers > ServerConfig.MaxPeerLimit
           || options.MaxTransfers > 1024 || options.TransfersPerPlayer > 32 || options.CacheEntries > 65536 then
            "Phantoms admission/queue bounds exceed hard limits."
        if options.TotalPoseBytesPerSecond < options.PoseBytesPerSecond then "Phantoms.TotalPoseBytesPerSecond must include one player budget."
        if options.Limits.PoseBytes > 128 * 1024 || options.Limits.RawPoseBytes > 256 * 1024 || options.Limits.Channels > 4096 then
            "Phantoms pose/channel exceeds format bound."
        if options.Limits.CompressedBytes > 64 * 1024 * 1024 || options.Limits.RawBytes > 128 * 1024 * 1024 then
            "Phantoms asset exceeds format bound."
        if not (Single.IsFinite options.Distance) || options.Distance < 0.0f then "Phantoms.Distance must be finite and nonnegative."
        if options.PlayerModelBytesPerSecond < options.ChunkBytes || options.ModelBytesPerSecond < options.PlayerModelBytesPerSecond then
            "Phantoms traffic budgets must allow one chunk."
    ]

    let policy options : PhantomServerPolicy = {
        Enabled = options.Enabled
        Limits = options.Limits
        SampleRate = max 1 (1000 / options.PoseIntervalMs)
        Maximum = options.Maximum
        Distance = options.Distance
        ConcurrentTransfers = options.TransfersPerPlayer
        ModelBytesPerSecond = options.PlayerModelBytesPerSecond
        PoseBytesPerSecond = options.PoseBytesPerSecond
    }
