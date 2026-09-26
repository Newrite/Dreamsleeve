import std;
import Dreamsleeve.Client.Runtime;
import DreamNet.Runtime;

namespace
{

  using namespace Dreamsleeve::Client;

  void PrintError(const DreamNetError& error)
  {
    std::osyncstream(std::cerr) << error.ToLogString() << '\n';
  }

  void PrintError(const Wire::Error& error)
  {
    std::osyncstream(std::cerr) << "Protocol error " << static_cast<int>(error.code) << ": " << error.field << '\n';
  }

  void PrintError(const Domain::Error& error)
  {
    std::osyncstream(std::cerr) << "Model error " << static_cast<int>(error.code) << ": " << error.field << '\n';
  }

  void Report(const ClientRuntime::Result<void>& result)
  {
    if (!result) std::visit([](const auto& error) { PrintError(error); }, result.error());
  }

  std::string_view PhaseName(SessionPhase phase)
  {
    switch (phase)
    {
      case SessionPhase::Disconnected:
        return "Disconnected";
      case SessionPhase::Connecting:
        return "Connecting";
      case SessionPhase::Opening:
        return "Opening";
      case SessionPhase::Ready:
        return "Ready";
      case SessionPhase::Disconnecting:
        return "Disconnecting";
      case SessionPhase::Faulted:
        return "Faulted";
    }
    return "Unknown";
  }

  void Print(ClientExchange& exchange)
  {
    ClientOutput output;
    exchange.Drain(output);

    std::osyncstream console(std::cout);
    console << "session=" << PhaseName(output.phase) << '\n';

    for (const auto& update : output.state.updates)
      if (const auto* snapshot = std::get_if<ClientSnapshot>(&update))
      {
        console << "snapshot generation=" << snapshot->generation << " players=" << snapshot->players.size() << '\n';
        for (const auto& player : snapshot->players)
          console << player.data.playerId << ": " << player.data.displayName << '\n';
      }

    for (const auto& event : output.rejections)
      console << "request " << event.rejection.requestId << " rejected (" << static_cast<int>(event.rejection.code)
              << "): " << event.rejection.message << '\n';
  }

}

int RunNetworkConsole(int argc, char* argv[])
{
  if (argc != 5 && argc != 6)
  {
    std::cerr << "Usage: Dreamsleeve.Client.Dev --connect <IPv4> <port> <username> [displayName]\n";
    return 2;
  }

  unsigned               port{};
  const std::string_view rawPort{argv[3]};
  const auto             parsed = std::from_chars(rawPort.data(), rawPort.data() + rawPort.size(), port);
  if (parsed.ec != std::errc{} || parsed.ptr != rawPort.data() + rawPort.size() || port == 0 || port > 65535) return 2;

  auto enet = DreamNetRuntime::TryInitialize();
  if (!enet)
  {
    PrintError(enet.error());
    return 1;
  }

  auto address = DreamNetAddress::TryParseIp(argv[2], static_cast<Port>(port));
  if (!address)
  {
    PrintError(address.error());
    return 2;
  }

  Configuration config;
  config.serverAddress = *address;
  const std::string username{argv[4]}, displayName{argc == 6 ? argv[5] : argv[4]};

  auto exchange = ClientExchange::TryCreate(8, 8);
  if (!exchange)
  {
    PrintError(exchange.error());
    return 1;
  }

  enum class Action
  {
    Connect,
    Disconnect
  };

  std::mutex         mutex;
  std::deque<Action> actions;
  std::atomic_bool   failed{};

  std::jthread worker([&](std::stop_token stop) {
    auto created = ClientRuntime::TryCreate(config, **exchange);
    if (!created)
    {
      std::visit([](const auto& error) { PrintError(error); }, created.error());
      failed = true;

      (*exchange)->Finish();
      return;
    }

    auto client = std::move(*created);
    Report(client->Connect(username, displayName));

    auto last = SessionPhase::Disconnected;

    while (!stop.stop_requested())
    {
      std::deque<Action> pending;
      {
        std::lock_guard lock(mutex);
        pending.swap(actions);
      }

      for (const auto action : pending)
        Report(action == Action::Connect ? client->Connect(username, displayName) : client->Disconnect());

      Report(client->Poll(10));

      if (client->Phase() != last)
      {
        last = client->Phase();
        std::osyncstream(std::cout) << "session=" << PhaseName(last) << '\n';
      }

      if (last == SessionPhase::Disconnected || last == SessionPhase::Faulted) std::this_thread::sleep_for(std::chrono::milliseconds(10));
    }

    Report(client->Disconnect());
    while (client->Phase() == SessionPhase::Disconnecting)
      Report(client->Poll(10));

    (*exchange)->Finish();
  });

  std::cout << "Real ENet connection. Commands: connect | disconnect | read | quit\n";

  std::string line;
  while (std::getline(std::cin, line) && line != "quit")
  {
    if (line == "read")
      Print(**exchange);
    else if (line == "connect" || line == "disconnect")
    {
      std::lock_guard lock(mutex);
      actions.push_back(line == "connect" ? Action::Connect : Action::Disconnect);
    }
    else
      std::cout << "Commands: connect | disconnect | read | quit\n";
  }

  worker.request_stop();
  worker.join();
  Print(**exchange);

  return failed ? 1 : 0;
}
