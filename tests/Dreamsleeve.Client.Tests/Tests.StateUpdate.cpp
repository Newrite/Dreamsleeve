#include <doctest/doctest.h>

import std;
import Dreamsleeve.Client.StateUpdate;

namespace
{

  using namespace Domain;
  using namespace Dreamsleeve::Client;

  Player MakePlayer(PlayerId id)
  {
    Player player;
    player.data = {id, "player", "Display"};
    player.actorValues.emplace(
      "skyrim:health",
      ActorValueInfo{
          "Health",
          ResourceActorValue{90, 100}
    });
    return player;
  }

  ChatMessage Message(ChatMessageId id)
  {
    return {id, 1, MakePlayer(7).data, "Hello", MessageTime{}};
  }

  const ClientStateDelta& Delta(const std::optional<ClientStateUpdate>& update)
  {
    REQUIRE(update);
    REQUIRE(std::holds_alternative<ClientStateDelta>(*update));
    return std::get<ClientStateDelta>(*update);
  }

}

TEST_SUITE_BEGIN("Client.StateUpdate");

TEST_CASE("State publication omits empty changes and leaves rejection notifications available")
{
  ClientModel model;
  ChangeBatch scratch;
  CHECK_FALSE(TakeStateUpdate(model, scratch));
  REQUIRE(model.Apply(model.Generation(), ServerRejection{42, RequestRejectionCode::InvalidRequest, "Rejected", "text"}));

  CHECK_FALSE(TakeStateUpdate(model, scratch));
  const auto rejections = model.TakeServerRejections();
  REQUIRE(rejections.size() == 1);
  CHECK(rejections.front().rejection.message == "Rejected");
}

TEST_CASE("State publication owns player data and distinguishes self removal from no change")
{
  std::optional<ClientStateUpdate> update;
  {
    ClientModel model;
    ChangeBatch scratch;
    REQUIRE(model.Apply(model.Generation(), PlayerUpserted{MakePlayer(7)}));
    REQUIRE(model.SetSelfPlayer(model.Generation(), 7));
    update = TakeStateUpdate(model, scratch);
    CHECK_FALSE(TakeStateUpdate(model, scratch));
    REQUIRE(model.Apply(
      model.Generation(),
      PlayerProfileUpdated{
          7,
          {7, "player", "Renamed"}
    }));
    REQUIRE(model.SetSelfPlayer(model.Generation(), std::nullopt));

    const auto  next  = TakeStateUpdate(model, scratch);
    const auto& delta = Delta(next);
    CHECK(delta.selfPlayerChanged);
    CHECK_FALSE(delta.selfPlayerId);
    REQUIRE(delta.players.size() == 1);
    CHECK(delta.players.front().data.displayName == "Renamed");
    CHECK(delta.revision == model.Snapshot().revision);
    CHECK(delta.generation == model.Generation());
  }

  const auto& delta = Delta(update);  // Model has been destroyed.
  CHECK(delta.selfPlayerChanged);
  CHECK(delta.selfPlayerId == std::optional<PlayerId>{7});
  REQUIRE(delta.players.size() == 1);
  CHECK(delta.players.front().data.displayName == "Display");
  CHECK(ActorValues::GetCurrent(delta.players.front().actorValues.at("skyrim:health").state) == 90);
}

TEST_CASE("State publication resolves removals and complete online replacements")
{
  ClientModel model;
  ChangeBatch scratch;
  REQUIRE(model.Apply(model.Generation(), PlayerUpserted{MakePlayer(7)}));
  REQUIRE(model.Apply(model.Generation(), PlayerUpserted{MakePlayer(8)}));
  TakeStateUpdate(model, scratch);

  REQUIRE(model.Apply(model.Generation(), PlayerRemoved{7}));
  auto update = TakeStateUpdate(model, scratch);
  CHECK(Delta(update).players.empty());
  CHECK(Delta(update).removedPlayers == std::vector<PlayerId>{7});
  CHECK_FALSE(Delta(update).playersReplaced);
  CHECK_FALSE(Delta(update).selfPlayerChanged);

  REQUIRE(model.Apply(
    model.Generation(),
    OnlinePlayersReplaced{
        {MakePlayer(10), MakePlayer(9)}
  }));
  REQUIRE(model.Apply(model.Generation(), PlayerRemoved{10}));
  update                  = TakeStateUpdate(model, scratch);
  const auto& replacement = Delta(update);
  CHECK(replacement.playersReplaced);
  REQUIRE(replacement.players.size() == 1);
  CHECK(replacement.players.front().data.playerId == 9);
  CHECK(replacement.removedPlayers.empty());

  REQUIRE(model.Apply(model.Generation(), OnlinePlayersReplaced{{}}));
  update = TakeStateUpdate(model, scratch);
  CHECK(Delta(update).playersReplaced);
  CHECK(Delta(update).players.empty());  // An empty replacement still clears online.
}

TEST_CASE("State publication forwards owned chat deltas without rereading full history")
{
  ClientModel model;
  ChangeBatch scratch;
  REQUIRE(model.RegisterChannel(1, 2));
  REQUIRE(model.Apply(
    model.Generation(),
    ChatMessagesReceived{
        1,
        {Message(1), Message(2)}
  }));
  auto        initial      = TakeStateUpdate(model, scratch);
  const auto& initialDelta = Delta(initial);
  REQUIRE(initialDelta.chats.size() == 1);
  CHECK(initialDelta.chats.front().resetContent);
  REQUIRE(initialDelta.chats.front().state);
  CHECK(initialDelta.chats.front().state->count == 2);

  REQUIRE(model.Apply(model.Generation(), ChatMessagesReceived{1, {Message(3)}}));
  auto        update = TakeStateUpdate(model, scratch);
  const auto& delta  = Delta(update);
  CHECK(delta.chats.empty());  // Ordinary messages carry only content changes.
  REQUIRE(delta.chatContent.size() == 2);
  REQUIRE(std::holds_alternative<ChatMessagesRemoved>(delta.chatContent[0]));
  CHECK(std::get<ChatMessagesRemoved>(delta.chatContent[0]).messageIds == std::vector<ChatMessageId>{1});
  REQUIRE(std::holds_alternative<ChatMessagesAdded>(delta.chatContent[1]));
  CHECK(std::get<ChatMessagesAdded>(delta.chatContent[1]).messages == std::vector<ChatMessage>{Message(3)});

  REQUIRE(model.Apply(model.Generation(), ChatMessagesReceived{1, {Message(3)}}));
  CHECK_FALSE(TakeStateUpdate(model, scratch));  // Duplicate produces no state payload.
  model.ResetSession();
  REQUIRE(initialDelta.chatContent.size() == 1);
  CHECK(std::get<ChatMessagesAdded>(initialDelta.chatContent[0]).messages == std::vector<ChatMessage>{Message(1), Message(2)});
}

TEST_CASE("State publication distinguishes channel reincarnation from history metadata")
{
  ClientModel model;
  ChangeBatch scratch;
  REQUIRE(model.RegisterChannel(1, 4));
  REQUIRE(model.Apply(model.Generation(), ChatMessagesReceived{1, {Message(1)}}));
  TakeStateUpdate(model, scratch);  // The receiver already has this channel/history.
  REQUIRE(model.BeginHistory(1));
  auto update = TakeStateUpdate(model, scratch);
  REQUIRE(Delta(update).chats.size() == 1);
  CHECK_FALSE(Delta(update).chats.front().resetContent);
  CHECK(Delta(update).chatContent.empty());

  REQUIRE(model.RemoveChannel(1));
  REQUIRE(model.RegisterChannel(1, 2));
  REQUIRE(model.Apply(model.Generation(), ChatMessagesReceived{1, {Message(2)}}));
  update            = TakeStateUpdate(model, scratch);
  const auto& delta = Delta(update);
  REQUIRE(delta.chats.size() == 1);
  REQUIRE(delta.chats.front().state);
  CHECK(delta.chats.front().channelId == 1);
  CHECK(delta.chats.front().resetContent);  // Clear the previously published message 1.
  CHECK(delta.chats.front().state->capacity == 2);
  REQUIRE(delta.chatContent.size() == 1);
  CHECK(std::get<ChatMessagesAdded>(delta.chatContent[0]).messages == std::vector<ChatMessage>{Message(2)});

  REQUIRE(model.RemoveChannel(1));
  update = TakeStateUpdate(model, scratch);
  REQUIRE(Delta(update).chats.size() == 1);
  CHECK_FALSE(Delta(update).chats.front().state);
  CHECK(Delta(update).chatContent.empty());
}

TEST_CASE("State publication returns a complete detached snapshot at a session boundary")
{
  ClientModel model;
  ChangeBatch scratch;
  REQUIRE(model.RegisterChannel(1, 4));
  REQUIRE(model.Apply(model.Generation(), ChatMessagesReceived{1, {Message(1)}}));
  REQUIRE(model.Apply(model.Generation(), PlayerUpserted{MakePlayer(7)}));
  bool retainsChat{};

  SUBCASE("disconnect keeps accepted history")
  {
    model.ClearOnlineState();
    retainsChat = true;
  }
  SUBCASE("server switch discards history")
  {
    model.ResetSession();
  }

  const auto update = TakeStateUpdate(model, scratch);
  REQUIRE(update);
  REQUIRE(std::holds_alternative<ClientSnapshot>(*update));
  const auto& snapshot = std::get<ClientSnapshot>(*update);
  CHECK(snapshot.generation == model.Generation());
  CHECK(snapshot.revision == model.Snapshot().revision);
  CHECK(snapshot.players.empty());
  CHECK(snapshot.chats.size() == (retainsChat ? 1 : 0));
  model.ResetSession();
  if (retainsChat) CHECK(snapshot.chats.front().messages == std::vector<ChatMessage>{Message(1)});
}

TEST_SUITE_END();
