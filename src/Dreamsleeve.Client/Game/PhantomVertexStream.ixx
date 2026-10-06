export module Dreamsleeve.Game.PhantomVertexStream;

import std;
import Dreamsleeve.Client.Phantom.Types;

// Local Bethesda packed vertex streams, not the network asset encoding.
// Pure byte decoding is shared by the engine adapter and native regressions.
export namespace Dreamsleeve::Game::PhantomVertexStream
{
  namespace P = Dreamsleeve::Client::Phantom;

  inline float Half(std::uint16_t h)
  {
    const auto sign     = std::uint32_t(h & 0x8000) << 16;
    const auto exponent = (h >> 10) & 31;
    const auto fraction = h & 1023;
    if (!exponent) return std::copysign(std::ldexp(float(fraction), -24), h & 0x8000 ? -1.f : 1.f);
    if (exponent == 31) return std::bit_cast<float>(sign | 0x7f800000 | std::uint32_t(fraction) << 13);
    return std::bit_cast<float>(sign | std::uint32_t(exponent + 112) << 23 | std::uint32_t(fraction) << 13);
  }

  template <class T>
  inline T Read(std::span<const std::byte> bytes, std::size_t offset)
  {
    T value;
    std::memcpy(&value, bytes.data() + offset, sizeof(T));
    return value;
  }

  class PositionLayout
  {
public:

    static P::Result<PositionLayout> From(std::uint64_t descriptor, std::uint32_t stride)
    {
      if (!stride || stride > 60 || stride != (descriptor & 15) * 4)
        return std::unexpected(P::Error{P::Failure::UnsupportedGeometry, "mesh.position-stride"});
      // Bit 54 (VF_FULLPREC) alone is insufficient: SSE stream-100 rigid
      // geometry keeps FP32 positions even without it. The first following
      // attribute bounds the position+bitangentX footprint (8 or 16 bytes).
      // Skin partitions can actually use the 8-byte FP16 representation.
      auto footprint = stride;
      for (unsigned attribute = 1; attribute <= 8; ++attribute)
        if (descriptor & (1ULL << (44 + attribute))) footprint = std::min(footprint, unsigned((descriptor >> (4 * attribute + 2)) & 0x3c));
      if (footprint >= 16) return PositionLayout{16};
      if (footprint == 8 && !(descriptor & (1ULL << 54))) return PositionLayout{8};
      return std::unexpected(P::Error{P::Failure::UnsupportedGeometry, "mesh.position-footprint"});
    }

    P::Result<P::Vec3> Decode(std::span<const std::byte> bytes) const
    {
      if (bytes.size() < bytes_) return std::unexpected(P::Error{P::Failure::InvalidGeometry, "mesh.position-size"});
      const P::Vec3 value =
        bytes_ == 16
          ? P::Vec3{Read<float>(bytes, 0), Read<float>(bytes, 4), Read<float>(bytes, 8)}
          : P::Vec3{Half(Read<std::uint16_t>(bytes, 0)), Half(Read<std::uint16_t>(bytes, 2)), Half(Read<std::uint16_t>(bytes, 4))};
      if (!std::isfinite(value.x) || !std::isfinite(value.y) || !std::isfinite(value.z))
        return std::unexpected(P::Error{P::Failure::InvalidNumber, "mesh.position"});
      return value;
    }

private:

    explicit PositionLayout(unsigned bytes) : bytes_(bytes) {}

    unsigned bytes_;
  };

}
