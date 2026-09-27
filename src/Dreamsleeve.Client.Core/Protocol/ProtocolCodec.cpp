module;
#include "protocol.pb.h"

module Dreamsleeve.Client.ProtocolCodec;
#include "CodecParts.h"

// Unknown wire values are handled; every newly generated named case must be listed.
#pragma warning(error : 4061 4062)

namespace Dreamsleeve::Client::Wire
{
  using namespace Detail;
  namespace
  {
    // No catch-all overload: adding a ClientRequest alternative must fail to compile.
    struct RequestWriter
    {
      P::ClientPacket& packet;

      void operator()(const OpenSession& value) const
      {
        packet.set_request_id(value.requestId);
        WriteSession(*packet.mutable_open_session(), value);
      }

      void operator()(const UpdatePlayer& value) const
      {
        packet.set_request_id(value.requestId);
        WritePlayerUpdate(*packet.mutable_update_player(), value.update);
      }

      void operator()(const SendChat& value) const
      {
        packet.set_request_id(value.requestId);
        WriteChat(*packet.mutable_send_chat(), value);
      }
    };

  }

  Result<ProtocolCodec> ProtocolCodec::TryCreate(Configuration config)
  {
    if (auto field = config.InvalidProtocolSetting()) return Failure(ErrorCode::InvalidConfig, std::string(*field));

    return ProtocolCodec{std::move(config)};
  }

  Result<DreamNetPacket> ProtocolCodec::Encode(const ClientRequest& request) const
  {
    P::ClientPacket packet;
    packet.set_protocol_version(Version);
    std::visit(RequestWriter{packet}, request);

    if (packet.request_id() == 0) return Failure(ErrorCode::InvalidEnvelope, "request_id");
    if (packet.has_open_session())
    {
      if (!ValidTicket(packet.open_session().session_ticket())) return Invalid("session_ticket");
    }

    if (packet.has_send_chat() && packet.send_chat().channel_id() == 0) return Invalid("channel_id");

    if (packet.has_update_player() && packet.update_player().has_set_actor_values() &&
        static_cast<std::size_t>(packet.update_player().set_actor_values().values_size()) > config.maxActorValues)
      return Invalid("actor_values");

    const auto size = packet.ByteSizeLong();
    if (size > config.network.maxPacketBytes) return Failure(ErrorCode::PacketTooLarge, "packet");

    auto result = DreamNetPacket::TryAllocateWith(size, [&](std::span<std::byte> buffer) {
      return packet.SerializeToArray(buffer.data(), static_cast<int>(buffer.size()));
    });
    if (!result) return Failure(ErrorCode::PacketCreationFailed, "packet");

    return std::move(*result);
  }

  Result<ServerResponse> ProtocolCodec::Decode(std::span<const std::byte> bytes) const
  {
    if (bytes.empty()) return Failure(ErrorCode::EmptyPacket, "packet");
    if (bytes.size() > config.network.maxPacketBytes) return Failure(ErrorCode::PacketTooLarge, "packet");

    P::ServerPacket packet;
    if (!packet.ParseFromArray(bytes.data(), static_cast<int>(bytes.size()))) return Failure(ErrorCode::MalformedPacket, "packet");
    if (packet.protocol_version() != Version) return Failure(ErrorCode::UnsupportedVersion, "protocol_version");
    if (packet.has_request_id() && packet.request_id() == 0) return Failure(ErrorCode::InvalidEnvelope, "request_id");

    switch (packet.payload_case())
    {
      case P::ServerPacket::kSessionOpened: {
        if (!packet.has_request_id()) return Failure(ErrorCode::InvalidEnvelope, "request_id");

        auto welcome = Welcome(config, packet.request_id(), packet.session_opened());
        if (!welcome) return std::unexpected{welcome.error()};

        return std::move(*welcome);
      }
      case P::ServerPacket::kChatPublished: {
        auto message = Message(packet.chat_published().message());
        if (!message) return std::unexpected{message.error()};

        const auto           channel = message->channelId;
        ChatMessagesReceived changes{channel, {std::move(*message)}};
        if (packet.has_request_id()) return ChatAccepted{packet.request_id(), std::move(changes)};

        return changes;
      }
      case P::ServerPacket::kRequestRejected: {
        if (!packet.has_request_id()) return Failure(ErrorCode::InvalidEnvelope, "request_id");

        const auto& rejection = packet.request_rejected();
        if (rejection.code() == P::REQUEST_REJECTION_CODE_UNSPECIFIED) return Invalid("code");

        return ServerRejection{
            packet.request_id(),
            static_cast<RequestRejectionCode>(rejection.code()),
            rejection.message(),
            rejection.field()
        };
      }
      case P::ServerPacket::kPlayerJoined: {
        if (packet.has_request_id()) return Failure(ErrorCode::InvalidEnvelope, "request_id");

        auto player = Player(config, packet.player_joined().player());
        if (!player) return std::unexpected{player.error()};

        return PlayerUpserted{std::move(*player)};
      }
      case P::ServerPacket::kPlayerUpdated: {
        if (packet.has_request_id()) return Failure(ErrorCode::InvalidEnvelope, "request_id");
        auto player = Player(config, packet.player_updated().player());
        if (!player) return std::unexpected{player.error()};
        return PlayerUpserted{std::move(*player)};
      }
      case P::ServerPacket::kPlayerMetadataChanged: {
        if (packet.has_request_id()) return Invalid("player_metadata_changed");
        auto result = ReadMetadata(config, packet.player_metadata_changed());
        if (!result) return std::unexpected{result.error()};
        return std::move(*result);
      }
      case P::ServerPacket::kPlayerMoved: {
        if (packet.has_request_id()) return Failure(ErrorCode::InvalidEnvelope, "request_id");
        auto result = ReadMovement(packet.player_moved());
        if (!result) return std::unexpected{result.error()};
        return std::move(*result);
      }
      case P::ServerPacket::kPlayerUpdateAccepted:
        if (!packet.has_request_id()) return Failure(ErrorCode::InvalidEnvelope, "request_id");
        return PlayerUpdateAccepted{packet.request_id()};
      case P::ServerPacket::kPlayerLeft:
        if (packet.has_request_id()) return Failure(ErrorCode::InvalidEnvelope, "request_id");
        if (packet.player_left().player_id() == 0) return Invalid("player_id");

        return PlayerRemoved{packet.player_left().player_id()};
      case P::ServerPacket::PAYLOAD_NOT_SET:
        return Invalid("payload");
      default:
        return Invalid("payload");
    }
  }

}
