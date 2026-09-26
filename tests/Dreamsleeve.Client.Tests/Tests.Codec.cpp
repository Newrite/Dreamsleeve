#include <doctest/doctest.h>
#include "chat.pb.h"
import std;
import Dreamsleeve.Client.Codec;

namespace
{

  using namespace Dreamsleeve::Client;
  namespace W = Dreamsleeve::Client::Wire;
  const Configuration config{};
  namespace P = Dreamsleeve::Protocol::Chat;

  W::Codec MakeCodec(Configuration settings = config)
  {
    auto result = W::Codec::TryCreate(std::move(settings));
    REQUIRE(result);
    return std::move(*result);
  }

  std::vector<std::byte> Bytes(const P::ServerPacket& packet)
  {
    std::vector<std::byte> bytes(packet.ByteSizeLong());
    REQUIRE(packet.SerializeToArray(bytes.data(), static_cast<int>(bytes.size())));
    return bytes;
  }

  P::ServerPacket Published()
  {
    P::ServerPacket packet;
    packet.set_protocol_version(W::Version);
    auto* message = packet.mutable_chat_published()->mutable_message();
    message->set_message_id(std::numeric_limits<std::uint64_t>::max());
    message->set_channel_id(1);
    message->set_text("Привет\nworld");
    message->set_sent_at_unix_ms(-1);
    message->mutable_author()->set_player_id(7);
    message->mutable_author()->set_username("player");
    message->mutable_author()->set_display_name("Display");
    return packet;
  }

  P::ServerPacket Welcome()
  {
    auto            published = Published();
    P::ServerPacket packet;
    packet.set_protocol_version(W::Version);
    packet.set_request_id(1);
    auto* welcome = packet.mutable_session_opened();
    welcome->set_self_player_id(7);
    welcome->set_global_channel_id(1);
    *welcome->add_players()         = published.chat_published().message().author();
    *welcome->add_recent_messages() = published.chat_published().message();
    return packet;
  }

}

TEST_SUITE_BEGIN("Client.Codec");

TEST_CASE("Client encoding preserves raw input and full width correlation for server validation")
{
  const auto codec = MakeCodec();
  const auto hello = codec.Encode(W::OpenSession{42, " User ", " Имя "});
  REQUIRE(hello);
  CHECK(hello->Flags() == PacketFlag::Reliable);
  P::ClientPacket packet;
  REQUIRE(packet.ParseFromArray(hello->DataBytesView().data(), static_cast<int>(hello->Size())));
  CHECK(packet.protocol_version() == 1);
  CHECK(packet.request_id() == 42);
  CHECK(packet.open_session().username() == " User ");
  CHECK(packet.open_session().display_name() == " Имя ");
  auto chat = codec.Encode(SendChat{std::numeric_limits<std::uint64_t>::max(), 1, "Привет\nworld"});
  REQUIRE(chat);
  REQUIRE(packet.ParseFromArray(chat->DataBytesView().data(), static_cast<int>(chat->Size())));
  CHECK(packet.request_id() == std::numeric_limits<std::uint64_t>::max());
  CHECK(packet.send_chat().text() == "Привет\nworld");
  CHECK_FALSE(codec.Encode(SendChat{0, 1, "text"}));
  CHECK_FALSE(codec.Encode(SendChat{1, 0, "text"}));
  CHECK_FALSE(codec.Encode(SendChat{1, 1, std::string(config.network.maxPacketBytes, 'x')}));
}

TEST_CASE("Own and broadcast chat decode into the same owned normal chat event")
{
  const auto codec     = MakeCodec();
  auto       packet    = Published();
  auto       broadcast = codec.Decode(Bytes(packet));
  REQUIRE(broadcast);
  CHECK(std::holds_alternative<ChatMessagesReceived>(*broadcast));
  packet.set_request_id(42);
  auto own = codec.Decode(Bytes(packet));
  REQUIRE(own);
  CHECK(std::get<W::ChatAccepted>(*own).requestId == 42);
  const auto& first  = std::get<ChatMessagesReceived>(*broadcast);
  const auto& second = std::get<W::ChatAccepted>(*own).changes;
  REQUIRE(first.messages.size() == 1);
  CHECK(first.messages == second.messages);
  CHECK(first.messages[0].messageId == std::numeric_limits<std::uint64_t>::max());
  CHECK(Domain::ToUnixMilliseconds(first.messages[0].sentAt) == -1);
  packet.Clear();
  CHECK(first.messages[0].messageText == "Привет\nworld");
}

TEST_CASE("Welcome decoding returns ordinary player and chat data without applying model policy")
{
  const auto codec  = MakeCodec();
  auto       packet = Welcome();
  packet.mutable_session_opened()->mutable_recent_messages(0)->mutable_author()->set_player_id(99);
  auto decoded = codec.Decode(Bytes(packet));
  REQUIRE(decoded);
  const auto& opened = std::get<W::SessionOpened>(*decoded);
  CHECK(opened.requestId == 1);
  CHECK(opened.selfPlayerId == 7);
  CHECK(opened.globalChannelId == 1);
  REQUIRE(opened.players.size() == 1);
  CHECK(opened.players[0].data.playerId == 7);
  REQUIRE(opened.recentMessages.size() == 1);
  CHECK(opened.recentMessages[0].author.playerId == 99);  // An author may be offline.

  auto* welcome           = packet.mutable_session_opened();
  auto  duplicate         = welcome->players(0);
  *welcome->add_players() = duplicate;
  welcome->set_self_player_id(8);
  welcome->mutable_recent_messages(0)->set_channel_id(2);
  CHECK(codec.Decode(Bytes(packet)));  // Store/state policy is not repeated in the codec.
  packet.clear_request_id();
  CHECK_FALSE(codec.Decode(Bytes(packet)));
}

TEST_CASE("Rejections retain unknown codes and correlation while presence events forbid correlation")
{
  const auto      codec = MakeCodec();
  P::ServerPacket packet;
  packet.set_protocol_version(W::Version);
  packet.set_request_id(9);
  auto* rejected = packet.mutable_request_rejected();
  rejected->set_code(static_cast<P::RequestRejectionCode>(0x7FFF0001));
  rejected->set_message("Отказ");
  rejected->set_field("text");
  auto result = codec.Decode(Bytes(packet));
  REQUIRE(result);
  const auto& rejection = std::get<ServerRejection>(*result);
  CHECK(rejection.requestId == 9);
  CHECK(static_cast<std::int32_t>(rejection.code) == 0x7FFF0001);
  CHECK(rejection.message == "Отказ");
  packet.mutable_player_left()->set_player_id(7);
  CHECK_FALSE(codec.Decode(Bytes(packet)));
  packet.clear_request_id();
  result = codec.Decode(Bytes(packet));
  REQUIRE(result);
  CHECK(std::get<PlayerRemoved>(*result).playerId == 7);
  *packet.mutable_player_joined()->mutable_player() = Published().chat_published().message().author();
  result                                            = codec.Decode(Bytes(packet));
  REQUIRE(result);
  CHECK(std::get<PlayerUpserted>(*result).player.data.playerId == 7);
}

TEST_CASE("Malformed unsupported and structurally incomplete server packets return errors")
{
  const auto codec = MakeCodec();
  CHECK_FALSE(codec.Decode({}));
  const std::vector<std::byte> truncated{std::byte{0x5a}, std::byte{0xff}};
  CHECK_FALSE(codec.Decode(truncated));
  CHECK_FALSE(codec.Decode(std::vector<std::byte>(config.network.maxPacketBytes + 1)));
  auto packet = Published();
  packet.set_protocol_version(2);
  auto result = codec.Decode(Bytes(packet));
  REQUIRE_FALSE(result);
  CHECK(result.error().code == W::ErrorCode::UnsupportedVersion);
  packet.set_protocol_version(1);
  packet.mutable_chat_published()->clear_message();
  CHECK_FALSE(codec.Decode(Bytes(packet)));
  packet.mutable_chat_published()->mutable_message();
  CHECK_FALSE(codec.Decode(Bytes(packet)));  // Present but empty is equally invalid.
  packet = Published();
  packet.mutable_chat_published()->mutable_message()->clear_author();
  CHECK_FALSE(codec.Decode(Bytes(packet)));
  packet.mutable_player_joined();  // Absent profile exposes the default zero ID.
  CHECK_FALSE(codec.Decode(Bytes(packet)));
  packet = Published();
  packet.mutable_chat_published()->mutable_message()->set_sent_at_unix_ms(std::numeric_limits<std::int64_t>::max());
  CHECK_FALSE(codec.Decode(Bytes(packet)));
  packet = Published();
  packet.set_request_id(0);
  CHECK_FALSE(codec.Decode(Bytes(packet)));
  packet.clear_request_id();
  auto bytes = Bytes(packet);
  bytes.insert(bytes.end(), {std::byte{0x98}, std::byte{0x06}, std::byte{0x01}});
  CHECK(codec.Decode(bytes));  // An unknown additive field is allowed within this version.
  const std::vector<std::byte> futurePayload{std::byte{0x08}, std::byte{0x01}, std::byte{0x7a}, std::byte{0x00}};
  auto                         unknown = codec.Decode(futurePayload);
  REQUIRE_FALSE(unknown);
  CHECK(unknown.error().code == W::ErrorCode::InvalidPayload);
  CHECK(unknown.error().field == "payload");
}

TEST_CASE("Configured packet size applies to both codec directions at the exact boundary")
{
  const auto     codec = MakeCodec();
  Configuration  settings;
  const SendChat request{1, 1, std::string(180, 'x')};
  auto           encoded = MakeCodec(settings).Encode(request);
  REQUIRE(encoded);
  settings.network.maxPacketBytes = encoded->Size();
  const auto exact                = MakeCodec(settings);
  CHECK(exact.Encode(request));
  --settings.network.maxPacketBytes;
  CHECK(exact.Encode(request));  // Caller edits cannot change an existing codec's budget.
  auto tooLarge = MakeCodec(settings).Encode(request);
  REQUIRE_FALSE(tooLarge);
  CHECK(tooLarge.error().code == W::ErrorCode::PacketTooLarge);

  auto bytes                      = Bytes(Published());
  settings.network.maxPacketBytes = bytes.size();
  CHECK(MakeCodec(settings).Decode(bytes));
  --settings.network.maxPacketBytes;
  auto rejected = MakeCodec(settings).Decode(bytes);
  REQUIRE_FALSE(rejected);
  CHECK(rejected.error().code == W::ErrorCode::PacketTooLarge);
}

TEST_CASE("Bootstrap counts come from configuration and zero retained messages is allowed")
{
  const auto    codec = MakeCodec();
  Configuration settings;
  auto          packet  = Welcome();
  auto*         welcome = packet.mutable_session_opened();
  auto          second  = welcome->players(0);
  second.set_player_id(8);
  *welcome->add_players()    = second;
  settings.maxInitialPlayers = 2;
  CHECK(MakeCodec(settings).Decode(Bytes(packet)));
  settings.maxInitialPlayers = 1;
  CHECK_FALSE(MakeCodec(settings).Decode(Bytes(packet)));
  welcome->mutable_players()->RemoveLast();
  settings.maxRecentMessages = 0;
  CHECK_FALSE(MakeCodec(settings).Decode(Bytes(packet)));
  welcome->clear_recent_messages();
  CHECK(MakeCodec(settings).Decode(Bytes(packet)));
}

TEST_CASE("Invalid protocol configuration prevents codec creation")
{
  for (int which = 0; which != 5; ++which)
  {
    Configuration settings;
    switch (which)
    {
      case 0:
        settings.network.maxPacketBytes = 0;
        break;
      case 1:
        settings.network.maxPacketBytes = static_cast<std::size_t>(std::numeric_limits<int>::max()) + 1;
        break;
      case 2:
        settings.network.maxWaitingData = settings.network.maxPacketBytes - 1;
        break;
      case 3:
        settings.maxInitialPlayers = 0;
        break;
      case 4:
        settings.maxRecentMessages = static_cast<std::size_t>(std::numeric_limits<int>::max()) + 1;
        break;
    }
    auto result = W::Codec::TryCreate(settings);
    REQUIRE_FALSE(result);
    CHECK(result.error().code == W::ErrorCode::InvalidConfig);
  }
}

TEST_CASE("Rejection codes share protobuf names and retain future signed enum values")
{
  const auto      codec = MakeCodec();
  P::ServerPacket packet;
  packet.set_protocol_version(W::Version);
  packet.set_request_id(8);
  auto* rejected = packet.mutable_request_rejected();
  rejected->set_code(P::REQUEST_REJECTION_CODE_USERNAME_TAKEN);
  rejected->set_message("Имя занято");
  rejected->set_field("username");
  auto decoded = codec.Decode(Bytes(packet));
  REQUIRE(decoded);
  CHECK(std::get<ServerRejection>(*decoded).code == RequestRejectionCode::UsernameTaken);

  for (const auto code : {0x7FFF0001, -1})
  {
    rejected->set_code(static_cast<P::RequestRejectionCode>(code));
    auto future = codec.Decode(Bytes(packet));
    REQUIRE(future);
    const auto& value = std::get<ServerRejection>(*future);
    CHECK(static_cast<std::int32_t>(value.code) == code);
    CHECK(value.message == "Имя занято");
    CHECK(value.field == "username");
  }
  rejected->clear_code();
  auto missing = codec.Decode(Bytes(packet));
  REQUIRE_FALSE(missing);
  CHECK(missing.error().field == "code");
}

TEST_SUITE_END();
