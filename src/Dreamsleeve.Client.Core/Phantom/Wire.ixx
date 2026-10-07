export module Dreamsleeve.Client.Phantom.Wire;

import std;
export import Dreamsleeve.Client.Phantom.Codec;
import Dreamsleeve.Client.ProtocolChannels;

export namespace Dreamsleeve::Client::Phantom::Wire
{

  constexpr auto          ModelsLane = static_cast<std::uint8_t>(Dreamsleeve::Client::Wire::Channel::Models);
  constexpr auto          PosesLane  = static_cast<std::uint8_t>(Dreamsleeve::Client::Wire::Channel::Poses);
  constexpr std::uint32_t ChunkBytes = 16384;

  struct Descriptor
  {
    Digest        hash;
    Generation    generation;
    std::uint32_t format{AssetVersion}, compressedBytes{}, rawBytes{}, channels{};
    bool          operator==(const Descriptor&) const = default;

    bool SameContent(const Descriptor& other) const
    {
      auto content       = *this;
      content.generation = other.generation;
      return content == other;
    }
  };

  struct Preferences
  {
    bool          publish{}, receive{};
    std::uint32_t maximum{};
    float         distance{};
  };

  struct Publish
  {
    Descriptor    asset;
    std::uint64_t context{};
    RequestId     request;
  };

  struct Chunk
  {
    TransferId    transfer;
    std::uint32_t offset{};
    Bytes         data;
  };

  struct Download
  {
    std::uint64_t player{};
    Generation    generation;
    RequestId     request;
  };

  struct Cancel
  {
    TransferId transfer;
  };

  struct Withdraw
  {};

  struct Progress
  {
    TransferId    transfer;
    std::uint32_t nextOffset{};
  };

  struct Displayed
  {
    std::uint64_t player{}, view{};
    Generation    generation;
  };

  using Request = std::variant<Preferences, Publish, Chunk, Download, Cancel, Withdraw, Progress, Displayed>;

  struct Offer
  {
    std::uint64_t player{}, view{};
    Descriptor    asset;
  };

  struct Transfer
  {
    TransferId    transfer;
    Descriptor    asset;
    std::uint64_t player{};
    bool          upload{};
    RequestId     request;
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
    std::uint64_t player{}, view{};
  };

  struct Policy
  {
    bool          enabled{};
    Limits        limits;
    std::uint32_t sampleRate{}, maximumVisible{}, windowChunks{}, concurrentTransfers{}, modelBytesPerSecond{}, poseBytesPerSecond{};
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

  using Response = std::variant<Offer, Transfer, Chunk, Complete, Remove, Progress, Policy, Settled, PoseDemand>;

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
    std::uint64_t player{}, view{};
    Pose          sample;
  };

  Result<Bytes>      Encode(const Request& request);
  Result<Bytes>      Encode(const Pose& pose);
  Result<Response>   DecodeAsset(std::span<const std::uint8_t> data, const Limits& limits = {});
  Result<RemotePose> DecodePose(std::span<const std::uint8_t> data, const Limits& limits = {});

}
