#include <doctest/doctest.h>
#include <glaze/glaze.hpp>
import std;
import Dreamsleeve.Host.Session;
import Dreamsleeve.Host.Bubbles;
import Dreamsleeve.Client.Model;

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

  // Minimal JSON view for assertions; the UI parser is the real contract owner.
  glz::generic Parse(const std::string& json)
  {
    glz::generic value;
    REQUIRE_FALSE(glz::read_json(value, json));
    return value;
  }

  std::string Type(const std::string& json)
  {
    return Parse(json)["type"].get<std::string>();
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
    session.Process(exchange, output, settings, frame);
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

TEST_CASE("Older UI files inherit name preferences from client configuration")
{
  TempPath file;
  UiFile   seed;
  seed.ui.chat.showFireflyNames    = false;
  seed.ui.chat.fireflyNameFontSize = 30;
  auto missing                     = LoadUiFile(file.path, seed);
  REQUIRE(missing);
  CHECK(*missing == seed);
  {
    std::ofstream output{file.path};
    output << "[ui.chat]\nfontSize = 20\nfireflyNameOffset = 75\n";
  }
  auto loaded = LoadUiFile(file.path, seed);
  REQUIRE(loaded);
  CHECK_FALSE(loaded->ui.chat.showFireflyNames);
  CHECK(loaded->ui.chat.fireflyNameFontSize == 30);
  CHECK(loaded->ui.chat.fireflyNameOffset == 75);
  auto invalid                = loaded->ui.chat;
  invalid.fireflyNameFontSize = 100;
  invalid.fireflyNameOffset   = -1;
  auto normalized             = Dreamsleeve::Host::Normalize(invalid);
  CHECK(normalized.fireflyNameFontSize == 48);
  CHECK(normalized.fireflyNameOffset == 0);
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
  session.Process(*exchange, Drain(*exchange, model, SessionPhase::Disconnected), UiSettings{}, frame);
  REQUIRE(frame.events.size() == 2);

  for (int attempt = 0; attempt < 2; ++attempt)
  {
    REQUIRE(exchange->PostLogin(Credentials{"user", "short"}));
    exchange->CompleteAuthentication("Password must be 12 to 128 UTF-8 bytes", Auth::FailureCode::InvalidCredentials);
    frame = {};
    session.Process(*exchange, Drain(*exchange, model, SessionPhase::Disconnected), UiSettings{}, frame);
    REQUIRE(frame.events.size() == 1);
    CHECK(Type(frame.events[0]) == "auth");
    auto auth = Parse(frame.events[0]);
    CHECK(auth["authenticating"].get<bool>() == false);
    CHECK(auth["error"].get<std::string>() == "Password must be 12 to 128 UTF-8 bytes");
  }
}

TEST_CASE("Bridge validates UI commands")
{
  auto chat = Bridge::ParseCommand(R"({"type":"sendChat","requestId":"3","channelId":"1","text":"Привет","extra":1})");
  REQUIRE(chat);
  CHECK(chat->text == "Привет");
  CHECK_FALSE(Bridge::ParseCommand(R"({"type":"sendChat","requestId":"3","channelId":"1","text":""})"));
  CHECK_FALSE(Bridge::ParseCommand(R"({"type":"jump"})"));
  CHECK_FALSE(Bridge::ParseCommand("not json"));
  CHECK_FALSE(Bridge::ParseCommand(R"({"type":"signIn","username":"a"})"));
  auto save = Bridge::ParseCommand(R"({"type":"saveSettings","revision":4,"settings":{"scale":5,"theme":"skyrim"}})");
  REQUIRE(save);
  CHECK(save->settings->scale == 1.5);
  CHECK(save->revision == 4);
  CHECK(Bridge::ParseCommand(R"({"type":"close"})"));
  CHECK(Bridge::ParseCommand(R"({"type":"signInSaved"})"));
}

TEST_CASE("Session publishes snapshots only for a ready session and correlates chat requests")
{
  auto        exchange = MakeExchange();
  ClientModel model;
  REQUIRE(model.RegisterChannel(1, 16));
  Session        session;
  Session::Frame frame;

  // Initial publication while disconnected: state is tracked, UI gets status only.
  session.Process(*exchange, Drain(*exchange, model, SessionPhase::Disconnected), UiSettings{}, frame);
  REQUIRE(frame.events.size() == 2);
  CHECK(Type(frame.events[0]) == "connection");
  CHECK(Type(frame.events[1]) == "auth");
  CHECK_FALSE(session.Ready());
  CHECK(
    session.SendChat(*exchange, Bridge::UiCommand{.type = "sendChat", .requestId = "u1", .channelId = "1", .text = "x"}).has_value() ==
    false);

  REQUIRE(model.Apply(
    model.Generation(),
    OnlinePlayersReplaced{
        {MakePlayer(1, "Alice"), MakePlayer(2, "Bob")}
  }));
  REQUIRE(model.Apply(model.Generation(), SelfPlayerAssigned{1}));
  REQUIRE(model.Apply(model.Generation(), ChatMessagesReceived{1, {MakeMessage(10, 1, "history")}}));
  frame = {};
  session.Process(*exchange, Drain(*exchange, model, SessionPhase::Ready), UiSettings{}, frame);
  Settle(session, *exchange, model, frame);
  REQUIRE(frame.snapshot);
  CHECK(Type(frame.events[0]) == "snapshot");
  auto snapshot = Parse(frame.events[0]);
  CHECK(snapshot["selfId"].get<std::string>() == "1");
  CHECK(snapshot["serverName"].get<std::string>() == "Tamriel");
  CHECK(snapshot["channels"][0]["id"].get<std::string>() == "1");
  CHECK(snapshot["messages"][0]["text"].get<std::string>() == "history");
  CHECK(snapshot["players"].get_array().size() == 2);
  CHECK(snapshot["settings"]["activationKey"].get<std::string>() == "Enter");
  CHECK(session.Ready());
  CHECK(session.OnlinePlayers().size() == 2);

  // Chat request: UI id maps to the Core RequestId and completes on confirmation.
  REQUIRE(session.SendChat(*exchange, Bridge::UiCommand{.type = "sendChat", .requestId = "u1", .channelId = "1", .text = "hello"}));
  std::vector<QueuedClientCommand> commands;
  exchange->TakeCommands(commands);
  REQUIRE(commands.size() == 1);
  const auto requestId = std::get<SendChat>(commands[0].command).requestId;
  CHECK(session.PendingChatCount() == 1);

  REQUIRE(model.Apply(model.Generation(), ChatMessagesReceived{1, {MakeMessage(11, 1, "hello")}}));
  REQUIRE(exchange->Publish(model, false, SessionPhase::Ready, "Tamriel", ChatConfirmation{model.Generation(), requestId, 11}));
  ClientOutput output;
  exchange->Drain(output);
  frame = {};
  session.Process(*exchange, output, UiSettings{}, frame);
  REQUIRE(frame.events.size() == 2);
  CHECK(Type(frame.events[0]) == "messages");
  CHECK(Type(frame.events[1]) == "sendResult");
  auto result = Parse(frame.events[1]);
  CHECK(result["requestId"].get<std::string>() == "u1");
  CHECK(result["messageId"].get<std::string>() == "11");
  CHECK(session.PendingChatCount() == 0);

  // Local failure with a foreign request id is ignored; a known one reports an error.
  REQUIRE(session.SendChat(*exchange, Bridge::UiCommand{.type = "sendChat", .requestId = "u2", .channelId = "1", .text = "again"}));
  exchange->TakeCommands(commands);
  const auto second = std::get<SendChat>(commands[0].command).requestId;
  REQUIRE(exchange->PublishCommandFailure({model.Generation(), 999, CommandFailureCode::Busy}));
  REQUIRE(exchange->PublishCommandFailure({model.Generation(), second, CommandFailureCode::SessionNotReady}));
  REQUIRE(exchange->Publish(model, false, SessionPhase::Ready, "Tamriel"));
  exchange->Drain(output);
  frame = {};
  session.Process(*exchange, output, UiSettings{}, frame);
  REQUIRE(frame.events.size() == 1);
  auto failure = Parse(frame.events[0]);
  CHECK(failure["requestId"].get<std::string>() == "u2");
  CHECK(failure["error"].get<std::string>() == "Нет соединения с сервером");

  // Player changes produce one full list; removals are reflected.
  REQUIRE(model.Apply(model.Generation(), PlayerRemoved{2}));
  REQUIRE(model.Apply(model.Generation(), PlayerUpserted{MakePlayer(3, "Carol")}));
  frame = {};
  session.Process(*exchange, Drain(*exchange, model, SessionPhase::Ready), UiSettings{}, frame);
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
  session.Process(*exchange, Drain(*exchange, model, SessionPhase::Ready), UiSettings{}, frame);
  REQUIRE(frame.snapshot);
  REQUIRE(session.SendChat(*exchange, Bridge::UiCommand{.type = "sendChat", .requestId = "old", .channelId = "1", .text = "x"}));
  std::vector<QueuedClientCommand> commands;
  exchange->TakeCommands(commands);

  session.ResetView();
  CHECK(session.NeedsSnapshot());
  CHECK(session.PendingChatCount() == 0);
  frame = {};
  session.Process(*exchange, Drain(*exchange, model, SessionPhase::Ready), UiSettings{}, frame);
  // Status is republished for the new view and a snapshot was requested from Core.
  CHECK(std::ranges::any_of(frame.events, [](const auto& e) { return Type(e) == "connection"; }));
  exchange->TakeCommands(commands);
  REQUIRE(commands.size() == 1);
  CHECK(std::holds_alternative<RequestSnapshot>(commands[0].command));

  REQUIRE(exchange->Publish(model, true, SessionPhase::Ready, "Tamriel"));
  frame = {};
  ClientOutput output;
  exchange->Drain(output);
  session.Process(*exchange, output, UiSettings{}, frame);
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
  session.Process(*exchange, Drain(*exchange, model, SessionPhase::Ready), UiSettings{}, frame);
  const auto generation = session.Generation();

  model.ClearOnlineState();
  frame = {};
  session.Process(*exchange, Drain(*exchange, model, SessionPhase::Disconnected, ""), UiSettings{}, frame);
  CHECK_FALSE(frame.snapshot);
  CHECK(session.Generation() == generation + 1);
  auto connection = std::ranges::find_if(frame.events, [](const auto& e) { return Type(e) == "connection"; });
  REQUIRE(connection != frame.events.end());
  CHECK(Parse(*connection)["connected"].get<bool>() == false);

  REQUIRE(model.Apply(model.Generation(), SelfPlayerAssigned{1}));
  frame = {};
  session.Process(*exchange, Drain(*exchange, model, SessionPhase::Ready, "Tamriel"), UiSettings{}, frame);
  // Core publishes a delta after ClearOnlineState; the host must request a snapshot for the UI.
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

  bubbles.Post(7, "first", start);
  bubbles.Post(9, "other", start);
  REQUIRE(bubbles.Find(7, start + 1s, settings));
  CHECK(bubbles.Find(7, start + 1s, settings)->text == "first");
  CHECK(bubbles.Find(7, start + 1s, settings)->alpha == 1.0f);

  // Replacement restarts the timer; the old text is gone.
  bubbles.Post(7, "second", start + 5s);
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
  bubbles.Post(11, "stays", start + 20s);
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
  session.Process(*exchange, Drain(*exchange, model, SessionPhase::Ready), UiSettings{}, frame);
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
  session.Process(*exchange, Drain(*exchange, model, SessionPhase::Ready), UiSettings{}, frame);
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
  session.Process(*exchange, Drain(*exchange, model, SessionPhase::Ready), UiSettings{}, frame);
  CHECK(frame.freshMessages.empty());

  // A view reset replays the snapshot: still no bubbles from history.
  session.ResetView();
  frame = {};
  session.Process(*exchange, Drain(*exchange, model, SessionPhase::Ready), UiSettings{}, frame);
  Settle(session, *exchange, model, frame);
  REQUIRE(frame.snapshot);
  CHECK(frame.freshMessages.empty());

  // Another channel is not the global one.
  REQUIRE(model.RegisterChannel(2, 16));
  REQUIRE(model.Apply(model.Generation(), ChatMessagesReceived{2, {MakeMessage(20, 2, "elsewhere")}}));
  frame = {};
  session.Process(*exchange, Drain(*exchange, model, SessionPhase::Ready), UiSettings{}, frame);
  CHECK(frame.freshMessages.empty());
  if (session.NeedsSnapshot()) Settle(session, *exchange, model, frame);
  REQUIRE(model.Apply(model.Generation(), ChatMessagesReceived{2, {MakeMessage(21, 2, "elsewhere")}}));
  frame = {};
  session.Process(*exchange, Drain(*exchange, model, SessionPhase::Ready), UiSettings{}, frame);
  CHECK(frame.freshMessages.empty());
  REQUIRE(model.Apply(model.Generation(), ChatMessagesReceived{1, {MakeMessage(22, 1, "global again")}}));
  frame = {};
  session.Process(*exchange, Drain(*exchange, model, SessionPhase::Ready), UiSettings{}, frame);
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

  UiSettings legacy;
  legacy.nameMode = "account";
  CHECK(Dreamsleeve::Host::Normalize(legacy).nameMode == "username");
  legacy.nameMode = "character";
  CHECK(Dreamsleeve::Host::Normalize(legacy).nameMode == "character");
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
  session.Process(*exchange, Drain(*exchange, model, SessionPhase::Ready), UiSettings{}, frame);
  Settle(session, *exchange, model, frame);
  REQUIRE(frame.snapshot);

  CHECK_FALSE(session.Ignore(1));  // Self.
  CHECK(session.Ignore(7));
  session.Refresh();
  frame = {};
  session.Process(*exchange, Drain(*exchange, model, SessionPhase::Ready), UiSettings{}, frame);
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
  session.Process(*exchange, Drain(*exchange, model, SessionPhase::Ready), UiSettings{}, frame);
  CHECK(frame.freshMessages.empty());
  REQUIRE(frame.events.size() == 1);
  auto delta = Parse(frame.events[0]);
  REQUIRE(delta["messages"].get_array().size() == 1);
  CHECK(delta["messages"][0]["text"].get<std::string>() == "announcement");

  // Unignore: history returns in the chat, but as a refresh without bubbles.
  CHECK(session.Unignore(7));
  session.Refresh();
  frame = {};
  session.Process(*exchange, Drain(*exchange, model, SessionPhase::Ready), UiSettings{}, frame);
  Settle(session, *exchange, model, frame);
  REQUIRE(frame.snapshot);
  CHECK(Parse(frame.events[0])["messages"].get_array().size() == 4);
  CHECK(frame.freshMessages.empty());
  REQUIRE(model.Apply(model.Generation(), ChatMessagesReceived{1, {MakeMessage(14, 1, "seven again")}}));
  frame = {};
  session.Process(*exchange, Drain(*exchange, model, SessionPhase::Ready), UiSettings{}, frame);
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
  session.Process(*exchange, Drain(*exchange, model, SessionPhase::Ready), character, frame);
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
  session.Process(*exchange, output, streamer, frame);
  std::string all;
  for (const auto& event : frame.events)
    all += event;
  Settle(session, *exchange, model, frame, streamer);
  REQUIRE(frame.snapshot);
  for (const auto& event : frame.events)
    all += event;
  for (std::string_view real : {"Alice", "Seven", "user1", "user7", "Nerevar", "Lydia"})
    CHECK_MESSAGE(all.find(real) == std::string::npos, real);
  auto  hidden  = Parse(frame.events[0]);
  auto& players = hidden["players"];
  for (auto& player : players.get_array())
  {
    const auto name = player["name"].get<std::string>();
    CHECK(player["alias"].get<std::string>() == name);
    CHECK(player["username"].get<std::string>().empty());
    const auto id = Bridge::ParseId(player["id"].get<std::string>());
    REQUIRE(id);
    // Nameplates ask the same resolver: identical label.
    CHECK(session.PlayerNames().NameFor(*id, session.OnlinePlayers().at(*id).data, "Nerevar", streamer) == name);
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
  session.Process(*exchange, Drain(*exchange, model, SessionPhase::Ready), mask, frame);
  Settle(session, *exchange, model, frame, mask);
  REQUIRE(frame.snapshot);
  auto masked = Parse(frame.events[0]);
  CHECK(masked["messages"][0]["text"].get<std::string>() == "join ********* now");
  CHECK(masked["messages"][0]["filtered"].get<bool>());
  CHECK(frame.events[0].find("spam") == std::string::npos);

  auto live    = MakeMessage(11, 1, "t.me/spam");
  live.flagged = {
      Domain::TextSpan{0, 9}
  };
  REQUIRE(model.Apply(model.Generation(), ChatMessagesReceived{1, {live}}));
  frame = {};
  session.Process(*exchange, Drain(*exchange, model, SessionPhase::Ready), mask, frame);
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
  session.Process(*exchange, Drain(*exchange, model, SessionPhase::Ready), hide, frame);
  REQUIRE(frame.freshMessages.size() == 1);
  CHECK(frame.freshMessages[0].messageText == "clean");
  REQUIRE(frame.events.size() == 1);
  auto delta = Parse(frame.events[0]);
  REQUIRE(delta["messages"].get_array().size() == 2);
  CHECK(delta["messages"][0]["text"].get<std::string>() == std::string{Bridge::HiddenOwnText});
  CHECK(delta["messages"][1]["text"].get<std::string>() == "clean");
  CHECK(frame.events[0].find("spam") == std::string::npos);
}

TEST_SUITE_END();
