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
    output << (message.author ? message.author->displayName : std::string{"<system>"});
    // A hidden author is shown by the server pseudonym only; the marker lets scripts tell.
    if (message.author && message.author->pseudonymous) output << " [pseudonymous]";
    output << ": " << message.messageText << '\n';
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

  std::string_view SanctionName(Domain::SanctionKind kind)
  {
    return kind == Domain::SanctionKind::Ban ? "ban" : "mute";
  }

  // "<player> <mute|ban> until=<unix ms|lifted>: <reason>"
  void PrintSanction(std::ostream& output, const Domain::Sanction& sanction)
  {
    output << sanction.playerId << ' ' << SanctionName(sanction.kind)
           << " until=" << (sanction.untilUnixMs ? std::to_string(*sanction.untilUnixMs) : std::string{"lifted"}) << ": "
           << sanction.reason;
  }

  std::optional<Domain::PlayerId> ParseId(std::string_view text)
  {
    Domain::PlayerId id{};
    const auto       parsed = std::from_chars(text.data(), text.data() + text.size(), id);
    if (parsed.ec != std::errc{} || parsed.ptr != text.data() + text.size() || id == Domain::InvalidId) return std::nullopt;
    return id;
  }

  // mod <mute|ban> <id> <minutes|forever> <reason> | mod lift <mute|ban> <id> | mod kick <id> <reason> |
  // mod sanctions | mod marks <id> | mod clear <id> <notes|deaths|all> | mod delete <message id>
  std::optional<ClientCommand> ParseModeration(std::string_view line, std::uint64_t requestId, Domain::ChatChannelId global)
  {
    std::istringstream input{std::string{line.substr(4)}};
    std::string        action, first, second;
    input >> action;
    const auto rest = [&] {
      std::string text;
      std::getline(input >> std::ws, text);
      return text;
    };
    if (action == "mute" || action == "ban")
    {
      if (!(input >> first >> second)) return std::nullopt;
      const auto                   id = ParseId(first);
      std::optional<std::uint32_t> minutes;
      if (second != "forever")
      {
        std::uint32_t value{};
        if (std::from_chars(second.data(), second.data() + second.size(), value).ec != std::errc{}) return std::nullopt;
        minutes = value;
      }
      if (!id) return std::nullopt;
      const auto kind = action == "ban" ? Domain::SanctionKind::Ban : Domain::SanctionKind::Mute;
      return SanctionPlayer{requestId, *id, kind, minutes, rest()};
    }
    if (action == "lift" && input >> first >> second && (first == "mute" || first == "ban"))
    {
      const auto id = ParseId(second);
      if (!id) return std::nullopt;
      return LiftSanction{requestId, *id, first == "ban" ? Domain::SanctionKind::Ban : Domain::SanctionKind::Mute};
    }
    if (action == "kick" && input >> first)
    {
      const auto id = ParseId(first);
      if (!id) return std::nullopt;
      return KickPlayer{requestId, *id, rest()};
    }
    if (action == "sanctions") return ListSanctions{requestId};
    if (action == "marks" && input >> first)
    {
      const auto id = ParseId(first);
      if (!id) return std::nullopt;
      return ListPlayerMarks{requestId, *id};
    }
    if (action == "clear" && input >> first >> second && (second == "notes" || second == "deaths" || second == "all"))
    {
      const auto id = ParseId(first);
      if (!id) return std::nullopt;
      return ClearPlayerMarks{requestId, *id, second != "deaths", second != "notes"};
    }
    if (action == "delete" && input >> first)
    {
      const auto id = ParseId(first);
      if (!id) return std::nullopt;
      return DeleteChatMessage{requestId, global, *id};
    }
    return std::nullopt;
  }

  std::optional<Domain::GuildRole> GuildRoleNamed(std::string_view name)
  {
    if (name == "member") return Domain::GuildRole::Member;
    if (name == "officer") return Domain::GuildRole::Officer;
    return std::nullopt;
  }

  std::string_view GuildRoleName(Domain::GuildRole role)
  {
    switch (role)
    {
      case Domain::GuildRole::Member:
        return "member";
      case Domain::GuildRole::Officer:
        return "officer";
      case Domain::GuildRole::Master:
        return "master";
      case Domain::GuildRole::Unspecified:
        break;
    }
    return "unknown";
  }

  // guild create <name> | guild invite <guild> <player> | guild accept|decline|leave|disband <guild> |
  // guild exclude|transfer|unmute <guild> <player> | guild role <guild> <player> <member|officer> |
  // guild mute <guild> <player> <minutes|forever> <reason> | guild say <guild> <text> | guild delete <guild> <message id>
  std::optional<ClientCommand> ParseGuild(std::string_view line, std::uint64_t requestId, const GuildBook* book)
  {
    std::istringstream input{std::string{line.substr(6)}};
    std::string        action, first, second, third;
    input >> action;
    const auto rest = [&] {
      std::string text;
      std::getline(input >> std::ws, text);
      return text;
    };
    if (action == "create") return GuildRequest{requestId, CreateGuild{rest()}};
    if (!(input >> first)) return std::nullopt;
    const auto guild = ParseId(first);
    if (!guild) return std::nullopt;
    if (action == "accept" || action == "decline")
      return GuildRequest{
          requestId,
          AnswerGuildInvite{*guild, action == "accept"}
      };
    if (action == "leave") return GuildRequest{requestId, LeaveGuild{*guild}};
    if (action == "disband") return GuildRequest{requestId, DisbandGuild{*guild}};
    // The guild's channel follows from the book; an unknown guild sends nothing.
    if (action == "say" || action == "delete")
    {
      const auto* found = book ? book->Find(*guild) : nullptr;
      if (!found) return std::nullopt;
      if (action == "say") return SendChat{requestId, found->channelId, rest()};
      if (!(input >> second)) return std::nullopt;
      const auto message = ParseId(second);
      if (!message) return std::nullopt;
      return DeleteChatMessage{requestId, found->channelId, *message};
    }
    if (!(input >> second)) return std::nullopt;
    const auto player = ParseId(second);
    if (!player) return std::nullopt;
    if (action == "invite")
      return GuildRequest{
          requestId,
          InviteToGuild{*guild, *player}
      };
    if (action == "exclude")
      return GuildRequest{
          requestId,
          ExcludeGuildMember{*guild, *player}
      };
    if (action == "transfer")
      return GuildRequest{
          requestId,
          TransferGuild{*guild, *player}
      };
    if (action == "unmute")
      return GuildRequest{
          requestId,
          UnmuteGuildMember{*guild, *player}
      };
    if (!(input >> third)) return std::nullopt;
    if (action == "role")
    {
      const auto role = GuildRoleNamed(third);
      if (!role) return std::nullopt;
      return GuildRequest{
          requestId,
          SetGuildRole{*guild, *player, *role}
      };
    }
    if (action == "mute")
    {
      std::optional<std::uint32_t> minutes;
      if (third != "forever")
      {
        std::uint32_t value{};
        if (std::from_chars(third.data(), third.data() + third.size(), value).ec != std::errc{}) return std::nullopt;
        minutes = value;
      }
      return GuildRequest{
          requestId,
          MuteGuildMember{*guild, *player, minutes, rest()}
      };
    }
    return std::nullopt;
  }

  // "guilds <n> invites <n> per-player=<n> members=<n> name=<min>..<max>", then
  // "guild <id> name=<name> channel=<id> members=<n>" with "member <guild> <player> <role> online=<0|1>[ muted] name=<name>"
  // per member, and "invite <guild> name=<name> by=<player> expires=<unix ms>" per invitation.
  void PrintGuilds(std::ostream& output, const GuildBook& book)
  {
    const auto& limits = book.Limits();
    output << "guilds " << book.Guilds().size() << " invites " << book.Invites().size() << " per-player=" << limits.maxGuildsPerPlayer
           << " members=" << limits.maxMembers << " name=" << limits.nameMinLength << ".." << limits.nameMaxLength << '\n';
    for (const auto& guild : book.Guilds())
    {
      output << "guild " << guild.guildId << " name=" << guild.name << " channel=" << guild.channelId << " members=" << guild.members.size()
             << '\n';
      for (const auto& member : guild.members)
        output << "member " << guild.guildId << ' ' << member.profile.playerId << ' ' << GuildRoleName(member.role)
               << " online=" << member.online << (member.mute ? " muted" : "") << " name=" << member.profile.displayName << '\n';
    }
    for (const auto& invite : book.Invites())
      output << "invite " << invite.guildId << " name=" << invite.guildName << " by=" << invite.invitedBy
             << " expires=" << invite.expiresAtUnixMs << '\n';
  }

  void PrintPlayer(std::ostream& output, const Domain::Player& player)
  {
    auto json = glz::write_json(player);
    if (json) output << "player " << *json << '\n';
  }

  // "<era>E<year>-<MM>-<DD>T<hh>:<mm>", or "-" for a mark stored without a game date.
  std::string DateText(const std::optional<Domain::GameDate>& date)
  {
    if (!date) return "-";
    return std::format("{}E{}-{:02}-{:02}T{:02}:{:02}", date->era, date->year, date->month, date->day, date->hour, date->minute);
  }

  // The console has no game calendar: every mark it places is dated Tirdas, 17 Last Seed 4E 201, 14:05.
  constexpr Domain::GameDate ConsoleGameDate{4, 201, 8, 17, 2, 14, 5};

  // "mark <id> kind=<1|2> author=<name>[ [pseudonymous]] x=<x> character=<name> date=<date> text=<text>": one line per visible mark.
  void PrintMark(std::ostream& output, const Domain::GroundMark& mark)
  {
    output << "mark " << mark.markId << " kind=" << static_cast<int>(mark.kind) << " author=" << mark.author.displayName
           << (mark.author.pseudonymous ? " [pseudonymous]" : "") << " x=" << mark.placement.position.X
           << " character=" << mark.characterName.value_or("") << " date=" << DateText(mark.gameDate) << " text=" << mark.text << '\n';
  }

  constexpr std::string_view Commands =
    "Commands: connect | disconnect | resume | steam | signout | forget | reset-password <code> | send <text> | announce <trusted|third> <kind> <signature|-> <text> | begin <name> | rename <name> | " "move <json> | location <json> | values <json> | details <json> | clear-location | leave | note <text> | death <label> | unmark <id> | marks | hide <on|except-marks|off> | name <display name> | color <#RRGGBB> | " "mod <mute|ban> <id> <minutes|forever> <reason> | mod lift <mute|ban> <id> | mod kick <id> <reason> | mod sanctions | " "mod marks <id> | mod clear <id> <notes|deaths|all> | mod delete <message id> | guild create <name> | guild invite <guild> <player> | " "guild accept|decline|leave|disband <guild> | guild exclude|transfer|unmute <guild> <player> | " "guild role <guild> <player> <member|officer> | guild mute <guild> <player> <minutes|forever> <reason> | guild say <guild> <text> | " "guild delete <guild> <message id> | read | pose <id> | watch <id> <ms> | quit\n";

  // "everywhere" / "except-marks": where the others see the pseudonym.
  std::string_view HidingName(Domain::HiddenIdentity hiding)
  {
    return hiding == Domain::HiddenIdentity::ExceptGroundMarks ? "except-marks" : "everywhere";
  }

  // "own-marks <n>" then "own <id> kind=<1|2> date=<date> text=<text>" per mark: the server's
  // complete list of this player's marks, wherever they stand.
  void PrintOwnMarks(std::ostream& output, const std::vector<Domain::GroundMark>& marks)
  {
    output << "own-marks " << marks.size() << '\n';
    for (const auto& mark : marks)
      output << "own " << mark.markId << " kind=" << static_cast<int>(mark.kind) << " date=" << DateText(mark.gameDate)
             << " text=" << mark.text << '\n';
  }

  // The last position sent by move/location; marks are placed where the player stands.
  std::optional<Domain::PlayerLocation> lastLocation;

  // The guild book last printed, and the newest one seen: a new book is a change.
  std::shared_ptr<const GuildBook> printedGuilds;
  // The route names of the configuration; printed when there is more than one.
  std::vector<std::string> routeNames;

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
      Domain::GroundMarkId   id{};
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
      const Domain::GroundMarkPlacement placement{lastLocation->location.locationId, lastLocation->position, 0.0f};
      if (line.starts_with("note "))
        command = PlaceGroundNote{*requestId, line.substr(5), placement, ConsoleGameDate};
      else
        command = ReportDeath{*requestId, line.size() > 6 ? line.substr(6) : std::string{}, placement, ConsoleGameDate};
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
    const bool guildsChanged = output.status.guilds != printedGuilds;
    printedGuilds            = output.status.guilds;
    if (!verbose && output.state.updates.empty() && output.results.empty() && !guildsChanged) return;

    std::osyncstream console(std::cout);
    console << "session=" << PhaseName(output.status.phase) << '\n';
    if (!output.status.serverName.empty()) console << "server=" << output.status.serverName << '\n';
    if (output.status.pseudonym) console << "pseudonym=" << *output.status.pseudonym << '\n';
    if (output.status.role == Domain::PlayerRole::Moderator) console << "role=moderator\n";
    if (routeNames.size() > 1 && output.status.route < routeNames.size())
      console << "route=" << routeNames[output.status.route] << (output.status.routeReached ? " reached" : "") << '\n';
    if (output.status.mute) console << "muted: " << output.status.mute->reason << '\n';
    if (output.status.sessionEnd)
      console << "ended=" << static_cast<int>(output.status.sessionEnd->reason) << ": " << output.status.sessionEnd->text << '\n';
    if (output.status.authenticating)
      console << "auth=Pending\n";
    else
      console << "auth=Idle operation=" << static_cast<int>(output.status.authOperation)
              << " failure=" << static_cast<int>(output.status.authFailure) << " saved=" << output.status.savedLogin
              << " registration=" << static_cast<int>(output.status.methods.registration) << " steam=" << output.status.methods.steam
              << '\n';
    if (!output.status.error.empty()) console << "Client: " << output.status.error << '\n';
    if (!output.status.steamBrowser.empty()) console << "Browser: " << output.status.steamBrowser << '\n';
    if (!output.status.steamBrowserError.empty()) console << "Browser failed: " << output.status.steamBrowserError << '\n';
    if (guildsChanged && output.status.guilds) PrintGuilds(console, *output.status.guilds);

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
          if (chat.kind == Domain::ChatChannelKind::System)
            channel.system = chat.channelId;
          else if (chat.kind == Domain::ChatChannelKind::Global)
            channel.global = chat.channelId;
        for (const auto& chat : snapshot->chats)
          for (const auto& message : chat.messages)
            PrintMessage(console, message);
        console << "marks " << snapshot->groundMarks.marks.size() << " revision=" << snapshot->groundMarks.viewRevision << '\n';
        for (const auto& mark : snapshot->groundMarks.marks)
          PrintMark(console, mark);
        PrintOwnMarks(console, snapshot->groundMarks.own);
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
          else if (const auto* deleted = std::get_if<ChatMessagesDeleted>(&change))
            for (const auto id : deleted->messageIds)
              console << "message-deleted " << id << '\n';
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
        if (delta.ownGroundMarks) PrintOwnMarks(console, *delta.ownGroundMarks);
      }
    }

    for (const auto& result : output.results)
    {
      console << "request " << result.requestId << ' ';
      std::visit(
        [&](const auto& value) {
          using Outcome = std::decay_t<decltype(value)>;
          if constexpr (std::is_same_v<Outcome, MessagePublished>)
            console << "published message " << value.messageId;
          else if constexpr (std::is_same_v<Outcome, MarkPlaced>)
          {
            console << "placed mark " << value.markId;
            if (value.evictedId) console << " evicted " << *value.evictedId;
          }
          else if constexpr (std::is_same_v<Outcome, MarkRemoved>)
            console << "removed mark " << value.markId;
          else if constexpr (std::is_same_v<Outcome, IdentityChanged>)
            console << "identity "
                    << (output.status.pseudonym ? "hidden as " + *output.status.pseudonym + " " + std::string{HidingName(value.hiding)}
                                                : std::string{"shown"});
          else if constexpr (std::is_same_v<Outcome, NameChanged>)
            console << "display name " << value.displayName;
          else if constexpr (std::is_same_v<Outcome, ColorChanged>)
            console << std::format("name color #{:06X}", value.nameColor);
          else if constexpr (std::is_same_v<Outcome, Sanctioned>)
            PrintSanction(console << "sanctioned ", value.sanction);
          else if constexpr (std::is_same_v<Outcome, Lifted>)
            console << "lifted " << SanctionName(value.kind) << " of " << value.playerId;
          else if constexpr (std::is_same_v<Outcome, Kicked>)
            console << "kicked " << value.playerId;
          else if constexpr (std::is_same_v<Outcome, SanctionsListed>)
          {
            console << "sanctions " << value.sanctions.size();
            for (const auto& sanction : value.sanctions)
              PrintSanction(console << "\nsanction ", sanction);
          }
          else if constexpr (std::is_same_v<Outcome, MarksListed>)
          {
            console << "player-marks " << value.playerId << ' ' << value.marks.size();
            for (const auto& mark : value.marks)
              console << "\nmark " << mark.markId << " kind=" << static_cast<int>(mark.kind) << " text=" << mark.text;
          }
          else if constexpr (std::is_same_v<Outcome, MarksCleared>)
            console << "cleared " << value.removed << " marks of " << value.playerId;
          else if constexpr (std::is_same_v<Outcome, MessageDeleted>)
            console << "deleted message " << value.messageId;
          else if constexpr (std::is_same_v<Outcome, GuildDone>)
            console << "guild done " << value.guildId;
          else if constexpr (std::is_same_v<Outcome, ServerRejection>)
            console << "rejected (" << static_cast<int>(value.code) << "): " << value.message;
          else
            console << "not sent (local " << static_cast<int>(value) << ')';
        },
        result.outcome);
      console << '\n';
    }
  }

  void PrintPose(const MovementView& movement, Domain::PlayerId id)
  {
    const auto pose = movement.Sample(id);
    if (pose)
      std::cout << "pose " << id << ' ' << pose->position.X << ' ' << pose->position.Y << ' ' << pose->position.Z << " camera=("
                << pose->cameraDirection.X << "," << pose->cameraDirection.Y << "," << pose->cameraDirection.Z << ")" << '\n';
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
  auto                                 hiding      = Domain::HiddenIdentity::None;
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
    if (option == "--hide" || option == "--hide-except-marks")
    {
      hiding = option == "--hide" ? Domain::HiddenIdentity::Everywhere : Domain::HiddenIdentity::ExceptGroundMarks;
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
    // Checked with every other setting when the application is created.
    settings.client.serverHost = argv[2];
    settings.client.serverPort = static_cast<Port>(port);
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
  auto application = ClientApplication::TryCreate(std::move(settings));
  if (!application)
  {
    std::cerr << application.error() << '\n';
    return 1;
  }
  // Validated by TryCreate; the view trusts it.
  auto movement = MovementView::Create((*application)->Settings().client.movement);
  for (const auto& route : (*application)->Routes())
    routeNames.push_back(route.name);
  auto& exchange = (*application)->Exchange();
  // Others see a server pseudonym from the very first packet of the session.
  exchange.SetHideIdentity(hiding);
  if (auto started = saved ? (*application)->ConnectSaved() : (*application)->Connect(credentials, registerName, remember); !started)
  {
    std::cerr << started.error() << '\n';
    return 1;
  }
  std::uint64_t generation{};
  Channels      channel{};

  std::cout << "Real ENet connection. " << Commands;

  std::string line;
  while (std::getline(std::cin, line) && line != "quit")
  {
    if (line == "read")
      Print(exchange, generation, channel, *movement);
    else if (line.starts_with("pose ") || line.starts_with("watch "))
    {
      if (!ReadMovement(line, exchange, generation, channel, *movement)) std::cout << Commands;
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
    // The browser opens Steam; "disconnect" cancels the wait.
    else if (line == "steam")
    {
      if (auto result = (*application)->ConnectSteam(true); !result) std::cout << result.error() << '\n';
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
      Print(exchange, generation, channel, *movement);
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
      Print(exchange, generation, channel, *movement);
      PostMarkCommand(line, exchange, generation);
    }
    else if (line == "hide on" || line == "hide except-marks" || line == "hide off")
    {
      Print(exchange, generation, channel, *movement);
      const auto requestId = exchange.NextRequestId();
      const auto requested = line == "hide on"           ? Domain::HiddenIdentity::Everywhere
                           : line == "hide except-marks" ? Domain::HiddenIdentity::ExceptGroundMarks
                                                         : Domain::HiddenIdentity::None;
      if (!requestId)
        std::cout << "Request IDs exhausted\n";
      else if (
        exchange.Post({
            generation,
            SetIdentityVisibility{*requestId, requested}
      }) == CommandPostResult::Queued)
      {
        // The next session opens the same way.
        exchange.SetHideIdentity(requested);
        std::cout << "request " << *requestId << " queued\n";
      }
      else
        std::cout << "Command queue is full or closed\n";
    }
    else if (line.starts_with("name "))
    {
      // The own display name; the username and PlayerId stay.
      Print(exchange, generation, channel, *movement);
      const auto requestId = exchange.NextRequestId();
      if (!requestId)
        std::cout << "Request IDs exhausted\n";
      else if (
        exchange.Post({
            generation,
            ChangeDisplayName{*requestId, line.substr(5)}
      }) == CommandPostResult::Queued)
        std::cout << "request " << *requestId << " queued\n";
      else
        std::cout << "Command queue is full or closed\n";
    }
    else if (line.starts_with("color #") && line.size() == 13)
    {
      // The own name color in chat, #RRGGBB.
      std::uint32_t color{};
      const auto*   begin = line.data() + 7;
      if (std::from_chars(begin, line.data() + line.size(), color, 16).ptr != line.data() + line.size())
        std::cout << "Usage: color #RRGGBB\n";
      else if (const auto requestId = exchange.NextRequestId(); !requestId)
        std::cout << "Request IDs exhausted\n";
      else if (
        exchange.Post({
            generation,
            SetNameColor{*requestId, color}
      }) == CommandPostResult::Queued)
        std::cout << "request " << *requestId << " queued\n";
      else
        std::cout << "Command queue is full or closed\n";
    }
    else if (line == "marks")
    {
      exchange.Post({generation, RequestSnapshot{}});
      Print(exchange, generation, channel, *movement);
    }
    else if (line.starts_with("mod "))
    {
      Print(exchange, generation, channel, *movement);
      const auto requestId = exchange.NextRequestId();
      auto       command   = requestId ? ParseModeration(line, *requestId, channel.global) : std::nullopt;
      if (!command)
        std::cout << Commands;
      else if (exchange.Post({generation, std::move(*command)}) == CommandPostResult::Queued)
        std::cout << "request " << *requestId << " queued\n";
      else
        std::cout << "Command queue is full or closed\n";
    }
    else if (line.starts_with("guild "))
    {
      Print(exchange, generation, channel, *movement);
      const auto requestId = exchange.NextRequestId();
      auto       command   = requestId ? ParseGuild(line, *requestId, printedGuilds.get()) : std::nullopt;
      if (!command)
        std::cout << Commands;
      else if (exchange.Post({generation, std::move(*command)}) == CommandPostResult::Queued)
        std::cout << "request " << *requestId << " queued\n";
      else
        std::cout << "Command queue is full or closed\n";
    }
    else if (line.starts_with("announce "))
    {
      Print(exchange, generation, channel, *movement);
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
      Print(exchange, generation, channel, *movement);
      if (!PostPlayerCommand(line, exchange, generation)) std::cout << Commands;
    }
  }

  (*application)->Stop();
  Print(exchange, generation, channel, *movement);
  return (*application)->Status().error.empty() ? 0 : 1;
}
