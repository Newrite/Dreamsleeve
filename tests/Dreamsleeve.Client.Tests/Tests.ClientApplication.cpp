#include <doctest/doctest.h>
import std;
import Dreamsleeve.Client.Application;

using namespace Dreamsleeve::Client;

namespace
{
  struct SettingsFixture
  {
    std::filesystem::path path = std::filesystem::temp_directory_path() /
      (L"dreamsleeve-config-" + std::to_wstring(std::chrono::steady_clock::now().time_since_epoch().count()) + L"-тест.json");
    ~SettingsFixture() { std::error_code error; std::filesystem::remove(path, error); }
    auto Load(std::string_view json)
    {
      { std::ofstream file{path, std::ios::binary}; file << json; }
      return LoadClientSettings(path);
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

TEST_CASE("Configuration path is caller-owned and partial JSON preserves defaults")
{
  SettingsFixture fixture;
  CHECK_FALSE(LoadClientSettings(fixture.path));
  auto defaults = fixture.Load("{}");
  REQUIRE(defaults);
  CHECK(defaults->client.playerSampleIntervalMs == 50);
  CHECK(defaults->client.network.channelLimit == 3);

  auto loaded = fixture.Load(R"({"serverIp":"127.0.0.2","serverPort":9000,"commandCapacity":12,
    "client":{"showFireflies":false,"playerSampleIntervalMs":25,"network":{"maxPacketBytes":2048}},
    "interpolation":{"delayMs":75,"historyCapacity":16}})");
  REQUIRE(loaded);
  CHECK(loaded->client.serverAddress.GetPort() == 9000);
  CHECK(loaded->client.serverAddress.ToIpString().value() == "127.0.0.2");
  CHECK(loaded->commandCapacity == 12);
  CHECK_FALSE(loaded->client.showFireflies);
  CHECK(loaded->client.playerSampleIntervalMs == 25);
  CHECK(loaded->client.network.maxPacketBytes == 2048);
  CHECK(loaded->client.network.channelLimit == 3);
  CHECK(loaded->client.movement.delay == std::chrono::milliseconds{75});
  CHECK(loaded->client.movement.historyCapacity == 16);
}

TEST_CASE("Configuration rejects malformed files, unknown fields and invalid bounds before startup")
{
  SettingsFixture fixture;
  for (auto json : {"", "[]", "null", "{", "{} {}", "{} trailing", R"({"password":"must-not-be-stored"})", R"({"version":2})",
    R"({"client":{"typo":3}})", R"({"client":null})", R"({"serverPort":0})", R"({"serverPort":65536})",
    R"({"serverIp":"not-an-ip"})", R"({"client":{"playerSampleIntervalMs":0}})",
    R"({"commandCapacity":0})", R"({"stateCapacity":0})", R"({"client":{"network":{"maxPeers":2}}})",
    R"({"client":{"network":{"channelLimit":2}}})", R"({"client":{"network":{"maxWaitingData":1}}})",
    R"({"interpolation":{"delayMs":-1}})", R"({"interpolation":{"maxGapMs":100}})",
    R"({"client":{"visibilityDistance":-1}})", R"({"authUrl":"http://192.168.1.2:8779"})"})
  {
    CAPTURE(json);
    CHECK_FALSE(fixture.Load(json));
  }
  CHECK_FALSE(fixture.Load(std::string(65537, ' ')));
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

TEST_CASE("Programmatic startup uses the same validation as file configuration")
{
  ClientSettings settings;
  settings.client.playerSampleIntervalMs = 0;
  CHECK_FALSE(ClientApplication::TryCreate(settings));
  settings.client.playerSampleIntervalMs = 50;
  settings.authUrl = "http://remote.example.test";
  CHECK_FALSE(ClientApplication::TryCreate(settings));
}

TEST_SUITE_END();
