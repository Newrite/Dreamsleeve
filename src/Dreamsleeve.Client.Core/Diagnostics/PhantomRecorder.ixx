export module Dreamsleeve.Client.Diagnostics.PhantomRecorder;
import std;
export import Dreamsleeve.Client.Phantom.Codec;

export namespace Dreamsleeve::Client::Diagnostics
{
  namespace P = Phantom;
  using Clock = std::chrono::steady_clock;

  enum class Phase
  {
    Idle,
    Recording,
    Saving,
    Complete,
    Failed
  };

  // Only fixed names reach paths/metadata; the UI cannot choose an arbitrary path.
  inline constexpr std::array<std::string_view, 6> Scenarios{"idle", "walk-turn", "sprint", "combat", "camera", "equipment-smp"};

  inline constexpr P::Limits CaptureLimits()
  {
    return {};
  }

  struct Budget
  {
    std::uint64_t queueBytes{128ULL * 1024 * 1024};
    std::uint64_t freeReserveBytes{64ULL * 1024};

    std::uint32_t queueJobs{64};
    std::uint32_t records{4096};
    std::uint32_t models{32};
  };

  // Actor Euler angles in radians, distinct from the sampled 3D root quaternion.
  struct Movement
  {
    std::uint64_t context{};
    std::uint64_t sequence{};
    std::uint64_t sampledAtUs{};

    P::Vec3 position;
    P::Vec3 euler;
  };

  struct Status
  {
    Phase phase{Phase::Idle};

    std::uint64_t samples{};
    std::uint64_t encoded{};
    std::uint64_t sent{};
    std::uint64_t movements{};
    std::uint64_t errors{};
    std::uint64_t dropped{};
    std::uint64_t bytes{};
    std::uint64_t queuedBytes{};

    double seconds{};
    double captureMs{};
    double encodeMs{};
    double sampleHz{};

    std::string directory;
    std::string reason;
    std::string lastCaptureError;

    std::uint32_t omittedGeometry{};
    std::uint32_t hiddenGeometry{};
    std::uint64_t partialSamples{};
    std::string   partialDetail;
  };

  // Recorder owns only detached data and its writer thread, never an engine object.
  class Recorder final
  {
public:

    explicit Recorder(Budget budget = {});
    ~Recorder();
    Recorder(const Recorder&)            = delete;
    Recorder& operator=(const Recorder&) = delete;

    bool      Start(std::filesystem::path directory, std::uint32_t scenario, std::uint32_t seconds, std::uint32_t sampleRate);
    void      Stop(std::string_view reason = "manual");
    void      Shutdown();
    bool      Active() const noexcept;
    Status    Read() const;

    void      Sample(
      std::shared_ptr<const P::ValidatedAsset> asset,
      std::shared_ptr<const P::Snapshot>       pose,
      Movement                                 actor,
      double                                   captureMs,
      bool                                     firstPerson);
    void Encoded(const P::Snapshot& pose, double milliseconds, std::size_t bytes);
    void Sent(std::span<const std::uint8_t> packet);
    void MovementSent(Movement movement, std::span<const std::uint8_t> packet);
    void Failed(const P::Error& failure);
    void Partial(std::uint32_t omitted, std::uint32_t hidden, std::string_view detail);

private:

    struct State;
    std::unique_ptr<State> state;
  };

  Recorder& Phantoms();

}
