module;
#include "protocol.pb.h"

module Dreamsleeve.Client.ProtocolCodec;
#include "CodecParts.h"

// Unknown wire values are handled; every newly generated named case must be listed.
#pragma warning(error : 4061 4062)

namespace Dreamsleeve::Client::Wire::Detail
{

  Result<Domain::PlayerRole> Role(int value)
  {
    if (!P::PlayerRole_IsValid(value)) return Invalid("role");
    return static_cast<Domain::PlayerRole>(value);
  }

  Result<Domain::SanctionKind> Kind(int value)
  {
    if (!P::SanctionKind_IsValid(value) || value == P::SANCTION_KIND_UNSPECIFIED) return Invalid("kind");
    return static_cast<Domain::SanctionKind>(value);
  }

  Result<Domain::Sanction> ReadSanction(const P::SanctionEntry& source)
  {
    if (source.player_id() == Domain::InvalidId) return Invalid("player_id");
    auto kind = Kind(source.kind());
    if (!kind) return std::unexpected{kind.error()};
    if (!ValidUnixMs(source.issued_at_unix_ms())) return Invalid("issued_at_unix_ms");
    if (source.has_until_unix_ms() && !ValidUnixMs(source.until_unix_ms())) return Invalid("until_unix_ms");

    Domain::Sanction result{source.player_id(), *kind, source.reason(), source.issued_at_unix_ms()};
    if (source.has_until_unix_ms()) result.untilUnixMs = source.until_unix_ms();
    return result;
  }

}
