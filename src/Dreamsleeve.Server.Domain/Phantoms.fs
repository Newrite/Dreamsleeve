namespace Dreamsleeve.Server.Domain

open System

[<Struct>]
type AssetHash = private AssetHash of string with
    member this.Hex = let (AssetHash value) = this in value

[<RequireQualifiedAccess>]
module AssetHash =
    let create (bytes: byte array) =
        if isNull bytes || bytes.Length <> 32 then Error "hash must contain 32 bytes"
        else Ok (AssetHash(Convert.ToHexStringLower bytes))
    let bytes (hash: AssetHash) = Convert.FromHexString hash.Hex

[<Struct>]
type AppearanceGeneration = private AppearanceGeneration of uint64 with
    member this.Value = let (AppearanceGeneration value) = this in value

[<RequireQualifiedAccess>]
module AppearanceGeneration =
    let create value = if value = 0UL then Error "generation must be positive" else Ok (AppearanceGeneration value)

[<Struct>]
type PhantomTransferId = PhantomTransferId of uint64 with
    member this.Value = let (PhantomTransferId value) = this in value

[<Struct>]
type PhantomRequestId = private PhantomRequestId of uint64 with
    member this.Value = let (PhantomRequestId value) = this in value

[<RequireQualifiedAccess>]
module PhantomRequestId =
    let create value = if value = 0UL then Error "request_id must be positive" else Ok (PhantomRequestId value)

[<Struct>]
type PhantomSequence = private PhantomSequence of uint64 with
    member this.Value = let (PhantomSequence value) = this in value

[<RequireQualifiedAccess>]
module PhantomSequence =
    let create value = if value = 0UL then Error "sequence must be positive" else Ok (PhantomSequence value)

type PhantomAssetLimits = {
    CompressedBytes: int; RawBytes: int; Channels: int; Geometry: int; PoseBytes: int; RawPoseBytes: int
}

/// The server validates the envelope and compressed content identity, never engine data.
type PhantomManifest = private {
    hash: AssetHash; generation: AppearanceGeneration; compressedBytes: int; rawBytes: int
    channels: int; geometry: int
} with
    member this.Hash = this.hash
    member this.Generation = this.generation
    member this.CompressedBytes = this.compressedBytes
    member this.RawBytes = this.rawBytes
    member this.Channels = this.channels
    member this.Geometry = this.geometry
    member _.FormatVersion = 1u

[<RequireQualifiedAccess>]
module PhantomManifest =
    let create (limits: PhantomAssetLimits) hash generation version compressed raw channels geometry =
        if version <> 1u then Error "unsupported format_version"
        elif compressed = 0u || uint64 compressed > uint64 limits.CompressedBytes then Error "compressed_bytes exceeds limit"
        elif raw = 0u || uint64 raw > uint64 limits.RawBytes then Error "raw_bytes exceeds limit"
        elif channels = 0u || uint64 channels > uint64 limits.Channels then Error "channels exceeds limit"
        elif geometry = 0u || uint64 geometry > uint64 limits.Geometry then Error "geometry exceeds limit"
        else Ok { hash = hash; generation = generation; compressedBytes = int compressed; rawBytes = int raw
                  channels = int channels; geometry = int geometry }

type PhantomPreferences = { Publish: bool; Receive: bool; Maximum: int; Distance: float32 }

type PhantomPose = private {
    generation: AppearanceGeneration; context: uint64; sequence: PhantomSequence
    sampledAtUs: uint64; payload: byte array
} with
    member this.Generation = this.generation
    member this.Context = this.context
    member this.Sequence = this.sequence
    member this.SampledAtUs = this.sampledAtUs
    member this.Payload = this.payload

[<RequireQualifiedAccess>]
module PhantomPose =
    // Keep signed client clock calculations inside their safe range. Zero remains
    // valid for an unspecified timestamp; compressed poses remain opaque here.
    let maximumSampledAtUs = uint64 Int64.MaxValue / 2UL
    let create limits generation context sequence sampledAtUs (payload: ReadOnlyMemory<byte>) =
        if context = 0UL then Error "pose context"
        elif sampledAtUs > maximumSampledAtUs then Error "pose timestamp"
        elif payload.Length = 0 || payload.Length > limits.PoseBytes then Error "pose payload"
        else Ok { generation = generation; context = context; sequence = sequence
                  sampledAtUs = sampledAtUs; payload = payload.ToArray() }

[<RequireQualifiedAccess>]
type PhantomRequest =
    | Preferences of PhantomPreferences
    | Publish of PhantomManifest * context: uint64 * PhantomRequestId
    | Chunk of PhantomTransferId * offset: int * byte array
    | Download of PlayerId * AppearanceGeneration * PhantomRequestId
    | Cancel of PhantomTransferId
    | Progress of PhantomTransferId * nextOffset: int
    | Withdraw

[<RequireQualifiedAccess>]
type PhantomServerPolicy = {
    Enabled: bool; Limits: PhantomAssetLimits; SampleRate: int; Maximum: int; Distance: float32
    WindowChunks: int; ConcurrentTransfers: int; ModelBytesPerSecond: int; PoseBytesPerSecond: int
}

[<Struct>]
type PhantomTarget = { Player: PlayerId; Generation: AppearanceGeneration }

type PhantomCompletion = { Target: PhantomTarget; Upload: bool; RetryAfterMs: int }

[<RequireQualifiedAccess>]
type PhantomResponse =
    | Offer of PlayerId * viewRevision: uint64 * PhantomManifest
    | Transfer of PhantomTransferId * PhantomManifest * PlayerId * upload: bool * PhantomRequestId
    | Chunk of PhantomTransferId * offset: int * byte array
    | Complete of PhantomTransferId * accepted: bool * reason: string * PhantomRequestId * PhantomCompletion option
    | Remove of PlayerId * viewRevision: uint64
    | Progress of PhantomTransferId * nextOffset: int
    | Policy of PhantomServerPolicy

/// Membership remains enabled for authenticated policy bootstrap when replication
/// is disabled; only Full projects Presence-authorized views and distances.
[<RequireQualifiedAccess>]
type PhantomObservationMode =
    | Disabled
    | Membership
    | Full

[<RequireQualifiedAccess>]
type PhantomObservation =
    | Member of Guid * PlayerSnapshot
    | View of observer: Guid * source: PlayerId * revision: uint64 * distanceSquared: double
    | Hidden of observer: Guid * source: PlayerId * revision: uint64
    | Departed of Guid
    | Batch of PhantomObservation array

[<RequireQualifiedAccess>]
module PhantomPolicy =
    let effective maximum distance (preferences: PhantomPreferences) =
        { preferences with Maximum = min maximum preferences.Maximum; Distance = min distance preferences.Distance }

    /// Candidates are already authorized by Presence. Keep selected sources until a
    /// newcomer is materially nearer, preventing churn at the count boundary.
    let select (preferences: PhantomPreferences) (selected: Set<PlayerId>) (candidates: seq<PlayerId * double>) =
        if not preferences.Receive || preferences.Maximum = 0 then Set.empty
        else
            let radius = double preferences.Distance * double preferences.Distance
            candidates
            |> Seq.filter (fun (_, squared) -> squared <= radius)
            |> Seq.sortBy (fun (id, squared) -> (if selected.Contains id then squared * 0.81 else squared), id)
            |> Seq.truncate preferences.Maximum
            |> Seq.map fst |> Set.ofSeq
