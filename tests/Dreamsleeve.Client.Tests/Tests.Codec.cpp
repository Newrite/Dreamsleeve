#include <doctest/doctest.h>
#include "protocol.pb.h"
import std;
import Dreamsleeve.Client.ProtocolCodec;

namespace
{

  using namespace Dreamsleeve::Client;
  namespace W = Dreamsleeve::Client::Wire;
  const Configuration config{};
  namespace P = Dreamsleeve::Protocol::Chat;

  W::ProtocolCodec MakeCodec(Configuration settings = config)
  {
    auto result = W::ProtocolCodec::TryCreate(std::move(settings));
    REQUIRE(result);
    return std::move(*result);
  }

  template <class T>
  std::vector<std::byte> Bytes(const T& packet)
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
    welcome->mutable_announcements()->set_max_text_length(500);
    auto* global = welcome->add_channels();
    global->set_channel_id(1);
    global->set_kind(P::CHAT_CHANNEL_KIND_GLOBAL);
    *welcome->add_players()->mutable_profile() = published.chat_published().message().author();
    *global->add_recent_messages()             = published.chat_published().message();
    return packet;
  }

}

TEST_SUITE_BEGIN("Client.Codec");

TEST_CASE("Client encoding preserves raw input and full width correlation for server validation")
{
  const auto codec = MakeCodec();
  const auto hello = codec.Encode(W::OpenSession{42, std::string(43, 'A')});
  REQUIRE(hello);
  CHECK(hello->Flags() == PacketFlag::Reliable);
  P::ClientPacket packet;
  REQUIRE(packet.ParseFromArray(hello->DataBytesView().data(), static_cast<int>(hello->Size())));
  CHECK(packet.protocol_version() == W::Version);
  CHECK(packet.request_id() == 42);
  CHECK(packet.open_session().session_ticket() == std::string(43, 'A'));
  CHECK_FALSE(codec.Encode(W::OpenSession{1, ""}));
  CHECK_FALSE(codec.Encode(W::OpenSession{1, std::string(43, '/')}));
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
  auto       broadcast = codec.Decode(Bytes(packet), packet.has_chat_published() ? W::Channel::Chat : W::Channel::Control);
  REQUIRE(broadcast);
  CHECK(std::holds_alternative<ChatMessagesReceived>(*broadcast));
  packet.set_request_id(42);
  auto own = codec.Decode(Bytes(packet), packet.has_chat_published() ? W::Channel::Chat : W::Channel::Control);
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

TEST_CASE("Chat messages keep the character snapshot and players the withheld-name flag")
{
  const auto codec  = MakeCodec();
  auto       packet = Published();
  auto       plain  = codec.Decode(Bytes(packet), W::Channel::Chat);
  REQUIRE(plain);
  CHECK_FALSE(std::get<ChatMessagesReceived>(*plain).messages[0].characterName);  // Old history: no snapshot.

  packet.mutable_chat_published()->mutable_message()->set_character_name("Lydia");
  auto named = codec.Decode(Bytes(packet), W::Channel::Chat);
  REQUIRE(named);
  CHECK(std::get<ChatMessagesReceived>(*named).messages[0].characterName == "Lydia");

  auto welcome = Welcome();
  welcome.mutable_session_opened()->mutable_players(0)->set_character_name_withheld(true);
  auto opened = codec.Decode(Bytes(welcome), W::Channel::Control);
  REQUIRE(opened);
  const auto& player = std::get<W::SessionOpened>(*opened).players.front();
  CHECK(player.characterNameWithheld);
  CHECK_FALSE(player.characterName);
}

TEST_CASE("Flagged ranges decode only inside the text, ascending and on code point boundaries")
{
  const auto codec   = MakeCodec();
  auto       packet  = Published();  // "Привет\nworld": Cyrillic letters take two bytes.
  auto*      message = packet.mutable_chat_published()->mutable_message();
  auto       add     = [&](std::uint32_t start, std::uint32_t length) {
    auto* span = message->add_flagged();
    span->set_start(start);
    span->set_length(length);
  };
  add(0, 4);
  add(13, 5);
  auto decoded = codec.Decode(Bytes(packet), W::Channel::Chat);
  REQUIRE(decoded);
  const auto& flagged = std::get<ChatMessagesReceived>(*decoded).messages[0].flagged;
  REQUIRE(flagged.size() == 2);
  CHECK(flagged[1] == Domain::TextSpan{13, 5});

  SUBCASE("past the end")
  {
    add(18, 1);
  }
  SUBCASE("overlapping")
  {
    add(16, 2);
  }
  SUBCASE("inside a code point")
  {
    message->mutable_flagged(0)->set_length(3);
  }
  SUBCASE("empty")
  {
    add(18, 0);
  }
  CHECK_FALSE(codec.Decode(Bytes(packet), W::Channel::Chat));
}

TEST_CASE("Welcome decoding returns ordinary player and chat data without applying model policy")
{
  const auto codec  = MakeCodec();
  auto       packet = Welcome();
  packet.mutable_session_opened()->mutable_channels(0)->mutable_recent_messages(0)->mutable_author()->set_player_id(99);
  auto decoded = codec.Decode(Bytes(packet), packet.has_chat_published() ? W::Channel::Chat : W::Channel::Control);
  REQUIRE(decoded);
  const auto& opened = std::get<W::SessionOpened>(*decoded);
  CHECK(opened.requestId == 1);
  CHECK(opened.selfPlayerId == 7);
  REQUIRE(opened.channels.size() == 1);
  CHECK(opened.channels[0].channelId == 1);
  CHECK(opened.channels[0].kind == Domain::ChatChannelKind::Global);
  REQUIRE(opened.players.size() == 1);
  CHECK(opened.players[0].data.playerId == 7);
  REQUIRE(opened.channels[0].recentMessages.size() == 1);
  CHECK(opened.channels[0].recentMessages[0].author->playerId == 99);  // An author may be offline.

  auto* welcome           = packet.mutable_session_opened();
  auto  duplicate         = welcome->players(0);
  *welcome->add_players() = duplicate;
  welcome->set_self_player_id(8);
  welcome->mutable_channels(0)->mutable_recent_messages(0)->set_channel_id(2);
  CHECK(codec.Decode(
    Bytes(packet),
    packet.has_chat_published() ? W::Channel::Chat : W::Channel::Control));  // Store/state policy is not repeated in the codec.
  packet.clear_request_id();
  CHECK_FALSE(codec.Decode(Bytes(packet), packet.has_chat_published() ? W::Channel::Chat : W::Channel::Control));
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
  auto result = codec.Decode(Bytes(packet), packet.has_chat_published() ? W::Channel::Chat : W::Channel::Control);
  REQUIRE(result);
  const auto& rejection = std::get<ServerRejection>(*result);
  CHECK(rejection.requestId == 9);
  CHECK(static_cast<std::int32_t>(rejection.code) == 0x7FFF0001);
  CHECK(rejection.message == "Отказ");
  packet.mutable_player_left()->set_player_id(7);
  CHECK_FALSE(codec.Decode(Bytes(packet), packet.has_chat_published() ? W::Channel::Chat : W::Channel::Control));
  packet.clear_request_id();
  result = codec.Decode(Bytes(packet), packet.has_chat_published() ? W::Channel::Chat : W::Channel::Control);
  REQUIRE(result);
  CHECK(std::get<PlayerRemoved>(*result).playerId == 7);
  *packet.mutable_player_joined()->mutable_player()->mutable_profile() = Published().chat_published().message().author();
  result = codec.Decode(Bytes(packet), packet.has_chat_published() ? W::Channel::Chat : W::Channel::Control);
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
  packet.set_protocol_version(1);
  auto result = codec.Decode(Bytes(packet), packet.has_chat_published() ? W::Channel::Chat : W::Channel::Control);
  REQUIRE_FALSE(result);
  CHECK(result.error().code == W::ErrorCode::UnsupportedVersion);
  packet.set_protocol_version(W::Version);
  packet.mutable_chat_published()->clear_message();
  CHECK_FALSE(codec.Decode(Bytes(packet), packet.has_chat_published() ? W::Channel::Chat : W::Channel::Control));
  packet.mutable_chat_published()->mutable_message();
  CHECK_FALSE(codec.Decode(
    Bytes(packet),
    packet.has_chat_published() ? W::Channel::Chat : W::Channel::Control));  // Present but empty is equally invalid.
  packet = Published();
  packet.mutable_chat_published()->mutable_message()->clear_author();
  CHECK_FALSE(codec.Decode(Bytes(packet), packet.has_chat_published() ? W::Channel::Chat : W::Channel::Control));
  packet.mutable_player_joined();  // Absent profile exposes the default zero ID.
  CHECK_FALSE(codec.Decode(Bytes(packet), packet.has_chat_published() ? W::Channel::Chat : W::Channel::Control));
  packet = Published();
  packet.mutable_chat_published()->mutable_message()->set_sent_at_unix_ms(std::numeric_limits<std::int64_t>::max());
  CHECK_FALSE(codec.Decode(Bytes(packet), packet.has_chat_published() ? W::Channel::Chat : W::Channel::Control));
  packet = Published();
  packet.set_request_id(0);
  CHECK_FALSE(codec.Decode(Bytes(packet), packet.has_chat_published() ? W::Channel::Chat : W::Channel::Control));
  packet.clear_request_id();
  auto bytes = Bytes(packet);
  bytes.insert(bytes.end(), {std::byte{0x98}, std::byte{0x06}, std::byte{0x01}});
  CHECK(codec.Decode(bytes, W::Channel::Chat));  // An unknown additive field is allowed within this version.
  const std::vector<std::byte> futurePayload{std::byte{0x08}, std::byte{W::Version}, std::byte{0xFA}, std::byte{0x01}, std::byte{0x00}};
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
  CHECK(MakeCodec(settings).Decode(bytes, W::Channel::Chat));
  --settings.network.maxPacketBytes;
  auto rejected = MakeCodec(settings).Decode(bytes, W::Channel::Chat);
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
  second.mutable_profile()->set_player_id(8);
  *welcome->add_players()    = second;
  settings.maxInitialPlayers = 2;
  CHECK(MakeCodec(settings).Decode(Bytes(packet)));
  settings.maxInitialPlayers = 1;
  CHECK_FALSE(MakeCodec(settings).Decode(Bytes(packet)));
  welcome->mutable_players()->RemoveLast();
  settings.maxRecentMessages = 0;
  CHECK_FALSE(MakeCodec(settings).Decode(Bytes(packet)));
  welcome->mutable_channels(0)->clear_recent_messages();
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
    auto result = W::ProtocolCodec::TryCreate(settings);
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
  auto decoded = codec.Decode(Bytes(packet), packet.has_chat_published() ? W::Channel::Chat : W::Channel::Control);
  REQUIRE(decoded);
  CHECK(std::get<ServerRejection>(*decoded).code == RequestRejectionCode::UsernameTaken);

  rejected->set_code(P::REQUEST_REJECTION_CODE_OVERLOADED);
  auto overloaded = codec.Decode(Bytes(packet), packet.has_chat_published() ? W::Channel::Chat : W::Channel::Control);
  REQUIRE(overloaded);
  CHECK(std::get<ServerRejection>(*overloaded).code == RequestRejectionCode::Overloaded);

  rejected->set_code(P::REQUEST_REJECTION_CODE_AUTHENTICATION_FAILED);
  auto unauthenticated = codec.Decode(Bytes(packet), packet.has_chat_published() ? W::Channel::Chat : W::Channel::Control);
  REQUIRE(unauthenticated);
  CHECK(std::get<ServerRejection>(*unauthenticated).code == RequestRejectionCode::AuthenticationFailed);

  for (const auto code : {0x7FFF0001, -1})
  {
    rejected->set_code(static_cast<P::RequestRejectionCode>(code));
    auto future = codec.Decode(Bytes(packet), packet.has_chat_published() ? W::Channel::Chat : W::Channel::Control);
    REQUIRE(future);
    const auto& value = std::get<ServerRejection>(*future);
    CHECK(static_cast<std::int32_t>(value.code) == code);
    CHECK(value.message == "Имя занято");
    CHECK(value.field == "username");
  }
  rejected->clear_code();
  auto missing = codec.Decode(Bytes(packet), packet.has_chat_published() ? W::Channel::Chat : W::Channel::Control);
  REQUIRE_FALSE(missing);
  CHECK(missing.error().field == "code");
}

TEST_CASE("Player update encoding retains full samples explicit zero resource values and structured details")
{
  const auto    codec = MakeCodec();
  LocalMovement sample;
  sample.location = Domain::PlayerLocation{
      {{"skyrim.esm", 0x123}, "Whiterun"},
      {1, 2, 3},
      {0, 0, 3.14f},
      123456789
  };
  LocalActorValues values;
  values.actorValues.emplace("speed", Domain::ActorValueInfo{"Speed", Domain::ScalarActorValue{0}});
  values.actorValues.emplace(
    "health",
    Domain::ActorValueInfo{
        "Health",
        Domain::ResourceActorValue{150, 100}
  });
  auto encoded = codec.Encode(
    W::UpdatePlayer{
        51,
        W::SetLocation{1, sample.location}
  });
  REQUIRE(encoded);
  P::ClientPacket packet;
  REQUIRE(packet.ParseFromArray(encoded->DataBytesView().data(), static_cast<int>(encoded->Size())));
  const auto& movement = packet.update_player().set_location();
  CHECK(movement.location().sampled_at_us() == 123456789);
  CHECK(movement.location().position().x() == 1);
  CHECK(movement.location().rotation().z() == doctest::Approx(3.14));
  encoded = codec.Encode(W::UpdatePlayer{52, values});
  REQUIRE(encoded);
  REQUIRE(packet.ParseFromArray(encoded->DataBytesView().data(), static_cast<int>(encoded->Size())));
  const auto& wire = packet.update_player().set_actor_values();
  CHECK(wire.values_size() == 2);
  for (const auto& entry : wire.values())
  {
    if (entry.key() == "speed")
    {
      CHECK(entry.value_case() == P::ActorValueEntry::kScalar);
      CHECK(entry.scalar() == 0);
    }
    else
    {
      CHECK(entry.resource().current() == 150);
      CHECK(entry.resource().maximum() == 100);
    }
  }

  Domain::PlayerDetails details;
  details.race = Domain::NamedForm{
      {"skyrim.esm", 0x13746},
      "Nord"
  };
  details.level               = 0;
  details.activity            = {Domain::ActivityKind::Lockpicking, "Chest", Domain::LockDifficulty::VeryHard, std::nullopt};
  details.place               = Domain::PlaceDescription{"Tamriel", "Whiterun", "Dragonsreach", "castle", false};
  details.gameStartedAtUnixMs = 123456789;
  encoded                     = codec.Encode(W::UpdatePlayer{52, PlayerDetailsChanged{details}});
  REQUIRE(encoded);
  REQUIRE(packet.ParseFromArray(encoded->DataBytesView().data(), static_cast<int>(encoded->Size())));
  const auto& rich = packet.update_player().set_details();
  CHECK(rich.has_level());
  CHECK(rich.level() == 0);
  CHECK(rich.race().name() == "Nord");
  CHECK(rich.activity().kind() == P::ACTIVITY_KIND_LOCKPICKING);
  CHECK(rich.activity().lock_difficulty() == P::LOCK_DIFFICULTY_VERY_HARD);
  CHECK(rich.activity().target_name() == "Chest");
  CHECK(rich.place().nearby_marker_name() == "Dragonsreach");
  CHECK(rich.game_started_at_unix_ms() == 123456789);
  details.place.reset();
  encoded = codec.Encode(W::UpdatePlayer{53, PlayerDetailsChanged{details}});
  REQUIRE(encoded);
  REQUIRE(packet.ParseFromArray(encoded->DataBytesView().data(), static_cast<int>(encoded->Size())));
  CHECK_FALSE(packet.update_player().set_details().has_place());
}

TEST_CASE("Full PlayerInfo preserves optional data zero scalars and generation for late join")
{
  const auto codec  = MakeCodec();
  auto       packet = Welcome();
  auto*      source = packet.mutable_session_opened()->mutable_players(0);
  source->set_character_name("Nerevar");
  source->set_character_generation(5);
  source->mutable_details()->set_level(25);
  source->mutable_details()->mutable_activity()->set_kind(P::ACTIVITY_KIND_COMBAT);
  source->mutable_details()->mutable_activity()->set_target_name("Dragon");
  source->mutable_details()->set_game_started_at_unix_ms(123);
  auto* scalar = source->add_actor_values();
  scalar->set_key("zero");
  scalar->set_scalar(0);
  auto result = codec.Decode(Bytes(packet), packet.has_chat_published() ? W::Channel::Chat : W::Channel::Control);
  REQUIRE(result);
  const auto& player = std::get<W::SessionOpened>(*result).players.front();
  CHECK(player.characterName == "Nerevar");
  CHECK(player.characterGeneration == 5);
  CHECK(player.details.level == 25);
  CHECK_FALSE(player.details.place);
  CHECK(player.details.activity.kind == Domain::ActivityKind::Combat);
  CHECK(player.details.activity.targetName == "Dragon");
  CHECK(player.details.gameStartedAtUnixMs == 123);
  CHECK(std::get<Domain::ScalarActorValue>(player.actorValues.at("zero").state).value == 0);

  SUBCASE("missing actor value")
  {
    scalar->clear_value();
  }
  SUBCASE("nonfinite actor value")
  {
    scalar->set_scalar(std::numeric_limits<float>::infinity());
  }
  SUBCASE("duplicate actor key")
  {
    *source->add_actor_values() = *scalar;
  }
  SUBCASE("too many values")
  {
    for (int index = 0; index < 64; ++index)
    {
      auto* value = source->add_actor_values();
      value->set_key(std::to_string(index));
      value->set_scalar(0);
    }
  }
  CHECK_FALSE(codec.Decode(Bytes(packet), packet.has_chat_published() ? W::Channel::Chat : W::Channel::Control));
}

TEST_CASE("Actor value limits are configured for both outgoing samples and incoming player state")
{
  Configuration settings;
  settings.maxActorValues = 1;
  const auto       codec  = MakeCodec(settings);
  LocalActorValues sample;
  sample.actorValues.emplace("skyrim:health", Domain::ActorValueInfo{"Health", Domain::ScalarActorValue{0}});
  sample.actorValues.emplace("skyrim:stamina", Domain::ActorValueInfo{"Stamina", Domain::ScalarActorValue{1}});
  CHECK_FALSE(codec.Encode(W::UpdatePlayer{1, sample}));
  auto  packet = Welcome();
  auto* player = packet.mutable_session_opened()->mutable_players(0);
  for (const auto* key : {"skyrim:health", "skyrim:stamina"})
  {
    auto* value = player->add_actor_values();
    value->set_key(key);
    value->set_scalar(0);
  }
  CHECK_FALSE(codec.Decode(Bytes(packet), packet.has_chat_published() ? W::Channel::Chat : W::Channel::Control));
  settings.maxActorValues = 65;
  CHECK(MakeCodec(settings).Decode(Bytes(packet)));
  settings.maxActorValues = 0;
  CHECK_FALSE(W::ProtocolCodec::TryCreate(settings));
}

TEST_CASE("Player update correlation is distinct from uncorrelated full and compact replication")
{
  const auto      codec = MakeCodec();
  P::ServerPacket packet;
  packet.set_protocol_version(W::Version);
  packet.mutable_player_update_accepted();
  CHECK_FALSE(codec.Decode(Bytes(packet), packet.has_chat_published() ? W::Channel::Chat : W::Channel::Control));
  packet.set_request_id(1);
  auto accepted = codec.Decode(Bytes(packet), packet.has_chat_published() ? W::Channel::Chat : W::Channel::Control);
  REQUIRE(accepted);
  CHECK(std::get<W::PlayerUpdateAccepted>(*accepted).requestId == 1);
  auto* boundary = packet.mutable_player_visibility_changed();
  boundary->set_player_id(7);
  boundary->set_view_revision(1);
  CHECK_FALSE(codec.Decode(Bytes(packet)));
  packet.clear_request_id();
  auto moved = codec.Decode(Bytes(packet));
  REQUIRE(moved);
  CHECK(std::get<PlayerLocationUpdated>(*moved).playerId == 7);
  CHECK_FALSE(std::get<PlayerLocationUpdated>(*moved).location);
  packet.mutable_player_updated()->mutable_player()->mutable_profile()->set_player_id(7);
  REQUIRE(codec.Decode(Bytes(packet), packet.has_chat_published() ? W::Channel::Chat : W::Channel::Control));
  packet.set_request_id(1);
  CHECK_FALSE(codec.Decode(Bytes(packet), packet.has_chat_published() ? W::Channel::Chat : W::Channel::Control));
}

TEST_CASE("Metadata notifications distinguish omitted components from empty replacements")
{
  const auto      codec = MakeCodec();
  P::ServerPacket packet;
  packet.set_protocol_version(W::Version);
  auto* patch = packet.mutable_player_metadata_changed();
  patch->set_player_id(7);
  CHECK_FALSE(codec.Decode(Bytes(packet), packet.has_chat_published() ? W::Channel::Chat : W::Channel::Control));
  patch->mutable_actor_values();
  auto result = codec.Decode(Bytes(packet), packet.has_chat_published() ? W::Channel::Chat : W::Channel::Control);
  REQUIRE(result);
  const auto& empty = std::get<PlayerMetadataUpdated>(*result);
  REQUIRE(empty.actorValues);
  CHECK(empty.actorValues->empty());
  CHECK_FALSE(empty.details);
  patch->clear_actor_values();
  patch->mutable_details()->set_level(0);
  result = codec.Decode(Bytes(packet), packet.has_chat_published() ? W::Channel::Chat : W::Channel::Control);
  REQUIRE(result);
  const auto& details = std::get<PlayerMetadataUpdated>(*result);
  CHECK_FALSE(details.actorValues);
  REQUIRE(details.details);
  CHECK(details.details->level == 0);
  packet.set_request_id(1);
  CHECK_FALSE(codec.Decode(Bytes(packet), packet.has_chat_published() ? W::Channel::Chat : W::Channel::Control));
}

TEST_CASE("Movement uses a separate unreliable envelope bounded by negotiated payload")
{
  const auto        codec = MakeCodec();
  W::MovementSample source{
      11,
      42,
      {{0, 2, 3}, {0, 0, 1}, 12345}
  };
  auto encoded = codec.Encode(source, 1200);
  REQUIRE(encoded);
  CHECK(encoded->Flags() == PacketFlag::None);
  P::ClientMovementPacket packet;
  REQUIRE(packet.ParseFromArray(encoded->DataBytesView().data(), static_cast<int>(encoded->Size())));
  CHECK(packet.sample().context_revision() == 11);
  CHECK(packet.sample().sequence() == 42);
  CHECK(packet.sample().pose().sampled_at_us() == 12345);
  CHECK(codec.Encode(source, encoded->Size()));
  CHECK_FALSE(codec.Encode(source, encoded->Size() - 1));
  CHECK_FALSE(codec.Encode(W::MovementSample{0, 1, {}}, 1200));
}

TEST_CASE("Movement batch validates sequence context finite pose and selected channel")
{
  const auto              codec = MakeCodec();
  P::ServerMovementPacket packet;
  packet.set_protocol_version(W::Version);
  CHECK_FALSE(codec.Decode(Bytes(packet), W::Channel::Realtime));
  for (auto id : {7, 8})
  {
    auto* sample = packet.mutable_movements()->add_players();
    sample->set_player_id(id);
    sample->set_view_revision(9);
    sample->set_sequence(12);
    sample->mutable_pose()->set_sampled_at_us(0);
  }
  auto decoded = codec.Decode(Bytes(packet), W::Channel::Realtime);
  REQUIRE(decoded);
  CHECK(std::get<W::PlayersMoved>(*decoded).players[0].pose.sampledAtUs == 0);
  CHECK(std::get<W::PlayersMoved>(*decoded).players[1].playerId == 8);
  CHECK_FALSE(codec.Decode(Bytes(packet), W::Channel::Control));
  packet.mutable_movements()->mutable_players(0)->set_sequence(0);
  CHECK(codec.Decode(Bytes(packet), W::Channel::Realtime));  // A repeated reliable baseline.
  SUBCASE("unknown player")
  {
    packet.mutable_movements()->mutable_players(1)->set_player_id(0);
  }
  SUBCASE("empty context")
  {
    packet.mutable_movements()->mutable_players(1)->set_view_revision(0);
  }
  SUBCASE("nonfinite pose")
  {
    packet.mutable_movements()->mutable_players(1)->mutable_pose()->mutable_position()->set_x(std::numeric_limits<float>::infinity());
  }
  SUBCASE("wrong version")
  {
    packet.set_protocol_version(5);
  }
  CHECK_FALSE(codec.Decode(Bytes(packet), W::Channel::Realtime));
}

TEST_SUITE_END();

TEST_SUITE_BEGIN("Client.Codec");

TEST_CASE("Announcements decode with unknown values kept, need an author unless the server posts, and encode on the chat lane")
{
  const auto codec  = MakeCodec();
  auto       packet = Published();
  auto*      value  = packet.mutable_chat_published()->mutable_message()->mutable_announcement();
  value->set_source(P::ANNOUNCEMENT_SOURCE_SERVER);
  value->set_kind(P::ANNOUNCEMENT_KIND_PERIODIC);
  auto decoded = codec.Decode(Bytes(packet), W::Channel::Chat);
  REQUIRE(decoded);
  const auto& message = std::get<ChatMessagesReceived>(*decoded).messages.front();
  REQUIRE(message.announcement);
  CHECK(message.announcement->source == Domain::AnnouncementSource::Server);
  CHECK(message.announcement->kind == Domain::AnnouncementKind::Periodic);
  CHECK(message.announcement->signature.empty());

  // A newer server may add values; they are not a protocol error.
  value->set_source(static_cast<P::AnnouncementSource>(42));
  value->set_kind(static_cast<P::AnnouncementKind>(43));
  value->set_signature("Mod");
  decoded = codec.Decode(Bytes(packet), W::Channel::Chat);
  REQUIRE(decoded);
  const auto& unknown = std::get<ChatMessagesReceived>(*decoded).messages.front();
  CHECK(static_cast<int>(unknown.announcement->source) == 42);
  CHECK(unknown.announcement->signature == "Mod");
  CHECK_FALSE(std::get<ChatMessagesReceived>(*codec.Decode(Bytes(Published()), W::Channel::Chat)).messages.front().announcement);

  // Only a server announcement has no author.
  auto authorless = Published();
  authorless.mutable_chat_published()->mutable_message()->clear_author();
  CHECK_FALSE(codec.Decode(Bytes(authorless), W::Channel::Chat));
  authorless.mutable_chat_published()->mutable_message()->mutable_announcement()->set_source(P::ANNOUNCEMENT_SOURCE_SERVER);
  auto server = codec.Decode(Bytes(authorless), W::Channel::Chat);
  REQUIRE(server);
  CHECK_FALSE(std::get<ChatMessagesReceived>(*server).messages.front().author);

  auto welcome = Welcome();
  welcome.mutable_session_opened()->clear_announcements();
  CHECK_FALSE(codec.Decode(Bytes(welcome), W::Channel::Control));
  welcome.mutable_session_opened()->mutable_channels(0)->set_kind(P::CHAT_CHANNEL_KIND_UNSPECIFIED);
  auto* policy = welcome.mutable_session_opened()->mutable_announcements();
  CHECK_FALSE(codec.Decode(Bytes(welcome), W::Channel::Control));
  welcome.mutable_session_opened()->mutable_channels(0)->set_kind(P::CHAT_CHANNEL_KIND_GLOBAL);
  policy->add_allowed_sources(P::CLIENT_ANNOUNCEMENT_SOURCE_TRUSTED_CLIENT);
  policy->set_max_text_length(500);
  policy->set_max_signature_length(64);
  auto opened = codec.Decode(Bytes(welcome), W::Channel::Control);
  REQUIRE(opened);
  const auto& announced = std::get<W::SessionOpened>(*opened).announcements;
  CHECK(announced.Allows(Domain::ClientAnnouncementSource::TrustedClient));
  CHECK_FALSE(announced.Allows(Domain::ClientAnnouncementSource::ThirdParty));
  CHECK(announced.maxSignatureLength == 64);

  const W::ClientRequest request =
    PostAnnouncement{5, 2, "Пал", Domain::AnnouncementKind::Event, Domain::ClientAnnouncementSource::ThirdParty, "Мод"};
  CHECK(W::ProtocolCodec::RequestChannel(request) == W::Channel::Chat);
  auto encoded = codec.Encode(request);
  REQUIRE(encoded);
  P::ClientPacket wire;
  REQUIRE(wire.ParseFromArray(encoded->DataBytesView().data(), static_cast<int>(encoded->Size())));
  CHECK(wire.request_id() == 5);
  CHECK(wire.post_announcement().channel_id() == 2);
  CHECK(wire.post_announcement().text() == "Пал");
  CHECK(wire.post_announcement().kind() == P::ANNOUNCEMENT_KIND_EVENT);
  CHECK(wire.post_announcement().source() == P::CLIENT_ANNOUNCEMENT_SOURCE_THIRD_PARTY);
  CHECK(wire.post_announcement().signature() == "Мод");
  CHECK_FALSE(codec.Encode(
    W::ClientRequest{
        PostAnnouncement{6, 2, "", Domain::AnnouncementKind::Event}
  }));
  CHECK_FALSE(codec.Encode(
    W::ClientRequest{
        PostAnnouncement{7, 0, "text", Domain::AnnouncementKind::Event}
  }));
}

TEST_SUITE_END();
