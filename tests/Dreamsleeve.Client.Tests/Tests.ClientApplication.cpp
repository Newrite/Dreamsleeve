#include <doctest/doctest.h>
#include <glaze/glaze.hpp>
import std;
import Dreamsleeve.Client.Application;

#include "Repository.h"

using namespace Dreamsleeve::Client;

namespace
{

  struct SettingsFixture
  {
    std::filesystem::path path =
      std::filesystem::temp_directory_path() /
      (L"dreamsleeve-config-" + std::to_wstring(std::chrono::steady_clock::now().time_since_epoch().count()) + L"-тест.toml");

    ~SettingsFixture()
    {
      std::error_code error;
      std::filesystem::remove(path, error);
    }

    auto Load(std::string_view source)
    {
      {
        std::ofstream file{path, std::ios::binary};
        file << source;
      }

      return LoadClientSettings(path);
    }

    // Parsed and valid, as the application starts only with such settings.
    bool Accepts(std::string_view source)
    {
      const auto loaded = Load(source);
      return loaded && ValidateClientSettings(*loaded);
    }
  };

  bool WaitIdle(ClientApplication& app)
  {
    const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds{2};
    while (app.Status().authenticating && std::chrono::steady_clock::now() < deadline)
      std::this_thread::sleep_for(std::chrono::milliseconds{1});
    return !app.Status().authenticating;
  }

}

TEST_SUITE_BEGIN("Client.Application");

TEST_CASE("Configuration path is caller-owned and partial TOML preserves defaults")
{
  SettingsFixture fixture;
  CHECK_FALSE(LoadClientSettings(fixture.path));
  auto defaults = fixture.Load("# defaults\n");
  REQUIRE(defaults);
  CHECK(defaults->client.playerSampleIntervalMs == 100);
  CHECK(defaults->client.network.channelLimit == MinChannels);
  auto previous = fixture.Load("[client.network]\nchannelLimit = 3\n");
  REQUIRE(previous);
  CHECK(previous->client.network.channelLimit == MinChannels);

  auto loaded = fixture.Load(R"(serverHost = "127.0.0.2"
serverPort = 9000
commandCapacity = 12

[client]
showFireflies = false
playerSampleIntervalMs = 25

[client.network]
maxPacketBytes = 2048

[interpolation]
delayMs = 75
historyCapacity = 16
)");
  REQUIRE(loaded);
  CHECK(loaded->client.serverPort == 9000);
  CHECK(loaded->client.serverHost == "127.0.0.2");
  // A DNS name is kept as written and resolved only when a connection is made.
  auto named = fixture.Load("serverHost = \"Play.Example.org\"\n");
  REQUIRE(named);
  CHECK(named->client.serverHost == "Play.Example.org");
  CHECK(loaded->commandCapacity == 12);
  CHECK_FALSE(loaded->client.showFireflies);
  CHECK(loaded->client.playerSampleIntervalMs == 25);
  CHECK(loaded->client.network.maxPacketBytes == 2048);
  CHECK(loaded->client.network.channelLimit == MinChannels);
  CHECK(loaded->client.movement.delay == std::chrono::milliseconds{75});
  CHECK(loaded->client.movement.historyCapacity == 16);
}

TEST_CASE("Firefly base form settings use plugin-local IDs and preserve defaults")
{
  SettingsFixture fixture;
  auto defaults = fixture.Load("version = 1\n");
  REQUIRE(defaults);
  CHECK(defaults->client.fireflyPlugin == "Skyrim.esm");
  CHECK(defaults->client.fireflyFormId == 0x02EB0F);
  CHECK(defaults->client.fireflyScale == doctest::Approx(0.25f));

  auto custom = fixture.Load("[client]\nfireflyPlugin = \"MyGlow.esl\"\nfireflyFormId = 0xABC\nfireflyScale = 1.5\n");
  REQUIRE(custom);
  CHECK(custom->client.fireflyPlugin == "MyGlow.esl");
  CHECK(custom->client.fireflyFormId == 0xABC);
  CHECK(custom->client.fireflyScale == doctest::Approx(1.5f));

  for (auto bad : {"fireflyScale = 0",
                   "fireflyScale = -1",
                   "fireflyScale = 10.1",
                   "fireflyScale = nan",
                   "fireflyScale = inf",
                   "fireflyFormId = 0",
                   "fireflyFormId = 0xFE000ABC",
                   "fireflyFormId = -1",
                   "fireflyPlugin = ''",
                   "fireflyPlugin = 'dir/MyGlow.esp'"})
    CHECK_FALSE(fixture.Accepts(std::string{"[client]\n"} + bad));
}

TEST_CASE("Ground mark base forms default to vanilla flat glows and reject invalid values")
{
  SettingsFixture fixture;
  auto            defaults = fixture.Load("");
  REQUIRE(defaults);
  CHECK(defaults->client.groundNotePlugin == "Skyrim.esm");
  CHECK(defaults->client.groundNoteFormId == 0x075DDB);
  CHECK(defaults->client.groundNoteScale == doctest::Approx(0.5f));
  CHECK(defaults->client.deathMarkPlugin == "Skyrim.esm");
  CHECK(defaults->client.deathMarkFormId == 0x075DD9);
  CHECK(defaults->client.deathMarkScale == doctest::Approx(0.5f));
  auto custom = fixture.Load(
    "[client]\ngroundNotePlugin = \"Marks.esl\"\ngroundNoteFormId = 0x801\ngroundNoteScale = 1.25\ndeathMarkPlugin = \"Marks.esl\"\ndeathMarkFormId = 0x802\ndeathMarkScale = 2\n");
  REQUIRE(custom);
  CHECK(custom->client.groundNotePlugin == "Marks.esl");
  CHECK(custom->client.groundNoteFormId == 0x801);
  CHECK(custom->client.groundNoteScale == doctest::Approx(1.25f));
  CHECK(custom->client.deathMarkPlugin == "Marks.esl");
  CHECK(custom->client.deathMarkFormId == 0x802);
  CHECK(custom->client.deathMarkScale == doctest::Approx(2.0f));
  for (auto bad : {"groundNoteScale = 0", "groundNoteScale = 10.1", "groundNoteFormId = 0", "groundNoteFormId = 0xFE000ABC",
                   "groundNotePlugin = ''", "groundNotePlugin = 'dir/Marks.esp'", "deathMarkScale = nan", "deathMarkFormId = -1",
                   "deathMarkPlugin = 'a:b'"})
    CHECK_FALSE(fixture.Accepts(std::string{"[client]\n"} + bad + "\n"));
}

TEST_CASE("Keyboard capture is on by default and can be switched off")
{
  SettingsFixture fixture;
  auto            defaults = fixture.Load("version = 1\n");
  REQUIRE(defaults);
  CHECK(defaults->client.captureKeyboard);

  auto custom = fixture.Load("[client]\ncaptureKeyboard = false\n");
  REQUIRE(custom);
  CHECK_FALSE(custom->client.captureKeyboard);
}

TEST_CASE("Configuration rejects malformed files, unknown fields and invalid bounds before startup")
{
  SettingsFixture fixture;
  for (
    auto source :
    {"[]",
     "null",
     "{",
     "{} {}",
     "{} trailing",
     R"(password = "must-not-be-stored"
)",
     R"(version = 2
)",
     R"([client]
typo = 3
)",
     R"(client = 0
)",
     R"(serverPort = 0
)",
     R"(serverPort = 65536
)",
     R"(serverHost = ""
)",
     R"(serverHost = "bad host"
)",
     R"(serverHost = "-bad.example.org"
)",
     R"(serverHost = "999.1.1.1"
)",
     R"(serverIp = "127.0.0.1"
)",
     R"([client]
playerSampleIntervalMs = 0
)",
     R"(commandCapacity = 0
)",
     R"(stateCapacity = 0
)",
     R"([client.network]
maxPeers = 2
)",
     R"([client.network]
channelLimit = 2
)",
     R"([client.network]
maxWaitingData = 1
)",
     R"([interpolation]
delayMs = -1
)",
     R"([interpolation]
maxGapMs = 100
)",
     R"([client]
visibilityDistance = -1
)",
     R"(authUrl = "http://192.168.1.2:8779"
)"})
  {
    CAPTURE(std::string_view{source});
    CHECK_FALSE(fixture.Accepts(source));
  }
  CHECK_FALSE(fixture.Accepts(std::string(65537, ' ')));
}

TEST_CASE("Routes come as [[routes]] tables after the main one and are checked like it")
{
  SettingsFixture fixture;
  constexpr std::string_view main = "serverHost = \"main.example.org\"\nauthUrl = \"https://main.example.org\"\n";
  const auto                 route = [](std::string_view body) { return std::format("[[routes]]\n{}\n", body); };
  const std::string          proxy =
    route("name = \"Прокси\"\nserverHost = \"proxy.example.org\"\nserverPort = 9000\nauthUrl = \"https://proxy.example.org\"");
  const std::string second = route("name = \"Второй\"\nserverHost = \"203.0.113.9\"\nauthUrl = \"https://203.0.113.9\"");
  auto              loaded = fixture.Load(std::string{main} + proxy + second);
  REQUIRE(loaded);
  REQUIRE(ValidateClientSettings(*loaded));
  const auto routes = RoutesOf(*loaded);
  REQUIRE(routes.size() == 3);
  CHECK(routes[0] == ConnectionRoute{std::string{MainRouteName}, "main.example.org", DefaultServerPort, "https://main.example.org"});
  CHECK(routes[1] == ConnectionRoute{"Прокси", "proxy.example.org", 9000, "https://proxy.example.org"});
  CHECK(routes[2].serverPort == DefaultServerPort);
  CHECK(RouteIndex(routes, "Второй") == std::optional<std::size_t>{2});
  CHECK_FALSE(RouteIndex(routes, "Нет такого"));

  std::string tooMany{main};
  for (int index = 0; index < 8; ++index)
    tooMany += route(std::format("name = \"r{}\"\nserverHost = \"203.0.113.9\"\nauthUrl = \"https://203.0.113.9\"", index));
  for (const auto& source : {
         std::string{main} + proxy + proxy,  // names are unique
         std::string{main} + route("name = \"Основной\"\nserverHost = \"a.example.org\"\nauthUrl = \"https://a.example.org\""),
         std::string{main} + route("serverHost = \"a.example.org\"\nauthUrl = \"https://a.example.org\""),
         std::string{main} + route("name = \"x\"\nserverHost = \"bad host\"\nauthUrl = \"https://a.example.org\""),
         std::string{main} + route("name = \"x\"\nserverHost = \"a.example.org\"\nserverPort = 0\nauthUrl = \"https://a.example.org\""),
         std::string{main} + route("name = \"x\"\nserverHost = \"a.example.org\"\nauthUrl = \"http://a.example.org\""),
         std::string{main} + route("name = \"x\"\nserverHost = \"a.example.org\"\nauthUrl = \"https://a.example.org\"\ntypo = 1"),
         std::string{main} + "[routes]\nname = \"x\"\n",
         std::string{main} + "routes = []\n" + proxy,
         std::string{main} + proxy + "[routes]\nname = \"y\"\n",
         tooMany,
       })
  {
    CAPTURE(source);
    CHECK_FALSE(fixture.Accepts(source));
  }
}

TEST_CASE("TOML supports comments inline tables and rejects ambiguous scalar values")
{
  SettingsFixture fixture;
  REQUIRE(fixture.Load(""));
  auto loaded = fixture.Load("serverPort = 9_001 # comment\nclient = { showFireflies = false }\n");
  REQUIRE(loaded);
  CHECK(loaded->client.serverPort == 9001);
  CHECK_FALSE(loaded->client.showFireflies);
  for (
    const auto source :
    {"serverPort=9000\nserverPort=9001",
     "serverPort=9000.5",
     "serverPort='9000'",
     "client={showFireflies=true, showFireflies=false}",
     "client.showFireflies=true\n[client]\nshowFireflies=false",
     "[client]\n[client]",
     "serverPort=-1",
     "ServerPort=9000",
     "client.visibilityDistance=nan",
     "client.visibilityDistance=inf",
     "{}"})
  {
    CAPTURE(std::string_view{source});
    CHECK_FALSE(fixture.Accepts(source));
  }
}

TEST_CASE("Application owns startup and final shutdown without a connection")
{
  auto app = ClientApplication::TryCreate({});
  REQUIRE(app);
  CHECK((*app)->Status().phase == SessionPhase::Disconnected);
  (*app)->Stop();
  (*app)->Stop();
  CHECK((*app)->Status().stopped);
  CHECK_FALSE((*app)->Connect({"player", "password-do-not-log"}));
  ClientOutput output;
  (*app)->Exchange().Drain(output);
  CHECK(output.status.stopped);
}

TEST_CASE("Routes chosen automatically take turns while none answers; a chosen route stays")
{
  // Nobody serves these ports: every attempt times out.
  ClientSettings settings;
  settings.client.serverPort       = 1;
  settings.client.connectTimeoutMs = 50;
  settings.routes.push_back({"Прокси", "127.0.0.1", 2, "http://127.0.0.1:9"});
  const auto routeOf = [](ClientApplication& app, std::size_t route) {
    const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds{3};
    while (app.Status().route != route && std::chrono::steady_clock::now() < deadline)
      std::this_thread::sleep_for(std::chrono::milliseconds{1});
    return app.Status().route == route;
  };

  // The remembered route goes first, the next one takes over when it does not answer.
  auto app = ClientApplication::TryCreate(settings, {std::nullopt, 1});
  REQUIRE(app);
  REQUIRE((*app)->Routes().size() == 2);
  CHECK((*app)->Routes()[0].name == MainRouteName);
  CHECK((*app)->Status().route == 1);
  CHECK(routeOf(**app, 0));
  CHECK(routeOf(**app, 1));
  CHECK_FALSE((*app)->Status().routeReached);

  // The player's choice holds, answered or not.
  (*app)->Exchange().SetRouteChoice(0);
  CHECK(routeOf(**app, 0));
  std::this_thread::sleep_for(std::chrono::milliseconds{300});
  CHECK((*app)->Status().route == 0);
  (*app)->Stop();

  auto chosen = ClientApplication::TryCreate(settings, {1, 0});
  REQUIRE(chosen);
  CHECK((*chosen)->Status().route == 1);
  std::this_thread::sleep_for(std::chrono::milliseconds{300});
  CHECK((*chosen)->Status().route == 1);
}

TEST_CASE("Authentication errors are observable and a subsequent explicit login is allowed")
{
  auto app = ClientApplication::TryCreate({});
  REQUIRE(app);
  // Rejected by the real auth boundary before HTTP; no dependency on a server.
  REQUIRE((*app)->Connect({"player", "short"}));
  REQUIRE(WaitIdle(**app));
  CHECK_FALSE((*app)->Status().error.empty());
  CHECK((*app)->Status().phase == SessionPhase::Disconnected);

  REQUIRE((*app)->Connect({"player", "short"}));
  REQUIRE(WaitIdle(**app));
  (*app)->Stop();
  CHECK_FALSE((*app)->Status().authenticating);
}

TEST_CASE("Stop discards an admitted login and closes the exchange")
{
  auto app = ClientApplication::TryCreate({});
  REQUIRE(app);
  REQUIRE((*app)->Connect({"player", "short"}));
  (*app)->Stop();
  CHECK((*app)->Status().stopped);
  CHECK_FALSE((*app)->Status().authenticating);
  CHECK((*app)->Exchange().Post({0, RequestSnapshot{}}) == CommandPostResult::Closed);
}

TEST_CASE("One check names the first invalid setting as client.toml spells it")
{
  const std::vector<std::pair<std::string_view, std::function<void(Configuration&)>>> cases{
      {"network.maxPacketBytes", [](auto& c) { c.network.maxPacketBytes = MaxProtobufCount + 1; }},
      {"network.channelLimit", [](auto& c) { c.network.channelLimit = MinChannels - 1; }},
      {"maxInitialPlayers", [](auto& c) { c.maxInitialPlayers = 0; }},
      {"maxRecentMessages", [](auto& c) { c.maxRecentMessages = MaxProtobufCount + 1; }},
      {"maxActorValues", [](auto& c) { c.maxActorValues = 0; }},
      {"chatCapacity", [](auto& c) { c.chatCapacity = 0; }},
      {"maxPendingChatRequests", [](auto& c) { c.maxPendingChatRequests = 0; }},
      {"sessionTimeoutMs", [](auto& c) { c.sessionTimeoutMs = 0; }},
      {"visibilityDistance", [](auto& c) { c.visibilityDistance = -1; }},
      {"visibilityDistance", [](auto& c) { c.visibilityDistance = std::numeric_limits<double>::infinity(); }},
      {"visibilityDistance", [](auto& c) { c.visibilityDistance = std::numeric_limits<double>::quiet_NaN(); }},
      {"interpolation.historyCapacity", [](auto& c) { c.movement.historyCapacity = MinMovementHistory - 1; }},
      {"interpolation.delayMs", [](auto& c) { c.movement.delay = std::chrono::milliseconds{-1}; }},
      {"interpolation.maxGapMs", [](auto& c) { c.movement.maxGap = c.movement.delay; }},
      {"interpolation.maxGapMs", [](auto& c) { c.movement.maxGap = std::chrono::milliseconds::max(); }},
      {"interpolation.teleportDistance", [](auto& c) { c.movement.teleportDistance = std::numeric_limits<double>::infinity(); }},
      {"interpolation.teleportDistance", [](auto& c) { c.movement.teleportDistance = 0; }},
  };
  CHECK_FALSE(Configuration{}.InvalidSetting());
  for (const auto& [field, spoil] : cases)
  {
    Configuration config;
    spoil(config);
    CHECK(config.InvalidSetting() == field);
  }
  // Exact co-location is a valid distance.
  Configuration near;
  near.visibilityDistance = 0;
  CHECK_FALSE(near.InvalidSetting());
}

TEST_CASE("Start-up checks the transport and the queues with the rules of their owners")
{
  const std::vector<std::function<void(ClientSettings&)>> cases{
      [](auto& s) { s.client.network.maxPeers = 2; },
      [](auto& s) { s.client.network.channelLimit = 300; },
      [](auto& s) { s.client.network.maxPacketBytes = 0; },
      [](auto& s) { s.client.network.maxWaitingData = s.client.network.maxPacketBytes - 1; },
      [](auto& s) { s.client.connectTimeoutMs = 0; },
      [](auto& s) { s.client.disconnectTimeoutMs = 0; },
      [](auto& s) { s.commandCapacity = 0; },
      [](auto& s) { s.stateCapacity = 0; },
  };
  CHECK(ValidateClientSettings({}));
  for (const auto& spoil : cases)
  {
    ClientSettings settings;
    spoil(settings);
    // Not a Configuration rule: DreamNetClient and ClientExchange own these.
    CHECK_FALSE(settings.client.InvalidSetting());
    CHECK_FALSE(ValidateClientSettings(settings));
  }
}

TEST_CASE("The bundled client.example.toml is the first-run file and holds every default")
{
  const auto path = RepositoryRoot() / "src" / "Dreamsleeve.Client.Core" / "client.example.toml";
  const auto text = ReadText(path);
  std::string embedded{DefaultClientToml()};
  std::erase(embedded, '\r');
  CHECK(embedded == text);

  const auto loaded = LoadClientSettings(path);
  REQUIRE(loaded);
  const ClientSettings defaults;
  CHECK(glz::write_json(loaded->client).value_or("") == glz::write_json(defaults.client).value_or(""));
  CHECK(loaded->client.serverHost == defaults.client.serverHost);
  CHECK(loaded->client.serverPort == defaults.client.serverPort);
  CHECK(glz::write_json(SettingsDetail::InterpolationFile{
                            loaded->client.movement.delay.count(), loaded->client.movement.maxGap.count(),
                            loaded->client.movement.historyCapacity, loaded->client.movement.teleportDistance})
            .value_or("") == glz::write_json(SettingsDetail::InterpolationFile{}).value_or(""));
  CHECK(loaded->authUrl == defaults.authUrl);
  CHECK(loaded->commandCapacity == defaults.commandCapacity);
  CHECK(loaded->stateCapacity == defaults.stateCapacity);
  CHECK(loaded->allowInsecureRemoteAuth == defaults.allowInsecureRemoteAuth);

  const auto named = [&](std::string_view key) {
    CHECK_MESSAGE(text.contains(std::format("\n{} = ", key)), std::string{key});
  };
  // Sections and the [[routes]] tables, which the example shows commented out.
  for (const auto key : glz::reflect<SettingsDetail::SettingsFile>::keys)
    if (key != "client" && key != "interpolation" && key != "routes") named(key);
  CHECK(loaded->routes.empty());
  for (const auto key : glz::reflect<ConnectionRoute>::keys)
    CHECK_MESSAGE(text.contains(std::format("\n# {} = ", key)), std::string{key});
  for (const auto key : glz::reflect<Configuration>::keys)
    if (key != "network") named(key);
  for (const auto key : glz::reflect<NetConfig>::keys)
    named(key);
  for (const auto key : glz::reflect<SettingsDetail::InterpolationFile>::keys)
    named(key);
}

TEST_CASE("The first run writes the example and never rewrites an existing file")
{
  SettingsFixture fixture;
  REQUIRE(EnsureClientSettings(fixture.path));
  CHECK(ReadText(fixture.path) == [] {
    std::string text{DefaultClientToml()};
    std::erase(text, '\r');
    return text;
  }());

  {
    std::ofstream file{fixture.path, std::ios::binary | std::ios::trunc};
    file << "serverPort = 9000\n";
  }
  REQUIRE(EnsureClientSettings(fixture.path));
  CHECK(ReadText(fixture.path) == "serverPort = 9000\n");
}

TEST_CASE("Programmatic startup uses the same validation as file configuration")
{
  ClientSettings settings;
  settings.client.playerSampleIntervalMs = 0;
  CHECK_FALSE(ClientApplication::TryCreate(settings));
  settings.client.playerSampleIntervalMs = 50;
  settings.authUrl                       = "http://remote.example.test";
  CHECK_FALSE(ClientApplication::TryCreate(settings));
}

TEST_CASE("Delayed methods replies cannot publish across a route change or ABA")
{
  bool returnToOriginal{};
  SUBCASE("another route") {}
  SUBCASE("original index selected again")
  {
    returnToOriginal = true;
  }

  struct Gates
  {
    std::promise<void>        firstEntered;
    std::promise<void>        firstRelease;
    std::promise<std::string> nextEntered;
    std::promise<void>        nextRelease;
    std::atomic_bool          firstReleased{};
    std::atomic_bool          nextReleased{};
    std::atomic_int           calls{};

    void Release()
    {
      if (!firstReleased.exchange(true)) firstRelease.set_value();
      if (!nextReleased.exchange(true)) nextRelease.set_value();
    }
  };

  auto             gates        = std::make_shared<Gates>();
  auto             firstEntered = gates->firstEntered.get_future();
  auto             nextEntered  = gates->nextEntered.get_future();
  auto             firstRelease = gates->firstRelease.get_future().share();
  auto             nextRelease  = gates->nextRelease.get_future().share();
  ApplicationPorts ports;
  ports.readMethods = [gates, firstRelease, nextRelease](std::string_view url, bool) -> std::expected<Auth::Methods, Auth::Failure> {
    if (++gates->calls == 1)
    {
      gates->firstEntered.set_value();
      firstRelease.wait();
      return Auth::Methods{Auth::RegistrationMode::Manual, true};
    }
    if (gates->calls == 2)
    {
      gates->nextEntered.set_value(std::string{url});
      nextRelease.wait();
    }
    return Auth::Methods{Auth::RegistrationMode::Open, false};
  };
  ClientSettings settings;
  settings.client.serverPort       = 1;
  settings.client.connectTimeoutMs = 50;
  settings.routes.push_back({"Proxy", "127.0.0.1", 2, "http://127.0.0.1:9"});
  auto app = ClientApplication::TryCreate(settings, {0, 0}, std::move(ports));

  struct ReleaseBeforeAppShutdown
  {
    std::shared_ptr<Gates> gates;

    ~ReleaseBeforeAppShutdown()
    {
      gates->Release();
    }
  } release{gates};

  REQUIRE(app);
  REQUIRE(firstEntered.wait_for(std::chrono::seconds{3}) == std::future_status::ready);
  const auto waitRoute = [&](std::size_t route) {
    const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds{3};
    while ((*app)->Status().route != route && std::chrono::steady_clock::now() < deadline)
      std::this_thread::sleep_for(std::chrono::milliseconds{1});
    return (*app)->Status().route == route;
  };
  (*app)->Exchange().SetRouteChoice(1);
  REQUIRE(waitRoute(1));
  if (returnToOriginal)
  {
    (*app)->Exchange().SetRouteChoice(0);
    REQUIRE(waitRoute(0));
  }

  gates->firstReleased = true;
  gates->firstRelease.set_value();
  REQUIRE(nextEntered.wait_for(std::chrono::seconds{3}) == std::future_status::ready);
  CHECK(nextEntered.get() == (returnToOriginal ? settings.authUrl : settings.routes[0].authUrl));
  CHECK((*app)->Status().methods == Auth::Methods{});

  gates->nextReleased = true;
  gates->nextRelease.set_value();
  const auto          deadline = std::chrono::steady_clock::now() + std::chrono::seconds{3};
  const Auth::Methods fresh{Auth::RegistrationMode::Open, false};
  while ((*app)->Status().methods != fresh && std::chrono::steady_clock::now() < deadline)
    std::this_thread::sleep_for(std::chrono::milliseconds{1});
  CHECK((*app)->Status().methods == fresh);
  (*app)->Stop();
}

TEST_CASE("First-run configuration supports a bare relative filename")
{
  struct File
  {
    std::filesystem::path path =
      L"dreamsleeve-relative-settings-" + std::to_wstring(std::chrono::steady_clock::now().time_since_epoch().count()) + L".toml";

    ~File()
    {
      std::error_code error;
      std::filesystem::remove(path, error);
    }
  } file;

  REQUIRE(file.path.parent_path().empty());
  REQUIRE(EnsureClientSettings(file.path));
  CHECK(LoadClientSettings(file.path));
}

TEST_CASE("Unicode configuration failures remain typed and existing files are preserved")
{
  struct Fixture
  {
    std::filesystem::path root =
      std::filesystem::temp_directory_path() /
      (L"dreamsleeve-settings-\U0001F984-\u4E2D-" + std::to_wstring(std::chrono::steady_clock::now().time_since_epoch().count()));

    ~Fixture()
    {
      std::error_code error;
      std::filesystem::remove_all(root, error);
    }
  } fixture;

  std::filesystem::create_directories(fixture.root);
  const auto blocked = fixture.root / L"\u975E\u76EE\u5F55-\U0001F984";
  {
    std::ofstream file{blocked, std::ios::binary};
    file << "keep";
  }
  const auto result = EnsureClientSettings(blocked / L"настройки.toml");
  CHECK_FALSE(result);
  if (!result) CHECK_FALSE(result.error().empty());
  CHECK(ReadText(blocked) == "keep");
  REQUIRE(EnsureClientSettings(blocked));
  CHECK(ReadText(blocked) == "keep");
}

TEST_SUITE_END();

TEST_CASE("Remote HTTP opt-in loads from client TOML")
{
  SettingsFixture fixture;
  CHECK_FALSE(fixture.Accepts("authUrl='http://auth.example.test:8779'"));
  auto settings = fixture.Load("authUrl='http://auth.example.test:8779'\nallowInsecureRemoteAuth=true");
  REQUIRE(settings);
  CHECK(settings->allowInsecureRemoteAuth);
  CHECK(ValidateClientSettings(*settings));
  CHECK_FALSE(fixture.Accepts("allowInsecureRemoteAuth='true'"));
}
