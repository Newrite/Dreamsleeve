#include <glaze/glaze.hpp>
import std;
import Dreamsleeve.Client.Application;
import Dreamsleeve.Client.MovementView;

namespace Dreamsleeve::Client::Dev
{

  Auth::Result<std::string> ReadPassword();

}

namespace
{

  using namespace Dreamsleeve::Client;

  void PrintError(const DreamNetError& error)
  {
    std::osyncstream(std::cerr) << error.ToLogString() << '\n';
  }

  void PrintError(const Domain::Error& error)
  {
    std::osyncstream(std::cerr) << "Model error " << static_cast<int>(error.code) << ": " << error.field << '\n';
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

  // The session's channels by kind; the "all" view of a UI is not one of them.
  struct Channels
  {
    Domain::ChatChannelId global{};
    Domain::ChatChannelId system{};
  };

  void PrintMessage(std::ostream& output, const Domain::ChatMessage& message)
  {
    output << '[' << message.channelId << "] ";
    if (message.announcement)
      output << "announcement source=" << static_cast<int>(message.announcement->source)
             << " kind=" << static_cast<int>(message.announcement->kind) << " signature=" << message.announcement->signature << ' ';
    output << (message.author ? message.author->displayName : std::string{"<system>"}) << ": " << message.messageText << '\n';
  }

  std::optional<Domain::AnnouncementKind> AnnouncementKindNamed(std::string_view name)
  {
    using Kind = Domain::AnnouncementKind;
    if (name == "announcement") return Kind::Announcement;
    if (name == "event") return Kind::Event;
    if (name == "admin") return Kind::Admin;
    if (name == "periodic") return Kind::Periodic;
    return std::nullopt;
  }

  // announce <trusted|third> <kind> <signature|-> <text>: server-only kinds are
  // accepted here on purpose, so a smoke run can see the server refuse them.
  std::optional<PostAnnouncement> ParseAnnouncement(std::string_view line, std::uint64_t requestId, Domain::ChatChannelId channel)
  {
    std::istringstream input{std::string{line.substr(9)}};
    std::string        source, kind, signature;
    if (!(input >> source >> kind >> signature) || (source != "trusted" && source != "third")) return std::nullopt;
    const auto parsed = AnnouncementKindNamed(kind);
    if (!parsed) return std::nullopt;
    std::string text;
    std::getline(input >> std::ws, text);
    return PostAnnouncement{
        requestId,
        channel,
        std::move(text),
        *parsed,
        source == "trusted" ? Domain::ClientAnnouncementSource::TrustedClient : Domain::ClientAnnouncementSource::ThirdParty,
        signature == "-" ? std::string{} : std::move(signature)
    };
  }

  void PrintPlayer(std::ostream& output, const Domain::Player& player)
  {
    auto json = glz::write_json(player);
    if (json) output << "player " << *json << '\n';
  }

  // "mark <id> kind=<1|2> author=<name> x=<x> character=<name> text=<text>": one line per visible mark.
  void PrintMark(std::ostream& output, const Domain::GroundMark& mark)
  {
    output << "mark " << mark.markId << " kind=" << static_cast<int>(mark.kind) << " author=" << mark.author.displayName
           << " x=" << mark.placement.position.X << " character=" << mark.characterName.value_or("") << " text=" << mark.text << '\n';
  }

  constexpr std::string_view Commands =
    "Commands: connect | disconnect | resume | signout | forget | reset-password <code> | send <text> | announce <trusted|third> <kind> <signature|-> <text> | begin <name> | rename <name> | " "move <json> | location <json> | values <json> | details <json> | clear-location | leave | note <text> | death <label> | unmark <id> | marks | read | pose <id> | watch <id> <ms> | quit\n";

  // The last position sent by move/location; marks are placed where the player stands.
  std::optional<Domain::PlayerLocation> lastLocation;

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
      lastLocation.reset();
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
      lastLocation = sample.location;
      command      = std::move(sample);
    }
    else if (line.starts_with("location "))
    {
      LocalLocation transition;
      if (glz::read_json(transition, std::string_view(line).substr(9)))
      {
        std::cout << "Invalid location JSON\n";
        return true;
      }
      lastLocation = transition.location;
      command      = std::move(transition);
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

  // note <text> | death <label> | unmark <id>: placed at the last sent position.
  bool PostMarkCommand(const std::string& line, ClientExchange& exchange, std::uint64_t generation)
  {
    const auto requestId = exchange.NextRequestId();
    if (!requestId)
    {
      std::cout << "Request IDs exhausted\n";
      return true;
    }
    ClientCommand command;
    if (line.starts_with("unmark "))
    {
      Domain::GroundMarkId id{};
      const std::string_view raw{line};
      const auto             parsed = std::from_chars(raw.data() + 7, raw.data() + raw.size(), id);
      if (parsed.ec != std::errc{} || id == 0)
      {
        std::cout << "Invalid mark id\n";
        return true;
      }
      command = RemoveGroundMark{*requestId, id};
    }
    else
    {
      if (!lastLocation)
      {
        std::cout << "No position: send move or location first\n";
        return true;
      }
      const Domain::GroundMarkPlacement placement{lastLocation->location.locationId, lastLocation->position, lastLocation->rotation.Z};
      if (line.starts_with("note "))
        command = PlaceGroundNote{*requestId, line.substr(5), placement};
      else
        command = ReportDeath{*requestId, line.size() > 6 ? line.substr(6) : std::string{}, placement};
    }
    if (exchange.Post({generation, std::move(command)}) == CommandPostResult::Queued)
      std::cout << "request " << *requestId << " queued\n";
    else
      std::cout << "Command queue is full or closed\n";
    return true;
  }

  void Print(ClientExchange& exchange, std::uint64_t& generation, Channels& channel, MovementView& movement, bool verbose = true)
  {
    ClientOutput output;
    exchange.Drain(output);
    movement.Apply(output.state);
    if (
      !verbose && output.state.updates.empty() && output.rejections.empty() && output.commandFailures.empty() &&
      output.groundMarkConfirmations.empty())
      return;

    std::osyncstream console(std::cout);
    console << "session=" << PhaseName(output.status.phase) << '\n';
    if (!output.status.serverName.empty()) console << "server=" << output.status.serverName << '\n';
    if (output.status.authenticating)
      console << "auth=Pending\n";
    else
      console << "auth=Idle operation=" << static_cast<int>(output.status.authOperation)
              << " failure=" << static_cast<int>(output.status.authFailure) << " saved=" << output.status.savedLogin << '\n';
    if (!output.status.error.empty()) console << "Client: " << output.status.error << '\n';

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

        channel = {};
        for (const auto& chat : snapshot->chats)
          (chat.kind == Domain::ChatChannelKind::System ? channel.system : channel.global) = chat.channelId;
        for (const auto& chat : snapshot->chats)
          for (const auto& message : chat.messages)
            PrintMessage(console, message);
        console << "marks " << snapshot->groundMarks.marks.size() << " revision=" << snapshot->groundMarks.viewRevision << '\n';
        for (const auto& mark : snapshot->groundMarks.marks)
          PrintMark(console, mark);
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
        for (const auto& change : delta.groundMarks)
        {
          if (std::holds_alternative<GroundMarksCleared>(change)) console << "marks-cleared\n";
          if (const auto* removed = std::get_if<GroundMarksRemoved>(&change))
            for (const auto id : removed->markIds)
              console << "mark-removed " << id << '\n';
          if (const auto* added = std::get_if<GroundMarksAdded>(&change))
            for (const auto& mark : added->marks)
              PrintMark(console, mark);
        }
      }
    }

    for (const auto& confirmation : output.groundMarkConfirmations)
    {
      console << "request " << confirmation.requestId << (confirmation.removed ? " removed mark " : " placed mark ") << confirmation.markId;
      if (confirmation.evictedId) console << " evicted " << *confirmation.evictedId;
      console << '\n';
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

  bool ReadMovement(const std::string& line, ClientExchange& exchange, std::uint64_t& generation, Channels& channel, MovementView& movement)
  {
    std::istringstream input{line};
    std::string        command;
    Domain::PlayerId   id{};
    int                durationMs{};
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

}

int RunNetworkConsole(int argc, char* argv[])
{
  const bool fromFile      = argc >= 2 && std::string_view{argv[1]} == "--config";
  const int  usernameIndex = fromFile ? 3 : 4;
  if (argc < usernameIndex) return 2;
  const bool                           hasUsername = argc > usernameIndex && !std::string_view{argv[usernameIndex]}.starts_with("--");
  const int                            optionStart = usernameIndex + (hasUsername ? 1 : 0);
  bool                                 remember    = false;
  bool                                 saved       = !hasUsername;
  std::optional<std::filesystem::path> configPath;
  if (fromFile) configPath = argv[2];
  std::optional<std::string> authUrl;
  std::optional<std::string> registerName;
  for (int index = optionStart; index < argc; ++index)
  {
    const std::string_view option{argv[index]};
    if (option == "--remember")
    {
      remember = true;
      continue;
    }
    if (option == "--saved")
    {
      saved = true;
      continue;
    }
    if (index + 1 >= argc) return 2;
    if (option == "--config" && !fromFile)
      configPath = argv[++index];
    else if (option == "--auth-url")
      authUrl = argv[++index];
    else if (option == "--register")
      registerName = argv[++index];
    else
      return 2;
  }
  if (saved && registerName) return 2;

  ClientSettings settings;
  if (configPath)
  {
    auto loaded = LoadClientSettings(*configPath);
    if (!loaded)
    {
      std::cerr << loaded.error() << '\n';
      return 2;
    }
    settings = std::move(*loaded);
  }
  if (authUrl) settings.authUrl = std::move(*authUrl);
  if (!fromFile)
  {
    unsigned               port{};
    const std::string_view rawPort{argv[3]};
    const auto             parsed = std::from_chars(rawPort.data(), rawPort.data() + rawPort.size(), port);
    if (parsed.ec != std::errc{} || parsed.ptr != rawPort.data() + rawPort.size() || port == 0 || port > 65535) return 2;
    auto address = DreamNetAddress::TryParseIp(argv[2], static_cast<Port>(port));
    if (!address)
    {
      PrintError(address.error());
      return 2;
    }
    settings.client.serverAddress = *address;
  }
  if (auto valid = ValidateClientSettings(settings); !valid)
  {
    std::cerr << valid.error() << '\n';
    return 2;
  }

  Credentials credentials;
  if (!saved)
  {
    auto password = Dreamsleeve::Client::Dev::ReadPassword();
    if (!password)
    {
      std::cerr << "Auth: " << password.error() << '\n';
      return 2;
    }
    credentials = {argv[usernameIndex], std::move(*password)};
  }
  auto movement = MovementView::TryCreate(settings.client.movement);
  if (!movement)
  {
    PrintError(movement.error());
    return 1;
  }

  auto application = ClientApplication::TryCreate(std::move(settings));
  if (!application)
  {
    std::cerr << application.error() << '\n';
    return 1;
  }
  auto& exchange = (*application)->Exchange();
  if (auto started = saved ? (*application)->ConnectSaved() : (*application)->Connect(credentials, registerName, remember); !started)
  {
    std::cerr << started.error() << '\n';
    return 1;
  }
  std::uint64_t         generation{};
  Channels              channel{};

  std::cout << "Real ENet connection. " << Commands;

  std::string line;
  while (std::getline(std::cin, line) && line != "quit")
  {
    if (line == "read")
      Print(exchange, generation, channel, **movement);
    else if (line.starts_with("pose ") || line.starts_with("watch "))
    {
      if (!ReadMovement(line, exchange, generation, channel, **movement)) std::cout << Commands;
    }
    else if (line == "connect" || line == "disconnect")
    {
      if (line == "disconnect")
        (*application)->Disconnect();
      else if (auto connected = (saved || remember) ? (*application)->ConnectSaved() : (*application)->Connect(credentials); !connected)
        std::cout << connected.error() << '\n';
    }
    else if (line == "resume" || line == "signout" || line == "forget")
    {
      auto result = line == "resume"  ? (*application)->ConnectSaved()
                  : line == "signout" ? (*application)->SignOut()
                                      : (*application)->ForgetSavedLogin();
      if (!result) std::cout << result.error() << '\n';
    }
    else if (line.starts_with("reset-password "))
    {
      auto password = Dreamsleeve::Client::Dev::ReadPassword();
      if (!password)
        std::cout << password.error() << '\n';
      else if (auto result = (*application)->ResetPassword(line.substr(15), std::move(*password)); !result)
        std::cout << result.error() << '\n';
    }
    else if (line.starts_with("send ") || line.starts_with("chat "))
    {
      Print(exchange, generation, channel, **movement);
      const auto requestId = exchange.NextRequestId();
      if (!requestId)
        std::cout << "Request IDs exhausted\n";
      else
      {
        const auto posted = exchange.Post({
            generation,
            SendChat{*requestId, channel.global, line.substr(5)}
        });
        if (posted == CommandPostResult::Queued)
          std::cout << "request " << *requestId << " queued\n";
        else
          std::cout << "Command queue is full or closed\n";
      }
    }
    else if (line.starts_with("note ") || line.starts_with("death") || line.starts_with("unmark "))
    {
      Print(exchange, generation, channel, **movement);
      PostMarkCommand(line, exchange, generation);
    }
    else if (line == "marks")
    {
      exchange.Post({generation, RequestSnapshot{}});
      Print(exchange, generation, channel, **movement);
    }
    else if (line.starts_with("announce "))
    {
      Print(exchange, generation, channel, **movement);
      const auto requestId = exchange.NextRequestId();
      auto       command   = requestId ? ParseAnnouncement(line, *requestId, channel.system) : std::nullopt;
      if (!command)
        std::cout << Commands;
      else if (exchange.Post({generation, std::move(*command)}) == CommandPostResult::Queued)
        std::cout << "request " << *requestId << " queued\n";
      else
        std::cout << "Command queue is full or closed\n";
    }
    else
    {
      Print(exchange, generation, channel, **movement);
      if (!PostPlayerCommand(line, exchange, generation)) std::cout << Commands;
    }
  }

  (*application)->Stop();
  Print(exchange, generation, channel, **movement);
  return (*application)->Status().error.empty() ? 0 : 1;
}
