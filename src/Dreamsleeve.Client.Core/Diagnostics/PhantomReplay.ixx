export module Dreamsleeve.Client.Diagnostics.PhantomReplay;
import std;
export import Dreamsleeve.Client.Diagnostics.PhantomRecorder;

export namespace Dreamsleeve::Client::Diagnostics
{

  struct ReplayFrame
  {
    std::shared_ptr<const P::ValidatedAsset> asset;
    std::shared_ptr<const P::Snapshot>       pose;
  };

  struct ReplayLoadStatus
  {
    bool busy{};
    bool complete{};

    std::string directory;
    std::string error;

    std::uint64_t frames{};
    std::uint64_t models{};
    double        decodeMs{};
  };

  // Bounded look-ahead; disk, hashing and the production decoders run here.
  // No engine object crosses this boundary. Stop cancels without joining.
  class ReplayReader final
  {
public:

    ReplayReader();
    ~ReplayReader();

    bool                       Start(std::filesystem::path root, std::uint32_t scenario, std::filesystem::path fallback = {});
    void                       Stop();
    void                       Shutdown();
    ReplayLoadStatus           Read() const;
    std::optional<ReplayFrame> Take();

private:

    struct State;
    std::unique_ptr<State> state_;
  };

}
