#include <doctest/doctest.h>

import std;
import Dreamsleeve.Client.Exchange;

namespace
{

  using namespace Dreamsleeve::Client;

  constexpr Domain::ChatChannelId GuildChannel(Domain::GuildId guildId)
  {
    return Domain::GuildChannelBase + guildId;
  }

  Domain::GuildMember MakeMember(Domain::PlayerId playerId, Domain::GuildRole role = Domain::GuildRole::Member)
  {
    return {
        Domain::PlayerData{playerId, "user" + std::to_string(playerId), "Name" + std::to_string(playerId)},
        role
    };
  }

  Domain::Guild MakeGuild(Domain::GuildId guildId, std::vector<Domain::GuildMember> members = {MakeMember(7, Domain::GuildRole::Master)})
  {
    return {guildId, "Guild" + std::to_string(guildId), GuildChannel(guildId), 1000, std::move(members)};
  }

  Domain::ChatMessage GuildMessage(Domain::GuildId guildId, Domain::ChatMessageId messageId)
  {
    return Domain::ChatMessage{
        messageId,
        GuildChannel(guildId),
        Domain::PlayerData{7, "user7", "Name7"},
        "Привет",
        Domain::MessageTime{}
    };
  }

}

TEST_SUITE_BEGIN("Client.Guilds");

TEST_CASE("A guild book holds each guild, member and invitation once and refuses changes to guilds it does not know")
{
  CHECK_FALSE(GuildBook::TryCreate({MakeGuild(4), MakeGuild(4)}, {}, {}));
  CHECK_FALSE(GuildBook::TryCreate({MakeGuild(4, {MakeMember(7), MakeMember(7)})}, {}, {}));
  CHECK_FALSE(GuildBook::TryCreate({}, {{5, "Five", 9, 1}, {5, "Five", 9, 2}}, {}));

  auto book = GuildBook::TryCreate(
    {
        MakeGuild(4)
  },
    {{5, "Five", 9, 1}},
    {3, 64, 3, 24});
  REQUIRE(book);
  CHECK(book->Limits() == Domain::GuildLimits{3, 64, 3, 24});
  REQUIRE(book->FindByChannel(GuildChannel(4)));
  CHECK(book->FindByChannel(GuildChannel(4))->name == "Guild4");
  CHECK_FALSE(book->FindByChannel(1));

  CHECK_FALSE(book->Add(MakeGuild(4)));
  REQUIRE(book->RemoveInvite(5));
  CHECK_FALSE(book->RemoveInvite(5));
  REQUIRE(book->Add(MakeGuild(5)));

  // An update replaces the member in place; a new player joins at the end.
  auto officer = MakeMember(8, Domain::GuildRole::Officer);
  REQUIRE(book->PutMember(4, MakeMember(8)));
  REQUIRE(book->PutMember(4, officer));
  REQUIRE(book->Find(4)->members.size() == 2);
  CHECK(book->Find(4)->members[1] == officer);
  CHECK_FALSE(book->PutMember(6, MakeMember(8)));
  REQUIRE(book->RemoveMember(4, 8));
  CHECK_FALSE(book->RemoveMember(4, 8));

  // A repeated invitation after the last expired replaces it.
  book->PutInvite({6, "Six", 9, 1});
  book->PutInvite({6, "Six", 10, 2});
  REQUIRE(book->Invites().size() == 1);
  CHECK(book->Invites()[0].invitedBy == 10);

  REQUIRE(book->Remove(4));
  CHECK_FALSE(book->Remove(4));
  CHECK_FALSE(book->Find(4));
  CHECK(book->Find(5));
}

TEST_CASE("A removed channel takes its undelivered content along and the next delta reports it absent")
{
  ClientModel model;
  REQUIRE(model.RegisterChannel(1, 20));
  REQUIRE(model.RegisterChannel(GuildChannel(4), 20, Domain::ChatChannelKind::Guild));
  ChangeBatch scratch;
  model.TakeChanges(scratch);

  REQUIRE(model.Apply(model.Generation(), ChatMessagesReceived{GuildChannel(4), {GuildMessage(4, 1)}}));
  REQUIRE(model.UnregisterChannel(GuildChannel(4)));
  CHECK_FALSE(model.UnregisterChannel(GuildChannel(4)));
  CHECK_FALSE(model.FindChatState(GuildChannel(4)));
  CHECK_FALSE(model.Apply(model.Generation(), ChatMessagesReceived{GuildChannel(4), {GuildMessage(4, 2)}}));

  auto update = TakeStateUpdate(model, scratch);
  REQUIRE(update);
  const auto& delta = std::get<ClientStateDelta>(*update);
  REQUIRE(delta.chats.size() == 1);
  CHECK(delta.chats[0].channelId == GuildChannel(4));
  CHECK_FALSE(delta.chats[0].state);
  CHECK(delta.chatContent.empty());

  // Registered again in one drain: a reset of the new cache, with only its own content.
  REQUIRE(model.RegisterChannel(GuildChannel(5), 20, Domain::ChatChannelKind::Guild));
  REQUIRE(model.Apply(model.Generation(), ChatMessagesReceived{GuildChannel(5), {GuildMessage(5, 1)}}));
  REQUIRE(model.UnregisterChannel(GuildChannel(5)));
  REQUIRE(model.RegisterChannel(GuildChannel(5), 20, Domain::ChatChannelKind::Guild));
  REQUIRE(model.Apply(model.Generation(), ChatMessagesReceived{GuildChannel(5), {GuildMessage(5, 2)}}));
  update = TakeStateUpdate(model, scratch);
  REQUIRE(update);
  const auto& again = std::get<ClientStateDelta>(*update);
  REQUIRE(again.chats.size() == 1);
  CHECK(again.chats[0].state);
  CHECK(again.chats[0].resetContent);
  REQUIRE(again.chatContent.size() == 1);
  const auto& added = std::get<ChatMessagesAdded>(again.chatContent[0]);
  REQUIRE(added.messages.size() == 1);
  CHECK(added.messages[0].messageId == 2);
}

TEST_SUITE_END();
