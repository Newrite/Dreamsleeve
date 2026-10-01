#include <doctest/doctest.h>
#include <glaze/glaze.hpp>
import std;
import Dreamsleeve.Host.Commands;
import Dreamsleeve.Client.Model;

#include "Bridge.h"

namespace
{

  using namespace Dreamsleeve::Client;
  using namespace Dreamsleeve::Host;

  Domain::Player MakePlayer(Domain::PlayerId id, std::string name)
  {
    Domain::Player player;
    player.data = {id, "user" + std::to_string(id), std::move(name)};
    return player;
  }

  // The host state a command changes and the plugin ports it calls, with a
  // record of what the ports were asked.
  struct Fixture
  {
    ClientExchange::Ptr        exchange{std::move(*ClientExchange::TryCreate(8, 8))};
    ClientModel                model;
    Session                    session;
    UiFile                     ui;
    Bubbles                    bubbles;
    bool                       manualDisconnect{};
    int                        saves{};
    std::optional<std::string> saveError;
    int                        closes{};
    std::vector<std::string>   keys;
    std::optional<Domain::MarkSpot> spot;

    CommandOutput Run(std::string_view json)
    {
      auto command = Bridge::ParseCommand(json);
      REQUIRE_MESSAGE(command, command.error());
      CommandContext context{
          *exchange,
          session,
          ui,
          bubbles,
          manualDisconnect,
          {.saveUi =
             [this]() -> std::expected<void, std::string> {
             ++saves;
             if (saveError) return std::unexpected{*saveError};
             return {};
           },
           .close         = [this] { ++closes; },
           .activationKey = [this](std::string_view key) { keys.emplace_back(key); },
           .noteSpot      = [this] { return spot; }}
      };
      return Handle(context, std::move(*command));
    }

    // A Ready session on one server: self = 1 and player 7 online.
    void Ready()
    {
      session.PlayerNames().Configure("127.0.0.1:8778", {});
      REQUIRE(model.Apply(
        model.Generation(),
        OnlinePlayersReplaced{
            {MakePlayer(1, "Self"), MakePlayer(7, "Seven")}
      }));
      REQUIRE(model.Apply(model.Generation(), SelfPlayerAssigned{1}));
      REQUIRE(exchange->Publish(model, true, SessionPhase::Ready, "Tamriel"));
      ClientOutput output;
      exchange->Drain(output);
      Session::Frame frame;
      session.Process(*exchange, output, ui.ui.chat, Domain::HiddenIdentity::None, frame);
      REQUIRE(session.Ready());
    }
  };

}

TEST_SUITE_BEGIN("Host.Commands");

TEST_CASE("Close only asks the plugin to leave chat focus")
{
  Fixture    fixture;
  const auto output = fixture.Run(R"({"type":"close"})");
  CHECK(fixture.closes == 1);
  CHECK(output.events.empty());
  CHECK(fixture.saves == 0);
}

TEST_CASE("Without a session requests are refused with the reason the UI shows")
{
  Fixture fixture;
  auto    chat = fixture.Run(R"({"type":"sendChat","requestId":"c1","channelId":"1","text":"x"})");
  REQUIRE(chat.events.size() == 1);
  CHECK(Parse(chat.events[0])["requestId"].get<std::string>() == "c1");
  CHECK(Parse(chat.events[0])["error"].get<std::string>() == "Нет соединения с сервером");
  auto removal = fixture.Run(R"({"type":"removeGroundMark","requestId":"r1","markId":"5"})");
  REQUIRE(removal.events.size() == 1);
  CHECK(Type(removal.events[0]) == "markResult");
  CHECK(Parse(removal.events[0])["error"].get<std::string>() == "Нет соединения с сервером");
  auto name = fixture.Run(R"({"type":"changeDisplayName","displayName":"Новое"})");
  REQUIRE(name.events.size() == 1);
  CHECK(Type(name.events[0]) == "displayName");
  CHECK(Parse(name.events[0])["error"].get<std::string>() == "Нет соединения с сервером");
}

TEST_CASE("A ground note needs the character in the world")
{
  Fixture fixture;
  fixture.Ready();
  auto nowhere = fixture.Run(R"({"type":"placeGroundNote","requestId":"n1","text":"Тролль"})");
  REQUIRE(nowhere.events.size() == 1);
  CHECK(Parse(nowhere.events[0])["requestId"].get<std::string>() == "n1");
  CHECK(Parse(nowhere.events[0])["error"].get<std::string>() == "Персонаж не в игровом мире");
  CHECK(fixture.session.PendingMarkCount() == 0);

  fixture.spot = Domain::MarkSpot{
      {{"skyrim.esm", 0x3C}, {1, 2, 3}, 0},
      {4, 201, 8, 17, 2, 10, 30}
  };
  const auto placed = fixture.Run(R"({"type":"placeGroundNote","requestId":"n2","text":"Тролль"})");
  CHECK(placed.events.empty());
  CHECK(fixture.session.PendingMarkCount() == 1);
}

TEST_CASE("Ignoring saves, drops the bubble, re-projects and answers with the list")
{
  Fixture fixture;
  fixture.Ready();
  const auto now = Bubbles::Clock::now();
  fixture.bubbles.Post(7, 7, "hello", now);

  auto ignored = fixture.Run(R"({"type":"ignore","playerId":"7"})");
  CHECK(fixture.saves == 1);
  CHECK(fixture.bubbles.Size() == 0);
  CHECK(fixture.session.NeedsSnapshot());
  REQUIRE(ignored.events.size() == 1);
  CHECK(Type(ignored.events[0]) == "ignored");
  CHECK(Parse(ignored.events[0])["players"][0]["id"].get<std::string>() == "7");

  // No change, no write; the list is still the answer.
  CHECK(fixture.Run(R"({"type":"ignore","playerId":"7"})").events.size() == 1);
  CHECK(fixture.saves == 1);

  auto unignored = fixture.Run(R"({"type":"unignore","playerId":"7"})");
  CHECK(fixture.saves == 2);
  REQUIRE(unignored.events.size() == 1);
  CHECK(Parse(unignored.events[0])["players"].get_array().empty());
}

TEST_CASE("Saved settings apply the activation key and report the revision with the save result")
{
  Fixture fixture;
  fixture.saveError = "disk full";
  auto saved        = fixture.Run(R"({"type":"saveSettings","revision":3,"settings":{"activationKey":"F2","fontSize":20}})");
  CHECK(fixture.ui.ui.chat.activationKey == "F2");
  CHECK(fixture.ui.ui.chat.fontSize == 20);
  CHECK(fixture.keys == std::vector<std::string>{"F2"});
  // Names did not change: no ignore list before the result.
  REQUIRE(saved.events.size() == 1);
  CHECK(Parse(saved.events[0])["revision"].get<double>() == 3);
  CHECK(Parse(saved.events[0])["error"].get<std::string>() == "disk full");

  fixture.saveError.reset();
  auto names = fixture.Run(R"({"type":"saveSettings","revision":4,"settings":{"streamerMode":true}})");
  REQUIRE(names.events.size() == 2);
  CHECK(Type(names.events[0]) == "ignored");
  CHECK(Type(names.events[1]) == "settingsResult");
  CHECK_FALSE(Parse(names.events[1]).contains("error"));
  CHECK(fixture.session.NeedsSnapshot());
}

TEST_CASE("Display settings take only the instant keys, clear the bubbles and save")
{
  Fixture fixture;
  fixture.bubbles.Post(7, 7, "hello", Bubbles::Clock::now());
  const auto output = fixture.Run(R"({"type":"displaySettings","settings":{"textFilter":"mask","fontSize":24}})");
  CHECK(fixture.ui.ui.chat.textFilter == "mask");
  CHECK(fixture.ui.ui.chat.fontSize == UiSettings{}.fontSize);
  CHECK(fixture.bubbles.Size() == 0);
  CHECK(fixture.saves == 1);
  REQUIRE(output.events.size() == 1);
  CHECK(Type(output.events[0]) == "ignored");
}

TEST_CASE("Account commands go to the Core and decide the manual disconnect")
{
  Fixture fixture;
  fixture.manualDisconnect = true;
  CHECK(fixture.Run(R"({"type":"signIn","username":"user","password":"password-long","remember":true,"displayName":"Имя"})").events.empty());
  CHECK_FALSE(fixture.manualDisconnect);
  auto control = fixture.exchange->TakeControl();
  REQUIRE(control.authentication);
  const auto* login = std::get_if<PasswordLogin>(&*control.authentication);
  REQUIRE(login);
  CHECK(login->credentials.username == "user");
  CHECK(login->credentials.password == "password-long");
  CHECK(login->registerName == "Имя");
  CHECK(login->remember);

  // One operation at a time: a second one is refused with the auth state.
  auto refused = fixture.Run(R"({"type":"signInSaved"})");
  REQUIRE(refused.events.size() == 1);
  CHECK(Type(refused.events[0]) == "auth");
  CHECK(Parse(refused.events[0])["error"].get<std::string>() == "A connection operation or session is already active");
  fixture.exchange->CompleteAuthentication();

  fixture.Run(R"({"type":"signOut"})");
  CHECK(fixture.manualDisconnect);
  CHECK(std::holds_alternative<SignOutAccount>(*fixture.exchange->TakeControl().authentication));
  fixture.exchange->CompleteAuthentication();

  fixture.manualDisconnect = false;
  fixture.Run(R"({"type":"forgetLogin"})");
  CHECK(fixture.manualDisconnect);
  CHECK(std::holds_alternative<ForgetLogin>(*fixture.exchange->TakeControl().authentication));
  fixture.exchange->CompleteAuthentication();

  fixture.Run(R"({"type":"signInSaved"})");
  CHECK_FALSE(fixture.manualDisconnect);
  CHECK(std::holds_alternative<ResumeLogin>(*fixture.exchange->TakeControl().authentication));
  fixture.exchange->CompleteAuthentication();

  // An administrator's code: the password is set, the player signs in afterwards.
  CHECK(fixture.Run(R"({"type":"resetPassword","code":"setup-code","password":"password-long"})").events.empty());
  const auto reset = fixture.exchange->TakeControl().authentication;
  REQUIRE(reset);
  const auto* code = std::get_if<ResetAccountPassword>(&*reset);
  REQUIRE(code);
  CHECK(code->code == "setup-code");
  CHECK(code->password == "password-long");
  fixture.exchange->CompleteAuthentication();

  // Steam: the browser sign-in may follow a manual disconnect.
  fixture.manualDisconnect = true;
  CHECK(fixture.Run(R"({"type":"signInSteam","remember":false})").events.empty());
  CHECK_FALSE(fixture.manualDisconnect);
  const auto steam = fixture.exchange->TakeControl().authentication;
  REQUIRE(steam);
  const auto* browser = std::get_if<SteamLogin>(&*steam);
  REQUIRE(browser);
  CHECK_FALSE(browser->remember);
  fixture.exchange->CompleteAuthentication();

  fixture.Run(R"({"type":"disconnect"})");
  CHECK(fixture.manualDisconnect);
  CHECK(fixture.exchange->TakeControl().disconnect);
}

TEST_CASE("Hiding the name outside a session changes the saved choice at once; while connecting it waits")
{
  Fixture fixture;
  auto    idle = fixture.Run(R"({"type":"setIdentityVisibility","hiding":"everywhere"})");
  CHECK(fixture.ui.ui.hideIdentity == "everywhere");
  CHECK(fixture.saves == 1);
  REQUIRE(idle.events.size() == 1);
  CHECK(Parse(idle.events[0])["mode"].get<std::string>() == "everywhere");
  CHECK_FALSE(Parse(idle.events[0])["pending"].get<bool>());

  REQUIRE(fixture.exchange->Publish(fixture.model, false, SessionPhase::Connecting, "Tamriel"));
  auto waiting = fixture.Run(R"({"type":"setIdentityVisibility","hiding":"off"})");
  CHECK(fixture.ui.ui.hideIdentity == "everywhere");
  CHECK(fixture.saves == 1);
  REQUIRE(waiting.events.size() == 1);
  CHECK(Parse(waiting.events[0])["mode"].get<std::string>() == "everywhere");
  CHECK(Parse(waiting.events[0])["error"].get<std::string>() == "Дождитесь подключения к серверу");
}

TEST_SUITE_END();
