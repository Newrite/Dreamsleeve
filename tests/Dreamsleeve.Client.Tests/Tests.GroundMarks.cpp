#include <doctest/doctest.h>
#include "protocol.pb.h"
import std;
import Dreamsleeve.Client.ProtocolCodec;
import Dreamsleeve.Client.StateUpdate;

#include "Results.h"

namespace
{

  using namespace Dreamsleeve::Client;
  namespace W = Dreamsleeve::Client::Wire;
  namespace P = Dreamsleeve::Protocol::Chat;

  const Domain::FormKey Whiterun{"skyrim.esm", 0x1A26F};
  const Domain::FormKey Riften{"skyrim.esm", 0x16BB4};

  Domain::GroundMarkPlacement Placement(float x, Domain::FormKey space = Whiterun)
  {
    return {space, {x, 0, 0}, 1.5f};
  }

  // Tirdas, 17 Last Seed 4E 201, 14:05.
  constexpr Domain::GameDate Date{4, 201, 8, 17, 2, 14, 5};

  void WriteDate(P::GameDate& target, const Domain::GameDate& date = Date)
  {
    target.set_era(date.era);
    target.set_year(date.year);
    target.set_month(date.month);
    target.set_day(date.day);
    target.set_day_of_week(date.dayOfWeek);
    target.set_hour(date.hour);
    target.set_minute(date.minute);
  }

  Domain::GroundMark Mark(Domain::GroundMarkId id, float x = 0, Domain::GroundMarkKind kind = Domain::GroundMarkKind::Note)
  {
    return Domain::GroundMark{
        id,
        Domain::PlayerData{7, "seven", "Seven"},
        kind,
        kind == Domain::GroundMarkKind::Note ? "note " + std::to_string(id) : std::string{},
        {},
        Placement(x),
        Domain::FromUnixMilliseconds(1700000000000)
    };
  }

  Domain::PlayerLocation Observer(float x, Domain::FormKey space = Whiterun)
  {
    return Domain::PlayerLocation{.location = {space, "Whiterun"}, .position = {x, 0, 0}};
  }

  std::vector<Domain::GroundMarkId> Ids(const std::vector<Domain::GroundMark>& marks)
  {
    std::vector<Domain::GroundMarkId> ids;
    for (const auto& mark : marks)
      ids.push_back(mark.markId);
    return ids;
  }

  template <class T>
  std::vector<std::byte> Bytes(const T& packet)
  {
    std::vector<std::byte> bytes(packet.ByteSizeLong());
    REQUIRE(packet.SerializeToArray(bytes.data(), static_cast<int>(bytes.size())));
    return bytes;
  }

  W::ProtocolCodec Codec()
  {
    auto result = W::ProtocolCodec::TryCreate(Configuration{});
    REQUIRE(result);
    return std::move(*result);
  }

  void WriteMark(P::GroundMark& target, Domain::GroundMarkId id, P::GroundMarkKind kind = P::GROUND_MARK_KIND_NOTE)
  {
    target.set_mark_id(id);
    target.mutable_author()->set_player_id(7);
    target.mutable_author()->set_username("seven");
    target.mutable_author()->set_display_name("Seven");
    target.set_kind(kind);
    target.set_text(kind == P::GROUND_MARK_KIND_NOTE ? "praise the sun" : "");
    target.mutable_placement()->mutable_location_id()->set_plugin_name("skyrim.esm");
    target.mutable_placement()->mutable_location_id()->set_local_form_id(0x1A26F);
    target.mutable_placement()->mutable_position()->set_x(1);
    target.mutable_placement()->set_heading(1.5f);
    target.set_created_at_unix_ms(1700000000000);
    if (kind == P::GROUND_MARK_KIND_NOTE) target.set_character_name("Nerevar");
    if (kind == P::GROUND_MARK_KIND_NOTE) WriteDate(*target.mutable_game_date());
  }

  P::ServerPacket Changed(std::uint64_t revision, std::vector<std::uint64_t> added, std::vector<std::uint64_t> removed = {}, bool clear = false)
  {
    P::ServerPacket packet;
    packet.set_protocol_version(W::Version);
    auto* changed = packet.mutable_ground_marks_changed();
    changed->set_view_revision(revision);
    changed->set_clear(clear);
    for (const auto id : added)
      WriteMark(*changed->add_added(), id);
    for (const auto id : removed)
      changed->add_removed_ids(id);
    return packet;
  }

}

TEST_SUITE_BEGIN("Client.GroundMarks");

TEST_CASE("Mark visibility needs the observer's space and radius, boundary included")
{
  CHECK(Domain::Spatial::IsMarkWithinRadius(100.0f, Observer(0), Placement(100)) == Domain::Result<bool>{true});
  CHECK(Domain::Spatial::IsMarkWithinRadius(100.0f, Observer(0), Placement(100.001f)) == Domain::Result<bool>{false});
  CHECK(Domain::Spatial::IsMarkWithinRadius(100.0f, Observer(0, Riften), Placement(0)) == Domain::Result<bool>{false});
  CHECK_FALSE(Domain::Spatial::IsMarkWithinRadius(-1.0f, Observer(0), Placement(0)));
}

TEST_CASE("The visible store applies baselines and deltas in order and refuses stale revisions")
{
  GroundMarkStore store;
  CHECK(store.ViewRevision() == 0);
  auto baseline = store.Apply(GroundMarksChanged{1, {Mark(1), Mark(2)}, {}, true});
  REQUIRE(baseline);
  REQUIRE(baseline->size() == 2);
  CHECK(std::holds_alternative<GroundMarksCleared>((*baseline)[0]));
  CHECK(Ids(std::get<GroundMarksAdded>((*baseline)[1]).marks) == std::vector<Domain::GroundMarkId>{1, 2});
  CHECK(store.Count() == 2);
  CHECK(store.Find(2));

  auto delta = store.Apply(GroundMarksChanged{2, {Mark(3)}, {1, 99}, false});
  REQUIRE(delta);
  REQUIRE(delta->size() == 2);
  CHECK(std::get<GroundMarksRemoved>((*delta)[0]).markIds == std::vector<Domain::GroundMarkId>{1});
  CHECK(Ids(std::get<GroundMarksAdded>((*delta)[1]).marks) == std::vector<Domain::GroundMarkId>{3});
  CHECK(Ids(store.Snapshot().marks) == std::vector<Domain::GroundMarkId>{2, 3});
  CHECK(store.Snapshot().viewRevision == 2);

  // Older or repeated revisions are protocol faults, not duplicates to skip.
  auto stale = store.Apply(GroundMarksChanged{2, {Mark(4)}, {}, false});
  REQUIRE_FALSE(stale);
  CHECK(stale.error().code == Domain::ErrorCode::InvalidCursor);
  CHECK(store.Count() == 2);
  CHECK_FALSE(store.Apply(GroundMarksChanged{0, {}, {}, true}));

  auto cleared = store.Apply(GroundMarksChanged{5, {}, {}, true});
  REQUIRE(cleared);
  REQUIRE(cleared->size() == 1);
  CHECK(std::holds_alternative<GroundMarksCleared>(cleared->front()));
  CHECK(store.Count() == 0);
  store.Clear();
  CHECK(store.ViewRevision() == 0);
}

TEST_CASE("The model forwards ordered mark transitions, snapshots include marks and a session reset drops them")
{
  ClientModel model;
  ChangeBatch scratch;
  REQUIRE(model.Apply(model.Generation(), GroundMarksChanged{1, {Mark(1), Mark(2)}, {}, true}));
  REQUIRE(model.Apply(model.Generation(), GroundMarksChanged{2, {Mark(3)}, {2}, false}));
  auto update = TakeStateUpdate(model, scratch);
  REQUIRE(update);
  REQUIRE(std::holds_alternative<ClientStateDelta>(*update));
  const auto& delta = std::get<ClientStateDelta>(*update);
  CHECK(delta.players.empty());
  CHECK(delta.chatContent.empty());
  REQUIRE(delta.groundMarks.size() == 4);
  CHECK(std::holds_alternative<GroundMarksCleared>(delta.groundMarks[0]));
  CHECK(Ids(std::get<GroundMarksAdded>(delta.groundMarks[1]).marks) == std::vector<Domain::GroundMarkId>{1, 2});
  CHECK(std::get<GroundMarksRemoved>(delta.groundMarks[2]).markIds == std::vector<Domain::GroundMarkId>{2});
  CHECK(Ids(std::get<GroundMarksAdded>(delta.groundMarks[3]).marks) == std::vector<Domain::GroundMarkId>{3});
  CHECK_FALSE(TakeStateUpdate(model, scratch));

  // A new baseline supersedes changes not yet taken.
  REQUIRE(model.Apply(model.Generation(), GroundMarksChanged{3, {Mark(4)}, {}, false}));
  REQUIRE(model.Apply(model.Generation(), GroundMarksChanged{4, {Mark(5)}, {}, true}));
  auto superseded = TakeStateUpdate(model, scratch);
  REQUIRE(superseded);
  const auto& replaced = std::get<ClientStateDelta>(*superseded).groundMarks;
  REQUIRE(replaced.size() == 2);
  CHECK(std::holds_alternative<GroundMarksCleared>(replaced[0]));
  CHECK(Ids(std::get<GroundMarksAdded>(replaced[1]).marks) == std::vector<Domain::GroundMarkId>{5});

  const auto snapshot = model.Snapshot();
  CHECK(Ids(snapshot.groundMarks.marks) == std::vector<Domain::GroundMarkId>{5});
  CHECK(snapshot.groundMarks.viewRevision == 4);
  CHECK(model.FindGroundMark(5));

  auto stale = model.Apply(model.Generation(), GroundMarksChanged{4, {Mark(6)}, {}, false});
  REQUIRE_FALSE(stale);
  CHECK(stale.error().code == Domain::ErrorCode::InvalidCursor);

  model.ResetSession();
  CHECK(model.Snapshot().groundMarks.marks.empty());
  CHECK(model.Snapshot().groundMarks.viewRevision == 0);
  REQUIRE(model.Apply(model.Generation(), GroundMarksChanged{1, {Mark(7)}, {}, true}));
}

TEST_CASE("Mark commands queue like chat and confirmations share the bounded result budget")
{
  auto created = ClientExchange::TryCreate(2, 4);
  REQUIRE(created);
  auto& exchange = **created;
  ClientModel model;
  REQUIRE(exchange.Publish(model));
  ClientOutput output;
  exchange.Drain(output);

  const auto first = *exchange.NextRequestId();
  CHECK(exchange.Post({1, PlaceGroundNote{first, "hello", Placement(0), Date}}) == CommandPostResult::Queued);
  CHECK(exchange.Post({1, ReportDeath{*exchange.NextRequestId(), "", Placement(0), Date}}) == CommandPostResult::Queued);
  CHECK(exchange.Post({1, RemoveGroundMark{*exchange.NextRequestId(), 5}}) == CommandPostResult::Full);
  std::vector<QueuedClientCommand> commands;
  exchange.TakeCommands(commands);
  REQUIRE(commands.size() == 2);
  CHECK(std::get<PlaceGroundNote>(commands[0].command).requestId == first);
  CHECK(std::holds_alternative<ReportDeath>(commands[1].command));

  REQUIRE(exchange.Publish(model, false, SessionPhase::Ready, "Tamriel", CommandResult{1, first, MarkPlaced{9, 3}}));
  REQUIRE(exchange.Publish(model, false, SessionPhase::Ready, "Tamriel", CommandResult{1, first + 1, MarkRemoved{10}}));
  // Two results fill the budget of two; the third waits for a drain.
  CHECK_FALSE(exchange.PublishResult({1, first + 2, CommandFailureCode::Busy}));
  exchange.Drain(output);
  REQUIRE(output.results.size() == 2);
  REQUIRE(ResultsOf<MarkPlaced>(output).size() == 1);
  CHECK(ResultsOf<MarkPlaced>(output)[0].value.markId == 9);
  CHECK(ResultsOf<MarkPlaced>(output)[0].value.evictedId == 3);
  CHECK(ResultsOf<MarkRemoved>(output)[0].requestId == first + 1);
  CHECK(ResultsOf<MessagePublished>(output).empty());
  exchange.Drain(output);
  CHECK(output.results.empty());
}

TEST_CASE("Mark requests encode on the control lane and refuse an empty note, a zero id or a bad placement")
{
  const auto codec = Codec();
  auto       note  = codec.Encode(PlaceGroundNote{5, "praise\nthe sun", Placement(42), Date});
  REQUIRE(note);
  CHECK(note->Flags() == PacketFlag::Reliable);
  P::ClientPacket packet;
  REQUIRE(packet.ParseFromArray(note->DataBytesView().data(), static_cast<int>(note->Size())));
  CHECK(packet.request_id() == 5);
  CHECK(packet.place_ground_note().text() == "praise\nthe sun");
  CHECK(packet.place_ground_note().placement().location_id().plugin_name() == "skyrim.esm");
  CHECK(packet.place_ground_note().placement().location_id().local_form_id() == 0x1A26F);
  CHECK(packet.place_ground_note().placement().position().x() == 42);
  CHECK(packet.place_ground_note().placement().heading() == 1.5f);
  CHECK(packet.place_ground_note().game_date().era() == 4);
  CHECK(packet.place_ground_note().game_date().year() == 201);
  CHECK(packet.place_ground_note().game_date().month() == 8);
  CHECK(packet.place_ground_note().game_date().day() == 17);
  CHECK(packet.place_ground_note().game_date().day_of_week() == 2);
  CHECK(packet.place_ground_note().game_date().hour() == 14);
  CHECK(packet.place_ground_note().game_date().minute() == 5);
  CHECK(W::ProtocolCodec::RequestChannel(PlaceGroundNote{5, "x", Placement(0)}) == W::Channel::Control);

  auto death = codec.Encode(ReportDeath{6, "", Placement(0), Date});
  REQUIRE(death);
  REQUIRE(packet.ParseFromArray(death->DataBytesView().data(), static_cast<int>(death->Size())));
  CHECK(packet.has_report_death());
  CHECK(packet.report_death().label().empty());
  CHECK(packet.report_death().game_date().day() == 17);

  auto removal = codec.Encode(RemoveGroundMark{7, 9});
  REQUIRE(removal);
  REQUIRE(packet.ParseFromArray(removal->DataBytesView().data(), static_cast<int>(removal->Size())));
  CHECK(packet.remove_ground_mark().mark_id() == 9);

  CHECK_FALSE(codec.Encode(PlaceGroundNote{8, "", Placement(0), Date}));
  CHECK_FALSE(codec.Encode(PlaceGroundNote{8, "x", Placement(std::numeric_limits<float>::quiet_NaN()), Date}));
  CHECK_FALSE(codec.Encode(ReportDeath{8, "x", {{"", 0}, {}, 0}, Date}));
  // The game date is required and must be a calendar date: no leap day, no hour 24.
  CHECK_FALSE(codec.Encode(PlaceGroundNote{8, "x", Placement(0)}));
  CHECK_FALSE(codec.Encode(ReportDeath{8, "x", Placement(0), {4, 201, 2, 29, 0, 0, 0}}));
  CHECK_FALSE(codec.Encode(ReportDeath{8, "x", Placement(0), {4, 201, 8, 17, 7, 0, 0}}));
  CHECK_FALSE(codec.Encode(ReportDeath{8, "x", Placement(0), {4, 201, 8, 17, 2, 24, 0}}));
  CHECK_FALSE(codec.Encode(ReportDeath{8, "x", Placement(0), {0, 201, 8, 17, 2, 0, 0}}));
  CHECK(codec.Encode(ReportDeath{8, "x", Placement(0), {99, 99999, 12, 31, 6, 23, 59}}));
  CHECK_FALSE(codec.Encode(RemoveGroundMark{8, 0}));
  CHECK_FALSE(codec.Encode(RemoveGroundMark{0, 9}));
}

TEST_CASE("Mark responses decode with their correlation rules and validate the mark shape")
{
  const auto codec   = Codec();
  auto       decoded = codec.Decode(Bytes(Changed(3, {1, 2}, {5}, true)));
  REQUIRE(decoded);
  const auto& changed = std::get<GroundMarksChanged>(*decoded);
  CHECK(changed.viewRevision == 3);
  CHECK(changed.clear);
  CHECK(Ids(changed.added) == std::vector<Domain::GroundMarkId>{1, 2});
  CHECK(changed.removedIds == std::vector<Domain::GroundMarkId>{5});
  CHECK(changed.added[0].author.displayName == "Seven");
  CHECK(changed.added[0].kind == Domain::GroundMarkKind::Note);
  CHECK(changed.added[0].placement == Placement(1));
  CHECK(changed.added[0].createdAt == Domain::FromUnixMilliseconds(1700000000000));
  CHECK(changed.added[0].characterName == "Nerevar");
  CHECK(changed.added[0].gameDate == Date);

  auto withId = Changed(3, {1});
  withId.set_request_id(4);
  CHECK_FALSE(codec.Decode(Bytes(withId)));
  CHECK_FALSE(codec.Decode(Bytes(Changed(0, {1}))));
  CHECK_FALSE(codec.Decode(Bytes(Changed(3, {}, {}, false))));
  CHECK_FALSE(codec.Decode(Bytes(Changed(3, {}, {0}, false))));
  CHECK_FALSE(codec.Decode(Bytes(Changed(3, {1}, {}, false)), W::Channel::Chat));

  auto empty = Changed(3, {1});
  empty.mutable_ground_marks_changed()->mutable_added(0)->set_text("");
  CHECK_FALSE(codec.Decode(Bytes(empty)));
  auto unknownKind = Changed(3, {1});
  unknownKind.mutable_ground_marks_changed()->mutable_added(0)->set_kind(P::GROUND_MARK_KIND_UNSPECIFIED);
  CHECK_FALSE(codec.Decode(Bytes(unknownKind)));
  auto badSpan = Changed(3, {1});
  badSpan.mutable_ground_marks_changed()->mutable_added(0)->add_flagged()->set_length(0);
  CHECK_FALSE(codec.Decode(Bytes(badSpan)));
  auto noAuthor = Changed(3, {1});
  noAuthor.mutable_ground_marks_changed()->mutable_added(0)->clear_author();
  CHECK_FALSE(codec.Decode(Bytes(noAuthor)));
  auto emptyDeath = Changed(3, {});
  WriteMark(*emptyDeath.mutable_ground_marks_changed()->add_added(), 8, P::GROUND_MARK_KIND_DEATH);
  auto death = codec.Decode(Bytes(emptyDeath));
  REQUIRE(death);
  CHECK(std::get<GroundMarksChanged>(*death).added[0].kind == Domain::GroundMarkKind::Death);
  CHECK(std::get<GroundMarksChanged>(*death).added[0].text.empty());
  CHECK_FALSE(std::get<GroundMarksChanged>(*death).added[0].characterName);
  // A mark stored before protocol 12 has no date; a present date must be valid.
  CHECK_FALSE(std::get<GroundMarksChanged>(*death).added[0].gameDate);
  auto badDate = Changed(3, {1});
  badDate.mutable_ground_marks_changed()->mutable_added(0)->mutable_game_date()->set_month(13);
  CHECK_FALSE(codec.Decode(Bytes(badDate)));

  P::ServerPacket placed;
  placed.set_protocol_version(W::Version);
  placed.set_request_id(11);
  WriteMark(*placed.mutable_ground_mark_placed()->mutable_mark(), 4);
  placed.mutable_ground_mark_placed()->set_evicted_id(1);
  auto placement = codec.Decode(Bytes(placed));
  REQUIRE(placement);
  CHECK(std::get<W::GroundMarkPlaced>(*placement).requestId == 11);
  CHECK(std::get<W::GroundMarkPlaced>(*placement).mark.markId == 4);
  CHECK(std::get<W::GroundMarkPlaced>(*placement).evictedId == 1);
  placed.mutable_ground_mark_placed()->set_evicted_id(0);
  CHECK_FALSE(std::get<W::GroundMarkPlaced>(*codec.Decode(Bytes(placed))).evictedId);
  placed.clear_request_id();
  CHECK_FALSE(codec.Decode(Bytes(placed)));

  P::ServerPacket removed;
  removed.set_protocol_version(W::Version);
  removed.set_request_id(12);
  removed.mutable_ground_mark_removed()->set_mark_id(4);
  auto removal = codec.Decode(Bytes(removed));
  REQUIRE(removal);
  CHECK(std::get<W::GroundMarkRemoved>(*removal).markId == 4);
  removed.mutable_ground_mark_removed()->set_mark_id(0);
  CHECK_FALSE(codec.Decode(Bytes(removed)));

  // The own list: no correlation, one author, unique ids; empty is a valid replacement.
  P::ServerPacket own;
  own.set_protocol_version(W::Version);
  WriteMark(*own.mutable_own_ground_marks()->add_marks(), 21);
  WriteMark(*own.mutable_own_ground_marks()->add_marks(), 22, P::GROUND_MARK_KIND_DEATH);
  auto listed = codec.Decode(Bytes(own));
  REQUIRE(listed);
  CHECK(Ids(std::get<OwnGroundMarksReplaced>(*listed).marks) == std::vector<Domain::GroundMarkId>{21, 22});
  own.set_request_id(3);
  CHECK_FALSE(codec.Decode(Bytes(own)));
  own.clear_request_id();
  own.mutable_own_ground_marks()->mutable_marks(1)->set_mark_id(21);
  CHECK_FALSE(codec.Decode(Bytes(own)));
  own.mutable_own_ground_marks()->mutable_marks(1)->set_mark_id(22);
  own.mutable_own_ground_marks()->mutable_marks(1)->mutable_author()->set_player_id(8);
  CHECK_FALSE(codec.Decode(Bytes(own)));
  P::ServerPacket none;
  none.set_protocol_version(W::Version);
  none.mutable_own_ground_marks();
  auto cleared = codec.Decode(Bytes(none));
  REQUIRE(cleared);
  CHECK(std::get<OwnGroundMarksReplaced>(*cleared).marks.empty());
}

TEST_CASE("The own list replaces the previous one, travels whole in the delta and clears with the session")
{
  ClientModel model;
  ChangeBatch scratch;
  REQUIRE(model.Apply(model.Generation(), OwnGroundMarksReplaced{{Mark(5), Mark(2)}}));
  auto update = TakeStateUpdate(model, scratch);
  REQUIRE(update);
  const auto& delta = std::get<ClientStateDelta>(*update);
  REQUIRE(delta.ownGroundMarks);
  CHECK(Ids(*delta.ownGroundMarks) == std::vector<Domain::GroundMarkId>{2, 5});
  CHECK(delta.groundMarks.empty());
  CHECK_FALSE(TakeStateUpdate(model, scratch));

  REQUIRE(model.Apply(model.Generation(), OwnGroundMarksReplaced{{Mark(7)}}));
  REQUIRE(model.Apply(model.Generation(), OwnGroundMarksReplaced{{}}));
  auto emptied = TakeStateUpdate(model, scratch);
  REQUIRE(emptied);
  REQUIRE(std::get<ClientStateDelta>(*emptied).ownGroundMarks);
  CHECK(std::get<ClientStateDelta>(*emptied).ownGroundMarks->empty());

  REQUIRE(model.Apply(model.Generation(), OwnGroundMarksReplaced{{Mark(9)}}));
  CHECK(Ids(model.Snapshot().groundMarks.own) == std::vector<Domain::GroundMarkId>{9});
  CHECK(Ids(model.OwnGroundMarks()) == std::vector<Domain::GroundMarkId>{9});
  model.ResetSession();
  CHECK(model.Snapshot().groundMarks.own.empty());
}
