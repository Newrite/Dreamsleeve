#include <doctest/doctest.h>

import std;
import Dreamsleeve.Client.ChatCache;
import Dreamsleeve.Client.PlayerStore;
import Dreamsleeve.Client.Model;
import Dreamsleeve.Client.SnapshotMailbox;

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

  ChatHistoryPage Page(
    const ChatCache& cache, std::vector<ChatMessage> messages,
    bool hasMore = false, bool hasGap = false)
  {
    ChatHistoryPage result;
    result.channelId = cache.ChannelId();
    result.round = cache.HistoryState().round;
    result.after = cache.HistoryState().cursor;
    result.nextCursor = result.after;
    for (const auto& message : messages)
      if (!result.nextCursor || message.messageId > *result.nextCursor)
        result.nextCursor = message.messageId;
    result.messages = std::move(messages);
    result.hasMore = hasMore;
    result.hasGap = hasGap;
    return result;
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
  CHECK_FALSE(store.Contains(9));
  const auto players = store.Snapshot();
  REQUIRE(players.size() == 2);
  CHECK(players[0].data.playerId == 2);
  CHECK(players[1].data.playerId == 5);
  REQUIRE(store.ReplaceAll({}).has_value());
  CHECK(store.Count() == 0);
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
  REQUIRE(store.ClearGameState(1).has_value());
  CHECK(snapshot[0].actorValues.size() == 2);
  CHECK(snapshot[0].location.has_value());
}

TEST_CASE("PlayerStore partial updates preserve unrelated data and guard profile identity")
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

  auto profile = initial.data;
  profile.username = " NewName ";
  profile.displayName = "";
  REQUIRE(store.UpdateProfile(1, profile).has_value());
  CHECK(store.Find(1)->data == profile);
  const auto before = store.Snapshot();
  const auto rejected = store.UpdateProfile(1, StateTests::MakePlayer(2).data);
  REQUIRE_FALSE(rejected.has_value());
  CHECK(rejected.error().code == ErrorCode::IdentityMismatch);
  CHECK(store.Snapshot() == before);
  REQUIRE(store.UpdateLocation(1, std::nullopt).has_value());
  CHECK_FALSE(store.Find(1)->location.has_value());
  CHECK(store.Find(1)->actorValues == initial.actorValues);
}

TEST_CASE("PlayerStore unknown partial updates cannot create incomplete players")
{
  PlayerStore store;
  const std::array results{
    store.UpdateProfile(42, StateTests::MakePlayer(42).data),
    store.UpdateLocation(42, std::nullopt),
    store.RenameCharacter(42, "Nerevar"),
    store.BeginCharacter(42, "Nerevar"),
    store.ClearGameState(42),
    store.ApplyActorValues(42, {})
  };
  for (const auto& result : results)
  {
    REQUIRE_FALSE(result.has_value());
    CHECK(result.error().code == ErrorCode::UnknownPlayer);
  }
  CHECK(store.Count() == 0);
}

TEST_CASE("PlayerStore renaming and character replacement have different telemetry semantics")
{
  PlayerStore store;
  const auto initial = StateTests::MakePlayer();
  REQUIRE(store.Upsert(initial));
  REQUIRE(store.RenameCharacter(1, "").has_value());
  CHECK(store.Find(1)->characterName == std::optional<std::string>{""});
  CHECK(store.Find(1)->actorValues == initial.actorValues);
  CHECK(store.Find(1)->location == initial.location);
  REQUIRE(store.BeginCharacter(1, "").has_value());
  const auto reset = store.Find(1);
  REQUIRE(reset.has_value());
  CHECK(reset->data == initial.data);
  CHECK(reset->characterName == std::optional<std::string>{""});
  CHECK_FALSE(reset->location.has_value());
  CHECK(reset->actorValues.empty());
}

TEST_CASE("PlayerStore Actor Value deltas use exact supplied keys and preserve unrelated values")
{
  PlayerStore store;
  REQUIRE(store.Upsert(StateTests::MakePlayer()));
  const ActorValueStorage delta{
    {"skyrim:health", {"", ResourceActorValue{120, 100}}},
    {"SKYRIM:Health", {"Здоровье", ResourceActorValue{70, 90}}}
  };
  REQUIRE(store.ApplyActorValues(1, delta).has_value());
  auto updated = store.Find(1);
  REQUIRE(updated.has_value());
  CHECK(updated->actorValues.size() == 3);
  CHECK(updated->actorValues.at("skyrim:health") == delta.at("skyrim:health"));
  CHECK(updated->actorValues.at("SKYRIM:Health") == delta.at("SKYRIM:Health"));
  CHECK(ActorValues::GetCurrent(updated->actorValues.at("skyrim:magicka").state) == 40);
  const std::array<ActorValueKey, 2> removed{"SKYRIM:Health", "SKYRIM:Health"};
  REQUIRE(store.ApplyActorValues(1, {}, removed).has_value());
  CHECK_FALSE(store.Find(1)->actorValues.contains("SKYRIM:Health"));
  CHECK(store.Find(1)->actorValues.contains("skyrim:health"));
}

TEST_CASE("PlayerStore rejects contradictory Actor Value deltas before applying any part")
{
  PlayerStore store;
  REQUIRE(store.Upsert(StateTests::MakePlayer()));
  const auto before = store.Snapshot();
  const ActorValueStorage delta{
    {"skyrim:health", {"Health", ScalarActorValue{1}}},
    {"skyrim:stamina", {"Stamina", ScalarActorValue{2}}}
  };
  const std::array<ActorValueKey, 2> removed{"skyrim:magicka", "skyrim:health"};
  CHECK_FALSE(store.ApplyActorValues(1, delta, removed).has_value());
  CHECK(store.Snapshot() == before);
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
  CHECK(cache.MaxObservedId() == std::optional<ChatMessageId>{40});
}

TEST_CASE("ChatCache retains exact accepted text and ignores only identical message duplicates")
{
  auto cache = StateTests::Cache();
  auto message = StateTests::Message(10);
  message.author.username = " PLAYER_1 ";
  message.author.displayName = "";
  message.messageText = "  Cafe\xCC\x81\nsecond line\t ";
  REQUIRE(cache.Merge(message).has_value());
  CHECK(cache.Find(10) == std::optional<ChatMessage>{message});
  const std::array batch{message, StateTests::Message(20), StateTests::Message(20)};
  const auto merged = cache.Merge(batch);
  REQUIRE(merged.has_value());
  CHECK(merged->added == 1);
  CHECK(merged->duplicates == 2);
  CHECK(cache.Count() == 2);
  auto changed = message;
  changed.author.username = "player_1";
  CHECK_FALSE(cache.Merge(changed).has_value());
  CHECK(cache.Find(10) == std::optional<ChatMessage>{message});
}

TEST_CASE("ChatCache conflicts and wrong channels reject the entire incoming batch")
{
  auto cache = StateTests::Cache();
  REQUIRE(cache.Merge(StateTests::Message(10)).has_value());
  auto conflict = StateTests::Message(10);
  conflict.messageText = "Conflicting body";
  const std::array retainedConflict{StateTests::Message(20), conflict};
  auto result = cache.Merge(retainedConflict);
  REQUIRE_FALSE(result.has_value());
  CHECK(result.error().code == ErrorCode::ConflictingMessage);
  CHECK(StateTests::Ids(cache) == std::vector<ChatMessageId>{10});
  CHECK(cache.MaxObservedId() == std::optional<ChatMessageId>{10});
  conflict = StateTests::Message(20);
  conflict.author.displayName = "A different author snapshot";
  const std::array newConflict{StateTests::Message(20), conflict};
  CHECK_FALSE(cache.Merge(newConflict).has_value());
  const std::array wrongChannel{StateTests::Message(20), StateTests::Message(30, 8)};
  result = cache.Merge(wrongChannel);
  REQUIRE_FALSE(result.has_value());
  CHECK(result.error().code == ErrorCode::ChannelMismatch);
  CHECK(StateTests::Ids(cache) == std::vector<ChatMessageId>{10});
}

TEST_CASE("ChatCache live messages do not skip the history cursor when pages interleave")
{
  auto cache = StateTests::Cache(8);
  REQUIRE(cache.BeginHistory(10).has_value());
  REQUIRE(cache.Merge(StateTests::Message(100)).has_value());
  CHECK(cache.HistoryState().cursor == std::optional<ChatMessageId>{10});
  auto page = StateTests::Page(cache, {StateTests::Message(20), StateTests::Message(30)}, true);
  REQUIRE(cache.ApplyHistoryPage(page).has_value());
  CHECK(cache.HistoryState().cursor == std::optional<ChatMessageId>{30});
  CHECK(cache.MaxObservedId() == std::optional<ChatMessageId>{100});
  const auto last = cache.ApplyHistoryPage(StateTests::Page(cache, {StateTests::Message(100)}));
  REQUIRE(last.has_value());
  CHECK(last->duplicates == 1);
  CHECK_FALSE(cache.HistoryState().hasMore);
  CHECK(StateTests::Ids(cache) == std::vector<ChatMessageId>{20, 30, 100});
}

TEST_CASE("ChatCache cursor advances even when an old history page is immediately evicted")
{
  auto cache = StateTests::Cache(1);
  REQUIRE(cache.Merge(StateTests::Message(100)).has_value());
  REQUIRE(cache.BeginHistory().has_value());
  const auto page = StateTests::Page(cache, {StateTests::Message(10), StateTests::Message(20)}, true);
  const auto merged = cache.ApplyHistoryPage(page);
  REQUIRE(merged.has_value());
  CHECK(merged->evicted == 2);
  CHECK(StateTests::Ids(cache) == std::vector<ChatMessageId>{100});
  CHECK(cache.HistoryState().cursor == std::optional<ChatMessageId>{20});
  CHECK(cache.HistoryState().hasMore);
  CHECK_FALSE(cache.HistoryState().hasGap);
}

TEST_CASE("ChatCache history gaps come from server metadata and remain sticky only within a round")
{
  auto cache = StateTests::Cache();
  REQUIRE(cache.BeginHistory(1).has_value());
  REQUIRE(cache.ApplyHistoryPage(StateTests::Page(cache, {StateTests::Message(1000)}, true, true)).has_value());
  REQUIRE(cache.ApplyHistoryPage(StateTests::Page(cache, {StateTests::Message(9000)})).has_value());
  CHECK(cache.HistoryState().hasGap);
  REQUIRE(cache.BeginHistory(9000).has_value());
  CHECK_FALSE(cache.HistoryState().hasGap);
  REQUIRE(cache.ApplyHistoryPage(StateTests::Page(cache, {StateTests::Message(100000)})).has_value());
  CHECK_FALSE(cache.HistoryState().hasGap);
}

TEST_CASE("ChatCache invalid cursors and empty progress cannot mutate history state")
{
  auto cache = StateTests::Cache();
  CHECK_FALSE(cache.BeginHistory(0).has_value());
  REQUIRE(cache.BeginHistory(10).has_value());
  auto page = StateTests::Page(cache, {StateTests::Message(20)});
  page.nextCursor = 21;
  CHECK_FALSE(cache.ApplyHistoryPage(page).has_value());
  CHECK_FALSE(cache.ApplyHistoryPage(StateTests::Page(cache, {}, true)).has_value());
  CHECK_FALSE(cache.ApplyHistoryPage(StateTests::Page(cache, {StateTests::Message(10)})).has_value());
  CHECK(cache.Count() == 0);
  CHECK(cache.HistoryState().cursor == std::optional<ChatMessageId>{10});
}

TEST_CASE("ChatCache canceled and completed history rounds cannot accept stale replies")
{
  auto cache = StateTests::Cache();
  REQUIRE(cache.Merge(StateTests::Message(10)).has_value());
  REQUIRE(cache.BeginHistory(std::nullopt, 50).has_value());
  const auto obsolete = StateTests::Page(cache, {StateTests::Message(20)});
  cache.CancelHistory();
  CHECK(StateTests::Ids(cache) == std::vector<ChatMessageId>{10});
  CHECK_FALSE(cache.ApplyHistoryPage(obsolete).has_value());
  CHECK_FALSE(cache.BeginHistory(std::nullopt, 50).has_value());
  const auto next = cache.BeginHistory();
  REQUIRE(next.has_value());
  CHECK(*next > 50);
  CHECK_FALSE(cache.ApplyHistoryPage(obsolete).has_value());
  REQUIRE(cache.ApplyHistoryPage(StateTests::Page(cache, {StateTests::Message(30)})).has_value());
  CHECK_FALSE(cache.ApplyHistoryPage(StateTests::Page(cache, {StateTests::Message(40)})).has_value());
  cache.Clear();
  const auto afterClear = cache.BeginHistory();
  REQUIRE(afterClear.has_value());
  CHECK(*afterClear > *next);
  CHECK_FALSE(cache.ApplyHistoryPage(obsolete).has_value());
  CHECK(cache.Count() == 0);
}

TEST_CASE("ClientModel routes accepted updates and advances revision only on success")
{
  ClientModel model;
  REQUIRE(model.RegisterChannel(7, 4).has_value());
  const auto generation = model.Generation();
  REQUIRE(model.Apply(generation, OnlinePlayersReplaced{{StateTests::MakePlayer(), StateTests::MakePlayer(2)}}).has_value());
  REQUIRE(model.Apply(generation, PlayerCharacterRenamed{1, "Renamed"}).has_value());
  REQUIRE(model.Apply(generation, PlayerLocationUpdated{1, std::nullopt}).has_value());
  REQUIRE(model.Apply(generation, PlayerActorValuesUpdated{1, {}, {"skyrim:health"}}).has_value());
  REQUIRE(model.Apply(generation, ChatMessagesReceived{7, {StateTests::Message(10)}}).has_value());
  const auto accepted = model.Snapshot();
  REQUIRE(accepted.players.size() == 2);
  REQUIRE(accepted.chats.size() == 1);
  CHECK(accepted.players[0].characterName == std::optional<std::string>{"Renamed"});
  CHECK_FALSE(accepted.players[0].location.has_value());
  CHECK(accepted.players[0].actorValues.size() == 1);
  CHECK(accepted.chats[0].messages[0].messageId == 10);
  CHECK_FALSE(model.Apply(generation, PlayerProfileUpdated{1, StateTests::MakePlayer(2).data}).has_value());
  CHECK(model.Snapshot().revision == accepted.revision);
  CHECK(model.Snapshot().players == accepted.players);
  REQUIRE(model.Apply(generation, PlayerCharacterStarted{1, "Renamed"}).has_value());
  CHECK(model.Snapshot().players[0].actorValues.empty());
  REQUIRE(model.Apply(generation, PlayerGameStateCleared{1}).has_value());
  CHECK_FALSE(model.Snapshot().players[0].characterName.has_value());
  REQUIRE(model.Apply(generation, PlayerRemoved{2}).has_value());
  CHECK(model.Snapshot().players.size() == 1);
}

TEST_CASE("ClientModel disconnect retains messages and invalidates old-generation updates")
{
  ClientModel model;
  REQUIRE(model.RegisterChannel(7, 4).has_value());
  const auto oldGeneration = model.Generation();
  REQUIRE(model.SetSelfPlayer(oldGeneration, 1).has_value());
  REQUIRE(model.Apply(oldGeneration, PlayerUpserted{StateTests::MakePlayer()}).has_value());
  REQUIRE(model.Apply(oldGeneration, ChatMessagesReceived{7, {StateTests::Message(10)}}).has_value());
  model.ClearOnlineState();
  const auto cleared = model.Snapshot();
  CHECK(cleared.players.empty());
  CHECK_FALSE(cleared.selfPlayerId.has_value());
  REQUIRE(cleared.chats.size() == 1);
  CHECK(cleared.chats[0].messages.size() == 1);
  CHECK(cleared.generation != oldGeneration);
  const auto rejected = model.Apply(oldGeneration, PlayerUpserted{StateTests::MakePlayer(9)});
  REQUIRE_FALSE(rejected.has_value());
  CHECK(rejected.error().code == ErrorCode::StaleGeneration);
  CHECK_FALSE(model.SetSelfPlayer(oldGeneration, 9).has_value());
  CHECK(model.Snapshot().revision == cleared.revision);
  CHECK(model.Snapshot().players.empty());
  CHECK_FALSE(model.Snapshot().selfPlayerId.has_value());
}

TEST_CASE("ClientModel server switch discards previous channel history before IDs can be reused")
{
  ClientModel model;
  REQUIRE(model.RegisterChannel(7, 4).has_value());
  const auto oldGeneration = model.Generation();
  REQUIRE(model.Apply(oldGeneration, ChatMessagesReceived{7, {StateTests::Message(10)}}).has_value());
  model.ResetSession();
  CHECK(model.Snapshot().chats.empty());
  CHECK(model.Generation() != oldGeneration);
  CHECK_FALSE(model.Apply(model.Generation(), ChatMessagesReceived{7, {StateTests::Message(10)}}).has_value());
  REQUIRE(model.RegisterChannel(7, 4).has_value());
  auto reused = StateTests::Message(10);
  reused.messageText = "A different server's message";
  REQUIRE(model.Apply(model.Generation(), ChatMessagesReceived{7, {reused}}).has_value());
  CHECK(model.Snapshot().chats[0].messages[0].messageText == reused.messageText);
}

TEST_CASE("ClientModel re-registering a channel cannot revive a canceled history request")
{
  ClientModel model;
  CHECK_FALSE(model.BeginHistory(7).has_value());
  REQUIRE(model.RegisterChannel(7, 4).has_value());
  CHECK_FALSE(model.RegisterChannel(7, 9).has_value());
  const auto first = model.BeginHistory(7);
  REQUIRE(first.has_value());
  ChatHistoryPage obsolete;
  obsolete.channelId = 7;
  obsolete.round = *first;
  obsolete.nextCursor = 10;
  obsolete.messages = {StateTests::Message(10)};
  REQUIRE(model.RemoveChannel(7));
  REQUIRE(model.RegisterChannel(7, 4).has_value());
  const auto second = model.BeginHistory(7);
  REQUIRE(second.has_value());
  CHECK(*second != *first);
  CHECK_FALSE(model.Apply(model.Generation(), ChatHistoryReceived{obsolete}).has_value());
  CHECK(model.Snapshot().chats[0].messages.empty());
  obsolete.round = *second;
  REQUIRE(model.Apply(model.Generation(), ChatHistoryReceived{obsolete}).has_value());
}

TEST_CASE("ClientModel disconnect cancels active history while preserving accepted messages")
{
  ClientModel model;
  REQUIRE(model.RegisterChannel(7, 4).has_value());
  REQUIRE(model.Apply(model.Generation(), ChatMessagesReceived{7, {StateTests::Message(10)}}).has_value());
  const auto round = model.BeginHistory(7, 10);
  REQUIRE(round.has_value());
  model.ClearOnlineState();
  const auto snapshot = model.Snapshot();
  REQUIRE(snapshot.chats.size() == 1);
  CHECK(snapshot.chats[0].messages.size() == 1);
  CHECK_FALSE(snapshot.chats[0].history.hasMore);
  CHECK_FALSE(snapshot.chats[0].history.cursor.has_value());
  ChatHistoryPage obsolete;
  obsolete.channelId = 7;
  obsolete.round = *round;
  obsolete.after = 10;
  obsolete.nextCursor = 20;
  obsolete.messages = {StateTests::Message(20)};
  CHECK_FALSE(model.Apply(model.Generation(), ChatHistoryReceived{obsolete}).has_value());
}

TEST_CASE("SnapshotMailbox gives readers owned immutable snapshots that outlive later publication")
{
  SnapshotMailbox mailbox;
  CHECK_FALSE(mailbox.Read());
  ClientModel model;
  REQUIRE(model.Apply(model.Generation(), PlayerUpserted{StateTests::MakePlayer()}).has_value());
  REQUIRE(model.RegisterChannel(7, 4).has_value());
  REQUIRE(model.Apply(model.Generation(), ChatMessagesReceived{7, {StateTests::Message(10)}}).has_value());
  auto source = model.Snapshot();
  mailbox.Publish(source);
  const auto first = mailbox.Read();
  REQUIRE(first);
  source.players[0].actorValues.clear();
  source.players[0].data.displayName = "Caller mutation";
  source.chats[0].messages[0].messageText = "Caller edit";
  CHECK(first->players[0].actorValues.size() == 2);
  CHECK(first->players[0].data.displayName == "Player 1");
  CHECK(first->chats[0].messages[0].messageText == "Message 10");
  model.ResetSession();
  mailbox.Publish(model.Snapshot());
  CHECK(mailbox.Read()->players.empty());
  CHECK(first->players[0].actorValues.size() == 2);
  CHECK(first->chats[0].messages.size() == 1);
}

TEST_CASE("SnapshotMailbox publishes coherent state from its owner to a concurrent reader")
{
  SnapshotMailbox mailbox;
  std::atomic<bool> complete{false};
  std::atomic<bool> coherent{true};
  constexpr std::uint64_t publications = 1000;
  std::jthread reader([&]
  {
    std::uint64_t lastRevision{};
    do
    {
      if (const auto snapshot = mailbox.Read())
      {
        if (snapshot->revision < lastRevision || snapshot->selfPlayerId != snapshot->revision ||
            snapshot->players.size() != 1 || snapshot->players[0].data.playerId != snapshot->revision ||
            snapshot->players[0].data.displayName != std::to_string(snapshot->revision))
          coherent.store(false, std::memory_order_relaxed);
        lastRevision = snapshot->revision;
      }
    } while (!complete.load(std::memory_order_acquire));
  });
  for (std::uint64_t revision = 1; revision <= publications; ++revision)
  {
    auto player = StateTests::MakePlayer(revision);
    player.data.displayName = std::to_string(revision);
    ClientSnapshot snapshot;
    snapshot.generation = 1;
    snapshot.revision = revision;
    snapshot.selfPlayerId = revision;
    snapshot.players.push_back(std::move(player));
    mailbox.Publish(std::move(snapshot));
  }
  complete.store(true, std::memory_order_release);
  reader.join();
  CHECK(coherent.load(std::memory_order_relaxed));
  REQUIRE(mailbox.Read());
  CHECK(mailbox.Read()->revision == publications);
}

TEST_CASE("ClientModel server rejection preserves unknown codes and text without changing accepted state")
{
  ClientModel model;
  const auto generation = model.Generation();
  REQUIRE(model.RegisterChannel(7, 4).has_value());
  REQUIRE(model.Apply(generation, PlayerUpserted{StateTests::MakePlayer()}).has_value());
  REQUIRE(model.Apply(generation, ChatMessagesReceived{7, {StateTests::Message(10)}}).has_value());
  const auto before = model.Snapshot();
  const ServerRejection rejection{42, 0xFFFF0001, "Имя уже занято", "username"};
  REQUIRE(model.Apply(generation, rejection).has_value());
  const auto after = model.Snapshot();
  CHECK(after.players == before.players);
  REQUIRE(after.chats.size() == before.chats.size());
  CHECK(after.chats[0].messages == before.chats[0].messages);
  CHECK(after.selfPlayerId == before.selfPlayerId);
  auto notifications = model.TakeServerRejections();
  REQUIRE(notifications.size() == 1);
  CHECK(notifications[0].generation == generation);
  CHECK(notifications[0].rejection.requestId == std::optional<std::uint64_t>{42});
  CHECK(notifications[0].rejection.code == rejection.code);
  CHECK(notifications[0].rejection.message == rejection.message);
  CHECK(notifications[0].rejection.field == rejection.field);
  CHECK(model.TakeServerRejections().empty());
}

TEST_CASE("ClientModel pending rejection explanations survive disconnect and reset with source generation")
{
  ClientModel model;
  const auto firstGeneration = model.Generation();
  REQUIRE(model.Apply(firstGeneration, ServerRejection{1, 100, "First error", "field"}).has_value());
  model.ClearOnlineState();
  const auto secondGeneration = model.Generation();
  REQUIRE(model.Apply(secondGeneration, ServerRejection{std::nullopt, 200, "Connection refused", ""}).has_value());
  model.ResetSession();
  auto notifications = model.TakeServerRejections();
  REQUIRE(notifications.size() == 2);
  CHECK(notifications[0].generation == firstGeneration);
  CHECK(notifications[0].rejection.requestId == std::optional<std::uint64_t>{1});
  CHECK(notifications[0].rejection.message == "First error");
  CHECK(notifications[1].generation == secondGeneration);
  CHECK_FALSE(notifications[1].rejection.requestId.has_value());
  CHECK(notifications[1].rejection.message == "Connection refused");
  CHECK(notifications[1].rejection.field.empty());
  CHECK(model.TakeServerRejections().empty());
}

TEST_CASE("ClientModel stale rejection results are refused before notification is enqueued")
{
  ClientModel model;
  const auto oldGeneration = model.Generation();
  model.ResetSession();
  const auto before = model.Snapshot();
  const auto result = model.Apply(oldGeneration, ServerRejection{99, 7, "Obsolete", "username"});
  REQUIRE_FALSE(result.has_value());
  CHECK(result.error().code == ErrorCode::StaleGeneration);
  CHECK(model.Snapshot().revision == before.revision);
  CHECK(model.TakeServerRejections().empty());
}

TEST_SUITE_END();
