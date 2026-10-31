// The phantom protocol: its messages on the model and pose lanes and their
// encoding (PhantomCodec.cpp, phantom.proto), apart from the session codec.
export module Dreamsleeve.Client.PhantomProtocol;

import std;
export import Dreamsleeve.Client.Phantom.Payload;
import Dreamsleeve.Client.ProtocolChannels;

export namespace Dreamsleeve::Client::Phantom::Wire
{

  constexpr auto          ModelsLane          = static_cast<std::uint8_t>(Dreamsleeve::Client::Wire::Channel::Models);
  constexpr auto          PosesLane           = static_cast<std::uint8_t>(Dreamsleeve::Client::Wire::Channel::Poses);
  constexpr std::uint32_t MaxAssetPacketBytes = 4096;

  struct Descriptor
  {
    Digest        hash;
    Generation    generation;
    std::uint32_t format{AssetVersion};
    std::uint32_t compressedBytes{};
    std::uint32_t rawBytes{};
    std::uint32_t channels{};

    bool operator==(const Descriptor&) const = default;

    bool SameContent(const Descriptor& other) const
    {
      auto content       = *this;
      content.generation = other.generation;
      return content == other;
    }
  };

  struct Preferences
  {
    bool          publish{};
    bool          receive{};
    std::uint32_t maximum{};
    float         distance{};
  };

  struct Publish
  {
    Descriptor                asset;
    std::uint64_t             context{};
    RequestId                 request;
    std::optional<AssetDelta> delta;
  };

  struct Download
  {
    std::uint64_t         player{};
    Generation            generation;
    RequestId             request;
    std::optional<Digest> baseHash;
  };

  struct Cancel
  {
    TransferId transfer;
  };

  struct Withdraw
  {};

  struct Displayed
  {
    std::uint64_t player{};
    std::uint64_t view{};
    Generation    generation;
  };

  using Request = std::variant<Preferences, Publish, Download, Cancel, Withdraw, Displayed>;

  struct Offer
  {
    std::uint64_t player{};
    std::uint64_t view{};
    Descriptor    asset;
  };

  struct Transfer
  {
    TransferId                transfer;
    Descriptor                asset;
    std::uint64_t             player{};
    bool                      upload{};
    RequestId                 request;
    std::string               httpToken;
    std::optional<AssetDelta> delta;

    std::uint32_t BodyBytes() const
    {
      return delta ? delta->compressedBytes : asset.compressedBytes;
    }
  };

  // How a transfer, or the request for one, ended (TransferOutcome in phantom.proto).
  enum class Outcome
  {
    Accepted,
    // An admitted transfer ended early.
    SourceChanged,
    ContextChanged,
    ViewRemoved,
    Disconnected,
    Cancelled,
    Stale,
    Superseded,
    Timeout,
    StorageFailure,
    HttpFailure,
    // The request was refused before a transfer began.
    DeltaBaseUnavailable,
    PublishRefused,
    ReplacementPending,
    InitialDisplayPending,
    PublishCooldown,
    SourceLimit,
    TransferLimit,
    ViewUnavailable,
  };

  // A short name for logs.
  constexpr std::string_view Name(Outcome outcome)
  {
    switch (outcome)
    {
      case Outcome::Accepted:
        return "accepted";
      case Outcome::SourceChanged:
        return "source changed";
      case Outcome::ContextChanged:
        return "context changed";
      case Outcome::ViewRemoved:
        return "view removed";
      case Outcome::Disconnected:
        return "disconnected";
      case Outcome::Cancelled:
        return "cancelled";
      case Outcome::Stale:
        return "stale transfer";
      case Outcome::Superseded:
        return "superseded";
      case Outcome::Timeout:
        return "transfer timeout";
      case Outcome::StorageFailure:
        return "storage failure";
      case Outcome::HttpFailure:
        return "HTTP failure";
      case Outcome::DeltaBaseUnavailable:
        return "delta base unavailable";
      case Outcome::PublishRefused:
        return "publish refused";
      case Outcome::ReplacementPending:
        return "replacement pending";
      case Outcome::InitialDisplayPending:
        return "initial display pending";
      case Outcome::PublishCooldown:
        return "publish cooldown";
      case Outcome::SourceLimit:
        return "source limit";
      case Outcome::TransferLimit:
        return "transfer limit";
      case Outcome::ViewUnavailable:
        return "view unavailable";
    }
    return "unknown";
  }

  // Server-supplied detail longer than this is a malformed packet.
  constexpr std::size_t MaxDetailBytes = 256;

  struct Complete
  {
    TransferId transfer;
    Outcome    outcome{};
    // Diagnostic text for logs only.
    std::string   detail;
    std::uint64_t player{};
    Generation    generation;
    std::uint32_t retryAfterMs{};
    bool          upload{};
    RequestId     request;

    bool Accepted() const
    {
      return outcome == Outcome::Accepted;
    }

    // The outcome and its detail for a log line.
    std::string Describe() const
    {
      return detail.empty() ? std::string(Name(outcome)) : std::format("{} ({})", Name(outcome), detail);
    }
  };

  struct Remove
  {
    std::uint64_t player{};
    std::uint64_t view{};
  };

  struct Policy
  {
    bool          enabled{};
    Limits        limits;
    std::uint32_t sampleRate{};
    std::uint32_t maximumVisible{};
    std::uint32_t concurrentTransfers{};
    std::uint32_t modelBytesPerSecond{};
    std::uint32_t poseBytesPerSecond{};
    float         distance{};
  };

  struct PoseDemand
  {
    std::uint64_t context{};
    bool          required{};
  };

  struct Settled
  {
    Generation    generation;
    std::uint64_t context{};
  };

  using Response = std::variant<Offer, Transfer, Complete, Remove, Policy, Settled, PoseDemand>;

  struct Pose
  {
    Generation                  generation;
    std::uint64_t               context{};
    Sequence                    sequence;
    std::uint64_t               sampledAtUs{};
    Bytes                       payload;
    std::shared_ptr<const Pose> previous;
  };

  struct RemotePose
  {
    std::uint64_t player{};
    std::uint64_t view{};
    Pose          sample;
  };

  Result<Bytes>      Encode(const Request& request);
  Result<Bytes>      Encode(const Pose& pose);
  Result<Response>   DecodeAsset(std::span<const std::uint8_t> data, const Limits& limits = {});
  Result<RemotePose> DecodePose(std::span<const std::uint8_t> data, const Limits& limits = {});

}
