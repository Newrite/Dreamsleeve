module;

#include "Prelude.hpp"

export module Dreamsleeve.Game.PhantomAsset;

import std;
import Dreamsleeve.Client.Phantom.Types;
import Dreamsleeve.Game.PhantomVertexStream;
import Dreamsleeve.Game.PhantomMesh;

// Geometry normalization and math. These functions never load files, resolve
// peer names, or invoke an engine factory. Borrowed engine memory is read only
// in the caller's post-update main-thread phase and is copied before returning.
export namespace Dreamsleeve::Game::PhantomAsset
{
  namespace P      = Dreamsleeve::Client::Phantom;
  namespace Stream = Dreamsleeve::Game::PhantomVertexStream;
  using Stream::Half;
  using Stream::Read;
  namespace Mesh = Dreamsleeve::Game::PhantomMesh;
  using Mesh::Fail;
  using Mesh::Finite;
  using Mesh::Add;
  using Mesh::Sub;
  using Mesh::Mul;
  using Mesh::Dot;
  using Mesh::Cross;
  using Mesh::Unit;
  using Mesh::RawMesh;
  using Mesh::Decode;
  static_assert(Mesh::PackedVertex::VA_SKINNING == RE::BSGraphics::Vertex::VA_SKINNING);
  static_assert(Mesh::PackedVertex::VF_FULLPREC == RE::BSGraphics::Vertex::VF_FULLPREC);

  inline P::Vec3 Value(const RE::NiPoint3& v)
  {
    return {v.x, v.y, v.z};
  }

  inline RE::NiPoint3 Native(P::Vec3 v)
  {
    return {v.x, v.y, v.z};
  }

  inline bool Finite(const P::Transform& t)
  {
    const auto&  q    = t.rotation;
    const double norm = double(q.x) * q.x + double(q.y) * q.y + double(q.z) * q.z + double(q.w) * q.w;
    return Finite(t.position) && std::isfinite(t.scale) && t.scale > 1e-6f && std::isfinite(norm) && std::abs(norm - 1) < 0.002;
  }

  inline RE::NiTransform Native(const P::Transform& t)
  {
    const auto&     q = t.rotation;
    RE::NiTransform out;
    auto&           m = out.rotate.entry;
    m[0][0]           = 1 - 2 * (q.y * q.y + q.z * q.z);
    m[0][1]           = 2 * (q.x * q.y - q.z * q.w);
    m[0][2]           = 2 * (q.x * q.z + q.y * q.w);
    m[1][0]           = 2 * (q.x * q.y + q.z * q.w);
    m[1][1]           = 1 - 2 * (q.x * q.x + q.z * q.z);
    m[1][2]           = 2 * (q.y * q.z - q.x * q.w);
    m[2][0]           = 2 * (q.x * q.z - q.y * q.w);
    m[2][1]           = 2 * (q.y * q.z + q.x * q.w);
    m[2][2]           = 1 - 2 * (q.x * q.x + q.y * q.y);
    out.translate     = Native(t.position);
    out.scale         = t.scale;
    return out;
  }

  inline P::Result<P::Transform> Value(const RE::NiTransform& t)
  {
    if (!Finite(Value(t.translate)) || !std::isfinite(t.scale) || t.scale <= 1e-6f)
      return Fail(P::Failure::InvalidNumber, "transform.scale/position");
    const auto& m = t.rotate.entry;
    for (unsigned i = 0; i < 3; ++i)
      for (unsigned j = 0; j < 3; ++j)
        if (!std::isfinite(m[i][j])) return Fail(P::Failure::InvalidNumber, "transform.rotation");
    const P::Vec3 x{m[0][0], m[1][0], m[2][0]}, y{m[0][1], m[1][1], m[2][1]}, z{m[0][2], m[1][2], m[2][2]};
    if (
      std::abs(Dot(x, x) - 1) > 0.02f || std::abs(Dot(y, y) - 1) > 0.02f || std::abs(Dot(z, z) - 1) > 0.02f ||
      std::abs(Dot(x, y)) > 0.02f || std::abs(Dot(y, z)) > 0.02f || std::abs(Dot(z, x)) > 0.02f || Dot(Cross(x, y), z) < 0.98f)
      return Fail(P::Failure::UnsupportedGeometry, "transform.shear/reflection");
    P::Quaternion q;
    const float   trace = m[0][0] + m[1][1] + m[2][2];
    if (trace > 0)
    {
      const auto s = 2 * std::sqrt(trace + 1);
      q.w          = s / 4;
      q.x          = (m[2][1] - m[1][2]) / s;
      q.y          = (m[0][2] - m[2][0]) / s;
      q.z          = (m[1][0] - m[0][1]) / s;
    }
    else if (m[0][0] > m[1][1] && m[0][0] > m[2][2])
    {
      const auto s = 2 * std::sqrt(1 + m[0][0] - m[1][1] - m[2][2]);
      q.x          = s / 4;
      q.w          = (m[2][1] - m[1][2]) / s;
      q.y          = (m[0][1] + m[1][0]) / s;
      q.z          = (m[0][2] + m[2][0]) / s;
    }
    else if (m[1][1] > m[2][2])
    {
      const auto s = 2 * std::sqrt(1 + m[1][1] - m[0][0] - m[2][2]);
      q.y          = s / 4;
      q.w          = (m[0][2] - m[2][0]) / s;
      q.x          = (m[0][1] + m[1][0]) / s;
      q.z          = (m[1][2] + m[2][1]) / s;
    }
    else
    {
      const auto s = 2 * std::sqrt(1 + m[2][2] - m[0][0] - m[1][1]);
      q.z          = s / 4;
      q.w          = (m[1][0] - m[0][1]) / s;
      q.x          = (m[0][2] + m[2][0]) / s;
      q.y          = (m[1][2] + m[2][1]) / s;
    }
    const auto n     = std::sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
    const auto sign  = q.w < 0 ? -1.f : 1.f;
    q.x             *= sign / n;
    q.y             *= sign / n;
    q.z             *= sign / n;
    q.w             *= sign / n;
    P::Transform out{Value(t.translate), q, t.scale};
    if (!Finite(out)) return Fail(P::Failure::InvalidNumber, "transform.quaternion");
    return out;
  }

  inline void Enclose(P::Bound& into, P::Bound other)
  {
    if (other.radius <= 0) return;
    if (into.radius <= 0)
    {
      into = other;
      return;
    }
    const auto delta    = Sub(other.center, into.center);
    const auto distance = std::sqrt(double(delta.x) * delta.x + double(delta.y) * delta.y + double(delta.z) * delta.z);
    if (distance + other.radius <= into.radius) return;
    if (distance + into.radius <= other.radius)
    {
      into = other;
      return;
    }
    const auto radius = (distance + into.radius + other.radius) * 0.5f;
    if (distance > 0) into.center = Add(into.center, Mul(delta, static_cast<float>((radius - into.radius) / distance)));
    into.radius = static_cast<float>(radius);
  }

  inline bool Finite(const P::Bound& bound)
  {
    return Finite(bound.center) && std::isfinite(bound.radius) && bound.radius >= 0;
  }

  inline void Bounds(RE::NiAVObject& object, P::Bound bound)
  {
    object.worldBound = {Native(bound.center), bound.radius};
    if (auto* box = object.GetVROcclusionBox())
    {
      box->center      = Native(bound.center);
      box->halfExtents = {bound.radius, bound.radius, bound.radius};
    }
  }

  // Narrow Hooks adapters. They copy under the engine's resource/mesh lock,
  // return Busy instead of blocking on a renderer/physics job, and enforce
  // limits BEFORE allocating or mapping a GPU resource. No borrowed buffers.
  struct Readback
  {
    P::Result<RawMesh>              (*buffer)(RE::BSGraphics::TriShape&, std::uint32_t, std::uint32_t, const P::Limits&){};
    P::Result<std::vector<P::Vec3>> (*dynamic)(RE::BSDynamicTriShape&, std::uint32_t, const P::Limits&){};
  };

  inline bool Kind(const RE::NiObject& object, std::string_view name)
  {
    for (auto* rtti = object.GetRTTI(); rtti; rtti = rtti->GetBaseRTTI())
      if (rtti->GetName() && name == rtti->GetName()) return true;
    return false;
  }

  inline P::Result<RawMesh> CopyBuffer(
    RE::BSGraphics::TriShape& buffer,
    std::uint32_t             vertices,
    std::uint32_t             triangles,
    const P::Limits&          limits,
    const Readback&           readback)
  {
    const auto desc   = std::bit_cast<std::uint64_t>(buffer.vertexDesc);
    const auto stride = std::uint32_t(desc & 15) * 4;
    const auto bytes  = std::uint64_t(stride) * vertices;
    if (
      !vertices || vertices > 65535 || vertices > limits.vertices || !triangles || bytes > limits.assetBytes ||
      std::uint64_t(triangles) * 6 > limits.assetBytes)
      return Fail(P::Failure::LimitExceeded, "mesh.buffer");
    if (!buffer.rawVertexData || !buffer.rawIndexData)
    {
      if (!readback.buffer) return Fail(P::Failure::MissingSource, "mesh.cpu-buffer/readback");
      return readback.buffer(buffer, vertices, triangles, limits);
    }
    if (!stride || stride > 60) return Fail(P::Failure::UnsupportedGeometry, "mesh.stride");
    RawMesh out;
    out.descriptor  = desc;
    out.stride      = stride;
    out.vertexCount = vertices;
    out.vertices.resize(static_cast<std::size_t>(bytes));
    std::memcpy(out.vertices.data(), buffer.rawVertexData, out.vertices.size());
    out.indices.assign(buffer.rawIndexData, buffer.rawIndexData + std::size_t(triangles) * 3);
    return out;
  }

  // Available to the parent mesh adapter for the ordinary Skyrim CPU path.
  // The caller establishes a stable post-animation/post-SMP read phase. If a
  // mod writes this stream asynchronously it must use its synchronized API.
  inline P::Result<RawMesh> CopyCpuMesh(RE::BSTriShape& shape, const P::Limits& limits, const Readback& readback = {})
  {
    const auto*            rtti = shape.GetRTTI();
    const std::string_view type = rtti && rtti->GetName() ? rtti->GetName() : "";
    if (type != "BSTriShape" && type != "BSDynamicTriShape" && type != "BSSubIndexTriShape")
      return Fail(P::Failure::UnsupportedGeometry, "mesh.class:" + std::string(type));
    auto&              data      = shape.GetGeometryRuntimeData();
    auto&              counts    = shape.GetTrishapeRuntimeData();
    P::Result<RawMesh> copied    = Fail(P::Failure::MissingSource, "mesh.renderer");
    auto*              partition = data.skinInstance ? data.skinInstance->skinPartition.get() : nullptr;
    if (partition && partition->numPartitions)
    {
      if (
        partition->numPartitions > limits.geometry || !partition->partitions.data() || !partition->vertexCount ||
        partition->vertexCount > 65535 || partition->vertexCount > limits.vertices)
        return Fail(P::Failure::LimitExceeded, "mesh.partitions");
      RE::BSDismemberSkinInstance::RUNTIME_DATA* dismember = nullptr;
      if (Kind(*data.skinInstance, "BSDismemberSkinInstance"))
      {
        dismember = &static_cast<RE::BSDismemberSkinInstance*>(data.skinInstance.get())->GetRuntimeData();
        if (dismember->numPartitions != static_cast<std::int32_t>(partition->numPartitions) || !dismember->partitions)
          return Fail(P::Failure::InvalidSkin, "mesh.dismember-partitions");
      }
      std::uint64_t totalIndices = 0;
      bool          allHidden    = dismember != nullptr;
      if (dismember)
        for (std::uint32_t i = 0; i < partition->numPartitions; ++i)
          allHidden &= !dismember->partitions[i].visible;
      for (std::uint32_t i = 0; i < partition->numPartitions; ++i)
      {
        const auto& part = partition->partitions.data()[i];
        // Retain bounded geometry for a completely hidden body under armor.
        // Capture hides its channel; do not turn ordinary full coverage into
        // a missing mesh or silently omit the body from the appearance.
        if (dismember && !allHidden && !dismember->partitions[i].visible) continue;
        if (!part.triangles) continue;
        if (part.strips || part.bonesPerVertex > 4 || !part.buffData) return Fail(P::Failure::UnsupportedGeometry, "mesh.partition-shape");
        totalIndices += std::uint64_t(part.triangles) * 3;
        if (totalIndices * 2 > limits.assetBytes) return Fail(P::Failure::LimitExceeded, "mesh.indices");
        auto next = CopyBuffer(*part.buffData, partition->vertexCount, part.triangles, limits, readback);
        if (!next) return std::unexpected(next.error());
        if (!copied)
          copied = std::move(next);
        else
        {
          if (next->descriptor != copied->descriptor || next->vertices != copied->vertices)
            return Fail(P::Failure::UnsupportedGeometry, "mesh.nonshared-partition-vertices");
          copied->indices.insert(copied->indices.end(), next->indices.begin(), next->indices.end());
        }
      }
      if (!copied) return Fail(P::Failure::MissingSource, "mesh.no-visible-partitions");
    }
    else if (data.rendererData)
      copied = CopyBuffer(*data.rendererData, counts.vertexCount, counts.triangleCount, limits, readback);
    if (!copied) return copied;
    if (auto* dynamic = shape.AsDynamicTriShape())
    {
      if (!readback.dynamic) return Fail(P::Failure::MissingSource, "mesh.dynamic-locked-readback");
      auto positions = readback.dynamic(*dynamic, copied->vertexCount, limits);
      if (!positions) return std::unexpected(positions.error());
      if (positions->size() != copied->vertexCount) return Fail(P::Failure::InvalidGeometry, "mesh.dynamic-count");
      copied->positions = std::move(*positions);
      copied->dynamic   = true;
    }
    return copied;
  }

  struct RenderVertex
  {
    P::Vec3                     position, normal, tangent;
    float                       u{}, v{};
    std::array<std::uint8_t, 4> color{};
  };

  static_assert(sizeof(RenderVertex) == 48);

  // Native bound/skin derivation follows BSGeometry::UpdateWorldBound and
  // NiSkinInstance's bound calculation: rootParentToSkin * inverse(rootWorld)
  // * boneWorld * skinToBone. The receiver has ordinary nodes, so no borrowed
  // engine skin matrix/frame cache survives a pose application.
  inline P::Result<P::Bound> Deform(
    const P::Geometry&         geometry,
    const P::Snapshot&         pose,
    const P::Deformation*      deformation,
    std::vector<RenderVertex>& output)
  {
    const auto                   world = Native(pose.channels[geometry.node.value].world);
    std::vector<RE::NiTransform> matrices;
    if (geometry.skin)
    {
      const auto& skin     = *geometry.skin;
      const auto  intoSkin = Native(skin.worldToSkin) * Native(pose.channels[skin.root.value].world).Invert();
      matrices.reserve(skin.bones.size());
      for (const auto& bone : skin.bones)
      {
        auto matrix = intoSkin * Native(pose.channels[bone.node.value].world) * Native(bone.bind);
        auto check  = Value(matrix);
        if (!check) return std::unexpected(check.error());
        matrices.push_back(matrix);
      }
    }
    output.resize(geometry.vertices.size());
    P::Vec3 minimum{std::numeric_limits<float>::max(), std::numeric_limits<float>::max(), std::numeric_limits<float>::max()};
    P::Vec3 maximum = Mul(minimum, -1);
    for (std::size_t i = 0; i < geometry.vertices.size(); ++i)
    {
      const auto& source   = geometry.vertices[i];
      auto&       vertex   = output[i];
      const auto  position = deformation ? deformation->positions[i] : source.position;
      const auto  normal   = deformation && !deformation->normals.empty() ? deformation->normals[i] : source.normal;
      P::Vec3     p = position, n = normal, t = source.tangent;
      if (geometry.skin)
      {
        p = {};
        n = {};
        t = {};
        for (unsigned b = 0; b < 4; ++b)
        {
          const auto weight = source.weights[b];
          if (weight <= 0) continue;
          const auto& matrix = matrices[source.bones[b]];
          p                  = Add(p, Mul(Value(matrix * Native(position)), weight));
          n                  = Add(n, Mul(Value(matrix.rotate * Native(normal)), weight / matrix.scale));
          t                  = Add(t, Mul(Value(matrix.rotate * Native(source.tangent)), weight * matrix.scale));
        }
      }
      if (!Finite(p) || !Finite(n) || !Finite(t)) return Fail(P::Failure::InvalidNumber, "skin.blended-vertex");
      vertex           = {p, Unit(n), Unit(Sub(t, Mul(Unit(n), Dot(Unit(n), t)))), source.u, source.v, source.color};
      const auto point = Value(world * Native(p));
      if (!Finite(point) || !Finite(vertex.position) || !Finite(vertex.normal) || !Finite(vertex.tangent))
        return Fail(P::Failure::InvalidNumber, "skin.deformed-vertex");
      minimum = {std::min(minimum.x, point.x), std::min(minimum.y, point.y), std::min(minimum.z, point.z)};
      maximum = {std::max(maximum.x, point.x), std::max(maximum.y, point.y), std::max(maximum.z, point.z)};
    }
    const auto center = Add(Mul(minimum, 0.5f), Mul(maximum, 0.5f));
    float      radius = 0;
    for (const auto& vertex : output)
    {
      const auto delta = Sub(Value(world * Native(vertex.position)), center);
      radius           = std::max(radius, std::sqrt(Dot(delta, delta)));
    }
    if (!Finite(center) || !std::isfinite(radius)) return Fail(P::Failure::InvalidNumber, "skin.bound");
    return P::Bound{center, radius + 0.01f};
  }

}
