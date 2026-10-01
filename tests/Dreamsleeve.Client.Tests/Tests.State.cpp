#include <doctest/doctest.h>

import std;
import Dreamsleeve.Client.ChatCache;
import Dreamsleeve.Client.PlayerStore;
import Dreamsleeve.Client.Model;

using namespace Domain;
using namespace Dreamsleeve::Client;

namespace StateTests
{
  Player MakePlayer(PlayerId id = 1)
  {
    return Player{
      .data = {id, "player_" + std::to_string(id), "Player " + std::to_string(id)},
      .characterName = "Nerevar",
      .location = PlayerLocation{
        .location = {{"skyrim.esm", 1}, "Whiterun"},
        .position = {1, 2, 3}
      },
      .actorValues = {
        {"skyrim:health", {"Health", ResourceActorValue{80, 100}}},
        {"skyrim:magicka", {"Magicka", ResourceActorValue{40, 50}}}
      }
    };
  }

  ChatMessage Message(ChatMessageId id, ChatChannelId channel = 7)
  {
    return ChatMessage{
      .messageId = id,
      .channelId = channel,
      .author = MakePlayer().data,
      .messageText = "Message " + std::to_string(id),
      .sentAt = Domain::FromUnixMilliseconds(1000)
    };
  }

  ChatCache Cache(std::size_t capacity = 4)
  {
    auto result = ChatCache::TryCreate(7, capacity);
    if (!result) throw std::logic_error{"Invalid test cache configuration"};
    return std::move(*result);
  }

  std::vector<ChatMessageId> Ids(const ChatCache& cache)
  {
    std::vector<ChatMessageId> ids;
    for (const auto& message : cache.Snapshot().messages)
      ids.push_back(message.messageId);
    return ids;
  }

  std::optional<ChatMessage> Find(const ChatCache& cache, ChatMessageId id)
  {
    for (const auto& message : cache.Snapshot().messages)
      if (message.messageId == id) return message;
    return std::nullopt;
  }
}

TEST_SUITE_BEGIN("Client.State");

TEST_CASE("PlayerStore full snapshots replace membership atomically and reject duplicate identities")
{
  PlayerStore store;
  REQUIRE(store.Upsert(StateTests::MakePlayer(9)));
  REQUIRE(store.Upsert(StateTests::MakePlayer(2)));
  const auto before = store.Snapshot();
  const std::array duplicates{StateTests::MakePlayer(3), StateTests::MakePlayer(3)};
  const auto rejected = store.ReplaceAll(duplicates);
  REQUIRE_FALSE(rejected.has_value());
  CHECK(rejected.error().code == ErrorCode::DuplicatePlayer);
  CHECK(store.Snapshot() == before);

  const std::array replacement{StateTests::MakePlayer(5), StateTests::MakePlayer(2)};
  REQUIRE(store.ReplaceAll(replacement).has_value());
  CHECK_FALSE(store.Find(9));
  const auto players = store.Snapshot();
  REQUIRE(players.size() == 2);
  CHECK(players[0].data.playerId == 2);
  CHECK(players[1].data.playerId == 5);
  REQUIRE(store.ReplaceAll({}).has_value());
  CHECK(store.Snapshot().empty());
}

TEST_CASE("PlayerStore preserves accepted payload bytes without normalizing or revalidating them")
{
  PlayerStore store;
  auto player = StateTests::MakePlayer(0);
  player.data.username = " Server-Name ";
  player.data.displayName = "  Cafe\xCC\x81  ";
  player.characterName = "";
  player.location->location.locationId = {"Skyrim.ESM", 0xFF000001};
  player.location->location.locationName = "";
  player.location->position.X = std::numeric_limits<float>::infinity();
  player.actorValues = {{"SKYRIM:Health", {"", ResourceActorValue{-10, -20}}}};
  REQUIRE(store.Upsert(player));
  CHECK(store.Find(0) == std::optional<Player>{player});

  const Player replacement{.data = player.data};
  CHECK_FALSE(store.Upsert(replacement));
  CHECK(store.Find(0) == std::optional<Player>{replacement});
}

TEST_CASE("PlayerStore snapshots and lookup results own their nested maps and strings")
{
  PlayerStore store;
  REQUIRE(store.Upsert(StateTests::MakePlayer()));
  auto snapshot = store.Snapshot();
  auto found = store.Find(1);
  REQUIRE(found.has_value());
  found->actorValues.clear();
  snapshot[0].data.displayName = "Changed locally";
  CHECK(store.Find(1)->actorValues.size() == 2);
  CHECK(store.Find(1)->data.displayName == "Player 1");
  REQUIRE(store
            .ApplyMetadata(
              1,
              ActorValuesPatch{
                  .removed = {"skyrim:health", "skyrim:magicka"}
  },
              std::nullopt)
            .has_value());
  REQUIRE(store.UpdateLocation(1, std::nullopt).has_value());
  CHECK(store.Find(1)->actorValues.empty());
  CHECK(snapshot[0].actorValues.size() == 2);
  CHECK(snapshot[0].location.has_value());
}

TEST_CASE("PlayerStore location and metadata updates replace only what they carry")
{
  PlayerStore store;
  const auto initial = StateTests::MakePlayer();
  REQUIRE(store.Upsert(initial));
  auto location = *initial.location;
  location.location.locationId.pluginName = "Skyrim.ESM";
  location.location.locationName = "";
  location.position = {40, 50, 60};
  REQUIRE(store.UpdateLocation(1, location).has_value());
  auto updated = store.Find(1);
  REQUIRE(updated.has_value());
  CHECK(updated->data == initial.data);
  CHECK(updated->actorValues == initial.actorValues);
  CHECK(updated->characterName == initial.characterName);
  CHECK(updated->location == std::optional<PlayerLocation>{location});

  PlayerDetailsPatch details;
  details.level = std::optional<std::uint32_t>{12};
  REQUIRE(store.ApplyMetadata(1, std::nullopt, details).has_value());
  CHECK(store.Find(1)->details.level == std::optional<std::uint32_t>{12});
  CHECK(store.Find(1)->actorValues == initial.actorValues);
  // A patch changes only the readings it names.
  const ActorValuesPatch values{.removed = {"skyrim:magicka"}, .set = {{"skyrim:stamina", {"Stamina", ScalarActorValue{5}}}}};
  REQUIRE(store.ApplyMetadata(1, values, std::nullopt).has_value());
  CHECK(
    store.Find(1)->actorValues == ActorValueStorage{
                                      {"skyrim:health",  {"Health", ResourceActorValue{80, 100}}},
                                      {"skyrim:stamina", {"Stamina", ScalarActorValue{5}}       }
  });
  CHECK(store.Find(1)->details.level == std::optional<std::uint32_t>{12});
  REQUIRE(store.UpdateLocation(1, std::nullopt).has_value());
  CHECK_FALSE(store.Find(1)->location.has_value());
}

TEST_CASE("PlayerStore unknown partial updates cannot create incomplete players")
{
  PlayerStore store;
  const std::array results{store.UpdateLocation(42, std::nullopt), store.ApplyMetadata(42, ActorValuesPatch{}, std::nullopt)};
  for (const auto& result : results)
  {
    REQUIRE_FALSE(result.has_value());
    CHECK(result.error().code == ErrorCode::UnknownPlayer);
  }
  CHECK(store.Snapshot().empty());
}

TEST_CASE("ChatCache requires capacity and retains the greatest IDs from out-of-order batches")
{
  CHECK_FALSE(ChatCache::TryCreate(7, 0).has_value());
  CHECK(ChatCache::TryCreate(0, 4).has_value());
  auto cache = StateTests::Cache(3);
  const std::array batch{
    StateTests::Message(30), StateTests::Message(10), StateTests::Message(20), StateTests::Message(40)
  };
  const auto result = cache.Merge(batch);
  REQUIRE(result.has_value());
  CHECK(result->added == 4);
  CHECK(result->evicted == 1);
  CHECK(StateTests::Ids(cache) == std::vector<ChatMessageId>{20, 30, 40});
  CHECK(cache.State().count == 3);
}

TEST_CASE("ChatCache retains exact accepted text and ignores only identical message duplicates")
{
  auto cache = StateTests::Cache();
  auto message = StateTests::Message(10);
  message.author->username    = " PLAYER_1 ";
  message.author->displayName = "";
  message.messageText = "  Cafe\xCC\x81\nsecond line\t ";
  REQUIRE(cache.Merge(std::array{message}).has_value());
  CHECK(StateTests::Find(cache, 10) == std::optional<ChatMessage>{message});
  const std::array batch{message, StateTests::Message(20), StateTests::Message(20)};
  const auto merged = cache.Merge(batch);
  REQUIRE(merged.has_value());
  CHECK(merged->added == 1);
  CHECK(merged->duplicates == 2);
  CHECK(cache.State().count == 2);
  auto changed = message;
  changed.author->username = "player_1";
  CHECK_FALSE(cache.Merge(std::array{changed}).has_value());
  CHECK(StateTests::Find(cache, 10) == std::optional<ChatMessage>{message});
}

TEST_CASE("ChatCache conflicts and wrong channels reject the entire incoming batch")
{
  auto cache = StateTests::Cache();
  REQUIRE(cache.Merge(std::array{StateTests::Message(10)}).has_value());
  auto conflict = StateTests::Message(10);
  conflict.messageText = "Conflicting body";
  const std::array retainedConflict{StateTests::Message(20), conflict};
  auto result = cache.Merge(retainedConflict);
  REQUIRE_FALSE(result.has_value());
  CHECK(result.error().code == ErrorCode::ConflictingMessage);
  CHECK(StateTests::Ids(cache) == std::vector<ChatMessageId>{10});
  conflict = StateTests::Message(20);
  conflict.author->displayName = "A different author snapshot";
  const std::array newConflict{StateTests::Message(20), conflict};
  CHECK_FALSE(cache.Merge(newConflict).has_value());
  const std::array wrongChannel{StateTests::Message(20), StateTests::Message(30, 8)};
  result = cache.Merge(wrongChannel);
  REQUIRE_FALSE(result.has_value());
  CHECK(result.error().code == ErrorCode::ChannelMismatch);
  CHECK(StateTests::Ids(cache) == std::vector<ChatMessageId>{10});
}

TEST_CASE("ClientModel routes accepted updates and advances revision only on success")
{
  ClientModel model;
  REQUIRE(model.RegisterChannel(7, 4).has_value());
  CHECK_FALSE(model.RegisterChannel(7, 9).has_value());
  const auto generation = model.Generation();
  REQUIRE(model.Apply(generation, OnlinePlayersReplaced{{StateTests::MakePlayer(), StateTests::MakePlayer(2)}}).has_value());
  REQUIRE(model.Apply(generation, PlayerLocationUpdated{1, std::nullopt}).has_value());
  REQUIRE(model.Apply(generation, PlayerMetadataUpdated{1, ActorValuesPatch{.removed = {"skyrim:health", "skyrim:magicka"}}, std::nullopt})
            .has_value());
  REQUIRE(model.Apply(generation, ChatMessagesReceived{7, {StateTests::Message(10)}}).has_value());
  const auto accepted = model.Snapshot();
  REQUIRE(accepted.players.size() == 2);
  REQUIRE(accepted.chats.size() == 1);
  CHECK_FALSE(accepted.players[0].location.has_value());
  CHECK(accepted.players[0].actorValues.empty());
  CHECK(accepted.chats[0].messages[0].messageId == 10);
  CHECK_FALSE(model.Apply(generation, PlayerLocationUpdated{42, std::nullopt}).has_value());
  CHECK_FALSE(model.Apply(generation, ChatMessagesReceived{8, {StateTests::Message(11, 8)}}).has_value());
  CHECK(model.Snapshot().revision == accepted.revision);
  CHECK(model.Snapshot().players == accepted.players);
  REQUIRE(model.Apply(generation, PlayerRemoved{2}).has_value());
  CHECK(model.Snapshot().players.size() == 1);
}

TEST_CASE("ClientModel server switch discards the session and rejects its late updates")
{
  ClientModel model;
  REQUIRE(model.RegisterChannel(7, 4).has_value());
  const auto oldGeneration = model.Generation();
  REQUIRE(model.SetSelfPlayer(oldGeneration, 1).has_value());
  REQUIRE(model.Apply(oldGeneration, PlayerUpserted{StateTests::MakePlayer()}).has_value());
  REQUIRE(model.Apply(oldGeneration, ChatMessagesReceived{7, {StateTests::Message(10)}}).has_value());
  model.ResetSession();
  const auto cleared = model.Snapshot();
  CHECK(cleared.chats.empty());
  CHECK(cleared.players.empty());
  CHECK_FALSE(cleared.selfPlayerId.has_value());
  CHECK(model.Generation() != oldGeneration);
  const auto rejected = model.Apply(oldGeneration, PlayerUpserted{StateTests::MakePlayer(9)});
  REQUIRE_FALSE(rejected.has_value());
  CHECK(rejected.error().code == ErrorCode::StaleGeneration);
  CHECK_FALSE(model.SetSelfPlayer(oldGeneration, 9).has_value());
  CHECK(model.Snapshot().revision == cleared.revision);

  // Channels come back with the new session; its IDs may repeat the old ones.
  CHECK_FALSE(model.Apply(model.Generation(), ChatMessagesReceived{7, {StateTests::Message(10)}}).has_value());
  REQUIRE(model.RegisterChannel(7, 4).has_value());
  auto reused = StateTests::Message(10);
  reused.messageText = "A different server's message";
  REQUIRE(model.Apply(model.Generation(), ChatMessagesReceived{7, {reused}}).has_value());
  CHECK(model.Snapshot().chats[0].messages[0].messageText == reused.messageText);
}

TEST_SUITE_END();
