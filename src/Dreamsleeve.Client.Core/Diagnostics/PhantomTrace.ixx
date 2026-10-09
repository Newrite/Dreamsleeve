export module Dreamsleeve.Client.Diagnostics.PhantomTrace;
import std;

export namespace Dreamsleeve::Client::Diagnostics::Trace
{

  enum class Metric
  {
    Frame,
    GameTick,
    Topology,
    Clone,
    Normalize,
    ValidateAsset,
    NativeLoad,
    ScenePrepare,
    CapturePose,
    ApplyPose,
    NativeAudit,
    NativeSave,
    NativeReserve,
    NativeDispose,
    ModelPrepare,
    AssetDump,
    AssetDecode,
    PoseEncode,
    PoseDecode,
    DeltaEncode,
    DeltaApply,
    Count
  };
  enum class TraceFailure
  {
    InvalidConfiguration,
    Storage,
    StartupLaunch,
    CleanupLaunch,
    CleanupPending
  };

  struct TraceError
  {
    TraceFailure kind;
    std::string  detail;
  };
  enum class StopOutcome
  {
    Stopped,
    CleanupPending
  };
  // The runtime lifecycle owner stops producers before Stop. Start/Stop serialize
  // one session, including its pending cleanup; a new session cannot bypass it.
  std::expected<void, TraceError>        Start(const std::filesystem::path& directory, std::size_t partBytes = 64ULL * 1024 * 1024);
  std::expected<StopOutcome, TraceError> Stop();
  bool                                   Enabled() noexcept;
  void                                   Event(std::string_view kind, std::string fields = {});
  // Detached compressed bytes only; caller is the model worker, never the game thread.
  void Asset(std::string_view hash, std::span<const std::uint8_t> bytes);
  void Packet(bool outgoing, std::uint8_t lane, std::span<const std::uint8_t> bytes);
  void Observe(Metric metric, double milliseconds) noexcept;
  void FlushMetrics();

  // Narrow causal controls for the diagnostic lifecycle tests, consumed once.
  namespace Testing
  {

    void FailNextCleanupLaunch();
    void GateNextCleanup(std::shared_future<void> gate);

  }

  class Span
  {
    Metric                                metric;
    bool                                  active;
    std::chrono::steady_clock::time_point start;

public:

    explicit Span(Metric value)
        : metric(value),
          active(Enabled()),
          start(active ? std::chrono::steady_clock::now() : std::chrono::steady_clock::time_point{})
    {}

    Span(const Span&) = delete;

    ~Span()
    {
      if (active) Observe(metric, std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - start).count());
    }
  };

}
