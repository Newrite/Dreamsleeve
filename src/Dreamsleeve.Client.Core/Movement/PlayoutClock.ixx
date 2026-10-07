export module Dreamsleeve.Client.PlayoutClock;
import std;

export namespace Dreamsleeve::Client
{

  // Packet insertion owns correction. Sampling is const, so immutable display
  // copies share the same continuous timeline without a second clock owner.
  class PlayoutClock
  {
    struct Anchor
    {
      std::uint64_t source{}, received{};
      std::int64_t  playhead{};
      double        speed{1}, interval{}, jitter{}, delay{};
      unsigned      samples{1};
    };

    std::optional<Anchor> anchor;

public:

    void Clear()
    {
      anchor.reset();
    }

    std::int64_t At(std::uint64_t now) const
    {
      if (!anchor) return 0;
      const auto elapsed = static_cast<std::int64_t>(now) - static_cast<std::int64_t>(anchor->received);
      return anchor->playhead + static_cast<std::int64_t>(static_cast<double>(elapsed) * anchor->speed);
    }

    void Push(std::uint64_t source, std::uint64_t received, std::uint64_t sequenceGap, std::uint64_t minimumDelayUs, std::size_t capacity)
    {
      if (!anchor)
      {
        anchor        = Anchor{source, received, static_cast<std::int64_t>(source) - static_cast<std::int64_t>(minimumDelayUs)};
        anchor->delay = static_cast<double>(minimumDelayUs);
        return;
      }
      auto&        a           = *anchor;
      const auto   playhead    = At(received);  // Never move an existing timeline at insertion.
      const double sourceGap   = static_cast<double>(source - a.source);
      const double deliveryGap = static_cast<double>(received - a.received);
      const double interval    = sourceGap / static_cast<double>(std::max<std::uint64_t>(1, sequenceGap));
      if (a.interval == 0)
        a.interval = interval;
      else
        a.interval += .125 * (std::clamp(interval, a.interval * .5, a.interval * 2) - a.interval);
      a.jitter   += .125 * (std::abs(deliveryGap - sourceGap) - a.jitter);
      a.playhead  = playhead;
      a.source    = source;
      a.received  = received;
      a.samples   = std::min(4u, a.samples + 1);
      // Two intervals cover one missing snapshot; jitter adds a bounded margin.
      // Bound automatic growth by history and 400 ms; the explicit minimum wins.
      const double ceiling =
        std::max(static_cast<double>(minimumDelayUs), std::min(400000., a.interval * static_cast<double>(capacity > 2 ? capacity - 2 : 1)));
      a.delay = std::clamp(2 * a.interval + 3 * a.jitter, static_cast<double>(minimumDelayUs), ceiling);
      if (a.samples < 4) return;
      const double drift     = static_cast<double>(static_cast<std::int64_t>(source) - playhead) - a.delay;
      const double threshold = std::max(1000., a.interval * .25);
      a.speed = drift < -a.interval ? .85 : drift < -threshold ? .98 : drift > 2 * a.interval ? 1.10 : drift > threshold ? 1.02 : 1.;
    }

    double DelayUs() const
    {
      return anchor ? anchor->delay : 0;
    }

    double Speed() const
    {
      return anchor ? anchor->speed : 1;
    }
  };

}
