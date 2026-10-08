export module Dreamsleeve.Client.Phantom.Codec;

import std;
export import Dreamsleeve.Client.Phantom.Types;

export namespace Dreamsleeve::Client::Phantom
{

  using Bytes  = std::vector<std::uint8_t>;
  using Digest = std::array<std::uint8_t, 32>;

  struct AssetDelta
  {
    Digest baseHash, hash;
    std::uint32_t compressedBytes{};
    bool operator==(const AssetDelta&) const = default;
  };
  struct PreparedDelta { AssetDelta descriptor; std::shared_ptr<const Bytes> bytes; };

  struct PreparedAsset
  {
    ValidatedAsset               asset;
    Digest                       hash;
    std::shared_ptr<const Bytes> compressed;
    std::uint32_t                rawBytes{};
    std::optional<PreparedDelta> delta;
  };

  Result<Digest>         Hash(std::span<const std::uint8_t> bytes);
  std::string            Hex(const Digest& hash);
  Result<PreparedAsset>  Prepare(ValidatedAsset asset, const Limits& limits = {});
  Result<ValidatedAsset> ReadAsset(std::span<const std::uint8_t> compressed, std::uint32_t rawBytes, const Limits& limits = {});
  Result<Bytes>          WriteSnapshot(const Snapshot& snapshot, const ValidatedAsset& asset, const Limits& limits = {});
  Result<Snapshot>       ReadSnapshot(std::span<const std::uint8_t> compressed, const ValidatedAsset& asset, const Limits& limits = {});
#ifdef DREAMSLEEVE_DIAGNOSTICS
  // Offline archives may carry the earlier pose layout. The network decoder
  // accepts only PoseVersion; both readers share validation and quantization.
  Result<Snapshot> ReadRecordedSnapshot(std::span<const std::uint8_t> compressed, const ValidatedAsset& asset, const Limits& limits = {});
  // The production encoder's input to Zstd; no second quantization implementation.
  Result<Bytes> SnapshotBytes(const Snapshot& snapshot, const ValidatedAsset& asset, const Limits& limits = {});
#endif

}
