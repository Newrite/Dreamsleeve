#include <doctest/doctest.h>
import std;
import Dreamsleeve.Client.Exchange;

#include "Results.h"

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
        Domain::PlayerData{7, "player", "Display"},
        std::to_string(id),
        {}
    };
    REQUIRE(model.Apply(model.Generation(), ChatMessagesReceived{1, {message}}));
  }

  void Initialize(ClientExchange& exchange, ClientModel& model)
  {
    REQUIRE(model.RegisterChannel(1, 3));
    REQUIRE(exchange.Publish(model));
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
  REQUIRE(exchange->Publish(model));
  ClientOutput output;
  exchange->Drain(output);
  CHECK(
    exchange->Post({
        model.Generation(),
        SendChat{42, 1, "Outgoing"}
  }) == CommandPostResult::Queued);
  REQUIRE(exchange->Publish(model));
  exchange->Drain(output);
  CHECK(output.state.updates.empty());
  REQUIRE(model.FindChatState(1)->count == 1);
  Receive(model, 2);
  REQUIRE(exchange->Publish(model));
  exchange->Drain(output);
  REQUIRE(output.state.updates.size() == 1);
  REQUIRE(std::holds_alternative<ClientStateDelta>(output.state.updates[0]));
  const auto& delta = std::get<ClientStateDelta>(output.state.updates[0]);
  REQUIRE(delta.chatContent.size() == 1);
  const auto& messages = std::get<ChatMessagesAdded>(delta.chatContent[0]).messages;
  REQUIRE(messages.size() == 1);
  CHECK(messages[0].messageId == 2);  // No copy of existing history in the payload.
  model.ResetSession();
  REQUIRE(exchange->Publish(model));
  CHECK(messages[0].messageId == 2);  // Already drained data is independent.
}

TEST_CASE("Local samples replace only adjacent samples of the same generation")
{
  auto             exchange = Exchange(4);
  LocalMovement first;
  first.location = Domain::PlayerLocation{};
  LocalMovement second;
  second.location = Domain::PlayerLocation{};
  second.location->position.X = 2;
  second.location->sampledAtUs = 12345;
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
  CHECK(std::get<LocalMovement>(commands[0].command).location == second.location);
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
  REQUIRE(exchange->Publish(model, true));
  ClientOutput output;
  exchange->Drain(output);
  REQUIRE(output.state.updates.size() == 1);
  const auto& snapshot = std::get<ClientSnapshot>(output.state.updates[0]);
  CHECK(snapshot.chats[0].messages == model.Snapshot().chats[0].messages);
  REQUIRE(exchange->Publish(model));
  exchange->Drain(output);
  CHECK(output.state.updates.empty());
}

TEST_CASE("State overflow and reset preserve results while recovering bounded chat history")
{
  auto        exchange = Exchange(4, 1);
  ClientModel model;
  Initialize(*exchange, model);
  const auto generation = model.Generation();
  REQUIRE(exchange->PublishResult({generation, 42, ServerRejection{RequestRejectionCode::InvalidRequest, "Rejected", "text"}}));
  for (Domain::ChatMessageId id = 1; id <= 4; ++id)
  {
    Receive(model, id);
    REQUIRE(exchange->Publish(model));
  }
  ClientOutput output;
  exchange->Drain(output);
  REQUIRE(output.state.updates.size() == 1);
  REQUIRE(std::holds_alternative<ClientSnapshot>(output.state.updates[0]));
  const auto& snapshot = std::get<ClientSnapshot>(output.state.updates[0]);
  CHECK(snapshot.chats[0].messages == model.Snapshot().chats[0].messages);
  REQUIRE(ResultsOf<ServerRejection>(output).size() == 1);
  CHECK(ResultsOf<ServerRejection>(output)[0].requestId == 42);
  REQUIRE(exchange->PublishResult({generation, 43, ServerRejection{RequestRejectionCode::InvalidRequest, "Old session", ""}}));
  model.ResetSession();
  REQUIRE(exchange->Publish(model));
  exchange->Drain(output);
  REQUIRE(ResultsOf<ServerRejection>(output).size() == 1);
  CHECK(ResultsOf<ServerRejection>(output)[0].generation == generation);
  CHECK(std::get<ClientSnapshot>(output.state.updates[0]).generation == model.Generation());
  exchange->Drain(output);
  CHECK(ResultsOf<ServerRejection>(output).empty());
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
      applied = exchange->Publish(model) && applied;
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
            Domain::PlayerData{7, "player", "Display"},
            send.text,
            {}
        };
        applied = applied && model.Apply(commands[0].generation, ChatMessagesReceived{1, {message}}).has_value();
      }
      else
        applied = false;
      exhausted = !exchange->TakeCommands(commands);
      applied = exchange->Publish(model) && applied;
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
  CHECK(final.status.stopped);
  REQUIRE(initial.state.updates.size() == 1);
  CHECK(std::holds_alternative<ClientSnapshot>(initial.state.updates[0]));
  REQUIRE(final.state.updates.size() == 1);
  const auto& delta = std::get<ClientStateDelta>(final.state.updates[0]);
  CHECK(std::get<ChatMessagesAdded>(delta.chatContent[0]).messages[0].messageText == "From consumer");
  CHECK(exchange->Post({1, RequestSnapshot{}}) == CommandPostResult::Closed);
}

TEST_CASE("Shared request IDs survive model generation changes")
{
  auto exchange = Exchange();
  const auto owner = exchange->NextRequestId();
  const auto producer = exchange->NextRequestId();
  REQUIRE(owner);
  REQUIRE(producer);
  CHECK(*owner != 0);
  CHECK(*producer > *owner);

  ClientModel model;
  REQUIRE(exchange->Publish(model));
  model.ResetSession();
  REQUIRE(exchange->Publish(model));
  const auto reconnect = exchange->NextRequestId();
  REQUIRE(reconnect);
  CHECK(*reconnect > *producer);
}

TEST_CASE("Undrained local failures backpressure commands within the configured capacity")
{
  auto exchange = Exchange(2);
  REQUIRE(exchange->Post({1, SendChat{1, 1, "first"}}) == CommandPostResult::Queued);
  REQUIRE(exchange->Post({1, SendChat{2, 1, "second"}}) == CommandPostResult::Queued);
  std::vector<QueuedClientCommand> commands;
  REQUIRE(exchange->TakeCommands(commands));
  REQUIRE(commands.size() == 2);
  CHECK(exchange->PublishResult({1, 1, CommandFailureCode::SessionNotReady}));
  CHECK(exchange->PublishResult({1, 2, CommandFailureCode::SessionNotReady}));
  CHECK_FALSE(exchange->PublishResult({1, 3, CommandFailureCode::SessionNotReady}));

  REQUIRE(exchange->Post({1, SendChat{3, 1, "third"}}) == CommandPostResult::Queued);
  exchange->CloseInput();
  CHECK(exchange->TakeCommands(commands));
  CHECK(commands.empty());

  ClientOutput output;
  exchange->Drain(output);
  REQUIRE(ResultsOf<CommandFailureCode>(output).size() == 2);
  CHECK(exchange->TakeCommands(commands));
  REQUIRE(commands.size() == 1);
  CHECK(std::get<SendChat>(commands.front().command).requestId == 3);
  CHECK_FALSE(exchange->TakeCommands(commands));
}

TEST_CASE("In-flight replies and both outcome kinds share one bounded budget")
{
  auto exchange = Exchange(2);
  ClientModel model;
  REQUIRE(exchange->Post({1, SendChat{2, 1, "invalid"}}) == CommandPostResult::Queued);
  REQUIRE(exchange->Post({1, SendChat{3, 1, "waiting"}}) == CommandPostResult::Queued);

  std::vector<QueuedClientCommand> commands;
  REQUIRE(exchange->TakeCommands(commands, 1));
  REQUIRE(commands.size() == 1);
  REQUIRE(exchange->PublishResult({1, 2, CommandFailureCode::InvalidRequest}));
  REQUIRE(exchange->TakeCommands(commands, 1));
  CHECK(commands.empty());

  const CommandResult busy{model.Generation(), 1, ServerRejection{RequestRejectionCode::Overloaded, "Busy", ""}};
  REQUIRE(exchange->Publish(model, false, std::nullopt, {}, busy));
  CHECK_FALSE(exchange->CanAcceptReplies());
  CHECK_FALSE(exchange->PublishResult({1, 3, CommandFailureCode::Busy}));
  REQUIRE(exchange->TakeCommands(commands));
  CHECK(commands.empty());

  ClientOutput output;
  exchange->Drain(output);
  REQUIRE(ResultsOf<CommandFailureCode>(output).size() == 1);
  REQUIRE(ResultsOf<ServerRejection>(output).size() == 1);
  CHECK(ResultsOf<ServerRejection>(output).front().requestId == 1);
  REQUIRE(exchange->TakeCommands(commands));
  REQUIRE(commands.size() == 1);
  CHECK(std::get<SendChat>(commands.front().command).requestId == 3);
}

TEST_CASE("Result overflow is explicit while the terminal state still publishes")
{
  auto exchange = Exchange(1);
  ClientModel model;
  Initialize(*exchange, model);
  const auto generation = model.Generation();
  REQUIRE(exchange->PublishResult({generation, 1, CommandFailureCode::Busy}));
  model.ResetSession();

  const CommandResult late{generation, 2, ServerRejection{RequestRejectionCode::Overloaded, "Busy", ""}};
  CHECK_FALSE(exchange->Publish(model, true, SessionPhase::Faulted, {}, late));
  ClientOutput output;
  exchange->Drain(output);
  CHECK(output.status.phase == SessionPhase::Faulted);
  REQUIRE(output.state.updates.size() == 1);
  CHECK(std::get<ClientSnapshot>(output.state.updates.front()).players.empty());
  REQUIRE(output.results.size() == 1);
  CHECK(ResultsOf<CommandFailureCode>(output).front().requestId == 1);
}


TEST_CASE("Movement coalescing preserves space changes and explicit reliable boundaries")
{
  auto exchange = Exchange(8);
  Domain::PlayerLocation first{{{"skyrim.esm", 1}, "A"}};
  auto next = first;
  next.position.X = 10;
  REQUIRE(exchange->Post({1, LocalMovement{first}}) == CommandPostResult::Queued);
  REQUIRE(exchange->Post({1, LocalMovement{next}}) == CommandPostResult::Replaced);
  next.location.locationId.localFormId = 2;
  REQUIRE(exchange->Post({1, LocalMovement{next}}) == CommandPostResult::Queued);
  REQUIRE(exchange->Post({1, LocalLocation{next}}) == CommandPostResult::Queued);
  REQUIRE(exchange->Post({1, LocalMovement{next}}) == CommandPostResult::Queued);
  REQUIRE(exchange->Post({1, GameExited{}}) == CommandPostResult::Queued);
  std::vector<QueuedClientCommand> commands;
  CHECK(exchange->TakeCommands(commands));
  REQUIRE(commands.size() == 5);
  CHECK(std::get<LocalMovement>(commands[0].command).location->position.X == 10);
  CHECK(std::holds_alternative<LocalLocation>(commands[2].command));
  CHECK(std::holds_alternative<GameExited>(commands[4].command));
}

TEST_CASE("Lifecycle admission and cancellation are independent of full game and reply queues")
{
  auto exchange = Exchange(1, 1);
  REQUIRE(exchange->Post({0, RequestSnapshot{}}) == CommandPostResult::Queued);
  REQUIRE(exchange->PublishResult({0, 1, CommandFailureCode::SessionNotReady}));
  REQUIRE(exchange->PostLogin({"player", "password-value"}));
  CHECK_FALSE(exchange->PostLogin({"other", "password-value"}));
  auto control = exchange->TakeControl();
  REQUIRE(control.authentication);
  CHECK(exchange->Status().authenticating);

  exchange->RequestDisconnect();
  CHECK(exchange->AuthenticationCanceled());
  CHECK(exchange->TakeControl().disconnect);
  CHECK_FALSE(exchange->PostLogin({"other", "password-value"}));
  exchange->CompleteAuthentication("late failure from canceled HTTP");
  CHECK(exchange->Status().error.empty());
  REQUIRE(exchange->PostLogin({"player", "new-password"}));
  CHECK_FALSE(exchange->AuthenticationCanceled());

  exchange->RequestStop();
  CHECK(exchange->StopRequested());
  CHECK(exchange->AuthenticationCanceled());
  CHECK_FALSE(exchange->TakeControl().authentication);
  CHECK_FALSE(exchange->PostLogin({"player", "new-password"}));
  exchange->Finish();
  ClientOutput output;
  exchange->Drain(output);
  CHECK(output.status.stopped);
  CHECK_FALSE(output.status.authenticating);
  CHECK(ResultsOf<CommandFailureCode>(output).size() == 1);
}

TEST_CASE("Network status and drained status are one shared publication")
{
  auto exchange = Exchange();
  REQUIRE(exchange->PostLogin({"player", "password-value"}));
  REQUIRE(exchange->TakeControl().authentication);
  exchange->PublishPhase(SessionPhase::Connecting);
  exchange->CompleteAuthentication();
  exchange->PublishError("transport failure");
  exchange->PublishPhase(SessionPhase::Faulted);
  ClientOutput output;
  exchange->Drain(output);
  CHECK(output.status.phase == exchange->Status().phase);
  CHECK(output.status.error == exchange->Status().error);
  CHECK_FALSE(output.status.authenticating);
  REQUIRE(exchange->PostLogin({"player", "password-value"}));
  CHECK(exchange->Status().error.empty());
}

TEST_CASE("Main thread drains coherent phase and snapshots during concurrent owner publication")
{
  auto exchange = Exchange(2, 1);
  std::atomic_bool accepted{true};
  std::jthread owner{[&] {
    ClientModel model;
    for (int i = 0; i < 256; ++i)
    {
      model.ResetSession();
      const auto phase = model.Generation() % 2 == 0 ? SessionPhase::Ready : SessionPhase::Disconnected;
      if (!exchange->Publish(model, true, phase)) accepted = false;
    }
    exchange->Finish();
  }};
  ClientOutput output;
  const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds{2};
  do
  {
    exchange->Drain(output);
    for (const auto& update : output.state.updates)
    {
      REQUIRE(std::holds_alternative<ClientSnapshot>(update));
      const auto generation = std::get<ClientSnapshot>(update).generation;
      CHECK(output.status.phase == (generation % 2 == 0 ? SessionPhase::Ready : SessionPhase::Disconnected));
    }
    std::this_thread::yield();
  } while (!output.status.stopped && std::chrono::steady_clock::now() < deadline);
  CHECK(output.status.stopped);
  CHECK(accepted);
}

TEST_CASE("Results reserve bounded reply slots until the main thread drains")
{
  auto exchange = Exchange(1, 1);
  ClientModel model;
  REQUIRE(exchange->Publish(model, true, SessionPhase::Ready, "Test", CommandResult{model.Generation(), 1, MessagePublished{42}}));
  CHECK_FALSE(exchange->CanAcceptReplies());
  CHECK_FALSE(exchange->PublishResult({model.Generation(), 2, CommandFailureCode::Busy}));

  ClientOutput output;
  exchange->Drain(output);
  REQUIRE(ResultsOf<MessagePublished>(output).size() == 1);
  CHECK(ResultsOf<MessagePublished>(output)[0].value.messageId == 42);
  CHECK(output.status.serverName == "Test");
  CHECK(exchange->CanAcceptReplies());
  exchange->Drain(output);
  CHECK(ResultsOf<MessagePublished>(output).empty());
}

TEST_SUITE_END();
