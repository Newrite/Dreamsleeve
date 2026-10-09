import std;
import Dreamsleeve.Client.MovementView;
import Dreamsleeve.Client.Exchange;

// Reproducible 60 Hz consumer with 100 ms source samples, delivery jitter,
// a stop and a teleport. Uses the same model/exchange/view as live ENet.
int RunMovementDemo()
{
  using namespace Dreamsleeve::Client;
  using namespace std::chrono_literals;
  const auto epoch    = MovementClock::time_point{};
  auto       exchange = ClientExchange::TryCreate(8, 8);
  auto       movement = MovementView::Create();
  if (!exchange) return 1;

  ClientModel    model;
  Domain::Player player{
      .data                = {7, "demo", "Demo"},
      .characterGeneration = 1
  };
  if (!model.Apply(1, PlayerUpserted{player}, epoch) || !(*exchange)->Publish(model)) return 1;

  ClientOutput output;
  (*exchange)->Drain(output);
  movement->Apply(output.state, epoch);

  struct Delivery
  {
    int   source;
    int   arrival;
    float x;
  };

  constexpr Delivery deliveries[] = {
      {0,    100,  0   },
      {100,  240,  10  },
      {200,  270,  20  },
      {300,  450,  30  },
      {400,  490,  40  },
      {500,  650,  50  },
      {1600, 1750, 5000},
      {1700, 1810, 5010}
  };
  std::size_t next{};

  std::cout << "frame_ms,rendered_x,history_size\n";
  for (int frame = 0; frame <= 2200; frame += 16)
  {
    while (next < std::size(deliveries) && deliveries[next].arrival <= frame)
    {
      const auto&            delivery = deliveries[next++];
      Domain::PlayerLocation location{
          {{"skyrim.esm", 0x3c}, "Tamriel"},
          {delivery.x, 0, 0},
          {},
          1000000 + static_cast<std::uint64_t>(delivery.source) * 1000
      };
      if (!model.Apply(1, PlayerLocationUpdated{7, location}, epoch + std::chrono::milliseconds{delivery.arrival})) return 1;
    }

    if (!(*exchange)->Publish(model)) return 1;
    (*exchange)->Drain(output);
    movement->Apply(output.state, epoch + std::chrono::milliseconds{frame});

    const auto pose = movement->Sample(7, epoch + std::chrono::milliseconds{frame});
    if (pose) std::cout << frame << ',' << pose->position.X << ',' << movement->HistorySize(7) << '\n';
  }

  return 0;
}
