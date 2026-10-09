export module Dreamsleeve.Client.Phantom.Delta;
import std;
export import Dreamsleeve.Client.Phantom.Codec;

export namespace Dreamsleeve::Client::Phantom::Delta
{

  // Opaque asset bytes only. The native NIF codec/renderer remains unchanged.
  Result<Bytes> Create(std::span<const std::uint8_t> base, std::span<const std::uint8_t> target, const Limits& limits = {});
  Result<Bytes> Apply(
    std::span<const std::uint8_t> base,
    std::span<const std::uint8_t> patch,
    std::uint32_t                 targetRawBytes,
    const Limits&                 limits = {});

}
