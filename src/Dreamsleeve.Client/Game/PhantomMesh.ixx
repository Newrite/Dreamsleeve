export module Dreamsleeve.Game.PhantomMesh;
import std;
import Dreamsleeve.Client.Phantom.Types;
import Dreamsleeve.Game.PhantomVertexStream;

// The SAME decoder serves live CPU/readback streams and saved regression data.
// No RE objects, GPU calls, filesystem access or engine factories here.
export namespace Dreamsleeve::Game::PhantomMesh
{
  namespace P      = Dreamsleeve::Client::Phantom;
  namespace Stream = Dreamsleeve::Game::PhantomVertexStream;
  using Stream::Half;
  using Stream::Read;

  // Bethesda packed stream flags; kept engine-free for offline decoding.
  // The native adapter asserts the shared values against CommonLib.
  struct PackedVertex
  {
    enum Attribute
    {
      VA_POSITION,
      VA_TEXCOORD0,
      VA_TEXCOORD1,
      VA_NORMAL,
      VA_BINORMAL,
      VA_COLOR,
      VA_SKINNING,
      VA_LANDDATA,
      VA_EYEDATA
    };

    enum Flags
    {
      VF_VERTEX   = 1 << VA_POSITION,
      VF_UV       = 1 << VA_TEXCOORD0,
      VF_UV_2     = 1 << VA_TEXCOORD1,
      VF_NORMAL   = 1 << VA_NORMAL,
      VF_TANGENT  = 1 << VA_BINORMAL,
      VF_COLORS   = 1 << VA_COLOR,
      VF_SKINNED  = 1 << VA_SKINNING,
      VF_EYEDATA  = 1 << VA_EYEDATA,
      VF_FULLPREC = 0x400
    };
  };

  inline std::unexpected<P::Error> Fail(P::Failure reason, std::string field)
  {
    return std::unexpected(P::Error{reason, std::move(field)});
  }

  inline bool Finite(P::Vec3 v)
  {
    return std::isfinite(v.x) && std::isfinite(v.y) && std::isfinite(v.z);
  }

  inline P::Vec3 Add(P::Vec3 a, P::Vec3 b)
  {
    return {a.x + b.x, a.y + b.y, a.z + b.z};
  }

  inline P::Vec3 Sub(P::Vec3 a, P::Vec3 b)
  {
    return {a.x - b.x, a.y - b.y, a.z - b.z};
  }

  inline P::Vec3 Mul(P::Vec3 a, float b)
  {
    return {a.x * b, a.y * b, a.z * b};
  }

  inline float Dot(P::Vec3 a, P::Vec3 b)
  {
    return a.x * b.x + a.y * b.y + a.z * b.z;
  }

  inline P::Vec3 Cross(P::Vec3 a, P::Vec3 b)
  {
    return {a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x};
  }

  inline P::Vec3 Unit(P::Vec3 v, P::Vec3 fallback = {0, 0, 1})
  {
    const auto length = std::sqrt(Dot(v, v));
    return std::isfinite(length) && length > 1e-8f ? Mul(v, 1 / length) : fallback;
  }

  // A skin blend with sums in [minimum, maximum] is not necessarily a
  // convex combination. Expand in mesh space before applying world translation.
  inline P::Bound WeightedBound(P::Bound hull, float minimum, float maximum)
  {
    const auto midpoint = (minimum + maximum) * .5f;
    return {Mul(hull.center, midpoint), hull.radius * maximum + std::sqrt(Dot(hull.center, hull.center)) * (maximum - minimum) * .5f};
  }

  // A copied, currently rendered pre-skin mesh, never a template NIF. SSE
  // partitions use a shared vertex stream with GLOBAL NiSkinData bone indices.
  // A nonstandard adapter must expand/remap its palette before returning this.
  // positions/normals override the packed stream for face/per-vertex morphs.
  struct RawMesh
  {
    std::uint64_t              descriptor{};
    std::uint32_t              stride{}, vertexCount{};
    std::vector<std::byte>     vertices;
    std::vector<std::uint16_t> indices;
    std::vector<P::Vec3>       positions, normals;
    bool                       dynamic{}, preSkin{true};
  };

  inline void GenerateNormals(std::vector<P::Vertex>& vertices, std::span<const std::uint16_t> indices)
  {
    for (auto& vertex : vertices)
      vertex.normal = {};
    for (std::size_t i = 0; i < indices.size(); i += 3)
    {
      auto&      a      = vertices[indices[i]];
      auto&      b      = vertices[indices[i + 1]];
      auto&      c      = vertices[indices[i + 2]];
      const auto normal = Cross(Sub(b.position, a.position), Sub(c.position, a.position));
      a.normal          = Add(a.normal, normal);
      b.normal          = Add(b.normal, normal);
      c.normal          = Add(c.normal, normal);
    }
    for (auto& vertex : vertices)
      vertex.normal = Unit(vertex.normal);
  }

  inline P::Result<P::Geometry> Decode(const RawMesh& raw, P::NodeId node, std::uint32_t boneCount, const P::Limits& limits)
  {
    if (!raw.preSkin) return Fail(P::Failure::UnsupportedGeometry, "mesh.post-skin-stream");
    if (
      !raw.vertexCount || raw.vertexCount > 65535 || raw.vertexCount > limits.vertices || !raw.stride || raw.stride > 60 ||
      raw.vertices.size() != std::uint64_t(raw.vertexCount) * raw.stride || raw.vertices.size() > limits.assetBytes ||
      raw.indices.empty() || raw.indices.size() % 3 || raw.indices.size() > limits.assetBytes / 2 ||
      (!raw.positions.empty() && raw.positions.size() != raw.vertexCount) ||
      (!raw.normals.empty() && raw.normals.size() != raw.vertexCount))
      return Fail(P::Failure::InvalidGeometry, "mesh.stream-size");
    using V              = PackedVertex;
    const auto     flags = std::uint32_t(raw.descriptor >> 44);
    constexpr auto supported =
      V::VF_VERTEX | V::VF_UV | V::VF_UV_2 | V::VF_NORMAL | V::VF_TANGENT | V::VF_COLORS | V::VF_SKINNED | V::VF_EYEDATA | V::VF_FULLPREC;
    if (flags & ~supported) return Fail(P::Failure::UnsupportedGeometry, "mesh.vertex-flags");
    if (!(flags & V::VF_VERTEX) && raw.positions.empty()) return Fail(P::Failure::InvalidGeometry, "mesh.position");
    if ((flags & V::VF_SKINNED) && !boneCount) return Fail(P::Failure::InvalidSkin, "mesh.weights-without-skin");
    if (!(flags & V::VF_SKINNED) && boneCount) return Fail(P::Failure::UnsupportedGeometry, "mesh.skin-without-packed-weights");
    const auto offset = [&](unsigned attribute) {
      return attribute ? unsigned((raw.descriptor >> (4 * attribute + 2)) & 0x3c) : 0U;
    };
    const auto fits = [&](unsigned attribute, unsigned bytes) {
      return offset(attribute) <= raw.stride && bytes <= raw.stride - offset(attribute);
    };
    const auto has = [&](unsigned flag) {
      return (flags & flag) != 0;
    };
    std::optional<Stream::PositionLayout> positions;
    if (raw.positions.empty())
    {
      auto layout = Stream::PositionLayout::From(raw.descriptor, raw.stride);
      if (!layout) return std::unexpected(layout.error());
      positions = *layout;
    }
    if (
      (has(V::VF_UV) && !fits(V::VA_TEXCOORD0, 4)) || (has(V::VF_UV_2) && !fits(V::VA_TEXCOORD1, 4)) ||
      (has(V::VF_NORMAL) && !fits(V::VA_NORMAL, 4)) || (has(V::VF_TANGENT) && !fits(V::VA_BINORMAL, 4)) ||
      (has(V::VF_COLORS) && !fits(V::VA_COLOR, 4)) || (has(V::VF_SKINNED) && !fits(V::VA_SKINNING, 12)))
      return Fail(P::Failure::UnsupportedGeometry, "mesh.attribute-offset");
    P::Geometry out;
    out.node    = node;
    out.dynamic = raw.dynamic;
    out.indices = raw.indices;
    for (auto index : out.indices)
      if (index >= raw.vertexCount) return Fail(P::Failure::InvalidGeometry, "mesh.index");
    out.vertices.resize(raw.vertexCount);
    for (std::size_t i = 0; i < out.vertices.size(); ++i)
    {
      auto&      v     = out.vertices[i];
      const auto bytes = std::span(raw.vertices).subspan(i * raw.stride, raw.stride);
      if (!raw.positions.empty())
        v.position = raw.positions[i];
      else
      {
        auto position = positions->Decode(bytes);
        if (!position)
          return Fail(
            position.error().reason,
            std::format("{} [descriptor={:X}, stride={}, vertex={}]", position.error().field, raw.descriptor, raw.stride, i));
        v.position = *position;
      }
      if (has(V::VF_UV))
      {
        const auto o = offset(V::VA_TEXCOORD0);
        v.u          = Half(Read<std::uint16_t>(bytes, o));
        v.v          = Half(Read<std::uint16_t>(bytes, o + 2));
      }
      const auto unpack = [&](unsigned attribute) {
        const auto o = offset(attribute);
        return Unit(
          {float(Read<std::uint8_t>(bytes, o)) / 127.5f - 1,
           float(Read<std::uint8_t>(bytes, o + 1)) / 127.5f - 1,
           float(Read<std::uint8_t>(bytes, o + 2)) / 127.5f - 1});
      };
      if (has(V::VF_NORMAL)) v.normal = unpack(V::VA_NORMAL);
      if (!raw.normals.empty()) v.normal = Unit(raw.normals[i]);
      if (has(V::VF_TANGENT)) v.tangent = unpack(V::VA_BINORMAL);
      if (has(V::VF_COLORS)) std::memcpy(v.color.data(), bytes.data() + offset(V::VA_COLOR), 4);
      if (has(V::VF_SKINNED))
      {
        const auto o = offset(V::VA_SKINNING);
        for (unsigned b = 0; b < 4; ++b)
        {
          v.weights[b] = Half(Read<std::uint16_t>(bytes, o + b * 2));
          v.bones[b]   = Read<std::uint8_t>(bytes, o + 8 + b);
          if (!v.weights[b]) v.bones[b] = 0;
        }
        if (!v.ValidWeights(boneCount)) return Fail(P::Failure::InvalidSkin, "mesh.weight/index");
      }
      if (!Finite(v.position) || !std::isfinite(v.u) || !std::isfinite(v.v) || (!raw.normals.empty() && !Finite(raw.normals[i])))
        return Fail(
          P::Failure::InvalidNumber,
          std::format(
            "mesh.vertex [descriptor={:X}, stride={}, vertex={}, position-valid={}, uv-valid={}, normals-valid={}]",
            raw.descriptor,
            raw.stride,
            i,
            Finite(v.position),
            std::isfinite(v.u) && std::isfinite(v.v),
            raw.normals.empty() || Finite(raw.normals[i])));
    }
    // Packed normals can belong to the pre-morph template. A face deformation
    // without a current normal stream gets normals from its ACTUAL triangles.
    if ((!has(V::VF_NORMAL) || !raw.positions.empty()) && raw.normals.empty()) GenerateNormals(out.vertices, out.indices);
    for (auto& v : out.vertices)
    {
      v.normal  = Unit(v.normal);
      v.tangent = Unit(
        Sub(v.tangent, Mul(v.normal, Dot(v.normal, v.tangent))),
        Unit(Cross(std::abs(v.normal.z) < 0.9f ? P::Vec3{0, 0, 1} : P::Vec3{0, 1, 0}, v.normal)));
    }
    return out;
  }

}
