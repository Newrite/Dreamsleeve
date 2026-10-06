module;
#include "Prelude.hpp"
export module Dreamsleeve.Game.PhantomMath;
import std;
import Dreamsleeve.Client.Phantom.Types;

export namespace Dreamsleeve::Game::PhantomMath
{
  namespace P = Dreamsleeve::Client::Phantom;

  inline auto Fail(P::Failure reason, std::string field)
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

}
