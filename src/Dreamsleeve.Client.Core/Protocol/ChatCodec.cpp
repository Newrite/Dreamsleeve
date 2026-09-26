module;
#include "chat.pb.h"

module Dreamsleeve.Client.Codec;

// Apply to our switches, after generated headers: default handles unknown wire
// values, but a newly generated named enum case must break the build.
#pragma warning(error : 4061 4062)

namespace Dreamsleeve::Client::Wire
{
  namespace P = Dreamsleeve::Protocol::Chat;

  namespace
  {

    // No catch-all overload: adding a ClientRequest alternative must fail to compile.
    struct RequestWriter
    {
      P::ClientPacket& packet;

      void operator()(const OpenSession& value) const
      {
        packet.set_request_id(value.requestId);
        packet.mutable_open_session()->set_username(value.username);
        packet.mutable_open_session()->set_display_name(value.displayName);
      }

      void operator()(const SendChat& value) const
      {
        packet.set_request_id(value.requestId);
        packet.mutable_send_chat()->set_channel_id(value.channelId);
        packet.mutable_send_chat()->set_text(value.text);
      }
    };

    auto Failure(ErrorCode code, std::string field)
    {
      return std::unexpected{
          Error{code, std::move(field)}
      };
    }

    auto Invalid(std::string field)
    {
      return Failure(ErrorCode::InvalidPayload, std::move(field));
    }

    // Protobuf getters return default instances for absent nested messages.
    // Required nonzero IDs reject those as well, without separate has_* checks.
    Result<Domain::PlayerData> Profile(const P::PlayerProfile& player)
    {
      if (player.player_id() == 0) return Invalid("player_id");

      // Strings have already been validated/canonicalized by the server.
      return Domain::PlayerData{player.player_id(), player.username(), player.display_name()};
    }

    Result<Domain::ChatMessage> Message(const P::ChatMessage& message)
    {
      if (message.message_id() == 0 || message.channel_id() == 0) return Invalid("message");
      if (message.sent_at_unix_ms() < -62135596800000LL || message.sent_at_unix_ms() > 253402300799999LL) return Invalid("sent_at_unix_ms");

      auto author = Profile(message.author());
      if (!author) return std::unexpected{author.error()};

      return Domain::ChatMessage{
          message.message_id(),
          message.channel_id(),
          std::move(*author),
          message.text(),
          Domain::FromUnixMilliseconds(message.sent_at_unix_ms())
      };
    }

    Result<SessionOpened> Welcome(const Configuration& config, std::uint64_t requestId, const P::SessionOpened& source)
    {
      if (source.self_player_id() == 0 || source.global_channel_id() == 0) return Invalid("session_opened");
      if (
        static_cast<std::size_t>(source.players_size()) > config.maxInitialPlayers ||
        static_cast<std::size_t>(source.recent_messages_size()) > config.maxRecentMessages)
        return Invalid("initial_count");

      SessionOpened result{requestId, source.self_player_id(), source.global_channel_id()};
      for (const auto& player : source.players())
      {
        auto profile = Profile(player);
        if (!profile) return std::unexpected{profile.error()};

        result.players.push_back(Domain::Player{.data = std::move(*profile)});
      }

      for (const auto& value : source.recent_messages())
      {
        auto message = Message(value);
        if (!message) return std::unexpected{message.error()};

        result.recentMessages.push_back(std::move(*message));
      }

      return result;
    }

  }

  Result<Codec> Codec::TryCreate(Configuration config)
  {
    if (auto field = config.InvalidProtocolSetting()) return Failure(ErrorCode::InvalidConfig, std::string(*field));

    return Codec{std::move(config)};
  }

  Result<DreamNetPacket> Codec::Encode(const ClientRequest& request) const
  {
    P::ClientPacket packet;
    packet.set_protocol_version(Version);
    std::visit(RequestWriter{packet}, request);

    if (packet.request_id() == 0) return Failure(ErrorCode::InvalidEnvelope, "request_id");
    if (packet.has_send_chat() && packet.send_chat().channel_id() == 0) return Invalid("channel_id");

    const auto size = packet.ByteSizeLong();
    if (size > config.network.maxPacketBytes) return Failure(ErrorCode::PacketTooLarge, "packet");

    auto result = DreamNetPacket::TryAllocateWith(size, [&](std::span<std::byte> buffer) {
      return packet.SerializeToArray(buffer.data(), static_cast<int>(buffer.size()));
    });
    if (!result) return Failure(ErrorCode::PacketCreationFailed, "packet");

    return std::move(*result);
  }

  Result<ServerResponse> Codec::Decode(std::span<const std::byte> bytes) const
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

        auto profile = Profile(packet.player_joined().player());
        if (!profile) return std::unexpected{profile.error()};

        return PlayerUpserted{Domain::Player{.data = std::move(*profile)}};
      }
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
