#include <doctest/doctest.h>
#include <glaze/glaze.hpp>
import std;
import Dreamsleeve.Host.Session;
import Dreamsleeve.Client.Model;

namespace
{

  using namespace Dreamsleeve::Client;
  using namespace Dreamsleeve::Host;
  namespace Api = Dreamsleeve::Host::Announcements;

  constexpr Domain::PlayerId ServerAuthor = std::numeric_limits<Domain::PlayerId>::max();

  struct TempPath
  {
    std::filesystem::path path =
      std::filesystem::temp_directory_path() /
      (L"dreamsleeve-announcements-" + std::to_wstring(std::chrono::steady_clock::now().time_since_epoch().count()) + L".toml");

    ~TempPath()
    {
      std::error_code error;
      std::filesystem::remove(path, error);
    }
  };

  Domain::Player MakePlayer(Domain::PlayerId id, std::string name)
  {
    Domain::Player player;
    player.data = {id, "user" + std::to_string(id), std::move(name)};
    return player;
  }

  Domain::ChatMessage MakeMessage(Domain::ChatMessageId id, Domain::PlayerData author, std::string text)
  {
    return {id, 1, std::move(author), std::move(text), Domain::FromUnixMilliseconds(1700000000000)};
  }

  Domain::ChatMessage MakeAnnouncement(
    Domain::ChatMessageId      id,
    Domain::AnnouncementSource source,
    Domain::AnnouncementKind   kind,
    std::string                signature = {})
  {
    const Domain::PlayerData author  = source == Domain::AnnouncementSource::Server ? Domain::PlayerData{ServerAuthor, "server", "Tamriel"}
                                                                                    : Domain::PlayerData{7, "seven", "Seven"};
    auto                     message = MakeMessage(id, author, "notice " + std::to_string(id));
    message.announcement             = Domain::Announcement{source, kind, std::move(signature)};
    return message;
  }

  Domain::AnnouncementPolicy Policy(std::vector<Domain::ClientAnnouncementSource> sources = {Domain::ClientAnnouncementSource::ThirdParty})
  {
    return {std::move(sources), 20, 8};
  }

  Api::Request Request(std::string text, std::string signature = "DeathMod")
  {
    return {std::move(text), Domain::AnnouncementKind::Event, Domain::ClientAnnouncementSource::ThirdParty, std::move(signature)};
  }

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

  // Ready session with a delivered snapshot of channel 1, self = 1, player 7 online.
  struct Fixture
  {
    ClientExchange::Ptr exchange;
    ClientModel         model;
    Session             session;
    Session::Frame      frame;

    Fixture()
    {
      auto created = ClientExchange::TryCreate(16, 8);
      REQUIRE(created);
      exchange = std::move(*created);
      session.PlayerNames().Configure("127.0.0.1:8778", {});
      REQUIRE(model.RegisterChannel(1, 16));
      REQUIRE(model.Apply(
        model.Generation(),
        OnlinePlayersReplaced{
            {MakePlayer(1, "Alice"), MakePlayer(7, "Seven")}
      }));
      REQUIRE(model.Apply(model.Generation(), SelfPlayerAssigned{1}));
      REQUIRE(exchange->Publish(model, true, SessionPhase::Ready, "Tamriel"));
      Process();
      REQUIRE(frame.snapshot);
    }

    void Process(const UiSettings& settings = {})
    {
      ClientOutput output;
      exchange->Drain(output);
      frame = {};
      session.Process(*exchange, output, settings, frame);
    }

    void Publish(std::optional<ChatConfirmation> confirmation = std::nullopt)
    {
      REQUIRE(exchange->Publish(model, false, SessionPhase::Ready, "Tamriel", confirmation));
      Process();
    }

    std::uint64_t TakeRequest()
    {
      std::vector<QueuedClientCommand> commands;
      exchange->TakeCommands(commands);
      REQUIRE(commands.size() == 1);
      const auto* command = std::get_if<PostAnnouncement>(&commands[0].command);
      REQUIRE(command);
      return command->requestId;
    }

    std::vector<glz::generic> Events(std::string_view type) const
    {
      std::vector<glz::generic> result;
      for (const auto& event : frame.events)
        if (Type(event) == type) result.push_back(Parse(event));
      return result;
    }
  };

}

TEST_SUITE_BEGIN("Client.Host");

TEST_CASE("Announcement checks reject malformed calls first, then follow the server policy")
{
  using Api::Result;
  const Api::Gate online{true, Policy()};

  CHECK(Api::Check(Request("Игрок пал в бою"), online) == Result::Queued);
  CHECK(Api::Check(Request(std::string(20, 'x')), online) == Result::Queued);
  CHECK(Api::Check(Request(std::string(21, 'x')), online) == Result::TooLong);
  CHECK(Api::Check(Request(std::string(2001, 'x')), Api::Gate{}) == Result::TooLong);
  CHECK(Api::Check(Request("one\ntwo\tthree"), online) == Result::Queued);

  for (std::string_view invalid : {"", "   \n", "bell\x07", "\xC0\xAF", "\xED\xA0\x80", "\xE2\x80", "\xC2\x85", "sep\xE2\x80\xA8"})
    CHECK(Api::Check(Request(std::string{invalid}), online) == Result::InvalidText);

  for (std::string_view label : {"", "  ", "two\nlines", "\xFF", "123456789"})
    CHECK(Api::Check(Request("text", std::string{label}), online) == Result::InvalidSource);
  CHECK(Api::Check(Request("text", "Мод-смер"), online) == Result::Queued);

  auto kind = Request("text");
  kind.kind = Domain::AnnouncementKind::Admin;
  CHECK(Api::Check(kind, online) == Result::InvalidKind);
  kind.kind = Domain::AnnouncementKind::Periodic;
  CHECK(Api::Check(kind, online) == Result::InvalidKind);

  auto trusted   = Request("text", "");
  trusted.source = Domain::ClientAnnouncementSource::TrustedClient;
  CHECK(Api::Check(trusted, Api::Gate{true, Policy({Domain::ClientAnnouncementSource::TrustedClient})}) == Result::Queued);
  CHECK(Api::Check(trusted, online) == Result::Rejected);

  CHECK(Api::Check(Request("text"), Api::Gate{false, Policy()}) == Result::NotConnected);
  CHECK(Api::Check(Request("text"), Api::Gate{true, std::nullopt}) == Result::Unsupported);
  CHECK(Api::Check(Request("text"), Api::Gate{true, Policy({})}) == Result::Rejected);

  CHECK(Api::CountCodePoints("Ж😀a", false) == 3);
  CHECK_FALSE(Api::CountCodePoints("\xF4\x90\x80\x80", false));
}

TEST_CASE("Session shows the system stream in its own channel; the server is never ignored or bubbled")
{
  Fixture fixture;
  auto    snapshot = Parse(fixture.frame.events[0]);
  auto&   channels = snapshot["channels"].get_array();
  REQUIRE(channels.size() == 2);
  CHECK(channels[1]["id"].get<std::string>() == "announcements");
  CHECK(channels[1]["kind"].get<std::string>() == "system");
  CHECK_FALSE(channels[1]["writable"].get<bool>());

  using Source = Domain::AnnouncementSource;
  using Kind   = Domain::AnnouncementKind;
  REQUIRE(fixture.model.Apply(
    fixture.model.Generation(),
    ChatMessagesReceived{
        1,
        {MakeAnnouncement(10, Source::Server, Kind::Periodic),
          MakeAnnouncement(11, Source::ThirdParty, Kind::Event, "Death\x01Mod\xC2\x85"),
          MakeAnnouncement(12, static_cast<Source>(42), static_cast<Kind>(42)),
          MakeMessage(13, {7, "seven", "Seven"}, "chat")}
  }));
  fixture.Publish();
  auto messages = fixture.Events("messages");
  REQUIRE(messages.size() == 1);
  auto& rows = messages[0]["messages"].get_array();
  REQUIRE(rows.size() == 4);

  CHECK(rows[0]["channelId"].get<std::string>() == "announcements");
  CHECK(rows[0]["source"].get<std::string>() == "system");
  CHECK(rows[0]["announcement"]["origin"].get<std::string>() == "server");
  CHECK(rows[0]["announcement"]["kind"].get<std::string>() == "periodic");
  CHECK_FALSE(rows[0].contains("author"));

  CHECK(rows[1]["announcement"]["origin"].get<std::string>() == "thirdParty");
  CHECK(rows[1]["announcement"]["signature"].get<std::string>() == "DeathMod");
  CHECK(rows[1]["author"]["id"].get<std::string>() == "7");

  // What a newer server adds is shown with the least trust.
  CHECK(rows[2]["announcement"]["origin"].get<std::string>() == "thirdParty");
  CHECK(rows[2]["announcement"]["kind"].get<std::string>() == "announcement");

  CHECK(rows[3]["channelId"].get<std::string>() == "1");
  CHECK(rows[3]["source"].get<std::string>() == "player");
  CHECK_FALSE(rows[3].contains("announcement"));

  // Only the plain chat line floats above the firefly.
  REQUIRE(fixture.frame.freshMessages.size() == 1);
  CHECK(fixture.frame.freshMessages[0].messageId == 13);

  // Ignoring the player hides their client announcements; the server stays.
  CHECK_FALSE(fixture.session.Ignore(ServerAuthor));
  REQUIRE(fixture.session.Ignore(7));
  fixture.session.Refresh();
  fixture.Publish();
  std::vector<QueuedClientCommand> commands;
  fixture.exchange->TakeCommands(commands);
  REQUIRE(fixture.exchange->Publish(fixture.model, true, SessionPhase::Ready, "Tamriel"));
  fixture.Process();
  REQUIRE(fixture.frame.snapshot);
  auto                     refreshed = Parse(fixture.frame.events[0]);
  std::vector<std::string> ids;
  for (auto& row : refreshed["messages"].get_array())
    ids.push_back(row["id"].get<std::string>());
  // Unknown origins are not the server either.
  CHECK(ids == std::vector<std::string>{"10"});
}

TEST_CASE("Plugin API announcements settle by request ID and refusals reach the UI")
{
  Fixture fixture;
  using Api::Result;

  CHECK(fixture.session.PostAnnouncement(*fixture.exchange, Request("Игрок пал")) == Result::Queued);
  const auto published = fixture.TakeRequest();
  CHECK(fixture.session.PendingAnnouncementCount() == 1);
  auto accepted   = MakeAnnouncement(20, Domain::AnnouncementSource::ThirdParty, Domain::AnnouncementKind::Event, "DeathMod");
  accepted.author = {1, "user1", "Alice"};
  REQUIRE(fixture.model.Apply(fixture.model.Generation(), ChatMessagesReceived{1, {accepted}}));
  fixture.Publish(ChatConfirmation{fixture.model.Generation(), published, 20});
  REQUIRE(fixture.frame.announcementResults.size() == 1);
  CHECK(fixture.frame.announcementResults[0].result == Result::Published);
  CHECK(fixture.frame.announcementResults[0].signature == "DeathMod");
  CHECK(fixture.Events("announcementResult").empty());
  CHECK(fixture.Events("sendResult").empty());

  CHECK(fixture.session.PostAnnouncement(*fixture.exchange, Request("Второе")) == Result::Queued);
  const auto refused = fixture.TakeRequest();
  REQUIRE(fixture.model.Apply(
    fixture.model.Generation(),
    ServerRejection{
        refused,
        RequestRejectionCode::AnnouncementNotAllowed,
        "This server does not accept announcements from this source.",
        "source"
    }));
  fixture.Publish();
  REQUIRE(fixture.frame.announcementResults.size() == 1);
  CHECK(fixture.frame.announcementResults[0].result == Result::Rejected);
  auto rows = fixture.Events("announcementResult");
  REQUIRE(rows.size() == 1);
  CHECK(rows[0]["source"].get<std::string>() == "DeathMod");
  CHECK(rows[0]["text"].get<std::string>() == "Второе");
  CHECK(rows[0]["error"].get<std::string>() == "Сервер не принимает объявления от этого источника");

  CHECK(fixture.session.PostAnnouncement(*fixture.exchange, Request("Слишком часто")) == Result::Queued);
  const auto limited = fixture.TakeRequest();
  REQUIRE(fixture.model.Apply(fixture.model.Generation(), ServerRejection{limited, RequestRejectionCode::RateLimited, "Too many", "text"}));
  fixture.Publish();
  REQUIRE(fixture.frame.announcementResults.size() == 1);
  CHECK(fixture.frame.announcementResults[0].result == Result::RateLimited);

  CHECK(fixture.session.PostAnnouncement(*fixture.exchange, Request("Старый сервер")) == Result::Queued);
  const auto unsupported = fixture.TakeRequest();
  REQUIRE(fixture.exchange->PublishCommandFailure({fixture.model.Generation(), unsupported, CommandFailureCode::Unsupported}));
  fixture.Publish();
  REQUIRE(fixture.frame.announcementResults.size() == 1);
  CHECK(fixture.frame.announcementResults[0].result == Result::Unsupported);
  CHECK(fixture.Events("announcementResult").size() == 1);

  // A new session cannot answer the previous one: the request settles as unknown.
  CHECK(fixture.session.PostAnnouncement(*fixture.exchange, Request("Потеряно")) == Result::Queued);
  fixture.TakeRequest();
  fixture.model.ResetSession();
  REQUIRE(fixture.model.RegisterChannel(1, 16));
  REQUIRE(fixture.exchange->Publish(fixture.model, true, SessionPhase::Ready, "Tamriel"));
  fixture.Process();
  REQUIRE(fixture.frame.announcementResults.size() == 1);
  CHECK(fixture.frame.announcementResults[0].result == Result::Failed);
  CHECK(fixture.session.PendingAnnouncementCount() == 0);

  Session offline;
  CHECK(offline.PostAnnouncement(*fixture.exchange, Request("x")) == Result::NotConnected);
}

TEST_CASE("Older UI files keep announcement defaults; invalid placement falls back")
{
  TempPath file;
  {
    std::ofstream output{file.path};
    output << "[ui.chat]\nfontSize = 20\n";
  }
  auto loaded = LoadUiFile(file.path);
  REQUIRE(loaded);
  const UiSettings defaults{};
  CHECK(loaded->ui.chat.announcementChannels == "all");
  CHECK(loaded->ui.chat.announcementsServer == defaults.announcementsServer);
  CHECK(loaded->ui.chat.announcementsTrustedClient);
  CHECK(loaded->ui.chat.announcementsThirdParty);
  CHECK(loaded->ui.chat.announcementsEvents);
  CHECK(loaded->ui.chat.announcementsPeriodic);

  UiFile edited;
  edited.ui.chat.announcementChannels    = "tab";
  edited.ui.chat.announcementsThirdParty = false;
  edited.ui.chat.announcementsPeriodic   = false;
  REQUIRE(SaveUiFile(file.path, edited));
  auto saved = LoadUiFile(file.path);
  REQUIRE(saved);
  CHECK(*saved == edited);

  auto invalid                 = edited.ui.chat;
  invalid.announcementChannels = "everywhere";
  CHECK(Dreamsleeve::Host::Normalize(invalid).announcementChannels == "all");

  auto json = glz::write_json(edited.ui.chat);
  REQUIRE(json);
  CHECK(json->find("\"announcementChannels\":\"tab\"") != std::string::npos);
}
