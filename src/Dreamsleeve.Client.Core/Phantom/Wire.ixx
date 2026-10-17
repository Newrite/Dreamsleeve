export module Dreamsleeve.Client.Phantom.Wire;

import std;
export import Dreamsleeve.Client.Phantom.Codec;
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

  struct Complete
  {
    TransferId    transfer;
    bool          accepted{};
    std::string   reason;
    std::uint64_t player{};
    Generation    generation;
    std::uint32_t retryAfterMs{};
    bool          upload{};
    RequestId     request;
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
