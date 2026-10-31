export module Dreamsleeve.Client.Phantom.Geometry;

import std;
export import Dreamsleeve.Client.Phantom.Types;

// Vector and rotation arithmetic of phantom poses, shared by the pose format,
// playback and the game adapter. Rules about which values are acceptable stay
// with their owners: the wire format and the capture check differ on purpose.
export namespace Dreamsleeve::Client::Phantom::Geometry
{

  inline bool Finite(const Vec3& v)
  {
    return std::isfinite(v.x) && std::isfinite(v.y) && std::isfinite(v.z);
  }

  inline Vec3 Add(const Vec3& a, const Vec3& b)
  {
    return {a.x + b.x, a.y + b.y, a.z + b.z};
  }

  inline Vec3 Sub(const Vec3& a, const Vec3& b)
  {
    return {a.x - b.x, a.y - b.y, a.z - b.z};
  }

  inline Vec3 Mul(const Vec3& a, float b)
  {
    return {a.x * b, a.y * b, a.z * b};
  }

  inline float Dot(const Vec3& a, const Vec3& b)
  {
    return a.x * b.x + a.y * b.y + a.z * b.z;
  }

  inline Vec3 Cross(const Vec3& a, const Vec3& b)
  {
    return {a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x};
  }

  inline float Distance(const Vec3& a, const Vec3& b)
  {
    const auto delta = Sub(a, b);
    return std::sqrt(Dot(delta, delta));
  }

  inline Vec3 Mix(const Vec3& a, const Vec3& b, float t)
  {
    return Add(a, Mul(Sub(b, a), t));
  }

  // Normalized linear blend along the shorter arc; a degenerate blend keeps a.
  inline Quaternion Mix(Quaternion a, Quaternion b, float t)
  {
    if (a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w < 0)
    {
      b.x = -b.x;
      b.y = -b.y;
      b.z = -b.z;
      b.w = -b.w;
    }

    Quaternion q{a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t, a.z + (b.z - a.z) * t, a.w + (b.w - a.w) * t};
    const auto length = std::sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
    if (length < 0.00001f) return a;

    q.x /= length;
    q.y /= length;
    q.z /= length;
    q.w /= length;
    return q;
  }

}
