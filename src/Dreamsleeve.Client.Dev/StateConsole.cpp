import std;
import Dreamsleeve.Client.Exchange;

namespace
{

  using namespace Dreamsleeve::Client;

  // Dev-only synthetic server and scheduling harness. Every model access occurs
  // on worker; Invoke waits only to make console commands/demo reproducible.
  class DevOwner final
  {
public:

    explicit DevOwner(ClientExchange& exchange) : exchange{exchange}, worker{[this](std::stop_token stop) { Run(stop); }} {}

    ~DevOwner()
    {
      exchange.CloseInput();
      Invoke([this](ClientModel& model) {
        while (!awaitingServer.empty())
          Reject(model, "Dev stopped before server acceptance");
        return true;
      });

      worker.request_stop();
      wake.notify_one();
      worker.join();
    }

    bool Invoke(std::function<bool(ClientModel&)> action)
    {
      std::packaged_task<bool(ClientModel&)> task{[this, action = std::move(action)](ClientModel& model) {
        const bool pumped = Pump(model);
        const bool result = action(model);
        exchange.Publish(model);
        return pumped && result;
      }};

      auto completion = task.get_future();
      {
        std::lock_guard lock{mutex};
        pending.emplace(std::move(task));
      }

      wake.notify_one();
      return completion.get();
    }

    bool Receive(ClientModel& model, std::string text)
    {
      Domain::ChatMessage message{
          nextMessage++,
          1,
          {7, "dev", "Dev"},
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

      return model.Apply(model.Generation(), ServerRejection{requestId, RequestRejectionCode::InvalidRequest, std::move(reason), "text"})
        .has_value();
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
      exchange.TakeCommands(commands);

      bool ok = true;
      for (auto& queued : commands)
      {
        if (std::holds_alternative<RequestSnapshot>(queued.command))
        {
          exchange.Publish(model, true);
          continue;
        }

        if (queued.generation != model.Generation())
        {
          if (const auto* chat = std::get_if<SendChat>(&queued.command))
          {
            ok = model
                   .Apply(
                     model.Generation(),
                     ServerRejection{chat->requestId, RequestRejectionCode::SessionNotReady, "Stale outgoing generation", "generation"})
                   .has_value() &&
                 ok;
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

    void Run(std::stop_token stop)
    {
      ClientModel model;

      for (;;)
      {
        std::optional<std::packaged_task<bool(ClientModel&)>> task;
        {
          std::unique_lock lock{mutex};
          wake.wait(lock, stop, [&] { return pending.has_value(); });
          if (!pending) break;
          task.swap(pending);
        }

        (*task)(model);
      }

      exchange.Finish();
    }

    ClientExchange&                                       exchange;
    std::mutex                                            mutex;
    std::condition_variable_any                           wake;
    std::optional<std::packaged_task<bool(ClientModel&)>> pending;
    std::vector<QueuedClientCommand>                      commands;
    std::deque<SendChat>                                  awaitingServer;
    Domain::ChatMessageId                                 nextMessage{1};
    std::jthread                                          worker;
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
          else
            for (const auto id : std::get<ChatMessagesRemoved>(change).messageIds)
              std::cout << "  removed " << id << '\n';
        }
      }
    }

    for (const auto& event : output.rejections)
      std::cout << " rejection generation=" << event.generation << " request=" << event.rejection.requestId << ": "
                << event.rejection.message << '\n';

    if (output.stopped) std::cout << "owner stopped and joined\n";
  }

  int RunCommands(std::istream& input, bool echo)
  {
    auto created = ClientExchange::TryCreate(8, 2);
    if (!created) return 1;

    auto          exchange = std::move(*created);
    ClientOutput  output;
    std::uint64_t generation{};
    std::uint64_t nextRequest{1};
    bool          failed{};
    {
      DevOwner owner{*exchange};
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
            outgoing = SendChat{nextRequest++, 1, std::move(text)};
          }
          else if (action == "sample")
            outgoing = LocalPlayerState{};

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
  std::cout << "One consumer, one model owner thread. Synthetic server; no network connection.\n"
            << "send <text> | accept | reject | receive <text> | sample | read | snapshot | reset | quit\n";
  if (!demo) return RunCommands(std::cin, false);

  std::istringstream script{
      "send First\nread\naccept\nread\n" "send Refused\nreject\nread\n" "receive Second\nreceive Third\nreceive Fourth\nread\n" "receive Fifth\nread\nsnapshot\nread\nsample\nreset\nread\nquit\n"
  };

  return RunCommands(script, true);
}
