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

      void operator()(const JoinAsGuest& value) const
      {
        packet.set_request_id(value.requestId);
        packet.mutable_join_as_guest();
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

      void operator()(const SanctionPlayer& value) const
      {
        packet.set_request_id(value.requestId);
        auto& sanction = *packet.mutable_sanction_player();
        sanction.set_player_id(value.playerId);
        sanction.set_kind(static_cast<P::SanctionKind>(value.kind));
        if (value.minutes) sanction.set_minutes(*value.minutes);
        sanction.set_reason(value.reason);
      }

      void operator()(const LiftSanction& value) const
      {
        packet.set_request_id(value.requestId);
        auto& lift = *packet.mutable_lift_sanction();
        lift.set_player_id(value.playerId);
        lift.set_kind(static_cast<P::SanctionKind>(value.kind));
      }

      void operator()(const KickPlayer& value) const
      {
        packet.set_request_id(value.requestId);
        auto& kick = *packet.mutable_kick_player();
        kick.set_player_id(value.playerId);
        kick.set_reason(value.reason);
      }

      void operator()(const ListSanctions& value) const
      {
        packet.set_request_id(value.requestId);
        packet.mutable_list_sanctions();
      }

      void operator()(const ListPlayerMarks& value) const
      {
        packet.set_request_id(value.requestId);
        packet.mutable_list_player_marks()->set_player_id(value.playerId);
      }

      void operator()(const ClearPlayerMarks& value) const
      {
        packet.set_request_id(value.requestId);
        auto& clear = *packet.mutable_clear_player_marks();
        clear.set_player_id(value.playerId);
        clear.set_notes(value.notes);
        clear.set_deaths(value.deaths);
      }

      void operator()(const DeleteChatMessage& value) const
      {
        packet.set_request_id(value.requestId);
        auto& removal = *packet.mutable_delete_chat_message();
        removal.set_channel_id(value.channelId);
        removal.set_message_id(value.messageId);
      }
    };

    // The first string field, nested ones included, that is not well-formed
    // UTF-8. The server's parser refuses such a packet whole and closes the
    // connection, so it never leaves; a new string field is covered as it is.
    std::optional<std::string> InvalidText(const google::protobuf::Message& message)
    {
      using Field                          = google::protobuf::FieldDescriptor;
      const auto* const         reflection = message.GetReflection();
      std::vector<const Field*> fields;
      reflection->ListFields(message, &fields);
      for (const auto* field : fields)
      {
        const bool repeated = field->is_repeated();
        const int  count    = repeated ? reflection->FieldSize(message, field) : 1;
        for (int index = 0; index < count; ++index)
          if (field->type() == Field::TYPE_STRING)
          {
            const auto text = repeated ? reflection->GetRepeatedString(message, field, index) : reflection->GetString(message, field);
            if (!Utils::Text::ValidUtf8(text)) return std::string{field->name()};
          }
          else if (field->cpp_type() == Field::CPPTYPE_MESSAGE)
          {
            const auto& nested = repeated ? reflection->GetRepeatedMessage(message, field, index) : reflection->GetMessage(message, field);
            if (auto invalid = InvalidText(nested)) return invalid;
          }
      }
      return std::nullopt;
    }

  }

  Result<DreamNetPacket> ProtocolCodec::Encode(const ClientRequest& request) const
  {
    // Only what would make the server close the connection is checked here:
    // the correlation ID, text that is not UTF-8 and the size. The content
    // (empty or blank text, IDs, places, dates, lengths) is the server's to
    // judge; it answers with a refusal of this request.
    if (std::visit([](const auto& value) { return value.requestId; }, request) == Domain::InvalidId)
      return Failure(ErrorCode::InvalidEnvelope, "request_id");

    P::ClientPacket packet;
    packet.set_protocol_version(Version);
    std::visit(RequestWriter{packet}, request);
    if (auto field = InvalidText(packet)) return Invalid(std::move(*field));

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
    // The server skips a sample it cannot use; nothing here can close the connection.
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

  Result<ServerResponse> ProtocolCodec::Decode(std::span<const std::byte> bytes, Channel channel, const ActorValueKinds& kinds) const
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

    const auto expected = packet.has_chat_published() || packet.has_chat_message_removed() ? Channel::Chat : Channel::Control;
    if (!packet.has_request_rejected() && channel != expected) return Failure(ErrorCode::InvalidEnvelope, "channel");

    switch (packet.payload_case())
    {
      case P::ServerPacket::kSessionOpened: {
        if (!packet.has_request_id()) return Failure(ErrorCode::InvalidEnvelope, "request_id");

        auto welcome = Welcome(config, packet.request_id(), packet.session_opened());
        if (!welcome) return std::unexpected{welcome.error()};

        return std::move(*welcome);
      }
      case P::ServerPacket::kMuteChanged: {
        if (packet.has_request_id()) return Failure(ErrorCode::InvalidEnvelope, "request_id");

        const auto& changed = packet.mute_changed();
        return MuteChanged{changed.has_mute() ? std::optional{Mute(changed.mute())} : std::nullopt};
      }
      case P::ServerPacket::kSessionEnded: {
        if (packet.has_request_id()) return Failure(ErrorCode::InvalidEnvelope, "request_id");

        auto ended = Ended(packet.session_ended());
        if (!ended) return std::unexpected{ended.error()};

        return std::move(*ended);
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
      case P::ServerPacket::kPresenceChanged: {
        if (packet.has_request_id()) return Failure(ErrorCode::InvalidEnvelope, "request_id");
        auto result = ReadPresence(config, packet.presence_changed(), kinds);
        if (!result) return std::unexpected{result.error()};
        return std::move(*result);
      }
      case P::ServerPacket::kPlayerUpdateAccepted:
        if (!packet.has_request_id()) return Failure(ErrorCode::InvalidEnvelope, "request_id");
        return PlayerUpdateAccepted{packet.request_id()};
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
      case P::ServerPacket::kRoleChanged: {
        if (packet.has_request_id()) return Failure(ErrorCode::InvalidEnvelope, "request_id");
        auto role = Role(packet.role_changed().role());
        if (!role) return std::unexpected{role.error()};
        return RoleChanged{*role};
      }
      case P::ServerPacket::kSanctionIssued: {
        if (!packet.has_request_id()) return Failure(ErrorCode::InvalidEnvelope, "request_id");
        auto sanction = ReadSanction(packet.sanction_issued().sanction());
        if (!sanction) return std::unexpected{sanction.error()};
        return SanctionIssued{packet.request_id(), std::move(*sanction)};
      }
      case P::ServerPacket::kSanctionLifted: {
        if (!packet.has_request_id()) return Failure(ErrorCode::InvalidEnvelope, "request_id");
        const auto& lifted = packet.sanction_lifted();
        if (lifted.player_id() == Domain::InvalidId) return Invalid("player_id");
        auto kind = Kind(lifted.kind());
        if (!kind) return std::unexpected{kind.error()};
        return SanctionLifted{packet.request_id(), lifted.player_id(), *kind};
      }
      case P::ServerPacket::kPlayerKicked:
        if (!packet.has_request_id()) return Failure(ErrorCode::InvalidEnvelope, "request_id");
        if (packet.player_kicked().player_id() == Domain::InvalidId) return Invalid("player_id");
        return PlayerKicked{packet.request_id(), packet.player_kicked().player_id()};
      case P::ServerPacket::kSanctionList: {
        if (!packet.has_request_id()) return Failure(ErrorCode::InvalidEnvelope, "request_id");
        SanctionList result{packet.request_id()};
        for (const auto& value : packet.sanction_list().sanctions())
        {
          auto sanction = ReadSanction(value);
          if (!sanction) return std::unexpected{sanction.error()};
          result.sanctions.push_back(std::move(*sanction));
        }
        return result;
      }
      case P::ServerPacket::kPlayerMarks: {
        if (!packet.has_request_id()) return Failure(ErrorCode::InvalidEnvelope, "request_id");
        const auto& listed = packet.player_marks();
        if (listed.player_id() == Domain::InvalidId) return Invalid("player_id");
        PlayerMarks result{packet.request_id(), listed.player_id()};
        for (const auto& value : listed.marks())
        {
          auto mark = Mark(value);
          if (!mark) return std::unexpected{mark.error()};
          result.marks.push_back(std::move(*mark));
        }
        return result;
      }
      case P::ServerPacket::kPlayerMarksCleared: {
        if (!packet.has_request_id()) return Failure(ErrorCode::InvalidEnvelope, "request_id");
        const auto& cleared = packet.player_marks_cleared();
        if (cleared.player_id() == Domain::InvalidId) return Invalid("player_id");
        return PlayerMarksCleared{packet.request_id(), cleared.player_id(), cleared.removed()};
      }
      case P::ServerPacket::kChatMessageRemoved: {
        const auto& removed = packet.chat_message_removed();
        if (removed.channel_id() == Domain::InvalidId) return Invalid("channel_id");
        if (removed.message_id() == Domain::InvalidId) return Invalid("message_id");
        const auto requestId = packet.has_request_id() ? std::optional{packet.request_id()} : std::nullopt;
        return ChatMessageRemoved{requestId, removed.channel_id(), removed.message_id()};
      }
      case P::ServerPacket::PAYLOAD_NOT_SET:
        return Invalid("payload");
      default:
        return Invalid("payload");
    }
  }

}
