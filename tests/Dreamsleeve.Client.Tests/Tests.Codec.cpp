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
    return W::ProtocolCodec{std::move(settings)};
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
  // The ticket's shape is the server's to judge, like every content rule.
  CHECK(codec.Encode(W::OpenSession{1, std::string(43, '/')}));
  auto chat = codec.Encode(SendChat{std::numeric_limits<std::uint64_t>::max(), 1, "Привет\nworld"});
  REQUIRE(chat);
  REQUIRE(packet.ParseFromArray(chat->DataBytesView().data(), static_cast<int>(chat->Size())));
  CHECK(packet.request_id() == std::numeric_limits<std::uint64_t>::max());
  CHECK(packet.send_chat().text() == "Привет\nworld");
  CHECK_FALSE(codec.Encode(SendChat{0, 1, "text"}));
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

TEST_CASE("A mute and the end of a session decode without correlation, with the reason and the end")
{
  const auto      codec = MakeCodec();
  P::ServerPacket muted;
  muted.set_protocol_version(W::Version);
  auto* mute = muted.mutable_mute_changed()->mutable_mute();
  mute->set_reason("Флуд");
  mute->set_until_unix_ms(1700000900000);
  auto decoded = codec.Decode(Bytes(muted));
  REQUIRE(decoded);
  CHECK(std::get<W::MuteChanged>(*decoded).mute == Domain::MuteState{"Флуд", 1700000900000});
  muted.mutable_mute_changed()->clear_mute();
  decoded = codec.Decode(Bytes(muted));
  REQUIRE(decoded);
  CHECK_FALSE(std::get<W::MuteChanged>(*decoded).mute);
  muted.set_request_id(3);
  CHECK_FALSE(codec.Decode(Bytes(muted)));

  P::ServerPacket ended;
  ended.set_protocol_version(W::Version);
  ended.mutable_session_ended()->set_reason(P::SESSION_END_REASON_BANNED);
  ended.mutable_session_ended()->set_text("Читы");
  decoded = codec.Decode(Bytes(ended));
  REQUIRE(decoded);
  CHECK(std::get<W::SessionEnded>(*decoded).end == Domain::SessionEnd{Domain::SessionEndReason::Banned, "Читы", std::nullopt});
  ended.mutable_session_ended()->set_reason(P::SESSION_END_REASON_ADDRESS_BANNED);
  ended.mutable_session_ended()->set_text("Рейд");
  ended.mutable_session_ended()->set_until_unix_ms(1800000000000);
  decoded = codec.Decode(Bytes(ended));
  REQUIRE(decoded);
  CHECK(std::get<W::SessionEnded>(*decoded).end == Domain::SessionEnd{Domain::SessionEndReason::AddressBanned, "Рейд", 1800000000000});
  ended.mutable_session_ended()->set_reason(P::SESSION_END_REASON_UNSPECIFIED);
  CHECK_FALSE(codec.Decode(Bytes(ended)));
  ended.mutable_session_ended()->set_reason(static_cast<P::SessionEndReason>(9));
  CHECK_FALSE(codec.Decode(Bytes(ended)));

  auto welcome = Welcome();
  welcome.mutable_session_opened()->mutable_mute()->set_reason("Флуд");
  decoded = codec.Decode(Bytes(welcome));
  REQUIRE(decoded);
  CHECK(std::get<W::SessionOpened>(*decoded).mute == Domain::MuteState{"Флуд", std::nullopt});
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
  const auto& rejected9 = std::get<W::RequestRejected>(*result);
  CHECK(rejected9.requestId == 9);
  CHECK(static_cast<std::int32_t>(rejected9.rejection.code) == 0x7FFF0001);
  CHECK(rejected9.rejection.message == "Отказ");
  auto* presence = packet.mutable_presence_changed();
  presence->add_left(7);
  CHECK_FALSE(codec.Decode(Bytes(packet), packet.has_chat_published() ? W::Channel::Chat : W::Channel::Control));
  packet.clear_request_id();
  result = codec.Decode(Bytes(packet), packet.has_chat_published() ? W::Channel::Chat : W::Channel::Control);
  REQUIRE(result);
  REQUIRE(std::get<W::PresenceChanged>(*result).updates.size() == 1);
  CHECK(std::get<PlayerRemoved>(std::get<W::PresenceChanged>(*result).updates[0]).playerId == 7);
  presence->clear_left();
  *presence->add_joined()->mutable_profile() = Published().chat_published().message().author();
  result = codec.Decode(Bytes(packet), packet.has_chat_published() ? W::Channel::Chat : W::Channel::Control);
  REQUIRE(result);
  CHECK(std::get<PlayerUpserted>(std::get<W::PresenceChanged>(*result).updates.at(0)).player.data.playerId == 7);
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
  packet.mutable_presence_changed()->add_joined();  // Absent profile exposes the default zero ID.
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
  // Field 60, not defined.
  const std::vector<std::byte> futurePayload{std::byte{0x08}, std::byte{W::Version}, std::byte{0xE2}, std::byte{0x03}, std::byte{0x00}};
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
  CHECK(std::get<W::RequestRejected>(*decoded).rejection.code == RequestRejectionCode::UsernameTaken);

  rejected->set_code(P::REQUEST_REJECTION_CODE_OVERLOADED);
  auto overloaded = codec.Decode(Bytes(packet), packet.has_chat_published() ? W::Channel::Chat : W::Channel::Control);
  REQUIRE(overloaded);
  CHECK(std::get<W::RequestRejected>(*overloaded).rejection.code == RequestRejectionCode::Overloaded);

  rejected->set_code(P::REQUEST_REJECTION_CODE_AUTHENTICATION_FAILED);
  auto unauthenticated = codec.Decode(Bytes(packet), packet.has_chat_published() ? W::Channel::Chat : W::Channel::Control);
  REQUIRE(unauthenticated);
  CHECK(std::get<W::RequestRejected>(*unauthenticated).rejection.code == RequestRejectionCode::AuthenticationFailed);

  for (const auto code : {0x7FFF0001, -1})
  {
    rejected->set_code(static_cast<P::RequestRejectionCode>(code));
    auto future = codec.Decode(Bytes(packet), packet.has_chat_published() ? W::Channel::Chat : W::Channel::Control);
    REQUIRE(future);
    const auto& value = std::get<W::RequestRejected>(*future).rejection;
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
  auto* zero = packet.mutable_session_opened()->add_actor_value_kinds();
  zero->set_id(1);
  zero->set_key("zero");
  zero->set_display_name("Zero");
  auto* scalar = source->add_actor_values();
  scalar->set_kind(1);
  scalar->set_scalar(0);
  auto* health = source->add_actor_values();
  auto* kind   = packet.mutable_session_opened()->add_actor_value_kinds();
  kind->set_id(2);
  kind->set_key("skyrim:health");
  kind->set_display_name("Health");
  health->set_kind(2);
  health->mutable_resource()->set_current(-15);  // A hit larger than the health left.
  health->mutable_resource()->set_maximum(300);
  auto result = codec.Decode(Bytes(packet), packet.has_chat_published() ? W::Channel::Chat : W::Channel::Control);
  REQUIRE(result);
  const auto& opened = std::get<W::SessionOpened>(*result);
  REQUIRE(opened.kinds.size() == 2);
  CHECK(opened.kinds[1].key == "skyrim:health");
  const auto& player = opened.players.front();
  CHECK(player.characterName == "Nerevar");
  CHECK(player.characterGeneration == 5);
  CHECK(player.details.level == 25);
  CHECK_FALSE(player.details.place);
  CHECK(player.details.activity.kind == Domain::ActivityKind::Combat);
  CHECK(player.details.activity.targetName == "Dragon");
  CHECK(player.details.gameStartedAtUnixMs == 123);
  CHECK(std::get<Domain::ScalarActorValue>(player.actorValues.at("zero").state).value == 0);
  CHECK(player.actorValues.at("zero").displayName == "Zero");
  CHECK(
    player.actorValues.at("skyrim:health").state == Domain::ActorValueState{
                                                        Domain::ResourceActorValue{-15, 300}
  });

  SUBCASE("missing actor value")
  {
    scalar->clear_value();
  }
  SUBCASE("undefined kind")
  {
    scalar->set_kind(3);
  }
  SUBCASE("kind defined twice")
  {
    *packet.mutable_session_opened()->add_actor_value_kinds() = *zero;
  }
  SUBCASE("zero kind")
  {
    zero->set_id(0);
    scalar->set_kind(0);
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
    for (int index = 3; index < 66; ++index)
    {
      auto* defined = packet.mutable_session_opened()->add_actor_value_kinds();
      defined->set_id(index);
      defined->set_key(std::to_string(index));
      auto* value = source->add_actor_values();
      value->set_kind(index);
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
  // Outgoing, the server applies its own limit and refuses the update.
  CHECK(codec.Encode(W::UpdatePlayer{1, sample}));
  auto          packet = Welcome();
  auto*         player = packet.mutable_session_opened()->mutable_players(0);
  std::uint32_t id     = 0;
  for (const auto* key : {"skyrim:health", "skyrim:stamina"})
  {
    auto* kind = packet.mutable_session_opened()->add_actor_value_kinds();
    kind->set_id(++id);
    kind->set_key(key);
    auto* value = player->add_actor_values();
    value->set_kind(id);
    value->set_scalar(0);
  }
  CHECK_FALSE(codec.Decode(Bytes(packet), packet.has_chat_published() ? W::Channel::Chat : W::Channel::Control));
  settings.maxActorValues = 65;
  CHECK(MakeCodec(settings).Decode(Bytes(packet)));
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
  auto* boundary = packet.mutable_presence_changed()->add_visibility();
  boundary->set_player_id(7);
  boundary->set_view_revision(1);
  CHECK_FALSE(codec.Decode(Bytes(packet)));
  packet.clear_request_id();
  auto moved = codec.Decode(Bytes(packet));
  REQUIRE(moved);
  const auto& cleared = std::get<PlayerLocationUpdated>(std::get<W::PresenceChanged>(*moved).updates.at(0));
  CHECK(cleared.playerId == 7);
  CHECK_FALSE(cleared.location);
  packet.mutable_presence_changed()->add_updated()->mutable_profile()->set_player_id(7);
  REQUIRE(codec.Decode(Bytes(packet), packet.has_chat_published() ? W::Channel::Chat : W::Channel::Control));
  packet.set_request_id(1);
  CHECK_FALSE(codec.Decode(Bytes(packet), packet.has_chat_published() ? W::Channel::Chat : W::Channel::Control));
}

TEST_CASE("Presence baselines take the place of their batch; a pose needs it and it needs a pose")
{
  const auto      codec = MakeCodec();
  P::ServerPacket packet;
  packet.set_protocol_version(W::Version);
  auto* presence = packet.mutable_presence_changed();
  auto* entry    = presence->add_visibility();
  entry->set_player_id(7);
  entry->set_view_revision(3);
  entry->set_sequence(9);
  entry->mutable_pose()->mutable_position()->set_x(10);
  entry->mutable_pose()->set_sampled_at_us(55);
  CHECK_FALSE(codec.Decode(Bytes(packet)));  // No space for the pose.
  presence->mutable_space()->mutable_location_id()->set_plugin_name("Skyrim.esm");
  presence->mutable_space()->mutable_location_id()->set_local_form_id(0x3C);
  presence->mutable_space()->set_location_name("Skyrim");
  auto result = codec.Decode(Bytes(packet));
  REQUIRE(result);
  const auto& baseline = std::get<PlayerLocationUpdated>(std::get<W::PresenceChanged>(*result).updates.at(0));
  REQUIRE(baseline.location);
  CHECK(baseline.location->location.locationId == Domain::FormKey{"Skyrim.esm", 0x3C});
  CHECK(baseline.location->location.locationName == "Skyrim");
  CHECK(baseline.location->position.X == 10);
  CHECK(baseline.location->sampledAtUs == 55);
  CHECK(baseline.viewRevision == 3);
  CHECK(baseline.sequence == 9);
  entry->clear_pose();
  CHECK_FALSE(codec.Decode(Bytes(packet)));  // A space without any pose.
  presence->Clear();
  CHECK_FALSE(codec.Decode(Bytes(packet)));  // Nothing changed.
}

TEST_CASE("Actor value kinds never take a number twice and forget kinds no player has")
{
  W::ActorValueKinds kinds;
  CHECK_FALSE(kinds.Define({0, "zero", ""}));
  REQUIRE(kinds.Define({1, "skyrim:health", "Health"}));
  REQUIRE(kinds.Define({2, "skyrim:health", "Здоровье"}));
  CHECK_FALSE(kinds.Define({2, "skyrim:magicka", "Magicka"}));
  Domain::Player player;
  player.actorValues.emplace(
    "skyrim:health",
    Domain::ActorValueInfo{
        "Здоровье",
        Domain::ResourceActorValue{1, 2}
  });
  kinds.Retain(std::span{&player, 1});
  CHECK(kinds.Size() == 1);
  CHECK_FALSE(kinds.Find(1));
  REQUIRE(kinds.Find(2));
  CHECK(kinds.Find(2)->displayName == "Здоровье");
}

TEST_CASE("Metadata patches resolve kinds the session knows or the same message defines")
{
  const auto         codec = MakeCodec();
  W::ActorValueKinds known;
  REQUIRE(known.Define({1, "skyrim:health", "Health"}));
  P::ServerPacket packet;
  packet.set_protocol_version(W::Version);
  auto* presence = packet.mutable_presence_changed();
  auto* patch    = presence->add_metadata();
  patch->set_player_id(7);
  CHECK_FALSE(codec.Decode(Bytes(packet), W::Channel::Control, known));  // Nothing changed.

  // The old label goes and the new one comes: a kind defined in this message.
  patch->add_removed_actor_values(1);
  auto* kind = presence->add_actor_value_kinds();
  kind->set_id(2);
  kind->set_key("skyrim:health");
  kind->set_display_name("Здоровье");
  auto* value = patch->add_actor_values();
  value->set_kind(2);
  value->mutable_resource()->set_current(-3);
  value->mutable_resource()->set_maximum(250);
  auto result = codec.Decode(Bytes(packet), W::Channel::Control, known);
  REQUIRE(result);
  const auto& changed = std::get<W::PresenceChanged>(*result);
  REQUIRE(changed.kinds.size() == 1);
  CHECK(changed.kinds[0].displayName == "Здоровье");
  const auto& values = std::get<PlayerMetadataUpdated>(changed.updates.at(0));
  REQUIRE(values.actorValues);
  CHECK(values.actorValues->removed == std::vector<Domain::ActorValueKey>{"skyrim:health"});
  REQUIRE(values.actorValues->set.size() == 1);
  CHECK(values.actorValues->set[0].second.displayName == "Здоровье");
  CHECK(
    values.actorValues->set[0].second.state == Domain::ActorValueState{
                                                   Domain::ResourceActorValue{-3, 250}
  });
  CHECK_FALSE(values.details);
  CHECK_FALSE(codec.Decode(Bytes(packet)));                              // Kind 1 is unknown to an empty table.
  kind->set_id(1);
  CHECK_FALSE(codec.Decode(Bytes(packet), W::Channel::Control, known));  // Numbers are never redefined.
  kind->set_id(2);

  // Details: present components replace, listed ones clear.
  patch->clear_removed_actor_values();
  patch->clear_actor_values();
  presence->clear_actor_value_kinds();
  patch->mutable_details()->set_level(0);
  patch->mutable_details()->mutable_activity()->set_kind(P::ACTIVITY_KIND_SNEAKING);
  patch->add_cleared_details(P::PLAYER_DETAILS_FIELD_PLACE);
  result = codec.Decode(Bytes(packet), W::Channel::Control, known);
  REQUIRE(result);
  const auto& details = std::get<PlayerMetadataUpdated>(std::get<W::PresenceChanged>(*result).updates.at(0));
  CHECK_FALSE(details.actorValues);
  REQUIRE(details.details);
  CHECK(details.details->level == std::optional<std::optional<std::uint32_t>>{0});
  CHECK(details.details->place == std::optional<std::optional<Domain::PlaceDescription>>{std::optional<Domain::PlaceDescription>{}});
  CHECK(details.details->activity->kind == Domain::ActivityKind::Sneaking);
  CHECK_FALSE(details.details->race);
  patch->add_cleared_details(P::PLAYER_DETAILS_FIELD_LEVEL);
  CHECK_FALSE(codec.Decode(Bytes(packet), W::Channel::Control, known));  // Replaced and cleared at once.
  patch->clear_cleared_details();
  patch->add_cleared_details(P::PLAYER_DETAILS_FIELD_UNSPECIFIED);
  CHECK_FALSE(codec.Decode(Bytes(packet), W::Channel::Control, known));
  patch->clear_cleared_details();
  packet.set_request_id(1);
  CHECK_FALSE(codec.Decode(Bytes(packet), W::Channel::Control, known));
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
  // The server skips a sample it cannot use.
  CHECK(codec.Encode(W::MovementSample{0, 1, {}}, 1200));
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

  // A server-wide kind appears once; a repeated one would also unbound the welcome.
  auto* repeated = welcome.mutable_session_opened()->add_channels();
  repeated->set_channel_id(3);
  repeated->set_kind(P::CHAT_CHANNEL_KIND_GLOBAL);
  CHECK_FALSE(codec.Decode(Bytes(welcome), W::Channel::Control));

  const W::ClientRequest request =
    PostAnnouncement{5, 2, "Пал", Domain::AnnouncementKind::Event, Domain::ClientAnnouncementSource::ThirdParty, "Мод"};
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
}

TEST_CASE("Pseudonymous profiles carry no username and the identity switch round-trips")
{
  const auto codec  = MakeCodec();
  auto       packet = Published();
  auto*      author = packet.mutable_chat_published()->mutable_message()->mutable_author();
  author->clear_username();
  author->set_display_name("Страж 2");
  author->set_pseudonymous(true);
  const auto decoded = codec.Decode(Bytes(packet), W::Channel::Chat);
  REQUIRE(decoded);
  const auto& message = std::get<ChatMessagesReceived>(*decoded).messages.front();
  REQUIRE(message.author);
  CHECK(message.author->pseudonymous);
  CHECK(message.author->username.empty());
  CHECK(message.author->displayName == "Страж 2");

  author->set_username("leak");
  CHECK_FALSE(codec.Decode(Bytes(packet), W::Channel::Chat));
  author->clear_username();
  author->clear_display_name();
  CHECK_FALSE(codec.Decode(Bytes(packet), W::Channel::Chat));

  const auto hidden = codec.Encode(W::OpenSession{3, std::string(43, 'A'), Domain::HiddenIdentity::Everywhere});
  REQUIRE(hidden);
  P::ClientPacket opening;
  REQUIRE(opening.ParseFromArray(hidden->DataBytesView().data(), static_cast<int>(hidden->Size())));
  CHECK(opening.open_session().hidden_identity() == P::HIDDEN_IDENTITY_EVERYWHERE);
  const auto request = W::ClientRequest{SetIdentityVisibility{4, Domain::HiddenIdentity::ExceptGroundMarks}};
  const auto encoded = codec.Encode(request);
  REQUIRE(encoded);
  P::ClientPacket sent;
  REQUIRE(sent.ParseFromArray(encoded->DataBytesView().data(), static_cast<int>(encoded->Size())));
  CHECK(sent.request_id() == 4);
  CHECK(sent.set_identity_visibility().hidden() == P::HIDDEN_IDENTITY_EXCEPT_GROUND_MARKS);

  P::ServerPacket changed;
  changed.set_protocol_version(W::Version);
  changed.set_request_id(4);
  changed.mutable_identity_visibility_changed()->set_pseudonym("Страж");
  changed.mutable_identity_visibility_changed()->set_hidden(P::HIDDEN_IDENTITY_EXCEPT_GROUND_MARKS);
  const auto settled = codec.Decode(Bytes(changed));
  REQUIRE(settled);
  const auto& value = std::get<W::IdentityVisibilityChanged>(*settled);
  CHECK(value.requestId == 4);
  CHECK(value.pseudonym == std::optional<std::string>{"Страж"});
  CHECK(value.hiding == Domain::HiddenIdentity::ExceptGroundMarks);
  // A pseudonym exactly when the names are hidden somewhere.
  changed.mutable_identity_visibility_changed()->clear_pseudonym();
  CHECK_FALSE(codec.Decode(Bytes(changed)));
  changed.mutable_identity_visibility_changed()->set_hidden(P::HIDDEN_IDENTITY_NONE);
  CHECK_FALSE(std::get<W::IdentityVisibilityChanged>(*codec.Decode(Bytes(changed))).pseudonym);
  changed.mutable_identity_visibility_changed()->set_hidden(static_cast<P::HiddenIdentity>(7));
  CHECK_FALSE(codec.Decode(Bytes(changed)));
  changed.mutable_identity_visibility_changed()->set_hidden(P::HIDDEN_IDENTITY_EVERYWHERE);
  changed.mutable_identity_visibility_changed()->set_pseudonym("");
  CHECK_FALSE(codec.Decode(Bytes(changed)));
  changed.clear_request_id();
  changed.mutable_identity_visibility_changed()->set_pseudonym("Страж");
  CHECK_FALSE(codec.Decode(Bytes(changed)));

  auto welcome = Welcome();
  welcome.mutable_session_opened()->set_own_pseudonym("Страж");
  CHECK_FALSE(codec.Decode(Bytes(welcome)));
  welcome.mutable_session_opened()->set_hidden_identity(P::HIDDEN_IDENTITY_EVERYWHERE);
  const auto opened = codec.Decode(Bytes(welcome));
  REQUIRE(opened);
  CHECK(std::get<W::SessionOpened>(*opened).ownPseudonym == std::optional<std::string>{"Страж"});
  CHECK(std::get<W::SessionOpened>(*opened).hiding == Domain::HiddenIdentity::Everywhere);
}

TEST_CASE("A display name change and its answer round-trip with the correlation")
{
  const auto codec   = MakeCodec();
  const auto request = W::ClientRequest{
      ChangeDisplayName{5, "Новое Имя"}
  };
  const auto encoded = codec.Encode(request);
  REQUIRE(encoded);
  P::ClientPacket sent;
  REQUIRE(sent.ParseFromArray(encoded->DataBytesView().data(), static_cast<int>(encoded->Size())));
  CHECK(sent.request_id() == 5);
  CHECK(sent.change_display_name().display_name() == "Новое Имя");
  CHECK_FALSE(codec.Encode(
    W::ClientRequest{
        ChangeDisplayName{6, "\xC3"}
  }));

  P::ServerPacket changed;
  changed.set_protocol_version(W::Version);
  changed.set_request_id(5);
  changed.mutable_display_name_changed()->set_display_name("Новое Имя");
  const auto settled = codec.Decode(Bytes(changed));
  REQUIRE(settled);
  const auto& value = std::get<W::DisplayNameChanged>(*settled);
  CHECK(value.requestId == 5);
  CHECK(value.displayName == "Новое Имя");
  CHECK_FALSE(codec.Decode(Bytes(changed), W::Channel::Chat));
  changed.mutable_display_name_changed()->set_display_name("");
  CHECK_FALSE(codec.Decode(Bytes(changed)));
  changed.mutable_display_name_changed()->set_display_name("Новое Имя");
  changed.clear_request_id();
  CHECK_FALSE(codec.Decode(Bytes(changed)));
}

TEST_CASE("Moderator requests encode as asked and their answers decode with correlation, lane and role")
{
  const auto codec = MakeCodec();
  const auto sent  = [&](W::ClientRequest request) {
    const auto encoded = codec.Encode(request);
    REQUIRE(encoded);
    P::ClientPacket packet;
    REQUIRE(packet.ParseFromArray(encoded->DataBytesView().data(), static_cast<int>(encoded->Size())));
    return packet;
  };
  const auto mute = sent(SanctionPlayer{5, 9, Domain::SanctionKind::Mute, 15, "Флуд"}).sanction_player();
  CHECK(mute.player_id() == 9);
  CHECK(mute.kind() == P::SANCTION_KIND_MUTE);
  CHECK(mute.minutes() == 15);
  CHECK(mute.reason() == "Флуд");
  CHECK_FALSE(sent(SanctionPlayer{6, 9, Domain::SanctionKind::Ban, std::nullopt, "Читы"}).sanction_player().has_minutes());
  const auto clear = sent(ClearPlayerMarks{7, 9, false, true}).clear_player_marks();
  CHECK((clear.player_id() == 9 && !clear.notes() && clear.deaths()));
  CHECK(sent(DeleteChatMessage{8, 1, 3}).delete_chat_message().message_id() == 3);
  CHECK(sent(ListSanctions{9}).has_list_sanctions());

  P::ServerPacket issued;
  issued.set_protocol_version(W::Version);
  issued.set_request_id(5);
  auto* entry = issued.mutable_sanction_issued()->mutable_sanction();
  entry->set_player_id(9);
  entry->set_kind(P::SANCTION_KIND_MUTE);
  entry->set_reason("Флуд");
  entry->set_issued_at_unix_ms(1000);
  entry->set_until_unix_ms(901000);
  const auto decoded = codec.Decode(Bytes(issued));
  REQUIRE(decoded);
  CHECK(std::get<W::SanctionIssued>(*decoded).sanction == Domain::Sanction{9, Domain::SanctionKind::Mute, "Флуд", 1000, 901000});
  entry->set_kind(P::SANCTION_KIND_UNSPECIFIED);
  CHECK_FALSE(codec.Decode(Bytes(issued)));
  entry->set_kind(P::SANCTION_KIND_BAN);
  issued.clear_request_id();
  CHECK_FALSE(codec.Decode(Bytes(issued)));

  // A removal travels on the chat lane; only the moderator's copy is correlated.
  P::ServerPacket removed;
  removed.set_protocol_version(W::Version);
  removed.mutable_chat_message_removed()->set_channel_id(1);
  removed.mutable_chat_message_removed()->set_message_id(3);
  const auto notice = codec.Decode(Bytes(removed), W::Channel::Chat);
  REQUIRE(notice);
  CHECK_FALSE(std::get<W::ChatMessageRemoved>(*notice).requestId);
  CHECK_FALSE(codec.Decode(Bytes(removed), W::Channel::Control));
  removed.set_request_id(8);
  const auto own = codec.Decode(Bytes(removed), W::Channel::Chat);
  REQUIRE(own);
  CHECK(std::get<W::ChatMessageRemoved>(*own).requestId == 8);

  P::ServerPacket role;
  role.set_protocol_version(W::Version);
  role.mutable_role_changed()->set_role(P::PLAYER_ROLE_MODERATOR);
  const auto changed = codec.Decode(Bytes(role));
  REQUIRE(changed);
  CHECK(std::get<W::RoleChanged>(*changed).role == Domain::PlayerRole::Moderator);
  role.mutable_role_changed()->set_role(static_cast<P::PlayerRole>(7));
  CHECK_FALSE(codec.Decode(Bytes(role)));

  auto welcome = Welcome();
  welcome.mutable_session_opened()->set_role(P::PLAYER_ROLE_MODERATOR);
  const auto opened = codec.Decode(Bytes(welcome));
  REQUIRE(opened);
  CHECK(std::get<W::SessionOpened>(*opened).role == Domain::PlayerRole::Moderator);
}

TEST_CASE("The codec refuses only what would close the connection: a zero request ID or text that is not UTF-8")
{
  const auto                        codec = MakeCodec();
  const Domain::GroundMarkPlacement place{
      {"skyrim.esm", 0x1A26F},
      {1, 2, 3},
      0.5f
  };
  const Domain::GameDate date{4, 201, 8, 17, 2, 14, 5};
  using Kind   = Domain::AnnouncementKind;
  using Source = Domain::ClientAnnouncementSource;
  const std::string broken{"\xFF"};

  // Every string field is checked, nested and map ones included, by its protobuf name.
  Domain::PlayerDetails details;
  details.place = Domain::PlaceDescription{"Tamriel", broken, "", "", false};
  Domain::ActorValueStorage badKey;
  badKey.emplace(broken, Domain::ActorValueInfo{"Health", Domain::ScalarActorValue{1}});
  Domain::ActorValueStorage badName;
  badName.emplace("skyrim:health", Domain::ActorValueInfo{broken, Domain::ScalarActorValue{1}});
  const std::vector<std::pair<std::string_view, W::ClientRequest>> cases{
      {"text",          SendChat{1, 1, broken}                                                   },
      {"signature",     PostAnnouncement{1, 2, "notice", Kind::Event, Source::ThirdParty, broken}},
      {"text",          PlaceGroundNote{1, broken, place, date}                                  },
      {"plugin_name",   PlaceGroundNote{1, "note", {{broken, 0x1A26F}, {1, 2, 3}, 0.5f}, date}   },
      {"label",         ReportDeath{1, "Wolf" + broken, place, date}                             },
      {"display_name",  ChangeDisplayName{1, broken}                                             },
      {"name",          W::UpdatePlayer{1, CharacterStarted{broken}}                             },
      {"location_name", W::UpdatePlayer{1, PlayerDetailsChanged{details}}                        },
      {"key",           W::UpdatePlayer{1, LocalActorValues{badKey}}                             },
      {"display_name",  W::UpdatePlayer{1, LocalActorValues{badName}}                            },
  };
  for (const auto& [field, request] : cases)
  {
    const auto encoded = codec.Encode(request);
    REQUIRE_FALSE(encoded);
    CHECK(encoded.error().code == W::ErrorCode::InvalidPayload);
    CHECK(encoded.error().field == field);
  }
  const auto unnumbered = codec.Encode(ChangeDisplayName{Domain::InvalidId, "Name"});
  REQUIRE_FALSE(unnumbered);
  CHECK(unnumbered.error().code == W::ErrorCode::InvalidEnvelope);

  // The content is the server's to judge; it answers with a refusal of the request.
  const std::vector<W::ClientRequest> judgedByServer{
      W::OpenSession{1, ""},
      SendChat{1, Domain::InvalidId, ""},
      PostAnnouncement{1, 2, "", Kind::Event, Source::ThirdParty, ""},
      PlaceGroundNote{1, "", {{"", 0}, {std::numeric_limits<float>::quiet_NaN(), 0, 0}, 0}, {4, 201, 2, 30, 7, 24, 0}},
      ReportDeath{1, "Wolf\n", place, date},
      RemoveGroundMark{1, Domain::InvalidId},
      ChangeDisplayName{1, " \t "},
  };
  for (const auto& request : judgedByServer)
    CHECK(codec.Encode(request));
}

TEST_SUITE_END();
