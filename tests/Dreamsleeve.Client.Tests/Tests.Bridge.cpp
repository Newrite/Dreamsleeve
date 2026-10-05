#include <doctest/doctest.h>
#include <glaze/glaze.hpp>
#include <magic_enum/magic_enum.hpp>

import std;
import Dreamsleeve.Host.Bridge;

#include "Bridge.h"
#include "Repository.h"

using namespace Dreamsleeve::Client;
using namespace Dreamsleeve::Host;

namespace
{

  // The wire names an enum would have: its enumerators with a lower-case first
  // letter, without Unspecified.
  template <class Enum>
  std::vector<std::string> EnumeratorNames()
  {
    std::vector<std::string> names;
    for (const auto value : magic_enum::enum_values<Enum>())
    {
      std::string name{magic_enum::enum_name(value)};
      if (name == "Unspecified") continue;
      name.front() = static_cast<char>(std::tolower(static_cast<unsigned char>(name.front())));
      names.push_back(std::move(name));
    }
    return names;
  }

  template <std::size_t Size>
  std::vector<std::string> Strings(const std::array<std::string_view, Size>& names)
  {
    return {names.begin(), names.end()};
  }

  template <std::size_t Size>
  std::string List(std::string_view name, const std::array<std::string_view, Size>& values)
  {
    std::string text = std::format("export const {} = [\n", name);
    for (const auto value : values)
      text += std::format("  \"{}\",\n", value);
    return text + "] as const;\n";
  }

  // The web UI's view of the bridge tables.
  std::string BridgeModule()
  {
    return "// Generated from src/Dreamsleeve.Client/Host/Bridge.ixx by the native test\n"
           "// \"The web UI bridge module is generated from the bridge tables\"; do not edit.\n"
           "// Regenerate: set DREAMSLEEVE_WRITE_GENERATED=1 and run Dreamsleeve.Client.Tests.\n\n"
           "// The \"type\" of every host event and UI command.\n" +
           List("eventTypes", Bridge::EventNames) + List("commandTypes", Bridge::CommandNames) + "\n// Enum strings of the events.\n" +
           List("connectionPhases", Bridge::PhaseNames) + List("authOperations", Bridge::OperationNames) +
           List("authFailures", Bridge::FailureNames) + List("announcementOrigins", Bridge::OriginNames) +
           List("announcementKinds", Bridge::KindNames) + List("groundMarkKinds", Bridge::MarkKindNames) +
           List("channelKinds", Bridge::ChannelKindNames) + List("hidingModes", HidingNames) + List("sessionEndReasons", Bridge::EndNames) +
           List("sanctionKinds", Bridge::SanctionKindNames) + List("registrationModes", Bridge::RegistrationNames) +
           List("guildRoles", Bridge::GuildRoleNames) + List("guildRemovalReasons", Bridge::GuildRemovalNames) +
           List("guildActions", Bridge::GuildActionNames) +
           std::format(
             "\n// Bounds of text the host sends: chat and mark text, snapshot lines per\n"
             "// channel, error strings (UTF-8 bytes, never more UTF-16 units).\n"
             "export const maxText = {};\n"
             "export const maxSnapshotRows = {};\n"
             "export const maxError = {};\n"
             "// Bytes of a moderator's reason the host accepts; the server's limit is smaller.\n"
             "export const maxReason = {};\n",
             Bridge::MaxChatText,
             Bridge::MaxSnapshotRows,
             Bridge::MaxErrorBytes,
             Bridge::MaxReasonBytes);
  }

  // Compares the file with expected, or writes it under DREAMSLEEVE_WRITE_GENERATED.
  void Golden(const std::filesystem::path& path, const std::string& expected)
  {
#pragma warning(suppress : 4996)  // Read once; no other thread touches the environment.
    if (std::getenv("DREAMSLEEVE_WRITE_GENERATED"))
    {
      std::filesystem::create_directories(path.parent_path());
      std::ofstream output{path, std::ios::binary | std::ios::trunc};
      output << expected;
    }
    const auto stale = path.filename().string() + " is stale; set DREAMSLEEVE_WRITE_GENERATED=1 and run the tests";
    CHECK_MESSAGE(ReadText(path) == expected, stale);
  }

  std::filesystem::path Contract(std::string_view name)
  {
    return RepositoryRoot() / "src" / "Dreamsleeve.Client.UI" / "tests" / "contract" / name;
  }

  Domain::Player SamplePlayer()
  {
    Domain::Player player;
    player.data          = {7, "seven", "Seven", false, 0xE57373};
    player.characterName = "Nerevar";
    player.details.level = 12;
    player.details.race  = Domain::NamedForm{
        {"skyrim.esm", 0x13746},
        "Nord"
    };
    player.details.place                = Domain::PlaceDescription{"Tamriel", "Whiterun", "Dragonsreach", "castle", false};
    player.details.activity.kind        = Domain::ActivityKind::Talking;
    player.details.activity.targetName  = "Balgruuf";
    player.actorValues["skyrim:health"] = {
        "Health",
        Domain::ResourceActorValue{50, 120}
    };
    player.actorValues["skyrim:speed"] = {"Speed", Domain::ScalarActorValue{100}};
    return player;
  }

  Domain::GroundMark SampleMark(Domain::GroundMarkKind kind, std::string text)
  {
    Domain::GroundMark mark;
    mark.markId        = kind == Domain::GroundMarkKind::Note ? 3 : 4;
    mark.author        = {7, "seven", "Seven"};
    mark.kind          = kind;
    mark.text          = std::move(text);
    mark.placement     = {{"skyrim.esm", 0x1A26F}, {100, 200, 300}, 1.5f};
    mark.createdAt     = Domain::FromUnixMilliseconds(1700000000000);
    mark.characterName = "Nerevar";
    mark.gameDate      = Domain::GameDate{4, 201, 8, 17, 2, 10, 30};
    return mark;
  }

  // One event of every kind, as the host builds them.
  // A sanction of a player the host has met and one of a player it has not.
  std::vector<Bridge::UiSanction> SampleSanctions()
  {
    return {
        Bridge::ToUiSanction({7, Domain::SanctionKind::Mute, "Флуд", 1700000000000, 1700000900000}, "Seven"),
        Bridge::ToUiSanction({9, Domain::SanctionKind::Ban, "Читы", 1700000000000, std::nullopt}, std::nullopt),
    };
  }

  std::vector<Bridge::HostEvent> SampleEvents()
  {
    Names            names;
    const UiSettings settings;
    const auto       player = Bridge::ToUiPlayer(SamplePlayer(), names, settings);

    Domain::ChatMessage chat{11, 1, Domain::PlayerData{7, "seven", "Seven", false, 0xE57373}, "Привет, Вайтран", Domain::FromUnixMilliseconds(1700000000000)};
    chat.characterName = "Nerevar";
    Domain::ChatMessage notice{12, 2, std::nullopt, "Сервер перезапустится", Domain::FromUnixMilliseconds(1700000001000)};
    notice.announcement = Domain::Announcement{Domain::AnnouncementSource::Server, Domain::AnnouncementKind::Admin, {}};
    const auto messages = std::vector{Bridge::ToUiMessage(chat, names, settings), Bridge::ToUiMessage(notice, names, settings)};

    const auto own    = Bridge::ToUiGroundMark(SampleMark(Domain::GroundMarkKind::Note, "Осторожно, тролль"), settings, true);
    auto       nearby = Bridge::ToUiGroundMark(SampleMark(Domain::GroundMarkKind::Death, "Волк"), settings, false);
    nearby.author     = "Seven";

    ClientStatus signedIn;
    signedIn.phase         = SessionPhase::Ready;
    signedIn.savedLogin    = true;
    signedIn.savedUsername = "seven";
    signedIn.authOperation = AuthOperation::PasswordLogin;
    signedIn.methods       = {Auth::RegistrationMode::Open, true};

    return {
        Bridge::SnapshotEvent{
                              .channels             = {Bridge::ToUiChannel(1, Domain::ChatChannelKind::Global), Bridge::ToUiChannel(2, Domain::ChatChannelKind::System)},
                              .messages             = messages,
                              .players              = {player},
                              .selfId               = "1",
                              .serverName           = "Tamriel",
                              .groundMarksSupported = true,
                              .groundMarks          = {own},
                              .nearbyMarks          = {nearby}},
        Bridge::MessagesEvent{.messages = messages},
        Bridge::PlayersEvent{.players = {player}},
        Bridge::ConnectionState(signedIn),
        Bridge::IgnoredEvent{.players = {{"9", "Nine"}}},
        Bridge::SendResultEvent{.requestId = "u1", .messageId = "11"},
        Bridge::AnnouncementResultEvent{.channelId = "2", .source = "DeathMod", .text = "Погиб", .error = "Слишком часто"},
        Bridge::SettingsResultEvent{.revision = 3},
        Bridge::AuthState(signedIn),
        Bridge::SettingsEvent{.settings = settings},
        Bridge::GroundMarksEvent{.marks = {own}},
        Bridge::MarkResultEvent{.requestId = "n1", .markId = "3", .evictedId = "2"},
        Bridge::NearbyMarksEvent{.marks = {nearby}},
        Bridge::IdentityEvent{.mode = "everywhere", .pseudonym = "Страж 2"},
        Bridge::DisplayNameEvent{.changed = "Seven"},
        Bridge::NameColorEvent{.changed = "#E57373"},
        Bridge::RoutesEvent{.routes = {"Основной", "Прокси"}, .active = "Прокси", .chosen = "", .reached = true},
        Bridge::MuteEvent{.muted = true, .reason = "Флуд", .until = 1700000900000},
        Bridge::Ended({Domain::SessionEndReason::Banned, "Читы", 1700086400000}),
        Bridge::RoleEvent{.moderator = true},
        Bridge::MessagesRemovedEvent{.channelId = "1", .messageIds = {"11"}},
        Bridge::ModerationResultEvent{.requestId = "m1", .sanctions = SampleSanctions()},
        Bridge::GuildsEvent{
                              .guilds  = {{"4",
                         "Вороны",
                         "4294967300",
                         1700000000000,
                         {Bridge::ToUiGuildMember({{7, "seven", "Seven"}, Domain::GuildRole::Master, true, std::nullopt, 1700000000000}, names, settings),
                          Bridge::ToUiGuildMember(
                            {{9, "nine", "Nine"}, Domain::GuildRole::Member, false, Domain::MuteState{"Флуд", 1700000900000}, 1700000500000},
                            names,
                            settings)}}},
                              .invites = {{"5", "Соратники", "8", "Eight", 1700604800000}},
                              .limits  = {3, 64, 3, 24},
                              .removed = {{"6", "Изгнанники", "excluded"}}},
        Bridge::GuildResultEvent{.requestId = "g1", .guildId = "4"},
        Bridge::ChannelsEvent{
                              .channels = {Bridge::ToUiChannel(1, Domain::ChatChannelKind::Global),
                         Bridge::ToUiChannel(Domain::GuildChannelBase + 4, Domain::ChatChannelKind::Guild, "Вороны"),
                         Bridge::ToUiChannel(2, Domain::ChatChannelKind::System)}},
        Bridge::ShowEvent{},
        Bridge::HideEvent{},
        Bridge::ActivateEvent{},
        Bridge::DeactivateEvent{},
        Bridge::PhantomEvent{.supported = true, .ready = true, .frames = 300, .status = "Запись готова"},
    };
  }

}

TEST_SUITE_BEGIN("Host.Bridge");

TEST_CASE("Bridge name tables follow the enumerators they name")
{
  CHECK(Strings(Bridge::PhaseNames) == EnumeratorNames<Bridge::ConnectionPhase>());
  CHECK(Strings(Bridge::OperationNames) == EnumeratorNames<AuthOperation>());
  CHECK(Strings(Bridge::FailureNames) == EnumeratorNames<Auth::FailureCode>());
  CHECK(Strings(Bridge::OriginNames) == EnumeratorNames<Domain::AnnouncementSource>());
  CHECK(Strings(Bridge::KindNames) == EnumeratorNames<Domain::AnnouncementKind>());
  CHECK(Strings(Bridge::MarkKindNames) == EnumeratorNames<Domain::GroundMarkKind>());
  CHECK(Strings(Bridge::ChannelKindNames) == EnumeratorNames<Domain::ChatChannelKind>());
  CHECK(Strings(Bridge::SanctionKindNames) == EnumeratorNames<Domain::SanctionKind>());
  CHECK(Strings(Bridge::RegistrationNames) == EnumeratorNames<Auth::RegistrationMode>());
  CHECK(Strings(Bridge::GuildRoleNames) == EnumeratorNames<Domain::GuildRole>());
  CHECK(Strings(Bridge::GuildRemovalNames) == EnumeratorNames<Domain::GuildRemovalReason>());
  // "off" is the ui.toml word for None; the others follow the enumerators.
  auto hiding    = EnumeratorNames<Domain::HiddenIdentity>();
  hiding.front() = "off";
  CHECK(Strings(HidingNames) == hiding);
  // Out of range: the least trust, a plain announcement.
  CHECK(Bridge::OriginName(Domain::AnnouncementSource::Unspecified) == "thirdParty");
  CHECK(Bridge::KindName(static_cast<Domain::AnnouncementKind>(99)) == "announcement");
}

TEST_CASE("Host events carry their type and commands parse by theirs")
{
  CHECK(Json(Bridge::ShowEvent{}) == R"({"type":"show"})");
  const auto result = Parse(Bridge::SettingsResultEvent{.revision = 2});
  CHECK(result["type"].get<std::string>() == "settingsResult");
  CHECK(result["revision"].get<double>() == 2);
  for (std::size_t index = 0; index < Bridge::CommandNames.size(); ++index)
  {
    // Every command is known by name; one without values parses from its type alone.
    const auto parsed = Bridge::ParseCommand(std::format(R"({{"type":"{}"}})", Bridge::CommandNames[index]));
    if (parsed) CHECK(parsed->index() == index);
  }
}

TEST_CASE("The web UI bridge module is generated from the bridge tables")
{
  Golden(RepositoryRoot() / "src" / "Dreamsleeve.Client.UI" / "src" / "bridge" / "bridge.generated.ts", BridgeModule());
}

TEST_CASE("Every host event has a sample for the web UI parser")
{
  const auto        samples = SampleEvents();
  std::set<std::size_t> kinds;
  std::string       text = "[\n";
  for (const auto& event : samples)
  {
    kinds.insert(event.index());
    text += (kinds.size() > 1 ? ",\n" : "") + Json(event);
  }
  text += "\n]\n";
  CHECK(kinds.size() == std::variant_size_v<Bridge::HostEvent>);
  CHECK(samples.size() == kinds.size());
  // tests/contract.test.ts parses each of them with parseHostEvent.
  Golden(Contract("events.json"), text);
}

TEST_CASE("Every web UI command sample is accepted")
{
  // Written by tests/contract.test.ts from one typed sample per Command.
  const auto path = Contract("commands.json");
  REQUIRE(std::filesystem::exists(path));
  std::vector<glz::generic> samples;
  REQUIRE_FALSE(glz::read_json(samples, ReadText(path)));
  std::set<std::size_t> kinds;
  for (const auto& sample : samples)
  {
    const auto json   = glz::write_json(sample).value_or("");
    const auto parsed = Bridge::ParseCommand(json);
    CHECK_MESSAGE(parsed, json);
    if (parsed) kinds.insert(parsed->index());
  }
  CHECK(kinds.size() == Bridge::CommandNames.size());
}

TEST_SUITE_END();
