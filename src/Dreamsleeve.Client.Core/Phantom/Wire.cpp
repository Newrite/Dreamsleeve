#include "phantom.pb.h"
import std;
import Dreamsleeve.Client.Phantom.Wire;
import Dreamsleeve.Client.ProtocolCodec;

namespace Dreamsleeve::Client::Phantom::Wire
{
  namespace Proto = Dreamsleeve::Protocol::Phantom;

  namespace
  {

    Error Invalid(std::string field)
    {
      return {Failure::InvalidFormat, std::move(field)};
    }

    template <class T>
    Result<Bytes> Serialize(const T& message)
    {
      Bytes bytes(message.ByteSizeLong());
      if (!message.SerializeToArray(bytes.data(), static_cast<int>(bytes.size()))) return std::unexpected(Invalid("protobuf"));
      return bytes;
    }

    void Set(const Descriptor& d, Proto::AssetDescriptor* out)
    {
      out->set_hash(d.hash.data(), d.hash.size());
      out->set_generation(d.generation.value);
      out->set_format_version(d.format);
      out->set_compressed_bytes(d.compressedBytes);
      out->set_raw_bytes(d.rawBytes);
      out->set_channels(d.channels);
    }

    Result<Descriptor> Get(const Proto::AssetDescriptor& in, const Limits& limits)
    {
      if (
        in.hash().size() != 32 || !in.generation() || in.format_version() != AssetVersion || !in.compressed_bytes() ||
        in.compressed_bytes() > limits.compressedAssetBytes || !in.raw_bytes() || in.raw_bytes() > limits.assetBytes || !in.channels() ||
        in.channels() > limits.nodes)
        return std::unexpected(Invalid("descriptor"));
      Digest digest;
      std::ranges::copy(in.hash(), digest.begin());
      return Descriptor{digest, Generation{in.generation()}, in.format_version(), in.compressed_bytes(), in.raw_bytes(), in.channels()};
    }

    void Set(const Pose& pose, Proto::PoseSample* out)
    {
      out->set_generation(pose.generation.value);
      out->set_context_revision(pose.context);
      out->set_sequence(pose.sequence.value);
      out->set_sampled_at_us(pose.sampledAtUs);
      out->set_payload(pose.payload.data(), pose.payload.size());
    }

  }

  Result<Bytes> Encode(const Request& request)
  {
    Proto::ClientAssetPacket packet;
    packet.set_protocol_version(Client::Wire::Version);
    std::visit(
      [&](const auto& value) {
        using T = std::decay_t<decltype(value)>;
        if constexpr (std::is_same_v<T, Preferences>)
        {
          auto* out = packet.mutable_preferences();
          out->set_publish(value.publish);
          out->set_receive(value.receive);
          out->set_maximum(value.maximum);
          out->set_distance(value.distance);
        }
        else if constexpr (std::is_same_v<T, Publish>)
        {
          auto* out = packet.mutable_publish();
          Set(value.asset, out->mutable_asset());
          out->set_context_revision(value.context);
          out->set_request_id(value.request.value);
        }
        else if constexpr (std::is_same_v<T, Chunk>)
        {
          auto* out = packet.mutable_chunk();
          out->set_transfer_id(value.transfer.value);
          out->set_offset(value.offset);
          out->set_data(value.data.data(), value.data.size());
        }
        else if constexpr (std::is_same_v<T, Download>)
        {
          auto* out = packet.mutable_download();
          out->set_player_id(value.player);
          out->set_generation(value.generation.value);
          out->set_request_id(value.request.value);
        }
        else if constexpr (std::is_same_v<T, Displayed>)
        {
          auto* out = packet.mutable_displayed();
          out->set_player_id(value.player);
          out->set_view_revision(value.view);
          out->set_generation(value.generation.value);
        }
        else if constexpr (std::is_same_v<T, Cancel>)
          packet.mutable_cancel()->set_transfer_id(value.transfer.value);
        else if constexpr (std::is_same_v<T, Progress>)
        {
          auto* out = packet.mutable_progress();
          out->set_transfer_id(value.transfer.value);
          out->set_next_offset(value.nextOffset);
        }
        else
          packet.mutable_withdraw();
      },
      request);
    return Serialize(packet);
  }

  Result<Bytes> Encode(const Pose& pose)
  {
    Proto::ClientPosePacket packet;
    packet.set_protocol_version(Client::Wire::Version);
    Set(pose, packet.mutable_sample());
    if (pose.previous) Set(*pose.previous, packet.mutable_previous_sample());
    return Serialize(packet);
  }

  Result<Response> DecodeAsset(std::span<const std::uint8_t> data, const Limits& limits)
  {
    if (data.size() > ChunkBytes + 1024) return std::unexpected(Invalid("asset.packet.size"));
    Proto::ServerAssetPacket packet;
    if (!packet.ParseFromArray(data.data(), static_cast<int>(data.size())) || packet.protocol_version() != Client::Wire::Version)
      return std::unexpected(Invalid("asset.packet"));
    switch (packet.payload_case())
    {
      case Proto::ServerAssetPacket::kPoseDemand: {
        const auto& v = packet.pose_demand();
        if (!v.context_revision()) return std::unexpected(Invalid("pose_demand.context"));
        return Response{
            PoseDemand{v.context_revision(), v.required()}
        };
      }
      case Proto::ServerAssetPacket::kSettled: {
        const auto& v = packet.settled();
        if (!v.generation() || !v.context_revision()) return std::unexpected(Invalid("settled"));
        return Response{
            Settled{Generation{v.generation()}, v.context_revision()}
        };
      }
      case Proto::ServerAssetPacket::kOffer: {
        const auto& v          = packet.offer();
        auto        descriptor = Get(v.asset(), limits);
        if (!descriptor || !v.player_id() || !v.view_revision()) return std::unexpected(Invalid("offer"));
        return Response{
            Offer{v.player_id(), v.view_revision(), *descriptor}
        };
      }
      case Proto::ServerAssetPacket::kTransfer: {
        const auto& v          = packet.transfer();
        auto        descriptor = Get(v.asset(), limits);
        if (!descriptor || !v.transfer_id() || !v.player_id() || !v.request_id()) return std::unexpected(Invalid("transfer"));
        return Response{
            Transfer{TransferId{v.transfer_id()}, *descriptor, v.player_id(), v.upload(), RequestId{v.request_id()}}
        };
      }
      case Proto::ServerAssetPacket::kChunk: {
        const auto& v = packet.chunk();
        if (!v.transfer_id() || v.data().empty() || v.data().size() > ChunkBytes) return std::unexpected(Invalid("chunk"));
        return Response{
            Chunk{TransferId{v.transfer_id()}, v.offset(), Bytes(v.data().begin(), v.data().end())}
        };
      }
      case Proto::ServerAssetPacket::kComplete: {
        const auto& v = packet.complete();
        if (
          !v.request_id() || (!v.transfer_id() && (v.accepted() || !v.player_id() || !v.generation())) || v.reason().size() > 256 ||
          v.retry_after_ms() > 60000)
          return std::unexpected(Invalid("complete"));
        return Response{
            Complete{
                     TransferId{v.transfer_id()},
                     v.accepted(),
                     v.reason(),
                     v.player_id(),
                     Generation{v.generation()},
                     v.retry_after_ms(),
                     v.upload(),
                     RequestId{v.request_id()}
            }
        };
      }
      case Proto::ServerAssetPacket::kRemove: {
        const auto& v = packet.remove();
        if (!v.player_id() || !v.view_revision()) return std::unexpected(Invalid("remove"));
        return Response{
            Remove{v.player_id(), v.view_revision()}
        };
      }
      case Proto::ServerAssetPacket::kProgress: {
        const auto& v = packet.progress();
        if (!v.transfer_id()) return std::unexpected(Invalid("progress"));
        return Response{
            Progress{TransferId{v.transfer_id()}, v.next_offset()}
        };
      }
      case Proto::ServerAssetPacket::kPolicy: {
        const auto& v = packet.policy();
        Policy      policy;
        policy.enabled                     = v.enabled();
        policy.limits.assetBytes           = std::min(limits.assetBytes, v.raw_asset_bytes());
        policy.limits.compressedAssetBytes = std::min(limits.compressedAssetBytes, v.compressed_asset_bytes());
        policy.limits.nodes                = std::min(limits.nodes, v.channels());
        policy.limits.poseBytes            = std::min(limits.poseBytes, v.pose_bytes());
        policy.limits.compressedPoseBytes  = std::min(limits.compressedPoseBytes, v.compressed_pose_bytes());
        policy.sampleRate                  = std::clamp(v.sample_rate(), 1u, 50u);
        policy.maximumVisible              = std::min(v.maximum_visible(), 16u);
        policy.windowChunks                = std::clamp(v.window_chunks(), 1u, 64u);
        policy.concurrentTransfers         = std::clamp(v.concurrent_transfers(), 1u, 8u);
        policy.modelBytesPerSecond         = std::min(v.model_bytes_per_second(), 64u * 1024 * 1024);
        policy.poseBytesPerSecond          = std::min(v.pose_bytes_per_second(), 4u * 1024 * 1024);
        if (
          !std::isfinite(v.distance()) || v.distance() < 0 || !policy.limits.assetBytes || !policy.limits.compressedAssetBytes ||
          !policy.limits.poseBytes || !policy.limits.compressedPoseBytes || !policy.limits.nodes)
          return std::unexpected(Invalid("policy"));
        policy.distance = std::min(v.distance(), 100000.0f);
        return Response{policy};
      }
      default:
        return std::unexpected(Invalid("asset.payload"));
    }
  }

  Result<RemotePose> DecodePose(std::span<const std::uint8_t> data, const Limits& limits)
  {
    if (data.size() > 2ULL * limits.compressedPoseBytes + 1024ULL) return std::unexpected(Invalid("pose.packet.size"));
    Proto::ServerPosePacket packet;
    if (
      !packet.ParseFromArray(data.data(), static_cast<int>(data.size())) || packet.protocol_version() != Client::Wire::Version ||
      !packet.player_id() || !packet.view_revision() || !packet.has_sample())
      return std::unexpected(Invalid("pose.packet"));
    const auto& v = packet.sample();
    if (
      !v.generation() || !v.context_revision() || !v.sequence() || v.sampled_at_us() > MaximumSampleTime || v.payload().empty() ||
      v.payload().size() > limits.compressedPoseBytes)
      return std::unexpected(Invalid("pose.sample"));
    RemotePose result{
        packet.player_id(),
        packet.view_revision(),
        Pose{
             Generation{v.generation()},
             v.context_revision(),
             Sequence{v.sequence()},
             v.sampled_at_us(),
             Bytes(v.payload().begin(), v.payload().end())
        }
    };
    if (packet.has_previous_sample())
    {
      const auto& p = packet.previous_sample();
      if (
        !p.generation() || p.generation() >= v.generation() || p.context_revision() != v.context_revision() ||
        p.sampled_at_us() != v.sampled_at_us() || !p.sequence() || p.payload().empty() || p.payload().size() > limits.compressedPoseBytes)
        return std::unexpected(Invalid("pose.previous"));
      result.sample.previous = std::make_shared<const Pose>(Pose{
          Generation{p.generation()},
          p.context_revision(),
          Sequence{p.sequence()},
          p.sampled_at_us(),
          Bytes(p.payload().begin(), p.payload().end())
      });
    }
    return result;
  }

}
