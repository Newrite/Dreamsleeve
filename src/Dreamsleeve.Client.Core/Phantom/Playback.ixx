export module Dreamsleeve.Client.Phantom.Playback;

import std;
export import Dreamsleeve.Client.Phantom.Types;

export namespace Dreamsleeve::Client::Phantom
{
  namespace Motion
  {

    Vec3 Mix(const Vec3& a, const Vec3& b, float t)
    {
      return {a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t, a.z + (b.z - a.z) * t};
    }

    Quaternion Mix(Quaternion a, Quaternion b, float t)
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

    float Distance(const Vec3& a, const Vec3& b)
    {
      const auto x = a.x - b.x, y = a.y - b.y, z = a.z - b.z;
      return std::sqrt(x * x + y * y + z * z);
    }

    // Conservative sphere containing both poses, including interpolated vertices.
    Bound Cover(const Bound& a, const Bound& b)
    {
      const auto center = Mix(a.center, b.center, 0.5f);
      return {center, std::max(a.radius, b.radius) + Distance(a.center, b.center) * 0.5f};
    }

    Snapshot Between(const Snapshot& a, const Snapshot& b, float t)
    {
      Snapshot result = b;
      result.origin   = Mix(a.origin, b.origin, t);
      for (std::size_t i = 0; i < result.channels.size(); ++i)
      {
        auto&       out    = result.channels[i];
        const auto& left   = a.channels[i];
        out.world.position = Mix(left.world.position, out.world.position, t);
        out.world.rotation = Mix(left.world.rotation, out.world.rotation, std::clamp(t, 0.0f, 1.0f));
        out.world.scale    = left.world.scale + (out.world.scale - left.world.scale) * std::clamp(t, 0.0f, 1.0f);
        out.hidden         = t < 1 ? left.hidden : out.hidden;
      }
      for (std::size_t i = 0; i < result.bounds.size(); ++i)
      {
        result.bounds[i] = Cover(a.bounds[i], b.bounds[i]);
        if (t > 1) result.bounds[i].radius += Distance(a.bounds[i].center, b.bounds[i].center) * (t - 1);
      }
      for (auto& deformation : result.deformations)
      {
        const auto previous = std::ranges::find(a.deformations, deformation.geometry, &Deformation::geometry);
        if (previous == a.deformations.end()) continue;
        for (std::size_t i = 0; i < deformation.positions.size(); ++i)
        {
          deformation.positions[i] = Mix(previous->positions[i], deformation.positions[i], std::clamp(t, 0.0f, 1.0f));
          deformation.normals[i]   = Mix(previous->normals[i], deformation.normals[i], std::clamp(t, 0.0f, 1.0f));
        }
      }
      return result;
    }

  }

  class Playback final
  {
    struct Sample
    {
      std::shared_ptr<const Snapshot> pose;
      std::uint64_t                   arrivalUs{};
    };

    std::deque<Sample> samples;
    std::int64_t       offsetUs{};

public:

    void Clear()
    {
      samples.clear();
      offsetUs = 0;
    }

    bool Push(std::shared_ptr<const Snapshot> pose, std::uint64_t arrivalUs)
    {
      if (pose->sampledAtUs > MaximumSampleTime || arrivalUs > MaximumSampleTime) return false;
      if (!samples.empty())
      {
        const auto& last = *samples.back().pose;
        if (pose->generation != last.generation || pose->context != last.context)
          Clear();
        else if (pose->sequence <= last.sequence || pose->sampledAtUs <= last.sampledAtUs)
          return false;
        else if (pose->sampledAtUs - last.sampledAtUs > 5000000)
          Clear();
      }
      const auto offset = static_cast<std::int64_t>(arrivalUs) - static_cast<std::int64_t>(pose->sampledAtUs);
      offsetUs          = samples.empty() ? offset : std::min(offsetUs, offset);
      samples.push_back({std::move(pose), arrivalUs});
      while (samples.size() > 8)
        samples.pop_front();
      return true;
    }

    std::optional<Snapshot> At(std::uint64_t nowUs, const ViewSettings& settings) const
    {
      if (samples.empty() || nowUs < samples.back().arrivalUs || nowUs - samples.back().arrivalUs > settings.timeoutMs * 1000ULL)
        return std::nullopt;
      const auto target = static_cast<std::int64_t>(nowUs) - offsetUs - static_cast<std::int64_t>(settings.delayMs) * 1000;
      if (target <= static_cast<std::int64_t>(samples.front().pose->sampledAtUs)) return *samples.front().pose;
      for (std::size_t i = 1; i < samples.size(); ++i)
      {
        const auto& a = *samples[i - 1].pose;
        const auto& b = *samples[i].pose;
        if (target <= static_cast<std::int64_t>(b.sampledAtUs))
          return Motion::Between(a, b, static_cast<float>(target - a.sampledAtUs) / static_cast<float>(b.sampledAtUs - a.sampledAtUs));
      }
      if (samples.size() < 2) return *samples.back().pose;
      const auto& a     = *samples[samples.size() - 2].pose;
      const auto& b     = *samples.back().pose;
      const auto  extra = std::min<std::uint64_t>(target - b.sampledAtUs, settings.extrapolationMs * 1000ULL);
      // A teleport is never interpreted as velocity.
      if (Motion::Distance(a.origin, b.origin) > 512) return b;
      return Motion::Between(a, b, 1 + static_cast<float>(extra) / static_cast<float>(b.sampledAtUs - a.sampledAtUs));
    }
  };

}
