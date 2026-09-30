module;
#include "protocol.pb.h"

module Dreamsleeve.Client.ProtocolCodec;
#include "CodecParts.h"

// Unknown wire values are handled; every newly generated named case must be listed.
#pragma warning(error : 4061 4062)

namespace Dreamsleeve::Client::Wire::Detail
{

  void WriteLocation(P::PlayerLocation& target, const Domain::PlayerLocation& value)
  {
    target.set_sampled_at_us(value.sampledAtUs);
    WriteKey(*target.mutable_location()->mutable_location_id(), value.location.locationId);
    target.mutable_location()->set_location_name(value.location.locationName);
    WritePosition(*target.mutable_position(), value.position);
    WriteRotation(*target.mutable_rotation(), value.rotation);
  }

  struct ActorValueWriter
  {
    P::ActorValueEntry& target;

    void operator()(const Domain::ScalarActorValue& value) const
    {
      target.set_scalar(value.value);
    }

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
      WriteKey(*race->mutable_form(), value.race->form);
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

    void operator()(const CharacterStarted& value) const
    {
      target.mutable_begin_character()->set_name(value.name);
    }

    void operator()(const CharacterRenamed& value) const
    {
      target.mutable_rename_character()->set_name(value.name);
    }

    void operator()(const PlayerDetailsChanged& value) const
    {
      WriteDetails(*target.mutable_set_details(), value.details);
    }

    void operator()(const GameExited&) const
    {
      target.mutable_leave_game();
    }

    void operator()(const SetLocation& value) const
    {
      auto* transition = target.mutable_set_location();
      transition->set_context_revision(value.contextRevision);
      if (value.location) WriteLocation(*transition->mutable_location(), *value.location);
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

  void WritePlayerUpdate(P::UpdatePlayer& target, const PlayerUpdate& value)
  {
    std::visit(PlayerUpdateWriter{target}, value);
  }

  // Protobuf getters return default instances for absent nested messages.
  // Required nonzero IDs reject those as well, without separate has_* checks.
  Result<Domain::PlayerData> Profile(const P::PlayerProfile& player)
  {
    if (player.player_id() == Domain::InvalidId) return Invalid("player_id");
    if (player.pseudonymous() && (!player.username().empty() || player.display_name().empty())) return Invalid("pseudonymous");

    // Strings have already been validated/canonicalized by the server.
    return Domain::PlayerData{player.player_id(), player.username(), player.display_name(), player.pseudonymous()};
  }

  Result<Domain::PlayerLocation> ReadLocation(const P::PlayerLocation& source)
  {
    Domain::PlayerLocation result{
        {KeyOf(source.location().location_id()), source.location().location_name()},
        PositionOf(source.position()),
        RotationOf(source.rotation()),
        source.sampled_at_us()
    };
    if (!ValidKey(result.location.locationId)) return Invalid("location_id");
    if (!Finite(result.position) || !Finite(result.rotation)) return Invalid("location");
    return result;
  }

  Result<Domain::ActorValueInfo> ReadActorValue(const P::ActorValueEntry& entry)
  {
    if (entry.key().empty()) return Invalid("actor_value_key");
    switch (entry.value_case())
    {
      case P::ActorValueEntry::kScalar:
        if (!Finite(entry.scalar())) return Invalid("actor_value");
        return Domain::ActorValueInfo{entry.display_name(), Domain::ScalarActorValue{entry.scalar()}};
      case P::ActorValueEntry::kResource:
        if (!Finite(entry.resource().current(), entry.resource().maximum())) return Invalid("actor_value");
        return Domain::ActorValueInfo{
            entry.display_name(),
            Domain::ResourceActorValue{entry.resource().current(), entry.resource().maximum()}
        };
      case P::ActorValueEntry::VALUE_NOT_SET:
        return Invalid("actor_value");
      default:
        return Invalid("actor_value");
    }
  }

  Domain::PlayerDetails ReadDetails(const P::PlayerDetails& source)
  {
    Domain::PlayerDetails result;
    if (source.has_race()) result.race = Domain::NamedForm{KeyOf(source.race().form()), source.race().name()};
    if (source.has_level()) result.level = source.level();
    const auto& activity           = source.activity();
    result.activity.kind           = static_cast<Domain::ActivityKind>(activity.kind());
    result.activity.lockDifficulty = static_cast<Domain::LockDifficulty>(activity.lock_difficulty());
    if (activity.has_target_name()) result.activity.targetName = activity.target_name();
    if (activity.has_menu_key()) result.activity.menuKey = activity.menu_key();
    if (source.has_place())
    {
      const auto& place = source.place();
      result.place      = Domain::PlaceDescription{
          place.worldspace_name(),
          place.location_name(),
          place.nearby_marker_name(),
          place.marker_kind(),
          place.is_interior()
      };
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
    result.viewRevision     = source.view_revision();
    result.movementSequence = source.movement_sequence();
    result.details          = ReadDetails(source.details());
    if (source.has_character_name()) result.characterName = source.character_name();
    result.characterNameWithheld = source.character_name_withheld() && !result.characterName;
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

  Result<PlayerMetadataUpdated> ReadMetadata(const Configuration& config, const P::PlayerMetadataChanged& source)
  {
    if (source.player_id() == Domain::InvalidId || (!source.has_actor_values() && !source.has_details()))
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

  void WritePose(P::MovementPose& target, const Domain::MovementPose& value)
  {
    WritePosition(*target.mutable_position(), value.position);
    WriteRotation(*target.mutable_rotation(), value.rotation);
    target.set_sampled_at_us(value.sampledAtUs);
  }

  Result<PlayerMovementReceived> ReadMovement(const P::PlayerMoved& source)
  {
    if (source.player_id() == Domain::InvalidId || source.view_revision() == 0 || !source.has_pose()) return Invalid("movement");
    const auto&                pose = source.pose();
    const Domain::MovementPose value{PositionOf(pose.position()), RotationOf(pose.rotation()), pose.sampled_at_us()};
    if (!Finite(value.position) || !Finite(value.rotation)) return Invalid("pose");
    return PlayerMovementReceived{source.player_id(), source.view_revision(), source.sequence(), value};
  }

  Result<PlayerLocationUpdated> ReadVisibility(const P::PlayerVisibilityChanged& source)
  {
    if (source.player_id() == Domain::InvalidId || source.view_revision() == 0) return Invalid("visibility");
    std::optional<Domain::PlayerLocation> location;
    if (source.has_location())
    {
      auto decoded = ReadLocation(source.location());
      if (!decoded) return std::unexpected{decoded.error()};
      location = std::move(*decoded);
    }
    return PlayerLocationUpdated{source.player_id(), std::move(location), source.view_revision(), source.sequence()};
  }

}
