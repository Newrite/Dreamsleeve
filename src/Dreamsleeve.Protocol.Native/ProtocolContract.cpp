#include "network.pb.h"

import Dreamsleeve.Protocol;

// Locks the hand-written DisconnectReason mirror in Dreamsleeve.Protocol.Native.ixx
// to the protoc-generated enum.
//
// This translation unit is a plain .cpp on purpose: no module interface is emitted
// for it, so it may include network.pb.h without tripping the MSVC ICE described in
// the .ixx. Any future code that needs real protobuf message types belongs in a .cpp
// like this one, or in a module implementation unit - never in a module interface.

namespace
{

  using Generated = Dreamsleeve::Protocol::Network::DisconnectReason;
  using Mirrored  = Protocol::Network::DisconnectReason;

  constexpr bool SameValue(const Mirrored mirrored, const Generated generated) noexcept
  {
    return static_cast<unsigned long long>(mirrored) == static_cast<unsigned long long>(generated);
  }

  static_assert(SameValue(Mirrored::Unspecified, Dreamsleeve::Protocol::Network::Unspecified));
  static_assert(SameValue(Mirrored::ClientShutdown, Dreamsleeve::Protocol::Network::ClientShutdown));
  static_assert(SameValue(Mirrored::ServerShutdown, Dreamsleeve::Protocol::Network::ServerShutdown));
  static_assert(SameValue(Mirrored::Kicked, Dreamsleeve::Protocol::Network::Kicked));
  static_assert(SameValue(Mirrored::AuthFailed, Dreamsleeve::Protocol::Network::AuthFailed));
  static_assert(SameValue(Mirrored::TimeoutPolicy, Dreamsleeve::Protocol::Network::TimeoutPolicy));
  static_assert(SameValue(Mirrored::ProtocolError, Dreamsleeve::Protocol::Network::ProtocolError));

  // Trips when network.proto gains a DisconnectReason value that the mirror lacks.
  static_assert(
    Dreamsleeve::Protocol::Network::DisconnectReason_ARRAYSIZE == 7,
    "network.proto gained a DisconnectReason value - update the mirror in Dreamsleeve.Protocol.Native.ixx");

}
