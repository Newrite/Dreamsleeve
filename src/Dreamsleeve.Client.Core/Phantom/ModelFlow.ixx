export module Dreamsleeve.Client.Phantom.ModelFlow;
import std;

export namespace Dreamsleeve::Client::Phantom
{

  // Sender-local flight control. Application ACK latency includes network,
  // storage and receiver credit; none of those may build an unbounded backlog.
  class ModelFlow final
  {
    using Clock = std::chrono::steady_clock;
    std::uint32_t                                           chunk, maximum;
    double                                                  window, baseline{std::numeric_limits<double>::max()}, smoothed{};
    Clock::time_point                                       adjustAt{};
    bool                                                    probing{true};
    std::deque<std::pair<std::uint32_t, Clock::time_point>> sent;

public:

    ModelFlow(std::uint32_t chunkBytes, std::uint32_t windowChunks)
        : chunk(chunkBytes),
          maximum(chunkBytes * windowChunks),
          window(chunkBytes * std::min(2u, windowChunks))
    {}

    bool Allows(std::uint32_t flight, std::uint32_t bytes) const
    {
      return double(flight) + bytes <= window;
    }

    void Sent(std::uint32_t end, Clock::time_point at)
    {
      sent.emplace_back(end, at);
    }

    void Acknowledge(std::uint32_t end, Clock::time_point at)
    {
      std::optional<Clock::time_point> sample;
      while (!sent.empty() && sent.front().first <= end)
      {
        sample = sent.front().second;
        sent.pop_front();
      }
      if (!sample) return;  // Duplicate/partial ACK cannot grow the window.
      const auto delay = std::max(1.0, std::chrono::duration<double, std::milli>(at - *sample).count());
      baseline         = std::min(baseline, delay);
      smoothed         = smoothed ? smoothed * .875 + delay * .125 : delay;
      if (at < adjustAt) return;
      // Estimate excess queued bytes from the bandwidth-delay product. A fixed
      // millisecond threshold can suppress even a one-chunk flight on jitter.
      const auto queued = window * (smoothed - baseline) / smoothed;
      if (queued >= chunk) probing = false;
      if (queued > 2 * chunk)
        window = std::max(double(chunk), window - chunk);
      else if (queued < chunk)
        window = std::min(double(maximum), probing ? window * 2 : window + chunk);
      adjustAt = at + std::chrono::duration_cast<Clock::duration>(std::chrono::duration<double, std::milli>(baseline));
    }
  };

}
