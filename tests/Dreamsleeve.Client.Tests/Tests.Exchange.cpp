#include <doctest/doctest.h>
import std;
import Dreamsleeve.Client.Exchange;

namespace
{

  using namespace Dreamsleeve::Client;

  ClientExchange::Ptr Exchange(std::size_t commands = 4, std::size_t states = 4)
  {
    auto result = ClientExchange::TryCreate(commands, states);
    REQUIRE(result);
    return std::move(*result);
  }

  void Receive(ClientModel& model, Domain::ChatMessageId id)
  {
    Domain::ChatMessage message{
        id,
        1,
        {7, "player", "Display"},
        std::to_string(id),
        {}
    };
    REQUIRE(model.Apply(model.Generation(), ChatMessagesReceived{1, {message}}));
  }

  void Initialize(ClientExchange& exchange, ClientModel& model)
  {
    REQUIRE(model.RegisterChannel(1, 3));
    exchange.Publish(model);
    ClientOutput output;
    exchange.Drain(output);
    REQUIRE(output.state.updates.size() == 1);
    REQUIRE(std::holds_alternative<ClientSnapshot>(output.state.updates[0]));
  }

}

TEST_SUITE_BEGIN("Client.Exchange");

TEST_CASE("Command admission reports full and closed without losing admitted FIFO commands")
{
  CHECK_FALSE(ClientExchange::TryCreate(0, 1));
  CHECK_FALSE(ClientExchange::TryCreate(1, 0));
  auto exchange = Exchange(2);
  CHECK(
    exchange->Post({
        1,
        SendChat{1, 1, "First"}
  }) == CommandPostResult::Queued);
  CHECK(
    exchange->Post({
        1,
        SendChat{2, 1, "Second"}
  }) == CommandPostResult::Queued);
  CHECK(
    exchange->Post({
        1,
        SendChat{3, 1, "Third"}
  }) == CommandPostResult::Full);
  exchange->CloseInput();
  CHECK(exchange->Post({1, RequestSnapshot{}}) == CommandPostResult::Closed);
  std::vector<QueuedClientCommand> commands;
  CHECK(exchange->TakeCommands(commands));
  REQUIRE(commands.size() == 2);
  CHECK(std::get<SendChat>(commands[0].command).text == "First");
  CHECK(std::get<SendChat>(commands[1].command).text == "Second");
  CHECK_FALSE(exchange->TakeCommands(commands));
  CHECK(commands.empty());
}

TEST_CASE("Posting chat does not mutate history and accepted messages are delivered only as deltas")
{
  auto        exchange = Exchange();
  ClientModel model;
  Initialize(*exchange, model);
  Receive(model, 1);
  exchange->Publish(model);
  ClientOutput output;
  exchange->Drain(output);
  CHECK(
    exchange->Post({
        model.Generation(),
        SendChat{42, 1, "Outgoing"}
  }) == CommandPostResult::Queued);
  exchange->Publish(model);
  exchange->Drain(output);
  CHECK(output.state.updates.empty());
  REQUIRE(model.FindChat(1)->messages.size() == 1);
  Receive(model, 2);
  exchange->Publish(model);
  exchange->Drain(output);
  REQUIRE(output.state.updates.size() == 1);
  REQUIRE(std::holds_alternative<ClientStateDelta>(output.state.updates[0]));
  const auto& delta = std::get<ClientStateDelta>(output.state.updates[0]);
  REQUIRE(delta.chatContent.size() == 1);
  const auto& messages = std::get<ChatMessagesAdded>(delta.chatContent[0]).messages;
  REQUIRE(messages.size() == 1);
  CHECK(messages[0].messageId == 2);  // No copy of existing history in the payload.
  model.ResetSession();
  exchange->Publish(model);
  CHECK(messages[0].messageId == 2);  // Already drained data is independent.
}

TEST_CASE("Local samples replace only adjacent samples of the same generation")
{
  auto             exchange = Exchange(4);
  LocalPlayerState first;
  first.actorValues.emplace("level", Domain::ActorValueInfo{"Level", Domain::ScalarActorValue{1}});
  LocalPlayerState second;
  second.actorValues.emplace("level", Domain::ActorValueInfo{"Level", Domain::ScalarActorValue{2}});
  CHECK(exchange->Post({1, first}) == CommandPostResult::Queued);
  CHECK(exchange->Post({1, second}) == CommandPostResult::Replaced);
  CHECK(exchange->Post({1, CharacterStarted{"New"}}) == CommandPostResult::Queued);
  CHECK(exchange->Post({1, first}) == CommandPostResult::Queued);
  CHECK(exchange->Post({2, second}) == CommandPostResult::Queued);
  CHECK(exchange->Post({2, first}) == CommandPostResult::Replaced);
  CHECK(exchange->Post({2, GameExited{}}) == CommandPostResult::Full);
  std::vector<QueuedClientCommand> commands;
  exchange->TakeCommands(commands);
  REQUIRE(commands.size() == 4);
  CHECK(std::get<LocalPlayerState>(commands[0].command).actorValues == second.actorValues);
  CHECK(std::holds_alternative<CharacterStarted>(commands[1].command));
  CHECK(commands[2].generation == 1);
  CHECK(commands[3].generation == 2);
}

TEST_CASE("Explicit snapshot consumes included changes and an idle pump emits nothing")
{
  auto        exchange = Exchange();
  ClientModel model;
  Initialize(*exchange, model);
  Receive(model, 1);
  exchange->Publish(model, true);
  ClientOutput output;
  exchange->Drain(output);
  REQUIRE(output.state.updates.size() == 1);
  const auto& snapshot = std::get<ClientSnapshot>(output.state.updates[0]);
  CHECK(snapshot.chats[0].messages == model.FindChat(1)->messages);
  exchange->Publish(model);
  exchange->Drain(output);
  CHECK(output.state.updates.empty());
}

TEST_CASE("State overflow and reset preserve rejections while recovering bounded chat history")
{
  auto        exchange = Exchange(4, 1);
  ClientModel model;
  Initialize(*exchange, model);
  const auto generation = model.Generation();
  REQUIRE(model.Apply(generation, ServerRejection{42, RequestRejectionCode::InvalidRequest, "Rejected", "text"}));
  for (Domain::ChatMessageId id = 1; id <= 4; ++id)
  {
    Receive(model, id);
    exchange->Publish(model);
  }
  ClientOutput output;
  exchange->Drain(output);
  REQUIRE(output.state.updates.size() == 1);
  REQUIRE(std::holds_alternative<ClientSnapshot>(output.state.updates[0]));
  const auto& snapshot = std::get<ClientSnapshot>(output.state.updates[0]);
  CHECK(snapshot.chats[0].messages == model.FindChat(1)->messages);
  REQUIRE(output.rejections.size() == 1);
  CHECK(output.rejections[0].rejection.requestId == 42);
  REQUIRE(model.Apply(generation, ServerRejection{43, RequestRejectionCode::InvalidRequest, "Old session", ""}));
  model.ResetSession();
  exchange->Publish(model);
  exchange->Drain(output);
  REQUIRE(output.rejections.size() == 1);
  CHECK(output.rejections[0].generation == generation);
  CHECK(std::get<ClientSnapshot>(output.state.updates[0]).generation == model.Generation());
  exchange->Drain(output);
  CHECK(output.rejections.empty());
}

TEST_CASE("Owner receives commands and publishes final output before joined shutdown")
{
  auto              exchange = Exchange();
  std::barrier      phase{2};
  bool              applied{}, accepted{}, exhausted{};
  ClientOutput      initial, final;
  CommandPostResult posted{};
  {
    std::jthread owner{[&] {
      ClientModel model;
      applied = model.RegisterChannel(1, 3).has_value();
      exchange->Publish(model);
      phase.arrive_and_wait();
      phase.arrive_and_wait();
      std::vector<QueuedClientCommand> commands;
      accepted = exchange->TakeCommands(commands);
      if (commands.size() == 1 && std::holds_alternative<SendChat>(commands[0].command))
      {
        const auto&         send = std::get<SendChat>(commands[0].command);
        Domain::ChatMessage message{
            1,
            1,
            {7, "player", "Display"},
            send.text,
            {}
        };
        applied = applied && model.Apply(commands[0].generation, ChatMessagesReceived{1, {message}}).has_value();
      }
      else
        applied = false;
      exhausted = !exchange->TakeCommands(commands);
      exchange->Publish(model);
      exchange->Finish();
    }};
    phase.arrive_and_wait();
    exchange->Drain(initial);
    posted = exchange->Post({
        1,
        SendChat{1, 1, "From consumer"}
    });
    exchange->CloseInput();
    phase.arrive_and_wait();
  }
  exchange->Drain(final);
  CHECK(posted == CommandPostResult::Queued);
  CHECK(applied);
  CHECK(accepted);
  CHECK(exhausted);
  CHECK(final.stopped);
  REQUIRE(initial.state.updates.size() == 1);
  CHECK(std::holds_alternative<ClientSnapshot>(initial.state.updates[0]));
  REQUIRE(final.state.updates.size() == 1);
  const auto& delta = std::get<ClientStateDelta>(final.state.updates[0]);
  CHECK(std::get<ChatMessagesAdded>(delta.chatContent[0]).messages[0].messageText == "From consumer");
  CHECK(exchange->Post({1, RequestSnapshot{}}) == CommandPostResult::Closed);
}

TEST_SUITE_END();
