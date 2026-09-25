export module Dreamsleeve.Client.Domain;

import std;

export using PlayerId      = std::uint64_t;
export using ChatMessageId = std::uint64_t;
export using ChatChannelId = std::uint64_t;

export using LocalFormId = std::uint32_t;

export using Username        = std::string;
export using DisplayName     = std::string;
export using ChatMessageText = std::string;
export using PluginName      = std::string;
export using LocationName    = std::string;
export using CharacterName   = std::string;
export using ActorValueKey   = std::string;
export using ActorValueName  = std::string;

export using MessageTime = std::chrono::sys_time<std::chrono::milliseconds>;

export using WorldUnit  = float;
export using Radian     = float;
export using ActorValue = float;

export struct ScalarActorValue
{
  ActorValue value{};
};

export struct ResourceActorValue
{
  ActorValue current{};
  ActorValue maximum{};
};

export using ActorValueState = std::variant<ScalarActorValue, ResourceActorValue>;

export struct ActorValueInfo
{
  ActorValueName  displayName{};
  ActorValueState state{};
};

export using ActorValueStorage = std::unordered_map<ActorValueKey, ActorValueInfo>;

export struct FormKey
{
  PluginName  pluginName{};
  LocalFormId localFormId{};
};

export using LocationId = FormKey;

export struct Location
{
  LocationId   locationId{};
  LocationName locationName{};
};

export struct Position
{
  WorldUnit X{};
  WorldUnit Y{};
  WorldUnit Z{};
};

export struct Rotation
{
  Radian X{};
  Radian Y{};
  Radian Z{};
};

export struct PlayerLocation
{
  Location location{};
  Position position{};
  Rotation rotation{};
};

export struct PlayerData final
{
  PlayerId    playerId{};
  Username    username{};
  DisplayName displayName{};
};

export struct Player
{
  PlayerData                    data{};
  std::optional<CharacterName>  characterName{};
  std::optional<PlayerLocation> location{};
  ActorValueStorage             actorValues{};
};

export struct ChatMessage final
{
  ChatMessageId   messageId{};
  ChatChannelId   channelId{};
  PlayerData      author{};
  ChatMessageText messageText{};
  MessageTime     sentAt{};
};

export namespace Domain::Core
{

  constexpr MessageTime FromUnixMilliseconds(std::int64_t value) noexcept
  {
    return MessageTime{std::chrono::milliseconds{value}};
  }

  constexpr std::int64_t ToUnixMilliseconds(MessageTime value) noexcept
  {
    return value.time_since_epoch().count();
  }

}
