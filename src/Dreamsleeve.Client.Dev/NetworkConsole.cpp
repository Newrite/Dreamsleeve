#include "AuthHttp.h"
#include <glaze/glaze.hpp>
import std;
import Dreamsleeve.Client.Runtime;
import Dreamsleeve.Client.MovementView;
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

  void PrintMessage(std::ostream& output, const Domain::ChatMessage& message)
  {
    output << '[' << message.channelId << "] " << message.author.displayName << ": " << message.messageText << '\n';
  }

  void PrintPlayer(std::ostream& output, const Domain::Player& player)
  {
    auto json = glz::write_json(player);
    if (json) output << "player " << *json << '\n';
  }

  constexpr std::string_view Commands =
    "Commands: connect | disconnect | send <text> | begin <name> | rename <name> | "
    "move <json> | location <json> | values <json> | details <json> | clear-location | leave | read | pose <id> | watch <id> <ms> | quit\n";

  bool PostPlayerCommand(const std::string& line, ClientExchange& exchange, std::uint64_t generation)
  {
    ClientCommand command;
    if (line.starts_with("begin "))
    {
      command = CharacterStarted{line.substr(6)};
    }
    else if (line.starts_with("rename "))
      command = CharacterRenamed{line.substr(7)};
    else if (line == "leave")
      command = GameExited{};
    else if (line == "clear-location")
    {
      command = LocalLocation{};
    }
    else if (line.starts_with("move "))
    {
      LocalMovement sample;
      if (glz::read_json(sample, std::string_view(line).substr(5)))
      {
        std::cout << "Invalid movement JSON\n";
        return true;
      }
      command = std::move(sample);
    }
    else if (line.starts_with("location "))
    {
      LocalLocation transition;
      if (glz::read_json(transition, std::string_view(line).substr(9)))
      {
        std::cout << "Invalid location JSON\n";
        return true;
      }
      command = std::move(transition);
    }
    else if (line.starts_with("values "))
    {
      LocalActorValues values;
      if (glz::read_json(values.actorValues, std::string_view(line).substr(7)))
      {
        std::cout << "Invalid actor values JSON\n";
        return true;
      }
      command = std::move(values);
    }
    else if (line.starts_with("details "))
    {
      PlayerDetailsChanged changed;
      if (glz::read_json(changed.details, std::string_view(line).substr(8)))
      {
        std::cout << "Invalid details JSON\n";
        return true;
      }
      command = std::move(changed);
    }
    else
      return false;

    const auto posted = exchange.Post({generation, command});
    if (posted == CommandPostResult::Queued || posted == CommandPostResult::Replaced)
    {
      std::cout << "player command queued\n";
    }
    else
      std::cout << "Command queue is full or closed\n";
    return true;
  }

  void Print(ClientExchange& exchange, std::uint64_t& generation, Domain::ChatChannelId& channel, MovementView& movement, bool verbose = true)
  {
    ClientOutput output;
    exchange.Drain(output);
    movement.Apply(output.state);
    if (!verbose && output.state.updates.empty() && output.rejections.empty() && output.commandFailures.empty()) return;

    std::osyncstream console(std::cout);
    console << "session=" << PhaseName(output.phase) << '\n';

    for (const auto& update : output.state.updates)
    {
      generation = std::visit([](const auto& value) { return value.generation; }, update);
      if (const auto* snapshot = std::get_if<ClientSnapshot>(&update))
      {
        console << "snapshot generation=" << snapshot->generation << " players=" << snapshot->players.size() << '\n';
        for (const auto& player : snapshot->players)
        {
          console << player.data.playerId << ": " << player.data.displayName << '\n';
          PrintPlayer(console, player);
        }

        channel = snapshot->chats.empty() ? 0 : snapshot->chats.front().channelId;
        for (const auto& chat : snapshot->chats)
          for (const auto& message : chat.messages)
            PrintMessage(console, message);
      }
      else
      {
        const auto& delta = std::get<ClientStateDelta>(update);
        for (const auto& player : delta.players)
        {
          console << "online " << player.data.playerId << ": " << player.data.displayName << '\n';
          PrintPlayer(console, player);
        }
        for (const auto playerId : delta.removedPlayers)
          console << "offline " << playerId << '\n';
        for (const auto& change : delta.chatContent)
          if (const auto* added = std::get_if<ChatMessagesAdded>(&change))
            for (const auto& message : added->messages)
              PrintMessage(console, message);
      }
    }

    for (const auto& event : output.rejections)
      console << "request " << event.rejection.requestId << " rejected (" << static_cast<int>(event.rejection.code)
              << "): " << event.rejection.message << '\n';

    for (const auto& failure : output.commandFailures)
      console << "request " << failure.requestId << " not sent (local " << static_cast<int>(failure.code) << ")\n";
  }

  void PrintPose(const MovementView& movement, Domain::PlayerId id)
  {
    const auto pose = movement.Sample(id);
    if (pose)
      std::cout << "pose " << id << ' ' << pose->position.X << ' ' << pose->position.Y << ' ' << pose->position.Z
                << " yaw=" << pose->rotation.Z << '\n';
    else
      std::cout << "pose " << id << " absent\n";
  }

  bool ReadMovement(const std::string& line, ClientExchange& exchange, std::uint64_t& generation,
                    Domain::ChatChannelId& channel, MovementView& movement)
  {
    std::istringstream input{line};
    std::string command;
    Domain::PlayerId id{};
    int durationMs{};
    if (!(input >> command >> id)) return false;
    if (command == "pose")
    {
      Print(exchange, generation, channel, movement, false);
      PrintPose(movement, id);
      return true;
    }
    if (command != "watch" || !(input >> durationMs) || durationMs < 1 || durationMs > 60000) return false;

    const auto end = MovementClock::now() + std::chrono::milliseconds{durationMs};
    while (MovementClock::now() < end)
    {
      Print(exchange, generation, channel, movement, false);
      PrintPose(movement, id);
      std::this_thread::sleep_for(std::chrono::milliseconds{16});
    }
    return true;
  }

  enum class Action
  {
    Connect,
    Disconnect
  };

  struct NetworkControl
  {
    std::mutex         mutex;
    std::deque<Action> actions;
    std::atomic_bool   failed{};
  };

  namespace Auth = Dreamsleeve::Client::Dev::Auth;

  void ConnectAuthenticated(ClientRuntime& client, std::string_view authUrl, const Auth::Credentials& credentials)
  {
    if (client.Phase() != SessionPhase::Disconnected && client.Phase() != SessionPhase::Faulted)
    {
      std::osyncstream(std::cerr) << "A session is already active\n";
      return;
    }
    auto ticket = Auth::Login(authUrl, credentials);
    if (!ticket)
      std::osyncstream(std::cerr) << "Auth: " << ticket.error() << '\n';
    else
      Report(client.Connect(std::move(*ticket)));
  }

  void RunNetwork(std::stop_token stop, Configuration config, ClientExchange& exchange,
                  const std::string& authUrl, const Auth::Credentials& credentials, NetworkControl& control)
  {
    auto created = ClientRuntime::TryCreate(config, exchange);
    if (!created)
    {
      std::visit([](const auto& error) { PrintError(error); }, created.error());
      control.failed = true;

      exchange.Finish();
      return;
    }

    auto client = std::move(*created);
    ConnectAuthenticated(*client, authUrl, credentials);

    auto last = SessionPhase::Disconnected;

    while (!stop.stop_requested())
    {
      std::deque<Action> pending;
      {
        std::lock_guard lock(control.mutex);
        pending.swap(control.actions);
      }

      for (const auto action : pending)
      {
        if (action == Action::Connect) ConnectAuthenticated(*client, authUrl, credentials);
        else Report(client->Disconnect());
      }

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

    exchange.Finish();
  }

}

int RunNetworkConsole(int argc, char* argv[])
{
  if (argc < 5 || (argc - 5) % 2 != 0)
  {
    std::cerr << "Usage: Dreamsleeve.Client.Dev --connect <IPv4> <port> <username> [--auth-url <origin>] [--register <displayName>]\n";
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
  std::string authUrl = "http://127.0.0.1:8779";
  std::optional<std::string> registerName;
  for (int index = 5; index < argc; index += 2)
  {
    const std::string_view option{argv[index]};
    if (option == "--auth-url") authUrl = argv[index + 1];
    else if (option == "--register") registerName = argv[index + 1];
    else return 2;
  }
  if (auto valid = Auth::ValidateUrl(authUrl); !valid)
  {
    std::cerr << "Auth: " << valid.error() << '\n';
    return 2;
  }
  auto password = Auth::ReadPassword();
  if (!password)
  {
    std::cerr << "Auth: " << password.error() << '\n';
    return 2;
  }
  const Auth::Credentials credentials{argv[4], std::move(*password)};
  if (registerName)
  {
    auto registered = Auth::Register(authUrl, credentials, *registerName);
    if (!registered)
    {
      std::cerr << "Auth: " << registered.error() << '\n';
      return 1;
    }
    std::cout << "Account registered\n";
  }

  auto exchange = ClientExchange::TryCreate(8, 8);
  if (!exchange)
  {
    PrintError(exchange.error());
    return 1;
  }

  auto movement = MovementView::TryCreate(config.movement);
  if (!movement)
  {
    PrintError(movement.error());
    return 1;
  }

  NetworkControl control;
  std::jthread worker(RunNetwork, config, std::ref(**exchange), std::cref(authUrl), std::cref(credentials), std::ref(control));
  std::uint64_t generation{};
  Domain::ChatChannelId channel{};

  std::cout << "Real ENet connection. " << Commands;

  std::string line;
  while (std::getline(std::cin, line) && line != "quit")
  {
    if (line == "read")
      Print(**exchange, generation, channel, **movement);
    else if (line.starts_with("pose ") || line.starts_with("watch "))
    {
      if (!ReadMovement(line, **exchange, generation, channel, **movement)) std::cout << Commands;
    }
    else if (line == "connect" || line == "disconnect")
    {
      std::lock_guard lock(control.mutex);
      if (control.actions.size() < 8)
        control.actions.push_back(line == "connect" ? Action::Connect : Action::Disconnect);
      else
        std::cout << "Control queue is full\n";
    }
    else if (line.starts_with("send ") || line.starts_with("chat "))
    {
      Print(**exchange, generation, channel, **movement);
      const auto requestId = (*exchange)->NextRequestId();
      if (!requestId)
        std::cout << "Request IDs exhausted\n";
      else
      {
        const auto posted = (*exchange)->Post({generation, SendChat{*requestId, channel, line.substr(5)}});
        if (posted == CommandPostResult::Queued)
          std::cout << "request " << *requestId << " queued\n";
        else
          std::cout << "Command queue is full or closed\n";
      }
    }
    else
    {
      Print(**exchange, generation, channel, **movement);
      if (!PostPlayerCommand(line, **exchange, generation)) std::cout << Commands;
    }
  }

  worker.request_stop();
  worker.join();
  Print(**exchange, generation, channel, **movement);

  return control.failed ? 1 : 0;
}
