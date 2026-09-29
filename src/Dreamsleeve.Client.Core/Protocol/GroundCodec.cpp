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
    target.mutable_location_id()->set_plugin_name(value.locationId.pluginName);
    target.mutable_location_id()->set_local_form_id(value.locationId.localFormId);
    target.mutable_position()->set_x(value.position.X);
    target.mutable_position()->set_y(value.position.Y);
    target.mutable_position()->set_z(value.position.Z);
    target.set_heading(value.heading);
  }

  bool ValidPlacement(const Domain::GroundMarkPlacement& value)
  {
    return !value.locationId.pluginName.empty() && value.locationId.localFormId != 0 && std::isfinite(value.position.X) &&
           std::isfinite(value.position.Y) && std::isfinite(value.position.Z) && std::isfinite(value.heading);
  }

  void WriteNote(P::PlaceGroundNote& target, const PlaceGroundNote& value)
  {
    target.set_text(value.text);
    WritePlacement(*target.mutable_placement(), value.placement);
  }

  void WriteDeath(P::ReportDeath& target, const ReportDeath& value)
  {
    target.set_label(value.label);
    WritePlacement(*target.mutable_placement(), value.placement);
  }

  Result<Domain::GroundMarkPlacement> ReadPlacement(const P::GroundMarkPlacement& source)
  {
    const auto& key      = source.location_id();
    const auto& position = source.position();
    if (key.plugin_name().empty() || key.local_form_id() == 0) return Invalid("placement");
    if (!std::isfinite(position.x()) || !std::isfinite(position.y()) || !std::isfinite(position.z()) || !std::isfinite(source.heading()))
      return Invalid("placement");
    return Domain::GroundMarkPlacement{
        {key.plugin_name(), key.local_form_id()},
        {position.x(), position.y(), position.z()},
        source.heading()
    };
  }

  // Values are kept as sent; an unknown kind stays as its number for a host to
  // treat as it likes. A death may carry an empty label, a note may not.
  Result<Domain::GroundMark> Mark(const P::GroundMark& source)
  {
    if (source.mark_id() == 0) return Invalid("mark_id");
    if (source.kind() == P::GROUND_MARK_KIND_UNSPECIFIED) return Invalid("kind");
    if (source.kind() == P::GROUND_MARK_KIND_NOTE && source.text().empty()) return Invalid("text");
    if (source.created_at_unix_ms() < -62135596800000LL || source.created_at_unix_ms() > 253402300799999LL)
      return Invalid("created_at_unix_ms");
    auto author = Profile(source.author());
    if (!author) return std::unexpected{author.error()};
    auto placement = ReadPlacement(source.placement());
    if (!placement) return std::unexpected{placement.error()};
    auto flagged = ReadFlagged(source.text(), source.flagged());
    if (!flagged) return std::unexpected{flagged.error()};
    return Domain::GroundMark{
        source.mark_id(),
        std::move(*author),
        static_cast<Domain::GroundMarkKind>(source.kind()),
        source.text(),
        std::move(*flagged),
        std::move(*placement),
        Domain::FromUnixMilliseconds(source.created_at_unix_ms()),
        source.has_character_name() ? std::optional{source.character_name()} : std::nullopt
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
      if (id == 0) return Invalid("removed_ids");
      result.removedIds.push_back(id);
    }
    return result;
  }

}
