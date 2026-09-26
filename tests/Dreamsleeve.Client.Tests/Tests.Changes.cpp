#include <doctest/doctest.h>

import std;
import Dreamsleeve.Client.Model;

namespace
{
  using namespace Domain;
  using namespace Dreamsleeve::Client;

  Player ChangeTestPlayer(PlayerId id = 7)
  {
    Player player;
    player.data = PlayerData{id, "player", "Display"};
    player.characterName = "Dragonborn";
    player.location = PlayerLocation{Location{FormKey{"skyrim.esm", 0x3c}, "Tamriel"}, Position{1, 2, 3}, Rotation{}};
    player.actorValues.emplace("skyrim:health", ActorValueInfo{"Health", ResourceActorValue{90, 100}});
    return player;
  }

  ChatMessage ChangeTestMessage(ChatMessageId id = 1)
  {
    return ChatMessage{id, 1, PlayerData{7, "player", "Display"}, "Hello", MessageTime{}};
  }

  void CheckSinglePlayerChange(const ChangeBatch& changes, PlayerId id)
  {
    CHECK_FALSE(changes.Empty());
    CHECK_FALSE(changes.requiresSnapshot);
    CHECK_FALSE(changes.playersReplaced);
    CHECK_FALSE(changes.selfPlayerChanged);
    REQUIRE(changes.players.size() == 1);
    CHECK(changes.players.front() == id);
    CHECK(changes.chats.empty());
  }
}

TEST_SUITE_BEGIN("Client.Changes");

TEST_CASE("ChangeBatch.Clear resets markers and retains reusable buffers")
{
  ChangeBatch changes;
  CHECK(changes.Empty());
  changes.players.reserve(64);
  changes.chats.reserve(32);
  changes.resetChats.reserve(16);
  const auto playerCapacity = changes.players.capacity();
  const auto chatCapacity = changes.chats.capacity();
  const auto resetCapacity = changes.resetChats.capacity();
  changes.generation = 4;
  changes.revision = 9;
  changes.requiresSnapshot = true;
  changes.selfPlayerChanged = true;
  changes.playersReplaced = true;
  changes.players.push_back(7);
  changes.chats.push_back(1);
  changes.resetChats.push_back(1);

  changes.Clear();

  CHECK(changes.Empty());
  CHECK(changes.generation == 0);
  CHECK(changes.revision == 0);
  CHECK_FALSE(changes.requiresSnapshot);
  CHECK_FALSE(changes.selfPlayerChanged);
  CHECK_FALSE(changes.playersReplaced);
  CHECK(changes.players.empty());
  CHECK(changes.chats.empty());
  CHECK(changes.resetChats.empty());
  CHECK(changes.chatContent.empty());
  CHECK(changes.players.capacity() == playerCapacity);
  CHECK(changes.chats.capacity() == chatCapacity);
  CHECK(changes.resetChats.capacity() == resetCapacity);
}

TEST_CASE("ClientModel coalesces changes and exposes detached targeted queries")
{
  ClientModel model;
  const auto generation = model.Generation();
  REQUIRE(model.RegisterChannel(1, 20));
  REQUIRE(model.Apply(generation, PlayerUpserted{ChangeTestPlayer()}));
  REQUIRE(model.SetSelfPlayer(generation, 7));
  REQUIRE(model.Apply(generation, PlayerProfileUpdated{7, PlayerData{7, "player", "Renamed"}}));
  REQUIRE(model.Apply(generation, ChatMessagesReceived{1, {ChangeTestMessage()}}));
  REQUIRE(model.Apply(generation, ChatMessagesReceived{1, {ChangeTestMessage(2)}}));

  ChangeBatch changes;
  model.TakeChanges(changes);
  CHECK(changes.generation == generation);
  CHECK(changes.revision == model.Snapshot().revision);
  CHECK(changes.selfPlayerChanged);
  CHECK_FALSE(changes.requiresSnapshot);
  CHECK_FALSE(changes.playersReplaced);
  REQUIRE(changes.players.size() == 1);
  CHECK(changes.players.front() == 7);
  REQUIRE(changes.chats.size() == 1); // channel registration metadata
  CHECK(changes.chats.front() == 1);
  REQUIRE(changes.chatContent.size() == 1);
  REQUIRE(std::holds_alternative<ChatMessagesAdded>(changes.chatContent.front()));
  CHECK(std::get<ChatMessagesAdded>(changes.chatContent.front()).messages.size() == 2);
  CHECK(model.SelfPlayerId() == std::optional<PlayerId>{7});
  CHECK_FALSE(model.FindPlayer(99));
  CHECK_FALSE(model.FindChat(99));

  auto player = model.FindPlayer(7);
  REQUIRE(player);
  player->data.displayName = "Local copy";
  player->actorValues.clear();
  auto sourcePlayer = model.FindPlayer(7);
  REQUIRE(sourcePlayer);
  CHECK(sourcePlayer->data.displayName == "Renamed");
  CHECK(sourcePlayer->actorValues.size() == 1);

  auto chat = model.FindChat(1);
  REQUIRE(chat);
  REQUIRE(chat->messages.size() == 2);
  chat->messages.front().messageText = "Local copy";
  auto sourceChat = model.FindChat(1);
  REQUIRE(sourceChat);
  CHECK(sourceChat->messages.front().messageText == "Hello");

  const auto revision = changes.revision;
  model.TakeChanges(changes);
  CHECK(changes.Empty());
  CHECK(changes.generation == generation);
  CHECK(changes.revision == revision);
}

TEST_CASE("ClientModel records every supported player mutation")
{
  ClientModel model;
  const auto generation = model.Generation();
  REQUIRE(model.Apply(generation, PlayerUpserted{ChangeTestPlayer()}));
  ChangeBatch changes;
  model.TakeChanges(changes);

  SUBCASE("complete player replacement")
  {
    auto player = ChangeTestPlayer();
    player.data.displayName = "Replacement";
    REQUIRE(model.Apply(generation, PlayerUpserted{std::move(player)}));
  }
  SUBCASE("profile")
  {
    REQUIRE(model.Apply(generation, PlayerProfileUpdated{7, PlayerData{7, "player", "Renamed"}}));
  }
  SUBCASE("location")
  {
    REQUIRE(model.Apply(generation, PlayerLocationUpdated{7, std::nullopt}));
  }
  SUBCASE("character rename")
  {
    REQUIRE(model.Apply(generation, PlayerCharacterRenamed{7, CharacterName{"Other"}}));
  }
  SUBCASE("new character")
  {
    REQUIRE(model.Apply(generation, PlayerCharacterStarted{7, "Other"}));
    const auto player = model.FindPlayer(7);
    REQUIRE(player);
    CHECK_FALSE(player->location);
    CHECK(player->actorValues.empty());
  }
  SUBCASE("game state cleared")
  {
    REQUIRE(model.Apply(generation, PlayerGameStateCleared{7}));
  }
  SUBCASE("actor-value delta")
  {
    ActorValueStorage values;
    values.emplace("skyrim:health", ActorValueInfo{"Health", ResourceActorValue{50, 100}});
    REQUIRE(model.Apply(generation, PlayerActorValuesUpdated{7, std::move(values), {}}));
  }
  SUBCASE("player removed")
  {
    REQUIRE(model.Apply(generation, PlayerRemoved{7}));
    CHECK_FALSE(model.FindPlayer(7));
  }

  model.TakeChanges(changes);
  CheckSinglePlayerChange(changes, 7);
}

TEST_CASE("ClientModel invalidates channel registration history and removal")
{
  ClientModel model;
  ChangeBatch changes;
  REQUIRE(model.RegisterChannel(1, 20));
  model.TakeChanges(changes);
  REQUIRE(changes.chats.size() == 1);
  CHECK(changes.chats.front() == 1);

  auto round = model.BeginHistory(1);
  REQUIRE(round);
  model.TakeChanges(changes);
  REQUIRE(changes.chats.size() == 1);
  CHECK(changes.chats.front() == 1);

  ChatHistoryPage page{.channelId = 1, .round = *round, .nextCursor = 1, .messages = {ChangeTestMessage()}};
  REQUIRE(model.Apply(model.Generation(), ChatHistoryReceived{page}));
  model.TakeChanges(changes);
  REQUIRE(changes.chats.size() == 1);
  CHECK(changes.chats.front() == 1);
  REQUIRE(changes.chatContent.size() == 1);
  REQUIRE(std::holds_alternative<ChatMessagesAdded>(changes.chatContent.front()));
  CHECK(std::get<ChatMessagesAdded>(changes.chatContent.front()).messages.size() == 1);
  const auto chat = model.FindChat(1);
  REQUIRE(chat);
  CHECK(chat->history.cursor == std::optional<ChatMessageId>{1});

  // A duplicate changes neither visible content nor chat metadata.
  REQUIRE(model.Apply(model.Generation(), ChatMessagesReceived{1, {ChangeTestMessage()}}));
  model.TakeChanges(changes);
  CHECK(changes.chats.empty());
  CHECK(changes.chatContent.empty());
  const auto afterDuplicate = model.FindChat(1);
  REQUIRE(afterDuplicate);
  CHECK(afterDuplicate->messages.size() == 1);

  REQUIRE(model.RemoveChannel(1));
  model.TakeChanges(changes);
  REQUIRE(changes.chats.size() == 1);
  CHECK(changes.chats.front() == 1);
  CHECK_FALSE(model.FindChat(1));
  CHECK_FALSE(model.RemoveChannel(1));
  model.TakeChanges(changes);
  CHECK(changes.Empty());
}

TEST_CASE("ClientModel rejected updates preserve earlier pending changes")
{
  ClientModel model;
  const auto generation = model.Generation();
  REQUIRE(model.RegisterChannel(1, 20));
  REQUIRE(model.Apply(generation, PlayerUpserted{ChangeTestPlayer()}));
  REQUIRE(model.Apply(generation, ChatMessagesReceived{1, {ChangeTestMessage()}}));
  ChangeBatch changes;
  model.TakeChanges(changes);
  REQUIRE(model.Apply(generation, PlayerProfileUpdated{7, PlayerData{7, "player", "Accepted"}}));
  const auto acceptedRevision = model.Snapshot().revision;

  CHECK_FALSE(model.Apply(generation + 1, PlayerUpserted{ChangeTestPlayer(8)}));
  CHECK_FALSE(model.Apply(generation, PlayerProfileUpdated{7, PlayerData{8, "other", "Wrong ID"}}));
  CHECK_FALSE(model.Apply(generation, PlayerLocationUpdated{99, std::nullopt}));
  CHECK_FALSE(model.Apply(generation, OnlinePlayersReplaced{{ChangeTestPlayer(8), ChangeTestPlayer(8)}}));
  CHECK_FALSE(model.RegisterChannel(1, 20));
  CHECK_FALSE(model.RegisterChannel(2, 0));
  CHECK_FALSE(model.BeginHistory(1, 0));
  auto conflict = ChangeTestMessage();
  conflict.messageText = "Conflict";
  CHECK_FALSE(model.Apply(generation, ChatMessagesReceived{1, {conflict}}));

  model.TakeChanges(changes);
  CheckSinglePlayerChange(changes, 7);
  CHECK(changes.revision == acceptedRevision);
  CHECK_FALSE(model.FindPlayer(8));
  const auto player = model.FindPlayer(7);
  REQUIRE(player);
  CHECK(player->data.displayName == "Accepted");
  model.TakeChanges(changes);
  CHECK(changes.Empty());
}

TEST_CASE("ClientModel full player replacement absorbs individual invalidations")
{
  ClientModel model;
  const auto generation = model.Generation();
  REQUIRE(model.Apply(generation, PlayerUpserted{ChangeTestPlayer(7)}));
  REQUIRE(model.Apply(generation, OnlinePlayersReplaced{{ChangeTestPlayer(2)}}));
  REQUIRE(model.Apply(generation, PlayerUpserted{ChangeTestPlayer(3)}));
  REQUIRE(model.Apply(generation, PlayerRemoved{2}));

  ChangeBatch changes;
  model.TakeChanges(changes);
  CHECK(changes.playersReplaced);
  CHECK_FALSE(changes.requiresSnapshot);
  CHECK(changes.players.empty());
  CHECK(changes.chats.empty());
  CHECK_FALSE(model.FindPlayer(7));
  CHECK_FALSE(model.FindPlayer(2));
  CHECK(model.FindPlayer(3).has_value());

  model.TakeChanges(changes);
  CHECK(changes.Empty());
  REQUIRE(model.Apply(generation, PlayerRemoved{3}));
  model.TakeChanges(changes);
  CheckSinglePlayerChange(changes, 3);
}

TEST_CASE("ClientModel session resets replace pending invalidations and preserve rejection events")
{
  ClientModel model;
  const auto generation = model.Generation();
  REQUIRE(model.RegisterChannel(1, 20));
  REQUIRE(model.Apply(generation, PlayerUpserted{ChangeTestPlayer()}));
  REQUIRE(model.SetSelfPlayer(generation, 7));
  REQUIRE(model.Apply(generation, ChatMessagesReceived{1, {ChangeTestMessage()}}));
  auto round = model.BeginHistory(1);
  REQUIRE(round);
  REQUIRE(model.Apply(generation, ServerRejection{42, RequestRejectionCode::InvalidRequest, "Rejected", "displayName"}));
  bool retainsChats{};

  SUBCASE("disconnect clears online state and keeps chat")
  {
    model.ClearOnlineState();
    retainsChats = true;
  }
  SUBCASE("server switch clears all session data")
  {
    model.ResetSession();
  }

  ChangeBatch changes;
  model.TakeChanges(changes);
  CHECK(changes.requiresSnapshot);
  CHECK_FALSE(changes.Empty());
  CHECK_FALSE(changes.selfPlayerChanged);
  CHECK_FALSE(changes.playersReplaced);
  CHECK(changes.players.empty());
  CHECK(changes.chats.empty());
  CHECK(changes.chatContent.empty());
  CHECK(changes.generation == model.Generation());
  CHECK(changes.generation != generation);
  CHECK(changes.revision == model.Snapshot().revision);
  CHECK_FALSE(model.FindPlayer(7));
  CHECK_FALSE(model.SelfPlayerId());
  const auto chat = model.FindChat(1);
  CHECK(chat.has_value() == retainsChats);
  if (chat)
  {
    CHECK(chat->messages.size() == 1);
    CHECK(chat->history.round == 0);
  }
  const auto rejections = model.TakeServerRejections();
  REQUIRE(rejections.size() == 1);
  CHECK(rejections.front().generation == generation);
  CHECK(rejections.front().rejection.requestId == 42);
  CHECK(model.TakeServerRejections().empty());
  CHECK_FALSE(model.Apply(generation, PlayerUpserted{ChangeTestPlayer()}));
  model.TakeChanges(changes);
  CHECK(changes.Empty());

  REQUIRE(model.Apply(model.Generation(), ServerRejection{43, RequestRejectionCode::InvalidRequest, "Another rejection", ""}));
  model.TakeChanges(changes);
  CHECK(changes.Empty());
  CHECK(changes.revision == model.Snapshot().revision);
  CHECK(model.TakeServerRejections().size() == 1);
}

TEST_CASE("ClientModel emits ordered chat deltas instead of invalidating full chat contents")
{
  ClientModel model;
  ChangeBatch changes;
  REQUIRE(model.RegisterChannel(1, 2));
  model.TakeChanges(changes); // discard registration metadata

  REQUIRE(model.Apply(model.Generation(), ChatMessagesReceived{1, {ChangeTestMessage(1), ChangeTestMessage(2)}}));
  model.TakeChanges(changes);
  CHECK(changes.chats.empty());
  REQUIRE(changes.chatContent.size() == 1);
  REQUIRE(std::holds_alternative<ChatMessagesAdded>(changes.chatContent[0]));
  const auto& initial = std::get<ChatMessagesAdded>(changes.chatContent[0]);
  CHECK(initial.channelId == 1);
  REQUIRE(initial.messages.size() == 2);
  CHECK(initial.messages[0].messageId == 1);
  CHECK(initial.messages[1].messageId == 2);

  REQUIRE(model.Apply(model.Generation(), ChatMessagesReceived{1, {ChangeTestMessage(3)}}));
  model.TakeChanges(changes);
  CHECK(changes.chats.empty());
  REQUIRE(changes.chatContent.size() == 2);
  REQUIRE(std::holds_alternative<ChatMessagesRemoved>(changes.chatContent[0]));
  REQUIRE(std::holds_alternative<ChatMessagesAdded>(changes.chatContent[1]));
  const auto& removed = std::get<ChatMessagesRemoved>(changes.chatContent[0]);
  const auto& added   = std::get<ChatMessagesAdded>(changes.chatContent[1]);
  REQUIRE(removed.messageIds.size() == 1);
  CHECK(removed.messageIds[0] == 1);
  REQUIRE(added.messages.size() == 1);
  CHECK(added.messages[0].messageId == 3);

  const auto state = model.FindChatState(1);
  REQUIRE(state);
  CHECK(state->count == 2);
}

TEST_CASE("Removing a channel discards pending content deltas for that channel")
{
  ClientModel model;
  ChangeBatch changes;
  REQUIRE(model.RegisterChannel(1, 4));
  model.TakeChanges(changes);
  REQUIRE(model.Apply(model.Generation(), ChatMessagesReceived{1, {ChangeTestMessage()}}));
  REQUIRE(model.RemoveChannel(1));

  model.TakeChanges(changes);
  REQUIRE(changes.chats.size() == 1);
  CHECK(changes.chats.front() == 1);
  CHECK(changes.chatContent.empty());
  CHECK_FALSE(model.FindChatState(1));
}

TEST_CASE("ClientModel.TakeChanges replaces the output and recycles its capacities")
{
  ClientModel model;
  const auto generation = model.Generation();
  REQUIRE(model.Apply(generation, PlayerUpserted{ChangeTestPlayer(7)}));
  REQUIRE(model.RegisterChannel(1, 20));

  ChangeBatch output;
  output.players.reserve(64);
  output.chats.reserve(32);
  auto* const reusablePlayers = output.players.data();
  auto* const reusableChats = output.chats.data();
  output.players.push_back(999);
  output.chats.push_back(999);
  output.generation = 999;
  output.revision = 999;
  output.requiresSnapshot = true;
  output.selfPlayerChanged = true;
  output.playersReplaced = true;

  model.TakeChanges(output);
  CHECK(output.generation == generation);
  CHECK_FALSE(output.requiresSnapshot);
  CHECK_FALSE(output.selfPlayerChanged);
  CHECK_FALSE(output.playersReplaced);
  REQUIRE(output.players.size() == 1);
  CHECK(output.players.front() == 7);
  REQUIRE(output.chats.size() == 1);
  CHECK(output.chats.front() == 1);

  REQUIRE(model.Apply(generation, PlayerUpserted{ChangeTestPlayer(8)}));
  REQUIRE(model.RegisterChannel(2, 20));
  model.TakeChanges(output);
  REQUIRE(output.players.size() == 1);
  CHECK(output.players.front() == 8);
  REQUIRE(output.chats.size() == 1);
  CHECK(output.chats.front() == 2);
  CHECK(output.players.data() == reusablePlayers);
  CHECK(output.chats.data() == reusableChats);
  CHECK(output.players.capacity() >= 64);
  CHECK(output.chats.capacity() >= 32);
}

TEST_SUITE_END();
