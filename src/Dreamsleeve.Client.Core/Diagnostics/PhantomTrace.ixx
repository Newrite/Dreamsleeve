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
  bool Start(const std::filesystem::path& directory, std::size_t partBytes = 64ULL * 1024 * 1024) noexcept;
  void Stop() noexcept;
  bool Enabled() noexcept;
  void Event(std::string_view kind, std::string fields = {}) noexcept;
  // Detached compressed bytes only; caller is the model worker, never the game thread.
  void Asset(std::string_view hash, std::span<const std::uint8_t> bytes) noexcept;
  void Packet(bool outgoing, std::uint8_t lane, std::span<const std::uint8_t> bytes) noexcept;
  void Observe(Metric metric, double milliseconds) noexcept;
  void FlushMetrics() noexcept;

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
