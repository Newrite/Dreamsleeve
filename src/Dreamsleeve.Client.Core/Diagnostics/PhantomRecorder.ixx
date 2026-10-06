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

  struct Budget
  {
    std::uint64_t queueBytes{128ULL * 1024 * 1024}, diskBytes{1024ULL * 1024 * 1024};
    std::uint32_t queueJobs{64}, records{4096}, models{32};
  };

  // Actor Euler angles in radians, distinct from the sampled 3D root quaternion.
  struct Movement
  {
    std::uint64_t context{}, sequence{}, sampledAtUs{};
    P::Vec3       position, euler;
  };

  struct Status
  {
    Phase         phase{Phase::Idle};
    std::uint64_t samples{}, encoded{}, sent{}, movements{}, errors{}, dropped{}, bytes{}, queuedBytes{};
    double        seconds{}, captureMs{}, encodeMs{}, sampleHz{};
    std::string   directory, reason;
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
    void Failed(std::uint32_t failure);

private:

    struct State;
    std::unique_ptr<State> state;
  };

  Recorder& Phantoms();

}
