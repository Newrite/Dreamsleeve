#include <doctest/doctest.h>
#include <glaze/glaze.hpp>
import std;
import Dreamsleeve.Host.Session;
import Dreamsleeve.Host.Bubbles;
import Dreamsleeve.Client.Model;

#include "Bridge.h"

namespace
{

  using namespace Dreamsleeve::Client;
  using namespace Dreamsleeve::Host;

  struct TempPath
  {
    std::filesystem::path path =
      std::filesystem::temp_directory_path() /
      (L"dreamsleeve-ui-" + std::to_wstring(std::chrono::steady_clock::now().time_since_epoch().count()) + L"-тест.toml");

    ~TempPath()
    {
      std::error_code error;
      std::filesystem::remove(path, error);
    }
  };

  Domain::Player MakePlayer(Domain::PlayerId id, std::string name)
  {
    Domain::Player player;
    player.data          = {id, "user" + std::to_string(id), std::move(name)};
    player.characterName = "Nerevar";
    player.details.level = 0;
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
    player.actorValues["skyrim:speed"] = {"Speed", Domain::ScalarActorValue{0}};
    return player;
  }

  Domain::ChatMessage MakeMessage(Domain::ChatMessageId id, Domain::ChatChannelId channel, std::string text)
  {
    return {
        id,
        channel,
        Domain::PlayerData{7, "seven", "Seven"},
        std::move(text),
        Domain::FromUnixMilliseconds(1700000000000)
    };
  }

  ClientExchange::Ptr MakeExchange()
  {
    auto result = ClientExchange::TryCreate(8, 8);
    REQUIRE(result);
    return std::move(*result);
  }

  ClientOutput Drain(ClientExchange& exchange, ClientModel& model, SessionPhase phase, std::string_view server = "Tamriel")
  {
    REQUIRE(exchange.Publish(model, false, phase, server));
    ClientOutput output;
    exchange.Drain(output);
    return output;
  }

  // A Ready publication that is only a delta makes the session ask Core for a
  // snapshot; this answers that request the way ClientRuntime would.
  void Settle(Session& session, ClientExchange& exchange, ClientModel& model, Session::Frame& frame, const UiSettings& settings = {})
  {
    if (frame.snapshot) return;
    std::vector<QueuedClientCommand> commands;
    exchange.TakeCommands(commands);
    REQUIRE(std::ranges::any_of(commands, [](const auto& c) { return std::holds_alternative<RequestSnapshot>(c.command); }));
    REQUIRE(exchange.Publish(model, true, SessionPhase::Ready, "Tamriel"));
    ClientOutput output;
    exchange.Drain(output);
    frame = {};
    session.Process(exchange, output, settings, Domain::HiddenIdentity::None, frame);
  }

}

TEST_SUITE_BEGIN("Client.Host");

TEST_CASE("UI settings round trip through TOML with normalization and atomic replace")
{
  TempPath file;
  auto     missing = LoadUiFile(file.path);
  REQUIRE(missing);
  CHECK(missing->ui.chat == UiSettings{});
  CHECK_FALSE(missing->ui.hideUi);

  UiFile edited;
  edited.ui.chat.showFireflyNames     = false;
  edited.ui.chat.fireflyNameOcclusion = false;
  edited.ui.chat.fireflyNameFontSize  = 24;
  edited.ui.chat.fireflyNameOffset    = 80;
  edited.ui.hideUi                    = true;
  edited.ui.chat.x                    = 0.5;
  edited.ui.chat.width                = 900;
  edited.ui.chat.theme                = "contrast";
  edited.ui.chat.activationKey        = "F2";
  REQUIRE(SaveUiFile(file.path, edited));
  auto loaded = LoadUiFile(file.path);
  REQUIRE(loaded);
  CHECK(*loaded == edited);
  CHECK_FALSE(std::filesystem::exists(file.path.string() + ".tmp"));

  {
    std::ofstream output{file.path, std::ios::binary | std::ios::trunc};
    output << "version = 1\n[ui]\nhideUi = false\n[ui.chat]\nscale = 9\ntheme = \"neon\"\nunknownKey = 1\n";
  }
  auto clamped = LoadUiFile(file.path);
  REQUIRE(clamped);
  CHECK(clamped->ui.chat.scale == 1.5);
  CHECK(clamped->ui.chat.theme == "skyrim");

  {
    std::ofstream output{file.path, std::ios::binary | std::ios::trunc};
    output << "version = 2\n";
  }
  CHECK_FALSE(LoadUiFile(file.path));
}

TEST_CASE("Bridge encodes players with string identifiers and safe text")
{
  Names names;
  auto  player = MakePlayer(18446744073709551615ull, "Display");
  auto  json   = Bridge::Encode(Bridge::PlayersEvent{.players = {Bridge::ToUiPlayer(player, names, UiSettings{})}});
  REQUIRE(json);
  auto  value = Parse(*json);
  auto& first = value["players"][0];
  CHECK(first["id"].get<std::string>() == "18446744073709551615");
  CHECK(first["name"].get<std::string>() == "Display");
  CHECK(first["inCharacter"].get<bool>());
  CHECK(first["level"].get<double>() == 0);
  CHECK(first["race"].get<std::string>() == "Nord");
  CHECK(first["activity"].get<std::string>() == "Разговор");
  CHECK(first["activityTarget"].get<std::string>() == "Balgruuf");
  CHECK(first["markerKind"].get<std::string>() == "Замок");
  CHECK(first["interior"].get<bool>() == false);
  CHECK(first["actorValues"][0]["value"]["maximum"].get<double>() == 120);
  CHECK(first["actorValues"][1]["value"].get<double>() == 0);
  CHECK(json->find("\"menu\"") == std::string::npos);

  auto message = MakeMessage(5, 1, "<script>alert('x')</script> \"quoted\" \\ end");
  auto encoded = Bridge::Encode(Bridge::MessagesEvent{.messages = {Bridge::ToUiMessage(message, names, UiSettings{})}});
  REQUIRE(encoded);
  auto parsed = Parse(*encoded);
  CHECK(parsed["messages"][0]["text"].get<std::string>() == message.messageText);
  CHECK(parsed["messages"][0]["time"].get<double>() == 1700000000000.0);
  CHECK(parsed["messages"][0]["author"]["id"].get<std::string>() == "7");
}

TEST_CASE("Bridge clips overlong auth errors on a UTF-8 boundary")
{
  ClientStatus status;
  status.error = "ConnectTimeout: OpenSession timed out";
  auto json    = Bridge::Encode(Bridge::AuthState(status));
  REQUIRE(json);
  CHECK(Parse(*json)["error"].get<std::string>() == status.error);

  status.error = std::string(510, 'a') + "Привет";
  json         = Bridge::Encode(Bridge::AuthState(status));
  REQUIRE(json);
  auto clipped = Parse(*json)["error"].get<std::string>();
  CHECK(clipped.size() == 512);
  CHECK(clipped == std::string(510, 'a') + "П");

  status.error = std::string(511, 'a') + "Привет";
  json         = Bridge::Encode(Bridge::AuthState(status));
  REQUIRE(json);
  CHECK(Parse(*json)["error"].get<std::string>() == std::string(511, 'a'));
}

TEST_CASE("Session reports every authentication completion, even an identical repeat")
{
  auto           exchange = MakeExchange();
  ClientModel    model;
  Session        session;
  Session::Frame frame;
  session.Process(*exchange, Drain(*exchange, model, SessionPhase::Disconnected), UiSettings{}, Domain::HiddenIdentity::None, frame);
  // Connection, authentication and the initial state of "hide my name".
  REQUIRE(frame.events.size() == 3);

  for (int attempt = 0; attempt < 2; ++attempt)
  {
    REQUIRE(exchange->PostLogin(Credentials{"user", "short"}));
    exchange->CompleteAuthentication("Password must be 12 to 128 UTF-8 bytes", Auth::FailureCode::InvalidCredentials);
    frame = {};
    session.Process(*exchange, Drain(*exchange, model, SessionPhase::Disconnected), UiSettings{}, Domain::HiddenIdentity::None, frame);
    REQUIRE(frame.events.size() == 1);
    CHECK(Type(frame.events[0]) == "auth");
    auto auth = Parse(frame.events[0]);
    CHECK(auth["authenticating"].get<bool>() == false);
    CHECK(auth["error"].get<std::string>() == "Password must be 12 to 128 UTF-8 bytes");
  }
}

TEST_CASE("Session tells the page of a mute and its lift, and of each end of a session once")
{
  auto           exchange = MakeExchange();
  ClientModel    model;
  Session        session;
  Session::Frame frame;
  session.Process(*exchange, Drain(*exchange, model, SessionPhase::Disconnected), UiSettings{}, Domain::HiddenIdentity::None, frame);
  // Not muted is where the page starts: no event for it.
  CHECK(std::ranges::none_of(frame.events, [](const auto& event) { return Type(event) == "mute"; }));

  const auto next = [&] {
    frame = {};
    session.Process(*exchange, Drain(*exchange, model, SessionPhase::Disconnected), UiSettings{}, Domain::HiddenIdentity::None, frame);
  };
  exchange->PublishMute(Domain::MuteState{"Флуд", 1700000900000});
  next();
  REQUIRE(frame.events.size() == 1);
  auto mute = Parse(frame.events[0]);
  CHECK(mute["type"].get<std::string>() == "mute");
  CHECK(mute["muted"].get<bool>());
  CHECK(mute["reason"].get<std::string>() == "Флуд");
  CHECK(mute["until"].get<double>() == 1700000900000.0);
  exchange->PublishMute(std::nullopt);
  next();
  REQUIRE(frame.events.size() == 1);
  CHECK_FALSE(Parse(frame.events[0])["muted"].get<bool>());

  const Domain::SessionEnd kicked{Domain::SessionEndReason::Kicked, "Остынь", std::nullopt};
  for (int repeat = 0; repeat < 2; ++repeat)
  {
    exchange->PublishSessionEnd(kicked);
    next();
    REQUIRE(frame.events.size() == 1);
    auto ended = Parse(frame.events[0]);
    CHECK(ended["type"].get<std::string>() == "sessionEnded");
    CHECK(ended["reason"].get<std::string>() == "kicked");
    CHECK(ended["text"].get<std::string>() == "Остынь");
    CHECK(frame.sessionEnded == Domain::SessionEndReason::Kicked);
  }
  next();
  CHECK(frame.events.empty());
  CHECK_FALSE(frame.sessionEnded);
  CHECK(Bridge::RejectionText(RequestRejectionCode::Muted, "The player is muted.") == "Вы в муте: писать сейчас нельзя");
}

TEST_CASE("Bridge parses each command into its own checked type")
{
  const auto chat = CommandOf<Bridge::Commands::SendChat>(R"({"type":"sendChat","requestId":"3","channelId":"1","text":"Привет","extra":1})");
  CHECK(chat.text == "Привет");
  CHECK(chat.channelId.value == 1);
  // The host owns the page's correlation and the size of page input; the
  // content of what travels on (empty text, ID 0) is Core's to refuse.
  CHECK_FALSE(Bridge::ParseCommand(R"({"type":"sendChat","channelId":"1","text":"x"})"));
  CHECK_FALSE(Bridge::ParseCommand(
    std::format(R"({{"type":"sendChat","requestId":"3","channelId":"1","text":"{}"}})", std::string(Bridge::MaxChatText + 1, 'x'))));
  CHECK(Bridge::ParseCommand(R"({"type":"sendChat","requestId":"3","channelId":"0","text":""})"));
  CHECK_FALSE(Bridge::ParseCommand(R"({"type":"sendChat","requestId":"3","channelId":"one","text":"x"})"));
  CHECK_FALSE(Bridge::ParseCommand(R"({"type":"jump"})"));
  CHECK_FALSE(Bridge::ParseCommand("not json"));
  // A registration name only when there is one.
  CHECK_FALSE(CommandOf<Bridge::Commands::SignIn>(R"({"type":"signIn","username":"a","password":"b","displayName":""})").displayName);
  const auto save = CommandOf<Bridge::Commands::SaveSettings>(R"({"type":"saveSettings","revision":4,"settings":{"scale":5,"theme":"skyrim"}})");
  CHECK(save.settings.scale == 1.5);
  CHECK(save.revision == 4);
  CommandOf<Bridge::Commands::Close>(R"({"type":"close"})");
  CommandOf<Bridge::Commands::SignInSaved>(R"({"type":"signInSaved"})");
  CHECK(
    CommandOf<Bridge::Commands::SanctionPlayer>(
      R"({"type":"sanctionPlayer","requestId":"m1","playerId":"7","kind":"ban","reason":"r","devices":true})")
      .devices);
  CHECK_FALSE(
    CommandOf<Bridge::Commands::SanctionPlayer>(R"({"type":"sanctionPlayer","requestId":"m1","playerId":"7","kind":"ban","reason":"r"})")
      .devices);
  const auto reset = CommandOf<Bridge::Commands::ResetPassword>(R"({"type":"resetPassword","code":"c","password":"p"})");
  CHECK(reset.code == "c");
  CHECK(reset.password == "p");
  CHECK(CommandOf<Bridge::Commands::Ignore>(R"({"type":"ignore","playerId":"18446744073709551615"})").playerId.value == 18446744073709551615ull);
}

TEST_CASE("Session publishes snapshots only for a ready session and correlates chat requests")
{
  auto        exchange = MakeExchange();
  ClientModel model;
  REQUIRE(model.RegisterChannel(1, 16));
  Session        session;
  Session::Frame frame;

  // Initial publication while disconnected: state is tracked, UI gets status only.
  session.Process(*exchange, Drain(*exchange, model, SessionPhase::Disconnected), UiSettings{}, Domain::HiddenIdentity::None, frame);
  REQUIRE(frame.events.size() == 3);
  CHECK(Type(frame.events[0]) == "connection");
  CHECK(Type(frame.events[1]) == "auth");
  CHECK(Type(frame.events[2]) == "identity");
  CHECK_FALSE(session.Ready());
  CHECK(
    session.SendChat(*exchange, Bridge::Commands::SendChat{.requestId = "u1", .channelId = {1}, .text = "x"}).has_value() ==
    false);

  REQUIRE(model.Apply(
    model.Generation(),
    OnlinePlayersReplaced{
        {MakePlayer(1, "Alice"), MakePlayer(2, "Bob")}
  }));
  REQUIRE(model.Apply(model.Generation(), SelfPlayerAssigned{1}));
  REQUIRE(model.Apply(model.Generation(), ChatMessagesReceived{1, {MakeMessage(10, 1, "history")}}));
  frame = {};
  session.Process(*exchange, Drain(*exchange, model, SessionPhase::Ready), UiSettings{}, Domain::HiddenIdentity::None, frame);
  Settle(session, *exchange, model, frame);
  REQUIRE(frame.snapshot);
  CHECK(Type(frame.events[0]) == "snapshot");
  auto snapshot = Parse(frame.events[0]);
  CHECK(snapshot["selfId"].get<std::string>() == "1");
  CHECK(snapshot["serverName"].get<std::string>() == "Tamriel");
  CHECK(snapshot["channels"][0]["id"].get<std::string>() == "1");
  CHECK(snapshot["messages"][0]["text"].get<std::string>() == "history");
  CHECK(snapshot["players"].get_array().size() == 2);
  // Settings travel only as their own event: a re-projection never undoes unsaved page edits.
  CHECK_FALSE(snapshot.contains("settings"));
  CHECK(session.Ready());
  CHECK(session.OnlinePlayers().size() == 2);

  // Chat request: UI id maps to the Core RequestId and completes on confirmation.
  REQUIRE(session.SendChat(*exchange, Bridge::Commands::SendChat{.requestId = "u1", .channelId = {1}, .text = "hello"}));
  std::vector<QueuedClientCommand> commands;
  exchange->TakeCommands(commands);
  REQUIRE(commands.size() == 1);
  const auto requestId = std::get<SendChat>(commands[0].command).requestId;
  CHECK(session.PendingChatCount() == 1);

  REQUIRE(model.Apply(model.Generation(), ChatMessagesReceived{1, {MakeMessage(11, 1, "hello")}}));
  REQUIRE(exchange->Publish(model, false, SessionPhase::Ready, "Tamriel", CommandResult{model.Generation(), requestId, MessagePublished{11}}));
  ClientOutput output;
  exchange->Drain(output);
  frame = {};
  session.Process(*exchange, output, UiSettings{}, Domain::HiddenIdentity::None, frame);
  REQUIRE(frame.events.size() == 2);
  CHECK(Type(frame.events[0]) == "messages");
  CHECK(Type(frame.events[1]) == "sendResult");
  auto result = Parse(frame.events[1]);
  CHECK(result["requestId"].get<std::string>() == "u1");
  CHECK(result["messageId"].get<std::string>() == "11");
  CHECK(session.PendingChatCount() == 0);

  // Local failure with a foreign request id is ignored; a known one reports an error.
  REQUIRE(session.SendChat(*exchange, Bridge::Commands::SendChat{.requestId = "u2", .channelId = {1}, .text = "again"}));
  exchange->TakeCommands(commands);
  const auto second = std::get<SendChat>(commands[0].command).requestId;
  REQUIRE(exchange->PublishResult({model.Generation(), 999, CommandFailureCode::Busy}));
  REQUIRE(exchange->PublishResult({model.Generation(), second, CommandFailureCode::SessionNotReady}));
  REQUIRE(exchange->Publish(model, false, SessionPhase::Ready, "Tamriel"));
  exchange->Drain(output);
  frame = {};
  session.Process(*exchange, output, UiSettings{}, Domain::HiddenIdentity::None, frame);
  REQUIRE(frame.events.size() == 1);
  auto failure = Parse(frame.events[0]);
  CHECK(failure["requestId"].get<std::string>() == "u2");
  CHECK(failure["error"].get<std::string>() == "Нет соединения с сервером");

  // Player changes produce one full list; removals are reflected.
  REQUIRE(model.Apply(model.Generation(), PlayerRemoved{2}));
  REQUIRE(model.Apply(model.Generation(), PlayerUpserted{MakePlayer(3, "Carol")}));
  frame = {};
  session.Process(*exchange, Drain(*exchange, model, SessionPhase::Ready), UiSettings{}, Domain::HiddenIdentity::None, frame);
  REQUIRE(frame.events.size() == 1);
  CHECK(Type(frame.events[0]) == "players");
  CHECK(Parse(frame.events[0])["players"].get_array().size() == 2);
  CHECK(session.OnlinePlayers().contains(3));
  CHECK_FALSE(session.OnlinePlayers().contains(2));
}

TEST_CASE("Session view reset requests a fresh snapshot and drops stale correlations")
{
  auto        exchange = MakeExchange();
  ClientModel model;
  REQUIRE(model.RegisterChannel(1, 16));
  REQUIRE(model.Apply(model.Generation(), SelfPlayerAssigned{1}));
  Session        session;
  Session::Frame frame;
  session.Process(*exchange, Drain(*exchange, model, SessionPhase::Ready), UiSettings{}, Domain::HiddenIdentity::None, frame);
  REQUIRE(frame.snapshot);
  REQUIRE(session.SendChat(*exchange, Bridge::Commands::SendChat{.requestId = "old", .channelId = {1}, .text = "x"}));
  std::vector<QueuedClientCommand> commands;
  exchange->TakeCommands(commands);

  session.ResetView();
  CHECK(session.NeedsSnapshot());
  CHECK(session.PendingChatCount() == 0);
  frame = {};
  session.Process(*exchange, Drain(*exchange, model, SessionPhase::Ready), UiSettings{}, Domain::HiddenIdentity::None, frame);
  // Status is republished for the new view and a snapshot was requested from Core.
  CHECK(std::ranges::any_of(frame.events, [](const auto& e) { return Type(e) == "connection"; }));
  exchange->TakeCommands(commands);
  REQUIRE(commands.size() == 1);
  CHECK(std::holds_alternative<RequestSnapshot>(commands[0].command));

  REQUIRE(exchange->Publish(model, true, SessionPhase::Ready, "Tamriel"));
  frame = {};
  ClientOutput output;
  exchange->Drain(output);
  session.Process(*exchange, output, UiSettings{}, Domain::HiddenIdentity::None, frame);
  CHECK(frame.snapshot);
  CHECK_FALSE(session.NeedsSnapshot());
}

TEST_CASE("Session reconnect: disconnect snapshot is silent and the new generation republishes")
{
  auto        exchange = MakeExchange();
  ClientModel model;
  REQUIRE(model.RegisterChannel(1, 16));
  REQUIRE(model.Apply(model.Generation(), SelfPlayerAssigned{1}));
  Session        session;
  Session::Frame frame;
  session.Process(*exchange, Drain(*exchange, model, SessionPhase::Ready), UiSettings{}, Domain::HiddenIdentity::None, frame);
  const auto generation = session.Generation();

  model.ResetSession();
  frame = {};
  session.Process(*exchange, Drain(*exchange, model, SessionPhase::Disconnected, ""), UiSettings{}, Domain::HiddenIdentity::None, frame);
  CHECK_FALSE(frame.snapshot);
  CHECK(session.Generation() == generation + 1);
  auto connection = std::ranges::find_if(frame.events, [](const auto& e) { return Type(e) == "connection"; });
  REQUIRE(connection != frame.events.end());
  CHECK(Parse(*connection)["connected"].get<bool>() == false);

  REQUIRE(model.RegisterChannel(1, 16));
  REQUIRE(model.Apply(model.Generation(), SelfPlayerAssigned{1}));
  frame = {};
  session.Process(*exchange, Drain(*exchange, model, SessionPhase::Ready, "Tamriel"), UiSettings{}, Domain::HiddenIdentity::None, frame);
  // Core publishes a delta after the reset; the host must request a snapshot for the UI.
  Settle(session, *exchange, model, frame);
  CHECK(frame.snapshot);
}

TEST_CASE("Older UI files keep bubble defaults and new bubble values are bounded")
{
  TempPath file;
  {
    std::ofstream output{file.path};
    output << "[ui.chat]\nfontSize = 20\nshowFireflyNames = false\n";
  }
  auto loaded = LoadUiFile(file.path);
  REQUIRE(loaded);
  const UiSettings defaults{};
  CHECK(loaded->ui.chat.showBubbles == defaults.showBubbles);
  CHECK(loaded->ui.chat.bubbleDuration == defaults.bubbleDuration);
  CHECK(loaded->ui.chat.bubbleFade == defaults.bubbleFade);
  CHECK(loaded->ui.chat.bubbleFadeDuration == defaults.bubbleFadeDuration);
  CHECK(loaded->ui.chat.bubbleFontSize == defaults.bubbleFontSize);
  CHECK(loaded->ui.chat.bubbleMaxWidth == defaults.bubbleMaxWidth);
  CHECK(loaded->ui.chat.bubbleBackground == defaults.bubbleBackground);
  CHECK_FALSE(loaded->ui.chat.combatHideFireflies);
  CHECK_FALSE(loaded->ui.chat.combatHideNames);
  CHECK_FALSE(loaded->ui.chat.combatHideBubbles);
  CHECK_FALSE(loaded->ui.chat.showFireflyNames);

  UiFile edited;
  edited.ui.chat.showBubbles        = false;
  edited.ui.chat.bubbleDuration     = 15;
  edited.ui.chat.bubbleFade         = false;
  edited.ui.chat.bubbleFadeDuration = 2.5;
  edited.ui.chat.bubbleFontSize     = 20;
  edited.ui.chat.bubbleMaxWidth     = 400;
  edited.ui.chat.bubbleBackground   = 0.4;

  edited.ui.chat.combatHideFireflies = true;
  edited.ui.chat.combatHideNames     = true;
  edited.ui.chat.combatHideBubbles   = true;
  REQUIRE(SaveUiFile(file.path, edited));
  auto saved = LoadUiFile(file.path);
  REQUIRE(saved);
  CHECK(*saved == edited);

  auto invalid               = edited.ui.chat;
  invalid.bubbleDuration     = 1000;
  invalid.bubbleFadeDuration = 0;
  invalid.bubbleFontSize     = 1;
  invalid.bubbleMaxWidth     = 10;
  invalid.bubbleBackground   = 3;
  auto normalized            = Dreamsleeve::Host::Normalize(invalid);
  CHECK(normalized.bubbleDuration == 60);
  CHECK(normalized.bubbleFadeDuration == 0.1);
  CHECK(normalized.bubbleFontSize == 8);
  CHECK(normalized.bubbleMaxWidth == 120);
  CHECK(normalized.bubbleBackground == 1);
}

TEST_CASE("Bubbles keep one text per player, replace it, expire and fade on elapsed time only")
{
  using namespace std::chrono_literals;
  Bubbles    bubbles;
  UiSettings settings;
  settings.bubbleDuration     = 8;
  settings.bubbleFade         = true;
  settings.bubbleFadeDuration = 2;
  const auto start            = Bubbles::Clock::time_point{} + 100s;

  bubbles.Post(7, 7, "first", start);
  bubbles.Post(9, 9, "other", start);
  REQUIRE(bubbles.Find(7, start + 1s, settings));
  CHECK(bubbles.Find(7, start + 1s, settings)->text == "first");
  CHECK(bubbles.Find(7, start + 1s, settings)->alpha == 1.0f);

  // Replacement restarts the timer; the old text is gone.
  bubbles.Post(7, 7, "second", start + 5s);
  CHECK(bubbles.Find(7, start + 12s, settings)->text == "second");
  CHECK(bubbles.Size() == 2);

  // Fully visible until the display time, then fading, then gone. Visibility is
  // not an input: the same clock decides whether or not the author was on screen.
  CHECK(bubbles.Find(7, start + 12900ms, settings)->alpha == 1.0f);
  const auto mid = bubbles.Find(7, start + 14s, settings);
  REQUIRE(mid);
  CHECK(mid->alpha == doctest::Approx(0.5f));
  CHECK_FALSE(bubbles.Find(7, start + 15s, settings));

  // Without fade the text disappears exactly after the display time.
  settings.bubbleFade = false;
  CHECK(bubbles.Find(9, start + 7900ms, settings));
  CHECK_FALSE(bubbles.Find(9, start + 8s, settings));

  // Pruning drops expired texts and players that left; the rest survive.
  bubbles.Post(11, 11, "stays", start + 20s);
  bubbles.Prune(start + 20s, settings, [](Domain::PlayerId id) { return id != 7; });
  CHECK(bubbles.Size() == 1);
  CHECK(bubbles.Find(11, start + 20s, settings));
  bubbles.Clear();
  CHECK(bubbles.Size() == 0);
}

TEST_CASE("Session admits only live global-channel publications of other players as fresh")
{
  auto        exchange = MakeExchange();
  ClientModel model;
  REQUIRE(model.RegisterChannel(1, 16));
  REQUIRE(model.Apply(
    model.Generation(),
    OnlinePlayersReplaced{
        {MakePlayer(1, "Alice"), MakePlayer(7, "Seven")}
  }));
  REQUIRE(model.Apply(model.Generation(), SelfPlayerAssigned{1}));
  REQUIRE(model.Apply(
    model.Generation(),
    ChatMessagesReceived{
        1,
        {MakeMessage(10, 1, "history"), MakeMessage(11, 1, "older")}
  }));
  Session        session;
  Session::Frame frame;
  session.Process(*exchange, Drain(*exchange, model, SessionPhase::Ready), UiSettings{}, Domain::HiddenIdentity::None, frame);
  Settle(session, *exchange, model, frame);
  REQUIRE(frame.snapshot);
  // Retained history in the snapshot never produces bubbles.
  CHECK(frame.freshMessages.empty());

  auto self     = MakeMessage(13, 1, "mine");
  self.author   = {1, "user1", "Alice"};
  auto system   = MakeMessage(14, 1, "announcement");
  system.author = {};
  REQUIRE(model.Apply(
    model.Generation(),
    ChatMessagesReceived{
        1,
        {MakeMessage(12, 1, "live"), self, system}
  }));
  frame = {};
  session.Process(*exchange, Drain(*exchange, model, SessionPhase::Ready), UiSettings{}, Domain::HiddenIdentity::None, frame);
  REQUIRE(frame.freshMessages.size() == 1);
  CHECK(frame.freshMessages[0].messageId == 12);
  CHECK(frame.freshMessages[0].author->playerId == 7);
  CHECK(frame.freshMessages[0].messageText == "live");
  // The UI still receives every message.
  CHECK(std::ranges::any_of(frame.events, [](const auto& e) { return Type(e) == "messages"; }));

  // A repeated or older ID (history page, replay) is not fresh again.
  REQUIRE(model.Apply(
    model.Generation(),
    ChatMessagesReceived{
        1,
        {MakeMessage(12, 1, "live"), MakeMessage(11, 1, "older")}
  }));
  frame = {};
  session.Process(*exchange, Drain(*exchange, model, SessionPhase::Ready), UiSettings{}, Domain::HiddenIdentity::None, frame);
  CHECK(frame.freshMessages.empty());

  // A view reset replays the snapshot: still no bubbles from history.
  session.ResetView();
  frame = {};
  session.Process(*exchange, Drain(*exchange, model, SessionPhase::Ready), UiSettings{}, Domain::HiddenIdentity::None, frame);
  Settle(session, *exchange, model, frame);
  REQUIRE(frame.snapshot);
  CHECK(frame.freshMessages.empty());

  // Another channel is not the global one.
  REQUIRE(model.RegisterChannel(2, 16));
  REQUIRE(model.Apply(model.Generation(), ChatMessagesReceived{2, {MakeMessage(20, 2, "elsewhere")}}));
  frame = {};
  session.Process(*exchange, Drain(*exchange, model, SessionPhase::Ready), UiSettings{}, Domain::HiddenIdentity::None, frame);
  CHECK(frame.freshMessages.empty());
  if (session.NeedsSnapshot()) Settle(session, *exchange, model, frame);
  REQUIRE(model.Apply(model.Generation(), ChatMessagesReceived{2, {MakeMessage(21, 2, "elsewhere")}}));
  frame = {};
  session.Process(*exchange, Drain(*exchange, model, SessionPhase::Ready), UiSettings{}, Domain::HiddenIdentity::None, frame);
  CHECK(frame.freshMessages.empty());
  REQUIRE(model.Apply(model.Generation(), ChatMessagesReceived{1, {MakeMessage(22, 1, "global again")}}));
  frame = {};
  session.Process(*exchange, Drain(*exchange, model, SessionPhase::Ready), UiSettings{}, Domain::HiddenIdentity::None, frame);
  REQUIRE(frame.freshMessages.size() == 1);
  CHECK(frame.freshMessages[0].messageText == "global again");
}

TEST_CASE("Name resolution follows the mode and falls back to checked names")
{
  const Domain::PlayerData profile{5, "lydia", "Lydia of Whiterun"};
  CHECK(ResolveName(NameMode::Username, profile, "Housecarl") == "lydia");
  CHECK(ResolveName(NameMode::Display, profile, "Housecarl") == "Lydia of Whiterun");
  CHECK(ResolveName(NameMode::Character, profile, "Housecarl") == "Housecarl");
  CHECK(ResolveName(NameMode::Character, profile, std::nullopt) == "Lydia of Whiterun");
  CHECK(ResolveName(NameMode::Character, profile, std::string{}) == "Lydia of Whiterun");
  CHECK(ResolveName(NameMode::Character, Domain::PlayerData{5, "", ""}, std::nullopt) == NeutralName);
  CHECK(ModeOf("username") == NameMode::Username);
  CHECK(ModeOf("unknown") == NameMode::Display);

  UiSettings chosen;
  chosen.nameMode = "account";
  CHECK(Dreamsleeve::Host::Normalize(chosen).nameMode == "display");
  chosen.nameMode = "character";
  CHECK(Dreamsleeve::Host::Normalize(chosen).nameMode == "character");
}

TEST_CASE("Pseudonyms are stable, scoped by server, numbered on collision and persisted")
{
  Names names;
  names.Configure("127.0.0.1:8778", {"Страж"});
  const auto first  = names.Alias(1);
  const auto second = names.Alias(2);
  CHECK(first == "Страж");
  CHECK(second == "Страж 2");
  CHECK(names.Alias(1) == first);
  CHECK(names.TakeDirty());
  CHECK_FALSE(names.TakeDirty());

  // The same account ID on another server is another player.
  Names other;
  other.Configure("10.0.0.2:8778", {"Страж"});
  other.Load(names.Book());
  CHECK(other.Alias(2) == "Страж");

  TempPath file;
  UiFile   saved;
  saved.names = names.Book();
  REQUIRE(SaveUiFile(file.path, saved));
  auto loaded = LoadUiFile(file.path);
  REQUIRE(loaded);
  Names restarted;
  restarted.Configure("127.0.0.1:8778", {"Бард"});
  restarted.Load(loaded->names);
  CHECK(restarted.Alias(1) == first);
  CHECK(restarted.Alias(2) == second);
  CHECK_FALSE(restarted.TakeDirty());

  // A pseudonym comes from the dictionary, never from the real name.
  UiSettings streamer;
  streamer.streamerMode = true;
  const auto alias      = restarted.NameFor(3, {3, "realuser", "Real Name"}, "Real Character", streamer);
  CHECK(alias == "Бард");
  CHECK(alias.find("Real") == std::string::npos);
}

TEST_CASE("Alias dictionary falls back to built-in names for missing, broken or empty files")
{
  TempPath file;
  auto     missing = LoadAliasDictionary(file.path);
  CHECK_FALSE(missing.names.empty());
  CHECK_FALSE(missing.warning.empty());
  const auto write = [&](std::string_view text) {
    std::ofstream output{file.path, std::ios::binary | std::ios::trunc};
    output << text;
  };
  write("names = [\"Страж\", \"\", \" padded\", \"<b>\", \"Страж\", \"Бард\"]\n");
  auto filtered = LoadAliasDictionary(file.path);
  CHECK(filtered.warning.empty());
  CHECK(filtered.names == std::vector<std::string>{"Страж", "Бард"});
  write("names = [\n");
  auto broken = LoadAliasDictionary(file.path);
  CHECK_FALSE(broken.names.empty());
  CHECK_FALSE(broken.warning.empty());
  write("names = []\n");
  CHECK(LoadAliasDictionary(file.path).names == missing.names);
  write("");
  CHECK(LoadAliasDictionary(file.path).names == missing.names);
}

TEST_CASE("Ignore list is per server, refuses self and system, and survives a restart")
{
  Names names;
  names.Configure("a:1", {});
  const Domain::PlayerData bob{7, "bob", "Bob"};
  CHECK_FALSE(names.Ignore(0, 1, nullptr));
  CHECK_FALSE(names.Ignore(1, 1, nullptr));
  CHECK(names.Ignore(7, 1, &bob));
  CHECK_FALSE(names.Ignore(7, 1, &bob));
  CHECK(names.Ignored(7));
  // Surfaces hide the ignored author, never self, even without a self id yet.
  CHECK(names.Hides(7, 1));
  CHECK(names.Hides(7, std::nullopt));
  CHECK_FALSE(names.Hides(7, 7));
  CHECK_FALSE(names.Hides(8, 1));
  REQUIRE(names.IgnoredList(UiSettings{}).size() == 1);
  CHECK(names.IgnoredList(UiSettings{})[0].name == "Bob");

  UiSettings streamer;
  streamer.streamerMode = true;
  const auto hidden     = names.IgnoredList(streamer);
  REQUIRE(hidden.size() == 1);
  CHECK(hidden[0].name != "Bob");
  CHECK(hidden[0].name == names.Alias(7));

  TempPath file;
  UiFile   saved;
  saved.names = names.Book();
  REQUIRE(SaveUiFile(file.path, saved));
  auto loaded = LoadUiFile(file.path);
  REQUIRE(loaded);
  Names same;
  same.Configure("a:1", {});
  same.Load(loaded->names);
  CHECK(same.Ignored(7));
  Names otherServer;
  otherServer.Configure("b:1", {});
  otherServer.Load(loaded->names);
  CHECK_FALSE(otherServer.Ignored(7));
  CHECK(otherServer.IgnoredList(UiSettings{}).empty());
  CHECK(same.Unignore(7));
  CHECK_FALSE(same.Ignored(7));
}

TEST_CASE("Ignored authors disappear from history, deltas and bubbles; unignore never replays bubbles")
{
  auto        exchange = MakeExchange();
  ClientModel model;
  REQUIRE(model.RegisterChannel(1, 16));
  REQUIRE(model.Apply(
    model.Generation(),
    OnlinePlayersReplaced{
        {MakePlayer(1, "Alice"), MakePlayer(7, "Seven")}
  }));
  REQUIRE(model.Apply(model.Generation(), SelfPlayerAssigned{1}));
  auto own   = MakeMessage(11, 1, "mine");
  own.author = {1, "user1", "Alice"};
  REQUIRE(model.Apply(
    model.Generation(),
    ChatMessagesReceived{
        1,
        {MakeMessage(10, 1, "seven history"), own}
  }));
  Session session;
  session.PlayerNames().Configure("srv:1", {});
  Session::Frame frame;
  session.Process(*exchange, Drain(*exchange, model, SessionPhase::Ready), UiSettings{}, Domain::HiddenIdentity::None, frame);
  Settle(session, *exchange, model, frame);
  REQUIRE(frame.snapshot);

  CHECK_FALSE(session.Ignore(1));  // Self.
  CHECK(session.Ignore(7));
  session.Refresh();
  frame = {};
  session.Process(*exchange, Drain(*exchange, model, SessionPhase::Ready), UiSettings{}, Domain::HiddenIdentity::None, frame);
  Settle(session, *exchange, model, frame);
  REQUIRE(frame.snapshot);
  auto snapshot = Parse(frame.events[0]);
  CHECK(snapshot["refresh"].get<bool>());
  REQUIRE(snapshot["messages"].get_array().size() == 1);
  CHECK(snapshot["messages"][0]["text"].get<std::string>() == "mine");
  CHECK(snapshot["players"].get_array().size() == 2);  // Presence stays.

  auto system   = MakeMessage(13, 1, "announcement");
  system.author = {};
  REQUIRE(model.Apply(
    model.Generation(),
    ChatMessagesReceived{
        1,
        {MakeMessage(12, 1, "seven live"), system}
  }));
  frame = {};
  session.Process(*exchange, Drain(*exchange, model, SessionPhase::Ready), UiSettings{}, Domain::HiddenIdentity::None, frame);
  CHECK(frame.freshMessages.empty());
  REQUIRE(frame.events.size() == 1);
  auto delta = Parse(frame.events[0]);
  REQUIRE(delta["messages"].get_array().size() == 1);
  CHECK(delta["messages"][0]["text"].get<std::string>() == "announcement");

  // Unignore: history returns in the chat, but as a refresh without bubbles.
  CHECK(session.Unignore(7));
  session.Refresh();
  frame = {};
  session.Process(*exchange, Drain(*exchange, model, SessionPhase::Ready), UiSettings{}, Domain::HiddenIdentity::None, frame);
  Settle(session, *exchange, model, frame);
  REQUIRE(frame.snapshot);
  CHECK(Parse(frame.events[0])["messages"].get_array().size() == 4);
  CHECK(frame.freshMessages.empty());
  REQUIRE(model.Apply(model.Generation(), ChatMessagesReceived{1, {MakeMessage(14, 1, "seven again")}}));
  frame = {};
  session.Process(*exchange, Drain(*exchange, model, SessionPhase::Ready), UiSettings{}, Domain::HiddenIdentity::None, frame);
  REQUIRE(frame.freshMessages.size() == 1);
  CHECK(frame.freshMessages[0].messageId == 14);
}

TEST_CASE("Streamer mode projects only pseudonyms; messages keep their character snapshot")
{
  auto        exchange = MakeExchange();
  ClientModel model;
  REQUIRE(model.RegisterChannel(1, 16));
  REQUIRE(model.Apply(
    model.Generation(),
    OnlinePlayersReplaced{
        {MakePlayer(1, "Alice"), MakePlayer(7, "Seven")}
  }));
  REQUIRE(model.Apply(model.Generation(), SelfPlayerAssigned{1}));
  auto snapshotted          = MakeMessage(10, 1, "hello");
  snapshotted.characterName = "Lydia";
  REQUIRE(model.Apply(
    model.Generation(),
    ChatMessagesReceived{
        1,
        {snapshotted, MakeMessage(11, 1, "old history")}
  }));

  UiSettings character;
  character.nameMode = "character";
  Session session;
  session.PlayerNames().Configure("srv:1", {});
  Session::Frame frame;
  session.Process(*exchange, Drain(*exchange, model, SessionPhase::Ready), character, Domain::HiddenIdentity::None, frame);
  Settle(session, *exchange, model, frame, character);
  REQUIRE(frame.snapshot);
  auto plain = Parse(frame.events[0]);
  // Snapshot at sending, not the current "Nerevar"; no snapshot falls back to the display name.
  CHECK(plain["messages"][0]["author"]["name"].get<std::string>() == "Lydia");
  CHECK(plain["messages"][1]["author"]["name"].get<std::string>() == "Seven");
  CHECK(plain["players"][0]["name"].get<std::string>() == "Nerevar");

  UiSettings streamer   = character;
  streamer.streamerMode = true;
  session.Refresh();
  frame                       = {};
  auto output                 = Drain(*exchange, model, SessionPhase::Ready);
  output.status.savedLogin    = true;
  output.status.savedUsername = "user1";
  session.Process(*exchange, output, streamer, Domain::HiddenIdentity::None, frame);
  std::string all;
  for (const auto& event : frame.events)
    all += Json(event);
  Settle(session, *exchange, model, frame, streamer);
  REQUIRE(frame.snapshot);
  for (const auto& event : frame.events)
    all += Json(event);
  for (std::string_view real : {"Alice", "Seven", "user1", "user7", "Nerevar", "Lydia"})
    CHECK_MESSAGE(all.find(real) == std::string::npos, real);
  auto  hidden  = Parse(frame.events[0]);
  auto& players = hidden["players"];
  for (auto& player : players.get_array())
  {
    const auto name = player["name"].get<std::string>();
    CHECK(player["alias"].get<std::string>() == name);
    CHECK(player["username"].get<std::string>().empty());
    const auto id = std::stoull(player["id"].get<std::string>());
    // Nameplates ask the same resolver: identical label.
    CHECK(session.PlayerNames().NameFor(id, session.OnlinePlayers().at(id).data, "Nerevar", streamer) == name);
  }
}

TEST_CASE("Server refusals map to readable reasons")
{
  using Code = Dreamsleeve::Client::RequestRejectionCode;
  CHECK(Bridge::RejectionText(Code::TextNotAllowed, "x") == "Сообщение содержит запрещённые слова");
  CHECK(Bridge::RejectionText(Code::RateLimited, "x").starts_with("Слишком часто"));
  CHECK(Bridge::RejectionText(Code::InvalidRequest, "Message exceeds 2000 characters.") == "Сообщение слишком длинное");
  CHECK(Bridge::RejectionText(static_cast<Code>(99), "future reason") == "future reason");
  CHECK(Bridge::RejectionText(Code::NotChannelMember, "") == "Сервер отклонил сообщение");
}

TEST_CASE("Local filter shows, masks or hides server-flagged ranges")
{
  Domain::ChatMessage message{
      5,
      1,
      Domain::PlayerData{7, "seven", "Seven"},
      "Ну ты хач, а?",
      Domain::FromUnixMilliseconds(0)
  };
  message.flagged = {
      Domain::TextSpan{10, 6}
  };  // "хач": three two-byte letters after 10 bytes.
  UiSettings settings;
  CHECK(Bridge::ShownText(message, settings, false) == message.messageText);
  settings.textFilter = "mask";
  CHECK(Bridge::ShownText(message, settings, false) == "Ну ты ***, а?");
  settings.textFilter = "hide";
  CHECK_FALSE(Bridge::ShownText(message, settings, false));
  CHECK(Bridge::ShownText(message, settings, true) == std::string{Bridge::HiddenOwnText});
  message.flagged.clear();
  CHECK(Bridge::ShownText(message, settings, false) == message.messageText);

  CHECK(
    Bridge::MaskFlagged(
      "bad phrase here",
      {
          Domain::TextSpan{0, 10}
  }) == "*** ****** here");
  settings.textFilter = "loud";
  CHECK(Dreamsleeve::Host::Normalize(settings).textFilter == "off");
}

TEST_CASE("Session applies the text filter to history, deltas and bubbles alike")
{
  auto        exchange = MakeExchange();
  ClientModel model;
  REQUIRE(model.RegisterChannel(1, 16));
  REQUIRE(model.Apply(
    model.Generation(),
    OnlinePlayersReplaced{
        {MakePlayer(1, "Alice"), MakePlayer(7, "Seven")}
  }));
  REQUIRE(model.Apply(model.Generation(), SelfPlayerAssigned{1}));
  auto flagged    = MakeMessage(10, 1, "join t.me/spam now");
  flagged.flagged = {
      Domain::TextSpan{5, 9}
  };
  REQUIRE(model.Apply(model.Generation(), ChatMessagesReceived{1, {flagged}}));

  UiSettings mask;
  mask.textFilter = "mask";
  Session session;
  session.PlayerNames().Configure("srv:1", {});
  Session::Frame frame;
  session.Process(*exchange, Drain(*exchange, model, SessionPhase::Ready), mask, Domain::HiddenIdentity::None, frame);
  Settle(session, *exchange, model, frame, mask);
  REQUIRE(frame.snapshot);
  auto masked = Parse(frame.events[0]);
  CHECK(masked["messages"][0]["text"].get<std::string>() == "join ********* now");
  CHECK(masked["messages"][0]["filtered"].get<bool>());
  CHECK(Json(frame.events[0]).find("spam") == std::string::npos);

  auto live    = MakeMessage(11, 1, "t.me/spam");
  live.flagged = {
      Domain::TextSpan{0, 9}
  };
  REQUIRE(model.Apply(model.Generation(), ChatMessagesReceived{1, {live}}));
  frame = {};
  session.Process(*exchange, Drain(*exchange, model, SessionPhase::Ready), mask, Domain::HiddenIdentity::None, frame);
  REQUIRE(frame.freshMessages.size() == 1);
  CHECK(frame.freshMessages[0].messageText == "*********");

  UiSettings hide;
  hide.textFilter = "hide";
  auto again      = MakeMessage(12, 1, "t.me/spam");
  again.flagged   = {
      Domain::TextSpan{0, 9}
  };
  auto own    = MakeMessage(13, 1, "t.me/mine");
  own.author  = {1, "user1", "Alice"};
  own.flagged = {
      Domain::TextSpan{0, 9}
  };
  REQUIRE(model.Apply(
    model.Generation(),
    ChatMessagesReceived{
        1,
        {again, own, MakeMessage(14, 1, "clean")}
  }));
  frame = {};
  session.Process(*exchange, Drain(*exchange, model, SessionPhase::Ready), hide, Domain::HiddenIdentity::None, frame);
  REQUIRE(frame.freshMessages.size() == 1);
  CHECK(frame.freshMessages[0].messageText == "clean");
  REQUIRE(frame.events.size() == 1);
  auto delta = Parse(frame.events[0]);
  REQUIRE(delta["messages"].get_array().size() == 2);
  CHECK(delta["messages"][0]["text"].get<std::string>() == std::string{Bridge::HiddenOwnText});
  CHECK(delta["messages"][1]["text"].get<std::string>() == "clean");
  CHECK(Json(frame.events[0]).find("spam") == std::string::npos);
}

TEST_CASE("Older UI files keep bubble style, firefly height and ground mark defaults; colours are validated")
{
  TempPath file;
  {
    std::ofstream output{file.path};
    output << "[ui.chat]\nbubbleBackground = 0.5\n";
  }
  auto loaded = LoadUiFile(file.path);
  REQUIRE(loaded);
  const UiSettings defaults{};
  CHECK(loaded->ui.chat.bubbleBorder == defaults.bubbleBorder);
  CHECK(loaded->ui.chat.bubbleTextColor == "#EEECE5");
  CHECK(loaded->ui.chat.fireflyNameColor == "#EEECE5");
  CHECK(loaded->ui.chat.fireflyHeightOffset == 110);
  CHECK(loaded->ui.chat.showGroundNotes);
  CHECK(loaded->ui.chat.showDeathMarks);
  CHECK(loaded->ui.chat.maxVisibleNotes == 16);
  CHECK(loaded->ui.chat.groundDrawDistance == 4096);
  CHECK(loaded->ui.chat.groundNameDistance == 600);
  CHECK(loaded->ui.chat.groundTextDistance == 150);
  CHECK(loaded->ui.chat.deathTextColor == "#D9534F");
  CHECK_FALSE(loaded->ui.chat.combatHideGroundMarks);
  CHECK_FALSE(loaded->ui.chat.combatHideGroundText);
  CHECK(loaded->ui.chat.markDateStyle == "tamriel");
  CHECK(loaded->ui.chat.deathDateHeader);
  CHECK_FALSE(loaded->ui.chat.noteDateHeader);
  CHECK(loaded->ui.chat.markDateColor == "#A9A69B");

  UiFile edited;
  edited.ui.chat.bubbleBorder          = false;
  edited.ui.chat.bubbleTextColor       = "#FF8800";
  edited.ui.chat.fireflyNameColor      = "#00FF88";
  edited.ui.chat.fireflyHeightOffset   = 90;
  edited.ui.chat.showGroundNotes       = false;
  edited.ui.chat.maxVisibleDeaths      = 4;
  edited.ui.chat.groundDrawDistance    = 2000;
  edited.ui.chat.groundNoteOffset      = -10;
  edited.ui.chat.deathMarkOffset       = 30;
  edited.ui.chat.groundTextColor       = "#ABCDEF";
  edited.ui.chat.deathBackground       = 0.2;
  edited.ui.chat.deathBorder           = false;
  edited.ui.chat.combatHideGroundMarks = true;
  edited.ui.chat.markDateStyle         = "earth";
  edited.ui.chat.noteDateHeader        = true;
  edited.ui.chat.markDateColor         = "#112233";
  REQUIRE(SaveUiFile(file.path, edited));
  auto saved = LoadUiFile(file.path);
  REQUIRE(saved);
  CHECK(*saved == edited);

  auto invalid                = edited.ui.chat;
  invalid.bubbleTextColor     = "red";
  invalid.fireflyNameColor    = "#12345";
  invalid.deathTextColor      = "#GGGGGG";
  invalid.fireflyHeightOffset = 1000;
  invalid.maxVisibleNotes     = 0;
  invalid.maxVisibleDeaths    = 7.9;
  invalid.groundNoteOffset    = -100;
  invalid.groundTextDistance  = 10;
  invalid.markDateStyle       = "gregorian";
  invalid.markDateColor       = "grey";
  auto normalized             = Dreamsleeve::Host::Normalize(invalid);
  CHECK(normalized.bubbleTextColor == "#EEECE5");
  CHECK(normalized.fireflyNameColor == "#EEECE5");
  CHECK(normalized.deathTextColor == "#D9534F");
  CHECK(normalized.groundTextColor == "#ABCDEF");
  CHECK(normalized.markDateStyle == "tamriel");
  CHECK(normalized.markDateColor == "#A9A69B");
  CHECK(normalized.fireflyHeightOffset == 512);
  CHECK(normalized.maxVisibleNotes == 1);
  CHECK(normalized.maxVisibleDeaths == 7);
  CHECK(normalized.groundNoteOffset == -64);
  CHECK(normalized.groundTextDistance == 50);

  CHECK(Dreamsleeve::Host::ParseColor("#D9534F") == 0xD9534F);
  CHECK(Dreamsleeve::Host::ParseColor("#d9534f") == 0xD9534F);
  CHECK_FALSE(Dreamsleeve::Host::ParseColor("D9534F"));
  CHECK_FALSE(Dreamsleeve::Host::ParseColor("#D9534"));
  CHECK_FALSE(Dreamsleeve::Host::ParseColor("#D9534FF"));
  CHECK_FALSE(Dreamsleeve::Host::ParseColor("#D953 F"));
}

namespace
{

  Domain::GroundMark MakeMark(Domain::GroundMarkId id, Domain::PlayerId author, Domain::GroundMarkKind kind, std::string text)
  {
    return Domain::GroundMark{
        id,
        Domain::PlayerData{author, "user" + std::to_string(author), "Player" + std::to_string(author)},
        kind,
        std::move(text),
        {},
        {{"skyrim.esm", 0x1A26F}, {100, 200, 300}, 1.5f},
        Domain::FromUnixMilliseconds(1700000000000)
    };
  }

}

TEST_CASE("Session projects visible marks for the game and the server's own list for the UI")
{
  auto        exchange = MakeExchange();
  ClientModel model;
  REQUIRE(model.RegisterChannel(1, 16));
  REQUIRE(model.Apply(model.Generation(), SelfPlayerAssigned{1}));
  REQUIRE(model.Apply(
    model.Generation(),
    GroundMarksChanged{
        1, {MakeMark(10, 1, Domain::GroundMarkKind::Note, "mine"), MakeMark(11, 7, Domain::GroundMarkKind::Death, "Bear")}, {}, true}));
  // The server lists every own mark, the far one included.
  auto far = MakeMark(9, 1, Domain::GroundMarkKind::Death, "Dragon");
  far.placement.locationId = {"skyrim.esm", 0x16BB4};
  far.characterName        = "Nerevar";
  REQUIRE(model.Apply(model.Generation(), OwnGroundMarksReplaced{{MakeMark(10, 1, Domain::GroundMarkKind::Note, "mine"), far}}));
  Session        session;
  Session::Frame frame;
  session.Process(*exchange, Drain(*exchange, model, SessionPhase::Ready), UiSettings{}, Domain::HiddenIdentity::None, frame);
  Settle(session, *exchange, model, frame);
  REQUIRE(frame.snapshot);
  CHECK(frame.visibleMarksChanged);
  CHECK(session.VisibleMarks().size() == 2);
  REQUIRE(session.OwnMarks().size() == 2);
  CHECK(session.OwnMarks().contains(9));
  auto snapshot = Parse(frame.events[0]);
  CHECK(snapshot["groundMarksSupported"].get<bool>());
  REQUIRE(snapshot["groundMarks"].size() == 2);
  CHECK(snapshot["groundMarks"][0]["id"].get<std::string>() == "9");
  CHECK(snapshot["groundMarks"][0]["kind"].get<std::string>() == "death");
  CHECK(snapshot["groundMarks"][0]["location"].get<std::string>() == "skyrim.esm:016BB4");
  CHECK(snapshot["groundMarks"][0]["character"].get<std::string>() == "Nerevar");
  CHECK(snapshot["groundMarks"][1]["text"].get<std::string>() == "mine");
  REQUIRE(snapshot["nearbyMarks"].size() == 2);
  CHECK(snapshot["nearbyMarks"][1]["id"].get<std::string>() == "11");
  CHECK(snapshot["nearbyMarks"][1]["author"].get<std::string>() == "Player7");
  CHECK(snapshot["nearbyMarks"][1]["text"].get<std::string>() == "Bear");
  CHECK(snapshot["nearbyMarks"][1]["x"].get<double>() == 100);

  // A visible delta changes the nearby list only; the own list waits for the server.
  REQUIRE(model.Apply(model.Generation(), GroundMarksChanged{2, {MakeMark(12, 7, Domain::GroundMarkKind::Death, "Wolf")}, {11}, false}));
  frame = {};
  session.Process(*exchange, Drain(*exchange, model, SessionPhase::Ready), UiSettings{}, Domain::HiddenIdentity::None, frame);
  CHECK(frame.visibleMarksChanged);
  CHECK(session.VisibleMarks().size() == 2);
  CHECK(session.VisibleMarks().contains(12));
  CHECK(session.OwnMarks().size() == 2);
  REQUIRE(frame.events.size() == 1);
  CHECK(Type(frame.events[0]) == "nearbyMarks");
  CHECK(Parse(frame.events[0])["marks"].size() == 2);

  // The server's replacement is the only source of the own list.
  REQUIRE(model.Apply(model.Generation(), OwnGroundMarksReplaced{{far}}));
  frame = {};
  session.Process(*exchange, Drain(*exchange, model, SessionPhase::Ready), UiSettings{}, Domain::HiddenIdentity::None, frame);
  CHECK(session.OwnMarks().size() == 1);
  CHECK_FALSE(session.OwnMarks().contains(10));
  REQUIRE(frame.events.size() == 1);
  CHECK(Type(frame.events[0]) == "groundMarks");
  CHECK(Parse(frame.events[0])["marks"].size() == 1);

  // Ignored authors leave the nearby list; a hidden foreign text is empty and a hidden own text a placeholder.
  auto flagged    = MakeMark(14, 7, Domain::GroundMarkKind::Note, "bad word");
  flagged.flagged = {{0, 3}};
  auto ownFlagged    = MakeMark(15, 1, Domain::GroundMarkKind::Note, "bad word");
  ownFlagged.flagged = {{0, 3}};
  REQUIRE(model.Apply(model.Generation(), GroundMarksChanged{3, {flagged, ownFlagged}, {}, false}));
  UiSettings hide;
  hide.textFilter = "hide";
  frame           = {};
  session.Process(*exchange, Drain(*exchange, model, SessionPhase::Ready), hide, Domain::HiddenIdentity::None, frame);
  REQUIRE(frame.events.size() == 1);
  auto nearby = Parse(frame.events[0])["marks"];
  REQUIRE(nearby.size() == 4);
  CHECK(nearby[2]["text"].get<std::string>().empty());
  CHECK(nearby[3]["text"].get<std::string>() == "[скрыто фильтром]");
  session.PlayerNames().Configure("s", {});
  REQUIRE(session.Ignore(7));
  REQUIRE(model.Apply(model.Generation(), GroundMarksChanged{4, {}, {14}, false}));
  frame = {};
  session.Process(*exchange, Drain(*exchange, model, SessionPhase::Ready), UiSettings{}, Domain::HiddenIdentity::None, frame);
  auto filtered = Parse(frame.events[0])["marks"];
  REQUIRE(filtered.size() == 2);
  CHECK(filtered[0]["id"].get<std::string>() == "10");
  CHECK(filtered[1]["id"].get<std::string>() == "15");
}

TEST_CASE("Session correlates note, removal and death requests with their outcomes")
{
  auto        exchange = MakeExchange();
  ClientModel model;
  REQUIRE(model.RegisterChannel(1, 16));
  REQUIRE(model.Apply(model.Generation(), SelfPlayerAssigned{1}));
  Session        session;
  Session::Frame frame;
  session.Process(*exchange, Drain(*exchange, model, SessionPhase::Ready), UiSettings{}, Domain::HiddenIdentity::None, frame);
  Settle(session, *exchange, model, frame);
  REQUIRE(session.Ready());

  const Domain::MarkSpot here{
      {{"skyrim.esm", 0x1A26F}, {1, 2, 3}, 0.5f},
      {4, 201, 8, 17, 2, 14, 5}
  };
  REQUIRE(session.PlaceGroundNote(*exchange, "ui-1", "hello", here));
  REQUIRE(session.ReportDeath(*exchange, "Bear", here));
  REQUIRE(session.RemoveGroundMark(*exchange, "ui-2", 77));
  CHECK(session.PendingMarkCount() == 3);
  std::vector<QueuedClientCommand> commands;
  exchange->TakeCommands(commands);
  REQUIRE(commands.size() == 3);
  const auto* note = std::get_if<PlaceGroundNote>(&commands[0].command);
  REQUIRE(note);
  CHECK(note->text == "hello");
  CHECK(note->placement == here.placement);
  CHECK(note->gameDate == here.gameDate);
  const auto* death = std::get_if<ReportDeath>(&commands[1].command);
  REQUIRE(death);
  CHECK(death->label == "Bear");
  CHECK(death->gameDate == here.gameDate);
  const auto* removal = std::get_if<RemoveGroundMark>(&commands[2].command);
  REQUIRE(removal);
  CHECK(removal->markId == 77);

  // Placed with an eviction: the UI row settles and learns the evicted id.
  REQUIRE(exchange->Publish(model, false, SessionPhase::Ready, "Tamriel", CommandResult{model.Generation(), note->requestId, MarkPlaced{41, 40}}));
  ClientOutput output;
  exchange->Drain(output);
  frame = {};
  session.Process(*exchange, output, UiSettings{}, Domain::HiddenIdentity::None, frame);
  REQUIRE(frame.events.size() == 1);
  auto placed = Parse(frame.events[0]);
  CHECK(placed["type"].get<std::string>() == "markResult");
  CHECK(placed["requestId"].get<std::string>() == "ui-1");
  CHECK(placed["markId"].get<std::string>() == "41");
  CHECK(placed["evictedId"].get<std::string>() == "40");
  CHECK(session.PendingMarkCount() == 2);

  // A death report settles without any UI event, a note in the log only.
  REQUIRE(exchange->Publish(model, false, SessionPhase::Ready, "Tamriel", CommandResult{model.Generation(), death->requestId, MarkPlaced{42}}));
  exchange->Drain(output);
  frame = {};
  session.Process(*exchange, output, UiSettings{}, Domain::HiddenIdentity::None, frame);
  CHECK(frame.events.empty());
  REQUIRE(frame.notes.size() == 1);
  CHECK(frame.notes[0].find("42") != std::string::npos);

  // A refused removal becomes a readable error for its UI row, never a chat sendResult.
  REQUIRE(exchange->PublishResult({model.Generation(), removal->requestId, ServerRejection{RequestRejectionCode::GroundMarkNotFound, "", ""}}));
  frame = {};
  session.Process(*exchange, Drain(*exchange, model, SessionPhase::Ready), UiSettings{}, Domain::HiddenIdentity::None, frame);
  REQUIRE(frame.events.size() == 1);
  auto refused = Parse(frame.events[0]);
  CHECK(refused["type"].get<std::string>() == "markResult");
  CHECK(refused["requestId"].get<std::string>() == "ui-2");
  CHECK(refused["error"].get<std::string>() == "Метка не найдена или уже удалена");
  CHECK(session.PendingMarkCount() == 0);

  CHECK(Bridge::RejectionText(RequestRejectionCode::GroundMarkAreaFull, "") == "Здесь уже слишком много меток");
  CHECK(Bridge::RejectionText(RequestRejectionCode::InvalidRequest, "Note exceeds 200 characters.") == "Текст метки слишком длинный");
  CHECK(Bridge::RejectionText(RequestRejectionCode::InvalidRequest, "The mark is not where the player is.") == "Метку нельзя оставить здесь");
}

TEST_CASE("Bridge validates ground mark commands")
{
  CHECK(CommandOf<Bridge::Commands::PlaceGroundNote>(R"({"type":"placeGroundNote","requestId":"5","text":"Осторожно, тролль"})").text ==
        "Осторожно, тролль");
  CHECK_FALSE(Bridge::ParseCommand(R"({"type":"placeGroundNote","text":"x"})"));
  CHECK(
    CommandOf<Bridge::Commands::RemoveGroundMark>(R"({"type":"removeGroundMark","requestId":"6","markId":"18446744073709551615"})").markId.value ==
    18446744073709551615ull);
  CHECK_FALSE(Bridge::ParseCommand(R"({"type":"removeGroundMark","markId":"6"})"));
  auto mark = Bridge::ToUiGroundMark(MakeMark(3, 1, Domain::GroundMarkKind::Death, "Wolf"), UiSettings{}, true);
  CHECK(mark.id == "3");
  CHECK(mark.kind == "death");
  CHECK(mark.text == "Wolf");
  CHECK(mark.time == 1700000000000);
  CHECK(mark.location == "skyrim.esm:01A26F");
  CHECK(mark.z == 300);
  CHECK_FALSE(mark.author);
  CHECK_FALSE(mark.gameDate);
  auto dated     = MakeMark(4, 1, Domain::GroundMarkKind::Death, "Wolf");
  dated.gameDate = Domain::GameDate{4, 201, 8, 17, 2, 14, 5};
  CHECK(Bridge::ToUiGroundMark(dated, UiSettings{}, true).gameDate == "Тирдас, 17 Последнего зерна 4Э 201, 14:05");
  UiSettings earth;
  earth.markDateStyle = "earth";
  CHECK(Bridge::ToUiGroundMark(dated, earth, true).gameDate == "Вторник, 17 августа 4Э 201, 14:05");
}

TEST_CASE("Game dates name the weekday and the month in the chosen calendar")
{
  using Dreamsleeve::Host::FormatGameDate;
  CHECK(FormatGameDate({4, 201, 1, 1, 0, 0, 0}, "tamriel") == "Сандас, 1 Утренней звезды 4Э 201, 00:00");
  CHECK(FormatGameDate({4, 202, 12, 31, 6, 23, 59}, "tamriel") == "Лордас, 31 Вечерней звезды 4Э 202, 23:59");
  CHECK(FormatGameDate({4, 201, 10, 3, 5, 9, 7}, "earth") == "Пятница, 3 октября 4Э 201, 09:07");
  CHECK(FormatGameDate({3, 433, 6, 16, 1, 12, 0}, "anything") == "Морндас, 16 Середины года 3Э 433, 12:00");
  // Out-of-range parts never index past the tables.
  CHECK(FormatGameDate({4, 201, 13, 1, 9, 0, 0}, "tamriel") == "?, 1 ? 4Э 201, 00:00");
}

TEST_CASE("A pseudonymous profile is named by its pseudonym in every mode; streamer mode keeps its alias")
{
  Names names;
  names.Configure("srv:1", {"Лис"});
  const Domain::PlayerData hidden{9, "", "Страж 2", true};
  for (std::string_view mode : {"username", "display", "character"})
  {
    UiSettings settings;
    settings.nameMode = std::string{mode};
    CHECK(names.NameFor(9, hidden, std::nullopt, settings) == "Страж 2");
    CHECK(names.NameFor(9, hidden, std::string{"Leaked"}, settings) == "Страж 2");
  }
  const Domain::PlayerData unnamed{9, "", "", false};
  UiSettings               character;
  character.nameMode = "character";
  CHECK(names.NameFor(9, unnamed, std::nullopt, character) == NeutralName);
  UiSettings streamer;
  streamer.streamerMode = true;
  CHECK(names.NameFor(9, hidden, std::nullopt, streamer) == "Лис");
  CHECK(Names::PlateName("Страж 2", hidden) == "~Страж 2");
  CHECK(Names::PlateName("Alice", Domain::PlayerData{1, "alice", "Alice"}) == "Alice");
}

TEST_CASE("Pseudonymous players and authors reach the UI flagged and without real names")
{
  auto        exchange = MakeExchange();
  ClientModel model;
  REQUIRE(model.RegisterChannel(1, 16));
  auto hidden          = MakePlayer(9, "Страж");
  hidden.data          = {9, "", "Страж", true};
  hidden.characterName = std::nullopt;
  REQUIRE(model.Apply(
    model.Generation(),
    OnlinePlayersReplaced{
        {MakePlayer(1, "Alice"), hidden}
  }));
  REQUIRE(model.Apply(model.Generation(), SelfPlayerAssigned{1}));
  auto message   = MakeMessage(10, 1, "from the shadows");
  message.author = hidden.data;
  REQUIRE(model.Apply(model.Generation(), ChatMessagesReceived{1, {message}}));

  UiSettings username;
  username.nameMode = "username";
  Session session;
  session.PlayerNames().Configure("srv:1", {});
  Session::Frame frame;
  session.Process(*exchange, Drain(*exchange, model, SessionPhase::Ready), username, Domain::HiddenIdentity::None, frame);
  Settle(session, *exchange, model, frame, username);
  REQUIRE(frame.snapshot);
  auto        snapshot = Parse(frame.events[0]);
  const auto& author   = snapshot["messages"][0]["author"];
  CHECK(author["name"].get<std::string>() == "Страж");
  CHECK(author["pseudonymous"].get<bool>());
  CHECK(author["username"].get<std::string>().empty());
  CHECK_FALSE(author.contains("character"));
  bool found = false;
  for (auto& player : snapshot["players"].get_array())
    if (player["id"].get<std::string>() == "9")
    {
      found = true;
      CHECK(player["name"].get<std::string>() == "Страж");
      CHECK(player["displayName"].get<std::string>() == "Страж");
      CHECK(player["pseudonymous"].get<bool>());
      CHECK_FALSE(player.contains("alias"));
    }
    else
      CHECK_FALSE(player["pseudonymous"].get<bool>());
  CHECK(found);
  CHECK(Json(frame.events[0]).find("user9") == std::string::npos);
}

TEST_CASE("The hide-my-name switch waits for the server and keeps the preference until it answers")
{
  auto        exchange = MakeExchange();
  ClientModel model;
  REQUIRE(model.RegisterChannel(1, 16));
  REQUIRE(model.Apply(model.Generation(), OnlinePlayersReplaced{{MakePlayer(1, "Alice")}}));
  REQUIRE(model.Apply(model.Generation(), SelfPlayerAssigned{1}));
  Session        session;
  Session::Frame frame;
  UiSettings     settings;
  const auto     identityOf = [](const Session::Frame& value) {
    std::optional<glz::generic> found;
    for (const auto& event : value.events)
      if (Type(event) == "identity") found = Parse(event);
    return found;
  };

  using Domain::HiddenIdentity;
  auto hiding = HiddenIdentity::None;
  CHECK_FALSE(session.SetIdentityVisibility(*exchange, HiddenIdentity::Everywhere));
  session.Process(*exchange, Drain(*exchange, model, SessionPhase::Ready), settings, hiding, frame);
  Settle(session, *exchange, model, frame, settings);
  REQUIRE(session.Ready());

  REQUIRE(session.SetIdentityVisibility(*exchange, HiddenIdentity::ExceptGroundMarks));
  CHECK_FALSE(session.SetIdentityVisibility(*exchange, HiddenIdentity::None));
  std::vector<QueuedClientCommand> commands;
  exchange->TakeCommands(commands);
  REQUIRE(commands.size() == 1);
  const auto request = std::get<SetIdentityVisibility>(commands[0].command);
  CHECK(request.hiding == HiddenIdentity::ExceptGroundMarks);
  auto pending = session.Identity(hiding);
  CHECK(pending.pending);
  CHECK(pending.mode == "exceptGroundMarks");
  CHECK_FALSE(pending.pseudonym);

  exchange->PublishIdentity("Страж", HiddenIdentity::ExceptGroundMarks);
  REQUIRE(exchange->Publish(
    model, false, SessionPhase::Ready, "Tamriel", CommandResult{model.Generation(), request.requestId, IdentityChanged{HiddenIdentity::ExceptGroundMarks}}));
  ClientOutput output;
  exchange->Drain(output);
  frame = {};
  session.Process(*exchange, output, settings, hiding, frame);
  REQUIRE(frame.hideIdentity);
  CHECK(*frame.hideIdentity == HiddenIdentity::ExceptGroundMarks);
  auto settled = identityOf(frame);
  REQUIRE(settled);
  CHECK((*settled)["mode"].get<std::string>() == "exceptGroundMarks");
  CHECK_FALSE((*settled)["pending"].get<bool>());
  CHECK((*settled)["pseudonym"].get<std::string>() == "Страж");
  hiding = HiddenIdentity::ExceptGroundMarks;

  // A refusal returns the choice to the server's state and names the reason.
  REQUIRE(session.SetIdentityVisibility(*exchange, HiddenIdentity::None));
  exchange->TakeCommands(commands);
  const auto second = std::get<SetIdentityVisibility>(commands[0].command).requestId;
  REQUIRE(exchange->PublishResult({model.Generation(), second, ServerRejection{RequestRejectionCode::RateLimited, "too soon", "hidden"}}));
  frame = {};
  session.Process(*exchange, Drain(*exchange, model, SessionPhase::Ready), settings, hiding, frame);
  CHECK_FALSE(frame.hideIdentity);
  auto refused = identityOf(frame);
  REQUIRE(refused);
  CHECK((*refused)["mode"].get<std::string>() == "exceptGroundMarks");
  CHECK_FALSE((*refused)["pending"].get<bool>());
  CHECK((*refused)["error"].get<std::string>().starts_with("Слишком часто"));

  // An opening refused for hidden names stops automatic reconnects.
  REQUIRE(exchange->PublishResult({model.Generation(), 77, ServerRejection{RequestRejectionCode::HiddenIdentityNotAllowed, "", ""}}));
  frame = {};
  session.Process(*exchange, Drain(*exchange, model, SessionPhase::Disconnected), settings, hiding, frame);
  CHECK(frame.identityRefused);
  auto disallowed = identityOf(frame);
  REQUIRE(disallowed);
  CHECK((*disallowed)["error"].get<std::string>() == "Сервер не разрешает скрывать имя");
  CHECK_FALSE(disallowed->contains("pseudonym"));
}

TEST_CASE("The hide-my-name preference round-trips through ui.toml and defaults to shown")
{
  TempPath file;
  UiFile   edited;
  CHECK(edited.ui.hideIdentity == "off");
  edited.ui.hideIdentity = "exceptGroundMarks";
  REQUIRE(SaveUiFile(file.path, edited));
  auto loaded = LoadUiFile(file.path);
  REQUIRE(loaded);
  CHECK(loaded->ui.hideIdentity == "exceptGroundMarks");
  {
    std::ofstream output{file.path, std::ios::binary | std::ios::trunc};
    output << "version = 1\n[ui]\nhideIdentity = \"sometimes\"\n[ui.chat]\nstreamerMode = true\n";
  }
  auto unknown = LoadUiFile(file.path);
  REQUIRE(unknown);
  CHECK(unknown->ui.chat.streamerMode);
  CHECK(unknown->ui.hideIdentity == "off");
  const auto command = CommandOf<Bridge::Commands::SetIdentityVisibility>(R"({"type":"setIdentityVisibility","hiding":"everywhere"})");
  CHECK(Bridge::HidingOf(command.hiding) == Domain::HiddenIdentity::Everywhere);
  CHECK_FALSE(Bridge::ParseCommand(R"({"type":"setIdentityVisibility","hiding":"sometimes"})"));
}

TEST_CASE("A display name change waits for the server and reports the stored name or the refusal")
{
  auto        exchange = MakeExchange();
  ClientModel model;
  REQUIRE(model.RegisterChannel(1, 16));
  REQUIRE(model.Apply(model.Generation(), OnlinePlayersReplaced{{MakePlayer(1, "Alice")}}));
  REQUIRE(model.Apply(model.Generation(), SelfPlayerAssigned{1}));
  Session        session;
  Session::Frame frame;
  UiSettings     settings;
  const auto     nameOf = [](const Session::Frame& value) {
    std::optional<glz::generic> found;
    for (const auto& event : value.events)
      if (Type(event) == "displayName") found = Parse(event);
    return found;
  };

  CHECK_FALSE(session.ChangeDisplayName(*exchange, "Новое Имя"));
  session.Process(*exchange, Drain(*exchange, model, SessionPhase::Ready), settings, Domain::HiddenIdentity::None, frame);
  Settle(session, *exchange, model, frame, settings);
  REQUIRE(session.Ready());

  REQUIRE(session.ChangeDisplayName(*exchange, "Новое Имя"));
  CHECK_FALSE(session.ChangeDisplayName(*exchange, "Другое"));
  std::vector<QueuedClientCommand> commands;
  exchange->TakeCommands(commands);
  REQUIRE(commands.size() == 1);
  const auto request = std::get<ChangeDisplayName>(commands[0].command);
  CHECK(request.displayName == "Новое Имя");
  frame = {};
  session.Process(*exchange, Drain(*exchange, model, SessionPhase::Ready), settings, Domain::HiddenIdentity::None, frame);
  auto pending = nameOf(frame);
  REQUIRE(pending);
  CHECK((*pending)["pending"].get<bool>());

  REQUIRE(exchange->Publish(model, false, SessionPhase::Ready, "Tamriel", CommandResult{model.Generation(), request.requestId, NameChanged{"Новое Имя"}}));
  ClientOutput output;
  exchange->Drain(output);
  frame = {};
  session.Process(*exchange, output, settings, Domain::HiddenIdentity::None, frame);
  auto settled = nameOf(frame);
  REQUIRE(settled);
  CHECK_FALSE((*settled)["pending"].get<bool>());
  CHECK((*settled)["changed"].get<std::string>() == "Новое Имя");
  // The stored name is reported once.
  frame = {};
  session.Process(*exchange, Drain(*exchange, model, SessionPhase::Ready), settings, Domain::HiddenIdentity::None, frame);
  auto quiet = nameOf(frame);
  REQUIRE(quiet);
  CHECK_FALSE(quiet->contains("changed"));

  // A refusal names the wait in the UI language.
  REQUIRE(session.ChangeDisplayName(*exchange, "Третье"));
  exchange->TakeCommands(commands);
  const auto second = std::get<ChangeDisplayName>(commands[0].command).requestId;
  REQUIRE(exchange->PublishResult({model.Generation(), second, ServerRejection{RequestRejectionCode::RateLimited, "The display name can be changed again in 90 min.", "display_name"}}));
  frame = {};
  session.Process(*exchange, Drain(*exchange, model, SessionPhase::Ready), settings, Domain::HiddenIdentity::None, frame);
  auto refused = nameOf(frame);
  REQUIRE(refused);
  CHECK_FALSE((*refused)["pending"].get<bool>());
  CHECK((*refused)["error"].get<std::string>() == "Имя можно сменить снова через 90 мин");
  CHECK(
    Bridge::DisplayNameRejectionText(RequestRejectionCode::RateLimited, "The display name can be changed again in 600 min.") ==
    "Имя можно сменить снова через 10 ч");
  CHECK(Bridge::DisplayNameRejectionText(RequestRejectionCode::TextNotAllowed, "") == "Имя содержит запрещённые слова");
  CHECK(Bridge::DisplayNameRejectionText(RequestRejectionCode::DisplayNameChangeNotAllowed, "") == "Сервер не разрешает менять имя");

  // A lost connection ends the wait instead of leaving it forever.
  REQUIRE(session.ChangeDisplayName(*exchange, "Четвёртое"));
  exchange->TakeCommands(commands);
  frame = {};
  session.Process(*exchange, Drain(*exchange, model, SessionPhase::Disconnected), settings, Domain::HiddenIdentity::None, frame);
  auto dropped = nameOf(frame);
  REQUIRE(dropped);
  CHECK_FALSE((*dropped)["pending"].get<bool>());
  CHECK((*dropped)["error"].get<std::string>() == "Соединение прервано до ответа сервера");

  CHECK(CommandOf<Bridge::Commands::ChangeDisplayName>(R"({"type":"changeDisplayName","displayName":"Имя"})").displayName == "Имя");
  CHECK_FALSE(
    Bridge::ParseCommand(std::format(R"({{"type":"changeDisplayName","displayName":"{}"}})", std::string(Bridge::MaxDisplayName + 1, 'x'))));
}

TEST_SUITE_END();
