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

  void WritePlayerUpdate(P::UpdatePlayer& target, const PlayerUpdate& value)
  {
    std::visit(PlayerUpdateWriter{target}, value);
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
      {rotation.x(), rotation.y(), rotation.z()}, source.sampled_at_us()
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

  Result<PlayerMetadataUpdated> ReadMetadata(const Configuration& config, const P::PlayerMetadataChanged& source)
  {
    if (source.player_id() == 0 || (!source.has_actor_values() && !source.has_details())) return Invalid("player_metadata_changed");
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

  Result<PlayerLocationUpdated> ReadMovement(const P::PlayerMoved& source)
  {
    if (source.player_id() == 0) return Invalid("player_id");
    std::optional<Domain::PlayerLocation> location;
    if (source.has_location())
    {
      auto decoded = ReadLocation(source.location());
      if (!decoded) return std::unexpected{decoded.error()};
      location = std::move(*decoded);
    }
    return PlayerLocationUpdated{source.player_id(), std::move(location)};
  }
}
