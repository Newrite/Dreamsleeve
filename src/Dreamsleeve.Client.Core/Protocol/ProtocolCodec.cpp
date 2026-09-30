module;
#include "protocol.pb.h"
#include "network.pb.h"

module Dreamsleeve.Client.ProtocolCodec;
import Dreamsleeve.Client.Utils;
#include "CodecParts.h"

// Unknown wire values are handled; every newly generated named case must be listed.
#pragma warning(error : 4061 4062)

namespace Dreamsleeve::Client::Wire
{

  using namespace Detail;
  static_assert(static_cast<int>(Channel::Control) == ::Dreamsleeve::Protocol::Network::Control);
  static_assert(static_cast<int>(Channel::Chat) == ::Dreamsleeve::Protocol::Network::Chat);
  static_assert(static_cast<int>(Channel::Realtime) == ::Dreamsleeve::Protocol::Network::Realtime);

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

      void operator()(const PostAnnouncement& value) const
      {
        packet.set_request_id(value.requestId);
        WriteAnnouncement(*packet.mutable_post_announcement(), value);
      }

      void operator()(const PlaceGroundNote& value) const
      {
        packet.set_request_id(value.requestId);
        WriteNote(*packet.mutable_place_ground_note(), value);
      }

      void operator()(const ReportDeath& value) const
      {
        packet.set_request_id(value.requestId);
        WriteDeath(*packet.mutable_report_death(), value);
      }

      void operator()(const RemoveGroundMark& value) const
      {
        packet.set_request_id(value.requestId);
        packet.mutable_remove_ground_mark()->set_mark_id(value.markId);
      }

      void operator()(const SetIdentityVisibility& value) const
      {
        packet.set_request_id(value.requestId);
        packet.mutable_set_identity_visibility()->set_hidden(static_cast<P::HiddenIdentity>(value.hiding));
      }

      void operator()(const ChangeDisplayName& value) const
      {
        packet.set_request_id(value.requestId);
        packet.mutable_change_display_name()->set_display_name(value.displayName);
      }
    };

    // The one check of a request's shape before it leaves: IDs, the text the
    // protocol requires, finite numbers, the calendar. What depends on the
    // session (channel, pending slots, the announced policy) is the runtime's.
    // Returns the invalid field. No catch-all overload.
    struct RequestShape
    {
      const Configuration& config;

      using Field = std::optional<std::string_view>;

      // Non-empty well-formed UTF-8.
      static bool Text(std::string_view text)
      {
        return !text.empty() && Utils::Text::ValidUtf8(text);
      }

      // One line of well-formed UTF-8, possibly empty.
      static bool Line(std::string_view text)
      {
        return Utils::Text::ValidUtf8(text) && !Utils::Text::HasControl(text);
      }

      Field operator()(const OpenSession& value) const
      {
        if (!Auth::ValidToken(value.sessionTicket)) return "session_ticket";
        return std::nullopt;
      }

      Field operator()(const SendChat& value) const
      {
        if (value.channelId == Domain::InvalidId) return "channel_id";
        if (!Text(value.text)) return "text";
        return std::nullopt;
      }

      Field operator()(const UpdatePlayer& value) const
      {
        const auto* values = std::get_if<LocalActorValues>(&value.update);
        if (values && values->actorValues.size() > config.maxActorValues) return "actor_values";
        return std::nullopt;
      }

      // A third-party mod signs its announcements; the server and trusted clients need not.
      Field operator()(const PostAnnouncement& value) const
      {
        if (value.channelId == Domain::InvalidId) return "channel_id";
        if (!Text(value.text)) return "text";
        if (!Line(value.signature)) return "signature";
        if (value.source == Domain::ClientAnnouncementSource::ThirdParty && value.signature.empty()) return "signature";
        return std::nullopt;
      }

      Field operator()(const PlaceGroundNote& value) const
      {
        if (!Text(value.text)) return "text";
        if (!ValidPlacement(value.placement)) return "placement";
        if (!ValidGameDate(value.gameDate)) return "game_date";
        return std::nullopt;
      }

      // A death may carry no label at all.
      Field operator()(const ReportDeath& value) const
      {
        if (!Line(value.label)) return "label";
        if (!ValidPlacement(value.placement)) return "placement";
        if (!ValidGameDate(value.gameDate)) return "game_date";
        return std::nullopt;
      }

      Field operator()(const RemoveGroundMark& value) const
      {
        if (value.markId == Domain::InvalidId) return "mark_id";
        return std::nullopt;
      }

      Field operator()(const SetIdentityVisibility&) const
      {
        return std::nullopt;
      }

      Field operator()(const ChangeDisplayName& value) const
      {
        const bool blank = std::ranges::all_of(value.displayName, [](char c) { return c == ' ' || c == '\t'; });
        if (blank || !Line(value.displayName)) return "display_name";
        return std::nullopt;
      }
    };

  }

  Result<DreamNetPacket> ProtocolCodec::Encode(const ClientRequest& request) const
  {
    if (std::visit([](const auto& value) { return value.requestId; }, request) == Domain::InvalidId)
      return Failure(ErrorCode::InvalidEnvelope, "request_id");
    if (const auto field = std::visit(RequestShape{config}, request)) return Invalid(std::string{*field});

    P::ClientPacket packet;
    packet.set_protocol_version(Version);
    std::visit(RequestWriter{packet}, request);

    const auto size = packet.ByteSizeLong();
    if (size > config.network.maxPacketBytes) return Failure(ErrorCode::PacketTooLarge, "packet");

    auto result = DreamNetPacket::TryAllocateWith(size, [&](std::span<std::byte> buffer) {
      return packet.SerializeToArray(buffer.data(), static_cast<int>(buffer.size()));
    });
    if (!result) return Failure(ErrorCode::PacketCreationFailed, "packet");

    return std::move(*result);
  }

  Result<DreamNetPacket> ProtocolCodec::Encode(const MovementSample& sample, std::size_t maxPayloadBytes) const
  {
    if (sample.contextRevision == 0 || sample.sequence == 0) return Invalid("movement");
    if (!Finite(sample.pose.position) || !Finite(sample.pose.rotation)) return Invalid("pose");
    P::ClientMovementPacket packet;
    packet.set_protocol_version(Version);
    packet.mutable_sample()->set_context_revision(sample.contextRevision);
    packet.mutable_sample()->set_sequence(sample.sequence);
    WritePose(*packet.mutable_sample()->mutable_pose(), sample.pose);
    const auto size = packet.ByteSizeLong();
    if (size > std::min(maxPayloadBytes, config.network.maxPacketBytes)) return Failure(ErrorCode::PacketTooLarge, "movement");
    auto result = DreamNetPacket::TryAllocateWith(
      size,
      [&](std::span<std::byte> buffer) { return packet.SerializeToArray(buffer.data(), static_cast<int>(buffer.size())); },
      PacketFlag::None);
    if (!result) return Failure(ErrorCode::PacketCreationFailed, "movement");
    return std::move(*result);
  }

  Result<ServerResponse> ProtocolCodec::Decode(std::span<const std::byte> bytes, Channel channel) const
  {
    if (bytes.empty()) return Failure(ErrorCode::EmptyPacket, "packet");
    if (bytes.size() > config.network.maxPacketBytes) return Failure(ErrorCode::PacketTooLarge, "packet");

    if (channel == Channel::Realtime)
    {
      P::ServerMovementPacket packet;
      if (!packet.ParseFromArray(bytes.data(), static_cast<int>(bytes.size()))) return Failure(ErrorCode::MalformedPacket, "movement");
      if (packet.protocol_version() != Version) return Failure(ErrorCode::UnsupportedVersion, "protocol_version");
      if (packet.movements().players().empty()) return Invalid("movements");
      PlayersMoved batch;
      batch.players.reserve(packet.movements().players_size());
      for (const auto& value : packet.movements().players())
      {
        auto sample = ReadMovement(value);
        if (!sample) return std::unexpected{sample.error()};
        batch.players.push_back(std::move(*sample));
      }
      return batch;
    }
    if (channel != Channel::Control && channel != Channel::Chat) return Failure(ErrorCode::InvalidEnvelope, "channel");

    P::ServerPacket packet;
    if (!packet.ParseFromArray(bytes.data(), static_cast<int>(bytes.size()))) return Failure(ErrorCode::MalformedPacket, "packet");
    if (packet.protocol_version() != Version) return Failure(ErrorCode::UnsupportedVersion, "protocol_version");
    if (packet.has_request_id() && packet.request_id() == Domain::InvalidId) return Failure(ErrorCode::InvalidEnvelope, "request_id");

    const auto expected = packet.has_chat_published() ? Channel::Chat : Channel::Control;
    if (!packet.has_request_rejected() && channel != expected) return Failure(ErrorCode::InvalidEnvelope, "channel");

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

        const auto           chatChannel = message->channelId;
        ChatMessagesReceived changes{chatChannel, {std::move(*message)}};
        if (packet.has_request_id()) return ChatAccepted{packet.request_id(), std::move(changes)};

        return changes;
      }
      case P::ServerPacket::kRequestRejected: {
        if (!packet.has_request_id()) return Failure(ErrorCode::InvalidEnvelope, "request_id");

        const auto& rejection = packet.request_rejected();
        if (rejection.code() == P::REQUEST_REJECTION_CODE_UNSPECIFIED) return Invalid("code");

        return RequestRejected{
            packet.request_id(),
            {static_cast<RequestRejectionCode>(rejection.code()), rejection.message(), rejection.field()}
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
      case P::ServerPacket::kPlayerVisibilityChanged: {
        if (packet.has_request_id()) return Failure(ErrorCode::InvalidEnvelope, "request_id");
        auto result = ReadVisibility(packet.player_visibility_changed());
        if (!result) return std::unexpected{result.error()};
        return std::move(*result);
      }
      case P::ServerPacket::kPlayerUpdateAccepted:
        if (!packet.has_request_id()) return Failure(ErrorCode::InvalidEnvelope, "request_id");
        return PlayerUpdateAccepted{packet.request_id()};
      case P::ServerPacket::kPlayerLeft:
        if (packet.has_request_id()) return Failure(ErrorCode::InvalidEnvelope, "request_id");
        if (packet.player_left().player_id() == Domain::InvalidId) return Invalid("player_id");

        return PlayerRemoved{packet.player_left().player_id()};
      case P::ServerPacket::kGroundMarksChanged: {
        if (packet.has_request_id()) return Failure(ErrorCode::InvalidEnvelope, "request_id");
        auto result = ReadMarksChanged(packet.ground_marks_changed());
        if (!result) return std::unexpected{result.error()};
        return std::move(*result);
      }
      case P::ServerPacket::kGroundMarkPlaced: {
        if (!packet.has_request_id()) return Failure(ErrorCode::InvalidEnvelope, "request_id");
        auto mark = Mark(packet.ground_mark_placed().mark());
        if (!mark) return std::unexpected{mark.error()};
        const auto evicted = packet.ground_mark_placed().evicted_id();
        return GroundMarkPlaced{packet.request_id(), std::move(*mark), evicted == 0 ? std::nullopt : std::optional{evicted}};
      }
      case P::ServerPacket::kGroundMarkRemoved:
        if (!packet.has_request_id()) return Failure(ErrorCode::InvalidEnvelope, "request_id");
        if (packet.ground_mark_removed().mark_id() == Domain::InvalidId) return Invalid("mark_id");
        return GroundMarkRemoved{packet.request_id(), packet.ground_mark_removed().mark_id()};
      case P::ServerPacket::kOwnGroundMarks: {
        if (packet.has_request_id()) return Failure(ErrorCode::InvalidEnvelope, "request_id");
        auto result = ReadOwnMarks(packet.own_ground_marks());
        if (!result) return std::unexpected{result.error()};
        return std::move(*result);
      }
      case P::ServerPacket::kIdentityVisibilityChanged: {
        if (!packet.has_request_id()) return Failure(ErrorCode::InvalidEnvelope, "request_id");
        const auto& changed = packet.identity_visibility_changed();
        if (changed.has_pseudonym() && changed.pseudonym().empty()) return Invalid("pseudonym");
        // A pseudonym exactly when the names are hidden somewhere.
        if (!P::HiddenIdentity_IsValid(changed.hidden()) || changed.has_pseudonym() != (changed.hidden() != P::HIDDEN_IDENTITY_NONE))
          return Invalid("hidden");
        return IdentityVisibilityChanged{
            packet.request_id(),
            changed.has_pseudonym() ? std::optional{changed.pseudonym()} : std::nullopt,
            static_cast<Domain::HiddenIdentity>(changed.hidden())
        };
      }
      case P::ServerPacket::kDisplayNameChanged:
        if (!packet.has_request_id()) return Failure(ErrorCode::InvalidEnvelope, "request_id");
        if (packet.display_name_changed().display_name().empty()) return Invalid("display_name");
        return DisplayNameChanged{packet.request_id(), packet.display_name_changed().display_name()};
      case P::ServerPacket::PAYLOAD_NOT_SET:
        return Invalid("payload");
      default:
        return Invalid("payload");
    }
  }

}
