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
    if (player.pseudonymous() && (!player.username().empty() || player.display_name().empty() || player.has_name_color()))
      return Invalid("pseudonymous");
    if (player.has_name_color() && player.name_color() > MaxNameColor) return Invalid("name_color");

    // Strings have already been validated/canonicalized by the server.
    return Domain::PlayerData{
        player.player_id(),
        player.username(),
        player.display_name(),
        player.pseudonymous(),
        player.has_name_color() ? std::optional{player.name_color()} : std::nullopt
    };
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

  // A published reading resolves its number through the kinds of the session.
  Result<std::pair<Domain::ActorValueKey, Domain::ActorValueInfo>> ReadActorValue(const P::ActorValue& entry, const ActorValueKinds& kinds)
  {
    const auto* kind = kinds.Find(entry.kind());
    if (!kind) return Invalid("actor_value_kind");
    switch (entry.value_case())
    {
      case P::ActorValue::kScalar:
        if (!Finite(entry.scalar())) return Invalid("actor_value");
        return std::pair{
            kind->key,
            Domain::ActorValueInfo{kind->displayName, Domain::ScalarActorValue{entry.scalar()}}
        };
      case P::ActorValue::kResource:
        return std::pair{
            kind->key,
            Domain::ActorValueInfo{kind->displayName, Domain::ResourceActorValue{entry.resource().current(), entry.resource().maximum()}}
        };
      case P::ActorValue::VALUE_NOT_SET:
        return Invalid("actor_value");
      default:
        return Invalid("actor_value");
    }
  }

  Domain::PlayerActivity ReadActivity(const P::PlayerActivity& source)
  {
    Domain::PlayerActivity result;
    result.kind           = static_cast<Domain::ActivityKind>(source.kind());
    result.lockDifficulty = static_cast<Domain::LockDifficulty>(source.lock_difficulty());
    if (source.has_target_name()) result.targetName = source.target_name();
    if (source.has_menu_key()) result.menuKey = source.menu_key();
    return result;
  }

  Domain::PlaceDescription ReadPlace(const P::PlaceDescription& place)
  {
    return {place.worldspace_name(), place.location_name(), place.nearby_marker_name(), place.marker_kind(), place.is_interior()};
  }

  Domain::NamedForm ReadRace(const P::NamedForm& race)
  {
    return {KeyOf(race.form()), race.name()};
  }

  Domain::PlayerDetails ReadDetails(const P::PlayerDetails& source)
  {
    Domain::PlayerDetails result;
    if (source.has_race()) result.race = ReadRace(source.race());
    if (source.has_level()) result.level = source.level();
    result.activity = ReadActivity(source.activity());
    if (source.has_place()) result.place = ReadPlace(source.place());
    if (source.has_game_started_at_unix_ms()) result.gameStartedAtUnixMs = source.game_started_at_unix_ms();
    return result;
  }

  // Present components replace, listed ones clear; one component never does both.
  Result<Domain::PlayerDetailsPatch> ReadDetailsPatch(const P::PlayerMetadataPatch& source)
  {
    Domain::PlayerDetailsPatch result;
    if (source.has_details())
    {
      const auto& set = source.details();
      if (set.has_race()) result.race = std::optional{ReadRace(set.race())};
      if (set.has_level()) result.level = std::optional{set.level()};
      if (set.has_activity()) result.activity = ReadActivity(set.activity());
      if (set.has_place()) result.place = std::optional{ReadPlace(set.place())};
      if (set.has_game_started_at_unix_ms()) result.gameStartedAtUnixMs = std::optional{set.game_started_at_unix_ms()};
    }
    const auto clear = [](auto& component) {
      if (component) return false;
      component.emplace();
      return true;
    };
    for (const int field : source.cleared_details())
    {
      bool cleared = false;
      if (field == P::PLAYER_DETAILS_FIELD_RACE)
        cleared = clear(result.race);
      else if (field == P::PLAYER_DETAILS_FIELD_LEVEL)
        cleared = clear(result.level);
      else if (field == P::PLAYER_DETAILS_FIELD_PLACE)
        cleared = clear(result.place);
      else if (field == P::PLAYER_DETAILS_FIELD_GAME_STARTED_AT)
        cleared = clear(result.gameStartedAtUnixMs);
      if (!cleared) return Invalid("cleared_details");
    }
    if (result.gameStartedAtUnixMs && *result.gameStartedAtUnixMs && !ValidUnixMs(**result.gameStartedAtUnixMs))
      return Invalid("game_started_at_unix_ms");
    return result;
  }

  Result<Domain::Player> Player(const Configuration& config, const P::PlayerInfo& source, const ActorValueKinds& kinds)
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
      auto value = ReadActorValue(entry, kinds);
      if (!value) return std::unexpected{value.error()};
      if (!result.actorValues.emplace(std::move(value->first), std::move(value->second)).second) return Invalid("actor_value_key");
    }
    return result;
  }

  Result<PlayerMetadataUpdated> ReadMetadata(
    const Configuration&          config,
    const P::PlayerMetadataPatch& source,
    const ActorValueKinds&        kinds)
  {
    const bool values  = !source.removed_actor_values().empty() || !source.actor_values().empty();
    const bool details = source.has_details() || !source.cleared_details().empty();
    if (source.player_id() == Domain::InvalidId || (!values && !details)) return Invalid("player_metadata_patch");
    PlayerMetadataUpdated result{source.player_id()};
    if (values)
    {
      if (
        static_cast<std::size_t>(source.actor_values_size()) > config.maxActorValues ||
        static_cast<std::size_t>(source.removed_actor_values_size()) > config.maxActorValues)
        return Invalid("actor_values");
      auto& patch = result.actorValues.emplace();
      for (const auto id : source.removed_actor_values())
      {
        const auto* kind = kinds.Find(id);
        if (!kind) return Invalid("actor_value_kind");
        patch.removed.push_back(kind->key);
      }
      for (const auto& entry : source.actor_values())
      {
        auto value = ReadActorValue(entry, kinds);
        if (!value) return std::unexpected{value.error()};
        if (std::ranges::contains(patch.set, value->first, &std::pair<Domain::ActorValueKey, Domain::ActorValueInfo>::first))
          return Invalid("actor_value_key");
        patch.set.push_back(std::move(*value));
      }
    }
    if (details)
    {
      auto patch = ReadDetailsPatch(source);
      if (!patch) return std::unexpected{patch.error()};
      result.details = std::move(*patch);
    }
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

  // A baseline's place is the recipient's own: the space of its batch.
  Result<PlayerLocationUpdated> ReadVisibility(const P::PlayerVisibility& source, const std::optional<Domain::Location>& space)
  {
    if (source.player_id() == Domain::InvalidId || source.view_revision() == 0) return Invalid("visibility");
    std::optional<Domain::PlayerLocation> location;
    if (source.has_pose())
    {
      if (!space) return Invalid("space");
      const auto& pose = source.pose();
      location         = Domain::PlayerLocation{*space, PositionOf(pose.position()), RotationOf(pose.rotation()), pose.sampled_at_us()};
      if (!Finite(location->position) || !Finite(location->rotation)) return Invalid("pose");
    }
    return PlayerLocationUpdated{source.player_id(), std::move(location), source.view_revision(), source.sequence()};
  }

  Result<std::vector<ActorValueKind>> ReadKinds(const google::protobuf::RepeatedPtrField<P::ActorValueKind>& source, ActorValueKinds& kinds)
  {
    std::vector<ActorValueKind> result;
    result.reserve(source.size());
    for (const auto& kind : source)
    {
      ActorValueKind value{kind.id(), kind.key(), kind.display_name()};
      if (value.key.empty() || !kinds.Define(value)) return Invalid("actor_value_kind");
      result.push_back(std::move(value));
    }
    return result;
  }

  Result<PresenceChanged> ReadPresence(const Configuration& config, const P::PresenceChanged& source, const ActorValueKinds& known)
  {
    PresenceChanged result;
    // New kinds may be used later in the same message.
    std::optional<ActorValueKinds> extended;
    if (!source.actor_value_kinds().empty())
    {
      extended.emplace(known);
      auto kinds = ReadKinds(source.actor_value_kinds(), *extended);
      if (!kinds) return std::unexpected{kinds.error()};
      result.kinds = std::move(*kinds);
    }
    const auto& kinds = extended ? *extended : known;

    for (const auto* players : {&source.joined(), &source.updated()})
      for (const auto& value : *players)
      {
        auto player = Player(config, value, kinds);
        if (!player) return std::unexpected{player.error()};
        result.updates.emplace_back(PlayerUpserted{std::move(*player)});
      }
    for (const auto& value : source.metadata())
    {
      auto patch = ReadMetadata(config, value, kinds);
      if (!patch) return std::unexpected{patch.error()};
      result.updates.emplace_back(std::move(*patch));
    }

    std::optional<Domain::Location> space;
    if (source.has_space())
    {
      space = Domain::Location{KeyOf(source.space().location_id()), source.space().location_name()};
      if (!ValidKey(space->locationId)) return Invalid("space");
    }
    bool posed = false;
    for (const auto& value : source.visibility())
    {
      auto location = ReadVisibility(value, space);
      if (!location) return std::unexpected{location.error()};
      posed = posed || location->location.has_value();
      result.updates.emplace_back(std::move(*location));
    }
    if (space && !posed) return Invalid("space");

    for (const auto id : source.left())
    {
      if (id == Domain::InvalidId) return Invalid("player_id");
      result.updates.emplace_back(PlayerRemoved{id});
    }
    if (result.updates.empty()) return Invalid("presence_changed");
    return result;
  }

}
