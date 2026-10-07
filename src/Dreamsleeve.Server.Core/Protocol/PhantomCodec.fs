namespace Dreamsleeve.Server.Core

open System
open Google.Protobuf
open Dreamsleeve.Server.Domain

[<RequireQualifiedAccess>]
module PhantomCodec =


    let private parse maximum (bytes: byte array) (parser: MessageParser<'a>) validate =
        if isNull bytes || bytes.Length = 0 || bytes.Length > maximum then Error "packet size"
        else
            try validate (parser.ParseFrom bytes)
            with :? InvalidProtocolBufferException -> Error "malformed protobuf"

    let private manifest options (asset: Dreamsleeve.Protocol.Phantom.AssetDescriptor) =
        if isNull asset then Error "missing asset"
        else
            match AssetHash.create (asset.Hash.ToByteArray()), AppearanceGeneration.create asset.Generation with
            | Ok hash, Ok generation -> PhantomManifest.create options.Limits hash generation asset.FormatVersion asset.CompressedBytes asset.RawBytes asset.Channels
            | Error error, _ | _, Error error -> Error error

    let decodeAsset options bytes =
        parse (options.ChunkBytes + 512) bytes Dreamsleeve.Protocol.Phantom.ClientAssetPacket.Parser (fun packet ->
            if packet.ProtocolVersion <> ProtocolCodec.Version then Error "protocol version"
            else
                match packet.PayloadCase with
                | Dreamsleeve.Protocol.Phantom.ClientAssetPacket.PayloadOneofCase.Preferences ->
                    let value = packet.Preferences
                    if not (Single.IsFinite value.Distance) || value.Distance < 0.0f || value.Maximum > uint32 Int32.MaxValue then Error "preferences"
                    else Ok (PhantomRequest.Preferences { Publish = value.Publish; Receive = value.Receive; Maximum = int value.Maximum; Distance = value.Distance })
                | Dreamsleeve.Protocol.Phantom.ClientAssetPacket.PayloadOneofCase.Publish ->
                    match manifest options packet.Publish.Asset, PhantomRequestId.create packet.Publish.RequestId with
                    | Ok value, Ok request when packet.Publish.ContextRevision <> 0UL -> Ok (PhantomRequest.Publish(value, packet.Publish.ContextRevision, request))
                    | _ -> Error "publish descriptor/context/request"
                | Dreamsleeve.Protocol.Phantom.ClientAssetPacket.PayloadOneofCase.Chunk ->
                    let value = packet.Chunk
                    if value.TransferId = 0UL || value.Offset > uint32 Int32.MaxValue || value.Data.Length = 0 || value.Data.Length > options.ChunkBytes then Error "chunk"
                    else Ok (PhantomRequest.Chunk(PhantomTransferId value.TransferId, int value.Offset, value.Data.ToByteArray()))
                | Dreamsleeve.Protocol.Phantom.ClientAssetPacket.PayloadOneofCase.Download ->
                    match PlayerId.create packet.Download.PlayerId, AppearanceGeneration.create packet.Download.Generation, PhantomRequestId.create packet.Download.RequestId with
                    | Ok player, Ok generation, Ok request -> Ok (PhantomRequest.Download(player, generation, request))
                    | _ -> Error "download"
                | Dreamsleeve.Protocol.Phantom.ClientAssetPacket.PayloadOneofCase.Cancel when packet.Cancel.TransferId <> 0UL -> Ok (PhantomRequest.Cancel(PhantomTransferId packet.Cancel.TransferId))
                | Dreamsleeve.Protocol.Phantom.ClientAssetPacket.PayloadOneofCase.Progress when packet.Progress.TransferId <> 0UL && packet.Progress.NextOffset <= uint32 Int32.MaxValue ->
                    Ok (PhantomRequest.Progress(PhantomTransferId packet.Progress.TransferId, int packet.Progress.NextOffset))
                | Dreamsleeve.Protocol.Phantom.ClientAssetPacket.PayloadOneofCase.Displayed ->
                    let value = packet.Displayed
                    match PlayerId.create value.PlayerId, AppearanceGeneration.create value.Generation with
                    | Ok player, Ok generation when value.ViewRevision <> 0UL -> Ok (PhantomRequest.Displayed(player, value.ViewRevision, generation))
                    | _ -> Error "displayed"
                | Dreamsleeve.Protocol.Phantom.ClientAssetPacket.PayloadOneofCase.Withdraw -> Ok PhantomRequest.Withdraw
                | _ -> Error "payload")

    let decodePose options bytes =
        let sample (value: Dreamsleeve.Protocol.Phantom.PoseSample) =
            if isNull value || value.ContextRevision = 0UL || value.Payload.Length = 0 || value.Payload.Length > options.Limits.PoseBytes then Error "pose envelope"
            else
                match AppearanceGeneration.create value.Generation, PhantomSequence.create value.Sequence with
                | Ok generation, Ok sequence -> PhantomPose.create options.Limits generation value.ContextRevision sequence value.SampledAtUs value.Payload.Memory
                | _ -> Error "pose generation/sequence"
        parse (PhantomAssetLimits.posePacketBytes options.Limits) bytes Dreamsleeve.Protocol.Phantom.ClientPosePacket.Parser (fun packet ->
            if packet.ProtocolVersion <> ProtocolCodec.Version then Error "protocol version"
            else
                match sample packet.Sample with
                | Error error -> Error error
                | Ok pose when isNull packet.PreviousSample -> Ok pose
                | Ok pose -> sample packet.PreviousSample |> Result.bind (fun previous -> PhantomPose.withPrevious previous pose))

    let private descriptor (value: PhantomManifest) =
        Dreamsleeve.Protocol.Phantom.AssetDescriptor(Hash = ByteString.CopyFrom(AssetHash.bytes value.Hash), Generation = value.Generation.Value
                             , FormatVersion = value.FormatVersion, CompressedBytes = uint32 value.CompressedBytes
                             , RawBytes = uint32 value.RawBytes, Channels = uint32 value.Channels)

    let encode response =
        let packet = Dreamsleeve.Protocol.Phantom.ServerAssetPacket(ProtocolVersion = ProtocolCodec.Version)
        match response with
        | PhantomResponse.Offer(player, revision, value) -> packet.Offer <- Dreamsleeve.Protocol.Phantom.Offer(PlayerId = PlayerId.value player, ViewRevision = revision, Asset = descriptor value)
        | PhantomResponse.Transfer(id, value, player, upload, request) -> packet.Transfer <- Dreamsleeve.Protocol.Phantom.Transfer(TransferId = id.Value, Asset = descriptor value, PlayerId = PlayerId.value player, Upload = upload, RequestId = request.Value)
        | PhantomResponse.Chunk(id, offset, bytes) -> packet.Chunk <- Dreamsleeve.Protocol.Phantom.Chunk(TransferId = id.Value, Offset = uint32 offset, Data = UnsafeByteOperations.UnsafeWrap(ReadOnlyMemory<byte>(bytes)))
        | PhantomResponse.Complete(id, accepted, reason, request, target) ->
            let value = Dreamsleeve.Protocol.Phantom.Complete(TransferId = id.Value, Accepted = accepted, Reason = reason, RequestId = request.Value)
            target |> Option.iter (fun completion ->
                value.PlayerId <- PlayerId.value completion.Target.Player
                value.Generation <- completion.Target.Generation.Value
                value.Upload <- completion.Upload
                value.RetryAfterMs <- uint32 completion.RetryAfterMs)
            packet.Complete <- value
        | PhantomResponse.Remove(player, revision) -> packet.Remove <- Dreamsleeve.Protocol.Phantom.Remove(PlayerId = PlayerId.value player, ViewRevision = revision)
        | PhantomResponse.Progress(id, offset) -> packet.Progress <- Dreamsleeve.Protocol.Phantom.Progress(TransferId = id.Value, NextOffset = uint32 offset)
        | PhantomResponse.PoseDemand(context, required) ->
            packet.PoseDemand <- Dreamsleeve.Protocol.Phantom.PoseDemand(ContextRevision = context, Required = required)
        | PhantomResponse.Settled(generation, context) ->
            packet.Settled <- Dreamsleeve.Protocol.Phantom.Settled(Generation = generation.Value, ContextRevision = context)
        | PhantomResponse.Policy policy ->
            packet.Policy <- Dreamsleeve.Protocol.Phantom.Policy(Enabled = policy.Enabled, RawAssetBytes = uint32 policy.Limits.RawBytes
                , CompressedAssetBytes = uint32 policy.Limits.CompressedBytes, Channels = uint32 policy.Limits.Channels
                , PoseBytes = uint32 policy.Limits.RawPoseBytes, CompressedPoseBytes = uint32 policy.Limits.PoseBytes, SampleRate = uint32 policy.SampleRate
                , MaximumVisible = uint32 policy.Maximum, Distance = policy.Distance, WindowChunks = uint32 policy.WindowChunks
                , ConcurrentTransfers = uint32 policy.ConcurrentTransfers, ModelBytesPerSecond = uint32 policy.ModelBytesPerSecond, PoseBytesPerSecond = uint32 policy.PoseBytesPerSecond)
        // Only view changes gate that source's poses; transfer progress does not.
        let source = if not (isNull packet.Offer) then packet.Offer.PlayerId elif not (isNull packet.Remove) then packet.Remove.PlayerId else 0UL
        { Schedule = if isNull packet.Chunk then PacketSchedule.ModelNotice source else PacketSchedule.Ordered
          Lane = DeliveryLane.Models; Bytes = packet.ToByteArray() }

    let private poseSampleSize (value: PhantomPose) =
        4 + CodedOutputStream.ComputeUInt64Size value.Generation.Value + CodedOutputStream.ComputeUInt64Size value.Context
          + CodedOutputStream.ComputeUInt64Size value.Sequence.Value + CodedOutputStream.ComputeLengthSize value.Payload.Length + value.Payload.Length
          + (if value.SampledAtUs = 0UL then 0 else 1 + CodedOutputStream.ComputeUInt64Size value.SampledAtUs)

    /// Check traffic admission before allocating the shared final envelope.
    let posePacketSize player revision (value: PhantomPose) =
        let sample = poseSampleSize value
        4 + CodedOutputStream.ComputeUInt32Size ProtocolCodec.Version + CodedOutputStream.ComputeUInt64Size (PlayerId.value player)
          + CodedOutputStream.ComputeUInt64Size revision + CodedOutputStream.ComputeLengthSize sample + sample
          + (value.Previous |> Option.map (fun p -> let n = poseSampleSize p in 1 + CodedOutputStream.ComputeLengthSize n + n) |> Option.defaultValue 0)

    /// Serialize the shared immutable sample once, independently of view epochs.
    let encodePoseSample (value: PhantomPose) =
        let sample = Dreamsleeve.Protocol.Phantom.PoseSample(Generation = value.Generation.Value, ContextRevision = value.Context, Sequence = value.Sequence.Value
                                    , SampledAtUs = value.SampledAtUs, Payload = UnsafeByteOperations.UnsafeWrap(ReadOnlyMemory<byte>(value.Payload)))
        sample.ToByteArray()

    let encodePoseEnvelope player revision (sample: byte array) =
        let player = PlayerId.value player
        let length = 4 + CodedOutputStream.ComputeUInt32Size ProtocolCodec.Version + CodedOutputStream.ComputeUInt64Size player
                       + CodedOutputStream.ComputeUInt64Size revision + CodedOutputStream.ComputeLengthSize sample.Length + sample.Length
        let bytes = Array.zeroCreate length
        use output = new CodedOutputStream(bytes)
        output.WriteTag(1, WireFormat.WireType.Varint)
        output.WriteUInt32 ProtocolCodec.Version
        output.WriteTag(2, WireFormat.WireType.Varint)
        output.WriteUInt64 player
        output.WriteTag(3, WireFormat.WireType.Varint)
        output.WriteUInt64 revision
        output.WriteTag(4, WireFormat.WireType.LengthDelimited)
        output.WriteBytes(UnsafeByteOperations.UnsafeWrap(ReadOnlyMemory<byte>(sample)))
        output.CheckNoSpaceLeft()
        { Schedule = PacketSchedule.LatestPose player; Lane = DeliveryLane.Poses; Bytes = bytes }

    /// Write the immutable compressed sample directly into the final packet.
    /// Fanout shares this packet among recipients with the same view revision.
    let encodePose player revision (value: PhantomPose) =
        let bytes = Array.zeroCreate (posePacketSize player revision value)
        use output = new CodedOutputStream(bytes)
        output.WriteTag(1, WireFormat.WireType.Varint)
        output.WriteUInt32 ProtocolCodec.Version
        output.WriteTag(2, WireFormat.WireType.Varint)
        output.WriteUInt64 (PlayerId.value player)
        output.WriteTag(3, WireFormat.WireType.Varint)
        output.WriteUInt64 revision
        output.WriteTag(4, WireFormat.WireType.LengthDelimited)
        output.WriteUInt32 (uint32 (poseSampleSize value))
        output.WriteTag(1, WireFormat.WireType.Varint)
        output.WriteUInt64 value.Generation.Value
        output.WriteTag(2, WireFormat.WireType.Varint)
        output.WriteUInt64 value.Context
        output.WriteTag(3, WireFormat.WireType.Varint)
        output.WriteUInt64 value.Sequence.Value
        if value.SampledAtUs <> 0UL then
            output.WriteTag(4, WireFormat.WireType.Varint)
            output.WriteUInt64 value.SampledAtUs
        output.WriteTag(5, WireFormat.WireType.LengthDelimited)
        output.WriteBytes(UnsafeByteOperations.UnsafeWrap(ReadOnlyMemory<byte>(value.Payload)))
        value.Previous |> Option.iter (fun previous ->
            output.WriteTag(5, WireFormat.WireType.LengthDelimited)
            output.WriteBytes(UnsafeByteOperations.UnsafeWrap(ReadOnlyMemory<byte>(encodePoseSample previous))))
        output.CheckNoSpaceLeft()
        { Schedule = PacketSchedule.LatestPose(PlayerId.value player); Lane = DeliveryLane.Poses; Bytes = bytes }
