import std;
import Dreamsleeve.Client.MovementView;
import Dreamsleeve.Client.Exchange;

namespace
{

  using namespace Dreamsleeve::Client;

  double Elapsed(MovementClock::time_point started)
  {
    return std::chrono::duration<double, std::milli>(MovementClock::now() - started).count();
  }

  double Percentile(std::vector<double> values, double fraction)
  {
    std::ranges::sort(values);
    return values[static_cast<std::size_t>(std::ceil(fraction * values.size())) - 1];
  }

  bool RunCase(int players, bool stalled)
  {
    auto exchange = ClientExchange::TryCreate(8, 8);
    auto view     = MovementView::Create();
    if (!exchange) return false;

    ClientModel                 model;
    ClientOutput                output;
    std::vector<Domain::Player> initial;
    for (int index = 0; index < players; ++index)
      initial.push_back({
          .data                = {static_cast<std::uint64_t>(index + 1), "benchmark", "Benchmark"},
          .characterGeneration = 1
      });
    if (!model.Apply(1, OnlinePlayersReplaced{initial}) || !(*exchange)->Publish(model)) return false;
    (*exchange)->Drain(output);
    const auto epoch = MovementClock::now();
    view->Apply(output.state, epoch);

    std::vector<double> updateMs, frameMs;
    std::size_t         recoveries{};
    std::size_t         poses{};
    double              checksum{};
    const auto          started = MovementClock::now();
    // 10 simulated seconds: 100 Hz scheduling grid, 10 Hz movement, 50 Hz rendering.
    for (int time = 0; time <= 10000; time += 10)
    {
      const auto frameTime = epoch + std::chrono::milliseconds{time};
      if (time % 100 == 0)
      {
        const auto before = MovementClock::now();
        for (int index = 0; index < players; ++index)
        {
          Domain::PlayerLocation location{
              {{"skyrim.esm", 0x3c}, "Benchmark"},
              {static_cast<float>(time) / 10.f, static_cast<float>(index), 0},
              {},
              1000000 + static_cast<std::uint64_t>(time) * 1000
          };
          if (!model.Apply(1, PlayerLocationUpdated{static_cast<std::uint64_t>(index + 1), location}, frameTime)) return false;
        }
        if (!(*exchange)->Publish(model)) return false;
        updateMs.push_back(Elapsed(before));
      }
      if (time % 20 == 0 && !(stalled && time >= 2000 && time < 4000))
      {
        const auto before = MovementClock::now();
        (*exchange)->Drain(output);
        for (auto& update : output.state.updates)
        {
          if (auto* snapshot = std::get_if<ClientSnapshot>(&update))
          {
            ++recoveries;
            // Snapshot normally captures wall time. This harness drives virtual
            // time, so translate its publication time from the retained sample.
            const auto stamp     = snapshot->players.front().location->sampledAtUs;
            snapshot->observedAt = epoch + std::chrono::microseconds{stamp - 1000000};
          }
        }
        view->Apply(output.state, frameTime);
        for (int index = 0; index < players; ++index)
        {
          const auto pose = view->Sample(static_cast<std::uint64_t>(index + 1), frameTime);
          if (!pose) return false;
          checksum += pose->position.X;
          ++poses;
        }
        frameMs.push_back(Elapsed(before));
      }
    }
    const auto last  = view->Sample(1, epoch + std::chrono::seconds{11});
    const bool valid = last && last->position.X == 1000.f && (stalled ? recoveries > 0 : recoveries == 0);
    std::cout << "{\"players\":" << players << ",\"stalledConsumer\":" << (stalled ? "true" : "false")
              << ",\"success\":" << (valid ? "true" : "false") << ",\"wallMs\":" << Elapsed(started)
              << ",\"updateP50Ms\":" << Percentile(updateMs, .5) << ",\"updateP95Ms\":" << Percentile(updateMs, .95)
              << ",\"updateP99Ms\":" << Percentile(updateMs, .99) << ",\"frameP50Ms\":" << Percentile(frameMs, .5)
              << ",\"frameP95Ms\":" << Percentile(frameMs, .95) << ",\"frameP99Ms\":" << Percentile(frameMs, .99)
              << ",\"snapshotRecoveriesObserved\":" << recoveries << ",\"sampledPoses\":" << poses << ",\"checksum\":" << checksum << "}\n";
    return valid;
  }

}

int RunMovementBenchmark()
{
  bool valid = true;
  for (int players : {100, 500, 1000})
    for (bool stalled : {false, true})
      valid = RunCase(players, stalled) && valid;
  return valid ? 0 : 1;
}
