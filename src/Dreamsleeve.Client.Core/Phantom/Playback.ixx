export module Dreamsleeve.Client.Phantom.Playback;

import std;
import Dreamsleeve.Client.Domain.Logic;
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
      return result;
    }

  }

  class Playback final
  {
    using Time = std::chrono::time_point<std::chrono::steady_clock, std::chrono::microseconds>;

    struct Sample
    {
      std::shared_ptr<const Snapshot> pose;
      std::uint64_t                   arrivalUs{};
      Time                            time;
    };

    std::deque<Sample> samples;

public:

    void Clear()
    {
      samples.clear();
    }

    bool Push(std::shared_ptr<const Snapshot> pose, std::uint64_t arrivalUs, const ViewSettings& settings)
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
      const Time received{std::chrono::microseconds{arrivalUs}};
      auto       time = received;
      if (!samples.empty())
      {
        // Reuse movement's source-clock mapping. A faster packet must not
        // advance the entire interpolation history in one rendered frame.
        const auto mapped = Domain::Motion::SourceTime(
          samples.back().time,
          samples.back().pose->sampledAtUs,
          pose->sampledAtUs,
          received,
          std::chrono::milliseconds{settings.delayMs},
          std::chrono::milliseconds{5000});
        if (mapped)
          time = *mapped;
        else
          Clear();
      }
      samples.push_back({std::move(pose), arrivalUs, time});
      while (samples.size() > BufferedPoseCount)
        samples.pop_front();
      return true;
    }

    struct Timing
    {
      std::size_t   samples{};
      std::uint64_t sequence{}, sourceGapUs{}, arrivalGapUs{}, ageUs{};
      std::int64_t  aheadUs{};
    };

    // A read-only view of the existing buffer, not another clock/state owner.
    Timing Inspect(std::uint64_t nowUs, const ViewSettings& settings) const
    {
      if (samples.empty()) return {};
      const auto& last = samples.back();
      Timing      result{samples.size(), last.pose->sequence.value};
      result.ageUs   = nowUs >= last.arrivalUs ? nowUs - last.arrivalUs : 0;
      result.aheadUs = last.time.time_since_epoch().count() - static_cast<std::int64_t>(nowUs) + settings.delayMs * 1000LL;
      for (std::size_t i = 1; i < samples.size(); ++i)
      {
        result.sourceGapUs = std::max(result.sourceGapUs, samples[i].pose->sampledAtUs - samples[i - 1].pose->sampledAtUs);
        if (samples[i].arrivalUs >= samples[i - 1].arrivalUs)
          result.arrivalGapUs = std::max(result.arrivalGapUs, samples[i].arrivalUs - samples[i - 1].arrivalUs);
      }
      return result;
    }

    std::optional<Snapshot> At(std::uint64_t nowUs, const ViewSettings& settings) const
    {
      if (samples.empty() || nowUs < samples.back().arrivalUs || nowUs - samples.back().arrivalUs > settings.timeoutMs * 1000ULL)
        return std::nullopt;
      const Time target{std::chrono::microseconds{nowUs} - std::chrono::milliseconds{settings.delayMs}};
      if (target <= samples.front().time) return *samples.front().pose;
      for (std::size_t i = 1; i < samples.size(); ++i)
      {
        const auto& a = *samples[i - 1].pose;
        const auto& b = *samples[i].pose;
        if (target <= samples[i].time)
          return Motion::Between(
            a,
            b,
            static_cast<float>((target - samples[i - 1].time).count()) / static_cast<float>(b.sampledAtUs - a.sampledAtUs));
      }
      if (samples.size() < 2) return *samples.back().pose;
      const auto& a     = *samples[samples.size() - 2].pose;
      const auto& b     = *samples.back().pose;
      const auto  extra = std::min<std::uint64_t>((target - samples.back().time).count(), settings.extrapolationMs * 1000ULL);
      // A teleport is never interpreted as velocity.
      if (Motion::Distance(a.origin, b.origin) > 512) return b;
      return Motion::Between(a, b, 1 + static_cast<float>(extra) / static_cast<float>(b.sampledAtUs - a.sampledAtUs));
    }
  };

}
