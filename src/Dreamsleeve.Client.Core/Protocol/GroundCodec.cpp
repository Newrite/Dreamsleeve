module;
#include "protocol.pb.h"

module Dreamsleeve.Client.ProtocolCodec;
#include "CodecParts.h"

// Unknown wire values are handled; every newly generated named case must be listed.
#pragma warning(error : 4061 4062)

namespace Dreamsleeve::Client::Wire::Detail
{

  void WritePlacement(P::GroundMarkPlacement& target, const Domain::GroundMarkPlacement& value)
  {
    WriteKey(*target.mutable_location_id(), value.locationId);
    WritePosition(*target.mutable_position(), value.position);
    target.set_heading(value.heading);
  }

  void WriteGameDate(P::GameDate& target, const Domain::GameDate& value)
  {
    target.set_era(value.era);
    target.set_year(value.year);
    target.set_month(value.month);
    target.set_day(value.day);
    target.set_day_of_week(value.dayOfWeek);
    target.set_hour(value.hour);
    target.set_minute(value.minute);
  }

  void WriteNote(P::PlaceGroundNote& target, const PlaceGroundNote& value)
  {
    target.set_text(value.text);
    WritePlacement(*target.mutable_placement(), value.placement);
    WriteGameDate(*target.mutable_game_date(), value.gameDate);
  }

  void WriteDeath(P::ReportDeath& target, const ReportDeath& value)
  {
    target.set_label(value.label);
    WritePlacement(*target.mutable_placement(), value.placement);
    WriteGameDate(*target.mutable_game_date(), value.gameDate);
  }

  // Absent on marks stored before protocol 12; present means valid.
  Result<std::optional<Domain::GameDate>> ReadGameDate(const P::GroundMark& source)
  {
    if (!source.has_game_date()) return std::optional<Domain::GameDate>{};
    const auto&            date = source.game_date();
    const Domain::GameDate value{
        date.era(),
        date.year(),
        date.month(),
        date.day(),
        date.day_of_week(),
        date.hour(),
        date.minute()
    };
    if (!ValidGameDate(value)) return Invalid("game_date");
    return std::optional{value};
  }

  Result<Domain::GroundMarkPlacement> ReadPlacement(const P::GroundMarkPlacement& source)
  {
    Domain::GroundMarkPlacement result{KeyOf(source.location_id()), PositionOf(source.position()), source.heading()};
    if (!ValidPlacement(result)) return Invalid("placement");
    return result;
  }

  // Values are kept as sent; an unknown kind stays as its number for a host to
  // treat as it likes. A death may carry an empty label, a note may not.
  Result<Domain::GroundMark> Mark(const P::GroundMark& source)
  {
    if (source.mark_id() == Domain::InvalidId) return Invalid("mark_id");
    if (source.kind() == P::GROUND_MARK_KIND_UNSPECIFIED) return Invalid("kind");
    if (source.kind() == P::GROUND_MARK_KIND_NOTE && source.text().empty()) return Invalid("text");
    if (!ValidUnixMs(source.created_at_unix_ms())) return Invalid("created_at_unix_ms");

    auto author = Profile(source.author());
    if (!author) return std::unexpected{author.error()};
    auto placement = ReadPlacement(source.placement());
    if (!placement) return std::unexpected{placement.error()};
    auto flagged = ReadFlagged(source.text(), source.flagged());
    if (!flagged) return std::unexpected{flagged.error()};
    auto gameDate = ReadGameDate(source);
    if (!gameDate) return std::unexpected{gameDate.error()};

    return Domain::GroundMark{
        source.mark_id(),
        std::move(*author),
        static_cast<Domain::GroundMarkKind>(source.kind()),
        source.text(),
        std::move(*flagged),
        std::move(*placement),
        Domain::FromUnixMilliseconds(source.created_at_unix_ms()),
        source.has_character_name() ? std::optional{source.character_name()} : std::nullopt,
        *gameDate
    };
  }

  Result<GroundMarksChanged> ReadMarksChanged(const P::GroundMarksChanged& source)
  {
    if (source.view_revision() == 0) return Invalid("view_revision");
    if (!source.clear() && source.added().empty() && source.removed_ids().empty()) return Invalid("ground_marks_changed");

    GroundMarksChanged result{source.view_revision(), {}, {}, source.clear()};
    result.added.reserve(static_cast<std::size_t>(source.added_size()));
    for (const auto& value : source.added())
    {
      auto mark = Mark(value);
      if (!mark) return std::unexpected{mark.error()};
      result.added.push_back(std::move(*mark));
    }

    for (const auto id : source.removed_ids())
    {
      if (id == Domain::InvalidId) return Invalid("removed_ids");
      result.removedIds.push_back(id);
    }
    return result;
  }

  // One author for the whole list and each ID once; the runtime checks the author is self.
  Result<OwnGroundMarksReplaced> ReadOwnMarks(const P::OwnGroundMarks& source)
  {
    OwnGroundMarksReplaced result;
    result.marks.reserve(static_cast<std::size_t>(source.marks_size()));
    std::set<Domain::GroundMarkId> ids;
    for (const auto& value : source.marks())
    {
      auto mark = Mark(value);
      if (!mark) return std::unexpected{mark.error()};
      if (!ids.insert(mark->markId).second) return Invalid("mark_id");
      if (!result.marks.empty() && result.marks.front().author.playerId != mark->author.playerId) return Invalid("author");
      result.marks.push_back(std::move(*mark));
    }
    return result;
  }

}
