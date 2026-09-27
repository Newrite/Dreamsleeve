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

    void WriteLocation(P::PlayerLocation& target, const Domain::PlayerLocation& value)
    {
      auto* location = target.mutable_location();
      location->mutable_location_id()->set_plugin_name(value.location.locationId.pluginName);
      location->mutable_location_id()->set_local_form_id(value.location.locationId.localFormId);
      location->set_location_name(value.location.locationName);
      auto* position = target.mutable_position();
      position->set_x(value.position.X);
      position->set_y(value.position.Y);
      position->set_z(value.position.Z);
      auto* rotation = target.mutable_rotation();
      rotation->set_x(value.rotation.X);
      rotation->set_y(value.rotation.Y);
      rotation->set_z(value.rotation.Z);
    }

    struct ActorValueWriter
    {
      P::ActorValueEntry& target;
      void operator()(const Domain::ScalarActorValue& value) const { target.set_scalar(value.value); }
      void operator()(const Domain::ResourceActorValue& value) const
      {
        target.mutable_resource()->set_current(value.current);
        target.mutable_resource()->set_maximum(value.maximum);
      }
    };

    void WriteDetails(P::PlayerDetails& target, const Domain::PlayerDetails& value)
    {
      if (value.race)
      {
        auto* race = target.mutable_race();
        race->mutable_form()->set_plugin_name(value.race->form.pluginName);
        race->mutable_form()->set_local_form_id(value.race->form.localFormId);
        race->set_name(value.race->name);
      }
      if (value.level) target.set_level(*value.level);
      auto* activity = target.mutable_activity();
      activity->set_kind(static_cast<P::ActivityKind>(value.activity.kind));
      activity->set_lock_difficulty(static_cast<P::LockDifficulty>(value.activity.lockDifficulty));
      if (value.activity.targetName) activity->set_target_name(*value.activity.targetName);
      if (value.activity.menuKey) activity->set_menu_key(*value.activity.menuKey);
      if (value.place)
      {
        auto* place = target.mutable_place();
        place->set_worldspace_name(value.place->worldspaceName);
        place->set_location_name(value.place->locationName);
        place->set_nearby_marker_name(value.place->nearbyMarkerName);
        place->set_marker_kind(value.place->markerKind);
        place->set_is_interior(value.place->isInterior);
      }
      if (value.gameStartedAtUnixMs) target.set_game_started_at_unix_ms(*value.gameStartedAtUnixMs);
    }

    struct PlayerUpdateWriter
    {
      P::UpdatePlayer& target;
      void operator()(const CharacterStarted& value) const { target.mutable_begin_character()->set_name(value.name); }
      void operator()(const CharacterRenamed& value) const { target.mutable_rename_character()->set_name(value.name); }
      void operator()(const PlayerDetailsChanged& value) const { WriteDetails(*target.mutable_set_details(), value.details); }
      void operator()(const GameExited&) const { target.mutable_leave_game(); }
      void operator()(const LocalMovement& value) const
      {
        auto* sample = target.mutable_sample_movement();
        if (value.location) WriteLocation(*sample->mutable_location(), *value.location);
      }
      void operator()(const LocalActorValues& value) const
      {
        auto* sample = target.mutable_set_actor_values();
        for (const auto& [key, info] : value.actorValues)
        {
          auto* entry = sample->add_values();
          entry->set_key(key);
          entry->set_display_name(info.displayName);
          std::visit(ActorValueWriter{*entry}, info.state);
        }
      }
    };

    // No catch-all overload: adding a ClientRequest alternative must fail to compile.
    struct RequestWriter
    {
      P::ClientPacket& packet;

      void operator()(const OpenSession& value) const
      {
        packet.set_request_id(value.requestId);
        packet.mutable_open_session()->set_session_ticket(value.sessionTicket);
      }

      void operator()(const UpdatePlayer& value) const
      {
        packet.set_request_id(value.requestId);
        std::visit(PlayerUpdateWriter{*packet.mutable_update_player()}, value.update);
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

    Result<Domain::PlayerLocation> ReadLocation(const P::PlayerLocation& source)
    {
      const auto& key = source.location().location_id();
      const auto& position = source.position();
      const auto& rotation = source.rotation();
      if (key.plugin_name().empty() || key.local_form_id() == 0) return Invalid("location_id");
      if (!std::isfinite(position.x()) || !std::isfinite(position.y()) || !std::isfinite(position.z()) ||
          !std::isfinite(rotation.x()) || !std::isfinite(rotation.y()) || !std::isfinite(rotation.z()))
        return Invalid("location");

      return Domain::PlayerLocation{
        {{key.plugin_name(), key.local_form_id()}, source.location().location_name()},
        {position.x(), position.y(), position.z()},
        {rotation.x(), rotation.y(), rotation.z()}
      };
    }

    Result<Domain::ActorValueInfo> ReadActorValue(const P::ActorValueEntry& entry)
    {
      if (entry.key().empty()) return Invalid("actor_value_key");
      switch (entry.value_case())
      {
        case P::ActorValueEntry::kScalar:
          if (!std::isfinite(entry.scalar())) return Invalid("actor_value");
          return Domain::ActorValueInfo{entry.display_name(), Domain::ScalarActorValue{entry.scalar()}};
        case P::ActorValueEntry::kResource:
          if (!std::isfinite(entry.resource().current()) || !std::isfinite(entry.resource().maximum())) return Invalid("actor_value");
          return Domain::ActorValueInfo{entry.display_name(), Domain::ResourceActorValue{entry.resource().current(), entry.resource().maximum()}};
        case P::ActorValueEntry::VALUE_NOT_SET:
          return Invalid("actor_value");
        default:
          return Invalid("actor_value");
      }
    }

    Domain::PlayerDetails ReadDetails(const P::PlayerDetails& source)
    {
      Domain::PlayerDetails result;
      if (source.has_race())
        result.race = Domain::NamedForm{{source.race().form().plugin_name(), source.race().form().local_form_id()}, source.race().name()};
      if (source.has_level()) result.level = source.level();
      const auto& activity = source.activity();
      result.activity.kind = static_cast<Domain::ActivityKind>(activity.kind());
      result.activity.lockDifficulty = static_cast<Domain::LockDifficulty>(activity.lock_difficulty());
      if (activity.has_target_name()) result.activity.targetName = activity.target_name();
      if (activity.has_menu_key()) result.activity.menuKey = activity.menu_key();
      if (source.has_place())
      {
        const auto& place = source.place();
        result.place = Domain::PlaceDescription{place.worldspace_name(), place.location_name(), place.nearby_marker_name(), place.marker_kind(), place.is_interior()};
      }
      if (source.has_game_started_at_unix_ms()) result.gameStartedAtUnixMs = source.game_started_at_unix_ms();
      return result;
    }

    Result<Domain::Player> Player(const Configuration& config, const P::PlayerInfo& source)
    {
      auto profile = Profile(source.profile());
      if (!profile) return std::unexpected{profile.error()};
      if (static_cast<std::size_t>(source.actor_values_size()) > config.maxActorValues) return Invalid("actor_values");

      Domain::Player result{.data = std::move(*profile), .characterGeneration = source.character_generation()};
      result.details = ReadDetails(source.details());
      if (source.has_character_name()) result.characterName = source.character_name();
      if (source.has_location())
      {
        auto location = ReadLocation(source.location());
        if (!location) return std::unexpected{location.error()};
        result.location = std::move(*location);
      }
      for (const auto& entry : source.actor_values())
      {
        auto value = ReadActorValue(entry);
        if (!value) return std::unexpected{value.error()};
        if (!result.actorValues.emplace(entry.key(), std::move(*value)).second) return Invalid("actor_value_key");
      }
      return result;
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
        auto decoded = Player(config, player);
        if (!decoded) return std::unexpected{decoded.error()};

        result.players.push_back(std::move(*decoded));
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
    if (packet.has_open_session())
    {
      const auto& ticket = packet.open_session().session_ticket();
      if (ticket.size() != 43 || !std::ranges::all_of(ticket, [](unsigned char c) {
            return (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '_';
          }))
        return Invalid("session_ticket");
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
        const auto& source = packet.player_metadata_changed();
        if (packet.has_request_id() || source.player_id() == 0 || (!source.has_actor_values() && !source.has_details()))
          return Invalid("player_metadata_changed");
        PlayerMetadataUpdated result{source.player_id()};
        if (source.has_actor_values())
        {
          if (static_cast<std::size_t>(source.actor_values().values_size()) > config.maxActorValues) return Invalid("actor_values");
          result.actorValues.emplace();
          for (const auto& entry : source.actor_values().values())
          {
            auto value = ReadActorValue(entry);
            if (!value) return std::unexpected{value.error()};
            if (!result.actorValues->emplace(entry.key(), std::move(*value)).second) return Invalid("actor_value_key");
          }
        }
        if (source.has_details()) result.details = ReadDetails(source.details());
        return result;
      }
      case P::ServerPacket::kPlayerMoved: {
        if (packet.has_request_id()) return Failure(ErrorCode::InvalidEnvelope, "request_id");
        if (packet.player_moved().player_id() == 0) return Invalid("player_id");
        std::optional<Domain::PlayerLocation> location;
        if (packet.player_moved().has_location())
        {
          auto decoded = ReadLocation(packet.player_moved().location());
          if (!decoded) return std::unexpected{decoded.error()};
          location = std::move(*decoded);
        }
        return PlayerLocationUpdated{packet.player_moved().player_id(), std::move(location)};
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
