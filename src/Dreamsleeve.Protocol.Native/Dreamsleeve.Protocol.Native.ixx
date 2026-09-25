export module Dreamsleeve.Protocol;

import std;

export namespace Protocol::Network
{

  // Hand-written mirror of Dreamsleeve::Protocol::Network::DisconnectReason
  // generated from Protocol/network.proto.
  //
  // network.pb.h is deliberately NOT included here. MSVC 14.51.36231 hits an
  // internal compiler error (C1001, msc1.cpp line 1672) while emitting the
  // module interface of any .ixx whose global module fragment includes it -
  // the include alone is enough, no export of protobuf types is required.
  // Keeping the header out of every module interface is what lets the project
  // build on current toolsets instead of being pinned to 14.44.
  //
  // ProtocolContract.cpp static_asserts this mirror against the generated enum,
  // so the two cannot drift apart silently.
  enum class DisconnectReason : std::uint32_t
  {
    Unspecified    = 0,
    ClientShutdown = 1,
    ServerShutdown = 2,
    Kicked         = 3,
    AuthFailed     = 4,
    TimeoutPolicy  = 5,
    ProtocolError  = 6,
  };

}
