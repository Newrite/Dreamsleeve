import std;
import Dreamsleeve.Client.Exchange;

namespace
{

  using namespace Dreamsleeve::Client;

  // Deterministic synthetic server. The demo runs entirely on the main thread.
  class StateDemo final
  {
public:

    explicit StateDemo(ClientExchange& exchange) : exchange{exchange} {}

    ~StateDemo()
    {
      exchange.CloseInput();
      Invoke([this](ClientModel& model) {
        while (!awaitingServer.empty())
          Reject(model, "Dev stopped before server acceptance");
        return true;
      });

      exchange.Finish();
    }

    bool Invoke(std::function<bool(ClientModel&)> action)
    {
      const bool pumped    = Pump(model);
      const bool result    = action(model);
      const bool published = exchange.Publish(model);
      return pumped && result && published;
    }

    bool Receive(ClientModel& model, std::string text)
    {
      Domain::ChatMessage message{
          nextMessage++,
          1,
          Domain::PlayerData{7, "dev", "Dev"},
          std::move(text),
          {}
      };

      return model.Apply(model.Generation(), ChatMessagesReceived{1, {message}}).has_value();
    }

    bool Accept(ClientModel& model)
    {
      if (awaitingServer.empty()) return false;

      auto send = std::move(awaitingServer.front());
      awaitingServer.pop_front();

      return Receive(model, std::move(send.text));
    }

    bool Reject(ClientModel& model, std::string reason)
    {
      if (awaitingServer.empty()) return false;

      const auto requestId = awaitingServer.front().requestId;
      awaitingServer.pop_front();

      return exchange.PublishResult({
          model.Generation(),
          requestId,
          ServerRejection{RequestRejectionCode::InvalidRequest, std::move(reason), "text"}
      });
    }

    bool Reset(ClientModel& model)
    {
      while (!awaitingServer.empty())
        Reject(model, "Session reset");

      model.ResetSession();

      const auto     generation = model.Generation();
      Domain::Player self{
          .data = {7, "dev", "Dev"}
      };
      const bool initialized = model.RegisterChannel(1, 3).has_value() &&
                               model.Apply(generation, OnlinePlayersReplaced{{self}}).has_value() &&
                               model.SetSelfPlayer(generation, self.data.playerId).has_value();

      // Invoke publishes only after this handler returns, never between operations.
      if (!initialized) model.ResetSession();

      return initialized;
    }

private:

    bool Pump(ClientModel& model)
    {
      exchange.TakeCommands(commands, awaitingServer.size());

      bool ok = true;
      for (auto& queued : commands)
      {
        if (std::holds_alternative<RequestSnapshot>(queued.command))
        {
          ok = exchange.Publish(model, true) && ok;
          continue;
        }

        if (queued.generation != model.Generation())
        {
          if (const auto* chat = std::get_if<SendChat>(&queued.command))
          {
            const ServerRejection stale{RequestRejectionCode::SessionNotReady, "Stale outgoing generation", "generation"};
            ok = exchange.PublishResult({
                model.Generation(),
                chat->requestId,
                stale
            }) && ok;
          }
          else
          {
            std::cout << "discarded stale local state command\n";
          }
          continue;
        }

        if (auto* chat = std::get_if<SendChat>(&queued.command))
        {
          std::cout << "outbound chat " << chat->requestId << " awaiting server\n";
          awaitingServer.push_back(std::move(*chat));
        }
        else
        {
          std::cout << "outbound game data (no local model mutation)\n";
        }
      }

      return ok;
    }

    ClientExchange&                  exchange;
    ClientModel                      model;

    std::vector<QueuedClientCommand> commands;
    std::deque<SendChat>             awaitingServer;
    Domain::ChatMessageId            nextMessage{1};
  };

  void PrintMessage(const Domain::ChatMessage& message)
  {
    std::cout << "  message " << message.messageId << ": " << message.messageText << '\n';
  }

  void PrintOutput(ClientOutput& output, std::uint64_t& generation)
  {
    std::cout << output.state.updates.size() << " updates\n";
    for (const auto& update : output.state.updates)
    {
      generation = std::visit([](const auto& value) { return value.generation; }, update);
      if (const auto* snapshot = std::get_if<ClientSnapshot>(&update))
      {
        std::cout << " snapshot generation=" << snapshot->generation << " revision=" << snapshot->revision << '\n';
        for (const auto& chat : snapshot->chats)
          for (const auto& message : chat.messages)
            PrintMessage(message);
      }
      else
      {
        const auto& delta = std::get<ClientStateDelta>(update);
        std::cout << " delta generation=" << delta.generation << " revision=" << delta.revision << '\n';
        for (const auto& change : delta.chatContent)
        {
          if (const auto* added = std::get_if<ChatMessagesAdded>(&change))
            for (const auto& message : added->messages)
              PrintMessage(message);
          else if (const auto* evicted = std::get_if<ChatMessagesEvicted>(&change))
            for (const auto id : evicted->messageIds)
              std::cout << "  evicted " << id << '\n';
          else
            for (const auto id : std::get<ChatMessagesDeleted>(change).messageIds)
              std::cout << "  deleted " << id << '\n';
        }
      }
    }

    for (const auto& result : output.results)
      if (const auto* rejection = std::get_if<ServerRejection>(&result.outcome))
        std::cout << " rejection generation=" << result.generation << " request=" << result.requestId << ": " << rejection->message << '\n';

    if (output.status.stopped) std::cout << "owner stopped\n";
  }

  int RunCommands(std::istream& input, bool echo)
  {
    auto created = ClientExchange::TryCreate(8, 2);
    if (!created) return 1;

    auto          exchange = std::move(*created);
    ClientOutput  output;
    std::uint64_t generation{};
    bool          failed{};
    {
      StateDemo owner{*exchange};
      if (!owner.Invoke([&](ClientModel& model) { return owner.Reset(model); })) return 1;

      exchange->Drain(output);
      PrintOutput(output, generation);

      std::string line;
      while (std::getline(input, line))
      {
        if (echo) std::cout << "> " << line << '\n';

        std::istringstream command{line};
        std::string        action;
        command >> action;
        if (action.empty()) continue;
        if (action == "quit") break;

        bool ok = false;
        if (action == "read")
        {
          exchange->Drain(output);
          PrintOutput(output, generation);
          ok = true;
        }
        else if (action == "send" || action == "snapshot" || action == "sample")
        {
          ClientCommand outgoing = RequestSnapshot{};
          if (action == "send")
          {
            std::string text;
            std::getline(command >> std::ws, text);
            if (text.empty())
            {
              failed = true;
              std::cerr << "Empty message\n";
              continue;
            }
            const auto requestId = exchange->NextRequestId();
            if (!requestId)
            {
              failed = true;
              std::cerr << "Request IDs exhausted\n";
              continue;
            }

            outgoing = SendChat{*requestId, 1, std::move(text)};
          }
          else if (action == "sample")
            outgoing = LocalMovement{};

          const auto posted = exchange->Post({generation, std::move(outgoing)});
          if (posted == CommandPostResult::Queued || posted == CommandPostResult::Replaced)
            ok = owner.Invoke([](ClientModel&) { return true; });
        }
        else if (action == "accept")
        {
          ok = owner.Invoke([&](ClientModel& model) { return owner.Accept(model); });
        }
        else if (action == "reject")
        {
          ok = owner.Invoke([&](ClientModel& model) { return owner.Reject(model, "Rejected by synthetic server"); });
        }
        else if (action == "receive")
        {
          std::string text;
          std::getline(command >> std::ws, text);
          if (!text.empty())
            ok = owner.Invoke([&, text = std::move(text)](ClientModel& model) mutable { return owner.Receive(model, std::move(text)); });
        }
        else if (action == "reset")
        {
          ok = owner.Invoke([&](ClientModel& model) { return owner.Reset(model); });
        }

        if (!ok)
        {
          failed = true;
          std::cerr << "Invalid command or arguments: " << line << '\n';
        }
      }
    }

    exchange->Drain(output);
    PrintOutput(output, generation);

    return failed ? 1 : 0;
  }

}

int RunStateConsole(bool demo)
{
  std::cout << "Synthetic state demo on the main thread; no network connection.\n"
            << "send <text> | accept | reject | receive <text> | sample | read | snapshot | reset | quit\n";
  if (!demo) return RunCommands(std::cin, false);

  std::istringstream script{
      "send First\nread\naccept\nread\n"
      "send Refused\nreject\nread\n"
      "receive Second\nreceive Third\nreceive Fourth\nread\n"
      "receive Fifth\nread\nsnapshot\nread\nsample\nreset\nread\nquit\n"
  };

  return RunCommands(script, true);
}
