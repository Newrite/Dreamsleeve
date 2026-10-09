export module Dreamsleeve.Client.Phantom.Nif;

import std;
export import Dreamsleeve.Client.Phantom.Types;

export namespace Dreamsleeve::Client::Phantom::Nif
{

  // A description of links in an existing native asset, not a geometry format.
  // Scene order is preorder over the NIF children. It survives NiStream Load
  // without relying on unique bone names or process-local addresses.
  using Node   = NativeNode;
  using Layout = NativeLayout;

  // Strict controller/physics/external-resource-free SSE stream-100 profile.
  // All block lengths, typed references and allocation-driving counts are
  // checked before the game is allowed to call a native loader.
  Result<Layout> Inspect(std::span<const std::uint8_t> bytes, const Limits& limits = {});

}
