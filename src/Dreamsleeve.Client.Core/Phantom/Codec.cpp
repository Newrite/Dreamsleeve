#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <bcrypt.h>
#include <zstd.h>

import std;
import Dreamsleeve.Client.Phantom.Codec;

namespace Dreamsleeve::Client::Phantom
{
  namespace
  {

    constexpr std::uint32_t AssetMagic = 0x41504C44;
    constexpr std::uint32_t PoseMagic  = 0x50504C44;

    std::unexpected<Error> Fail(Failure failure, std::string_view field)
    {
      return std::unexpected(Error{failure, std::string(field)});
    }

    class Writer
    {
  public:

      Bytes bytes;

      template <class T>
      void Put(T value)
      {
        static_assert(std::is_arithmetic_v<T>);
        const auto data = std::bit_cast<std::array<std::uint8_t, sizeof(T)>>(value);
        bytes.insert(bytes.end(), data.begin(), data.end());
      }

      void Vector(const Vec3& v)
      {
        Put(v.x);
        Put(v.y);
        Put(v.z);
      }

      void Rotation(const Quaternion& q)
      {
        Put(q.x);
        Put(q.y);
        Put(q.z);
        Put(q.w);
      }

      void TransformValue(const Transform& t)
      {
        Vector(t.position);
        Rotation(t.rotation);
        Put(t.scale);
      }

      void BoundValue(const Bound& b)
      {
        Vector(b.center);
        Put(b.radius);
      }
    };

    class Reader
    {
      std::span<const std::uint8_t> bytes;
      std::size_t                   at{};
      Result<void>                  status;

      void Reject(Failure reason, std::string_view field)
      {
        if (status) status = Fail(reason, field);
      }

  public:

      explicit Reader(std::span<const std::uint8_t> input) : bytes(input) {}

      std::size_t Remaining() const
      {
        return bytes.size() - at;
      }

      template <class T>
      T Get()
      {
        if (!status) return {};
        if (sizeof(T) > Remaining())
        {
          Reject(Failure::InvalidFormat, "truncated");
          return {};
        }
        std::array<std::uint8_t, sizeof(T)> value;
        std::ranges::copy(bytes.subspan(at, sizeof(T)), value.begin());
        at += sizeof(T);
        return std::bit_cast<T>(value);
      }

      std::uint32_t Count(std::uint32_t maximum, std::size_t stride = 1)
      {
        const auto count = Get<std::uint32_t>();
        if (!status) return 0;
        if (count > maximum || count > Remaining() / stride)
        {
          Reject(Failure::LimitExceeded, "count");
          return 0;
        }
        return count;
      }

      Vec3 Vector()
      {
        return {Get<float>(), Get<float>(), Get<float>()};
      }

      Quaternion Rotation()
      {
        return {Get<float>(), Get<float>(), Get<float>(), Get<float>()};
      }

      Transform TransformValue()
      {
        return {Vector(), Rotation(), Get<float>()};
      }

      Bound BoundValue()
      {
        return {Vector(), Get<float>()};
      }

      Bytes Data(std::uint32_t count)
      {
        if (!status) return {};
        if (count > Remaining())
        {
          Reject(Failure::InvalidFormat, "data");
          return {};
        }
        Bytes result(bytes.begin() + at, bytes.begin() + at + count);
        at += count;
        return result;
      }

      Result<void> Check() const
      {
        return status;
      }

      Result<void> End()
      {
        if (Remaining() != 0) Reject(Failure::InvalidFormat, "trailing");
        return status;
      }
    };

    Result<Bytes> Compress(std::span<const std::uint8_t> raw, std::uint32_t maximum, int level)
    {
      Bytes      result(std::min<std::size_t>(ZSTD_compressBound(raw.size()), maximum));
      const auto size = ZSTD_compress(result.data(), result.size(), raw.data(), raw.size(), level);
      if (ZSTD_isError(size)) return std::unexpected(Error{Failure::LimitExceeded, "compressed"});
      result.resize(size);
      return result;
    }

    Result<Bytes> Decompress(std::span<const std::uint8_t> compressed, std::uint32_t maximum, std::uint32_t exact = 0)
    {
      const auto size = ZSTD_getFrameContentSize(compressed.data(), compressed.size());
      if (
        !size || size > ZSTD_CONTENTSIZE_ERROR || size > maximum || (exact && size != exact) ||
        ZSTD_findFrameCompressedSize(compressed.data(), compressed.size()) != compressed.size())
        return std::unexpected(Error{Failure::InvalidFormat, "zstd.frame"});
      Bytes      result(static_cast<std::size_t>(size));
      const auto read = ZSTD_decompress(result.data(), result.size(), compressed.data(), compressed.size());
      if (ZSTD_isError(read) || read != result.size()) return std::unexpected(Error{Failure::InvalidFormat, "zstd.data"});
      return result;
    }

    // A reversible byte-plane permutation improves compression without changing
    // any quantized value or introducing a dependency on an earlier snapshot.
    template <std::size_t Width, bool Decode = false>
    void BytePlanes(std::span<std::uint8_t> records)
    {
      Bytes      copy(records.begin(), records.end());
      const auto count = records.size() / Width;
      for (std::size_t i = 0; i < count; ++i)
        for (std::size_t b = 0; b < Width; ++b)
          if constexpr (Decode)
            records[i * Width + b] = copy[b * count + i];
          else
            records[b * count + i] = copy[i * Width + b];
    }

    bool Finite(const Vec3& v)
    {
      return std::isfinite(v.x) && std::isfinite(v.y) && std::isfinite(v.z);
    }

    bool Valid(const Transform& t)
    {
      const auto& q    = t.rotation;
      const auto  norm = q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w;
      return Finite(t.position) && std::isfinite(t.scale) && t.scale > 0 && t.scale <= 1024 && std::isfinite(norm) &&
             std::abs(norm - 1) < 0.01f;
    }

    Result<void> ValidateSnapshot(const Snapshot& snapshot, const NativeLayout& asset)
    {
      if (
        !snapshot.generation.value || !snapshot.sequence.value || !snapshot.context || snapshot.sampledAtUs > MaximumSampleTime ||
        !Finite(snapshot.origin) || snapshot.channels.size() != asset.requiredChannels.size() ||
        snapshot.bounds.size() != asset.bounds.size())
        return Fail(Failure::InvalidFormat, "pose.shape");
      for (const auto& channel : snapshot.channels)
        if (!Valid(channel.world)) return Fail(Failure::InvalidNumber, "pose.transform");
      for (const auto& bound : snapshot.bounds)
        if (!Finite(bound.center) || !std::isfinite(bound.radius) || bound.radius < 0 || bound.radius > 100000)
          return Fail(Failure::InvalidNumber, "pose.bound");
      return {};
    }

    Result<std::int16_t> Quantize(float value, float step)
    {
      const auto quantized = std::round(value / step);
      if (!std::isfinite(quantized) || quantized < -32767 || quantized > 32767) return Fail(Failure::LimitExceeded, "pose.range");
      return static_cast<std::int16_t>(quantized);
    }

    Result<std::int32_t> Position(float value)
    {
      const auto quantized = std::round(double(value) * 16);
      if (
        !std::isfinite(quantized) || quantized < std::numeric_limits<std::int32_t>::min() ||
        quantized > std::numeric_limits<std::int32_t>::max())
        return Fail(Failure::LimitExceeded, "pose.position-range");
      return static_cast<std::int32_t>(quantized);
    }

    Result<void> PutPosition(Writer& writer, const Vec3& v, const Vec3& origin)
    {
      for (auto value : {v.x - origin.x, v.y - origin.y, v.z - origin.z})
      {
        auto position = Position(value);
        if (!position) return std::unexpected(position.error());
        writer.Put(*position);
      }
      return {};
    }

    Vec3 GetPosition(Reader& reader, const Vec3& origin)
    {
      return {
          origin.x + reader.Get<std::int32_t>() / 16.f,
          origin.y + reader.Get<std::int32_t>() / 16.f,
          origin.z + reader.Get<std::int32_t>() / 16.f
      };
    }

  }

  Result<void> CheckSnapshot(const Snapshot& snapshot, const ValidatedAsset& asset)
  {
    return ValidateSnapshot(snapshot, asset.Layout());
  }

  Result<Digest> Hash(std::span<const std::uint8_t> bytes)
  {
    if (bytes.size() > std::numeric_limits<ULONG>::max()) return std::unexpected(Error{Failure::LimitExceeded, "hash"});
    Digest     digest{};
    const auto status = BCryptHash(
      BCRYPT_SHA256_ALG_HANDLE,
      nullptr,
      0,
      const_cast<PUCHAR>(bytes.data()),
      static_cast<ULONG>(bytes.size()),
      digest.data(),
      static_cast<ULONG>(digest.size()));
    if (!BCRYPT_SUCCESS(status)) return std::unexpected(Error{Failure::Storage, "sha256"});
    return digest;
  }

  std::string Hex(const Digest& digest)
  {
    constexpr std::string_view digits = "0123456789abcdef";
    std::string                result;
    result.reserve(64);
    for (const auto byte : digest)
    {
      result += digits[byte >> 4];
      result += digits[byte & 15];
    }
    return result;
  }

  Result<PreparedAsset> Prepare(ValidatedAsset asset, const Limits& limits)
  {
    Writer      w;
    const auto& model = asset.Value();
    w.Put(AssetMagic);
    w.Put(AssetVersion);
    w.Put(static_cast<std::uint32_t>(model.nif.size()));
    w.bytes.insert(w.bytes.end(), model.nif.begin(), model.nif.end());
    if (w.bytes.size() > limits.assetBytes) return std::unexpected(Error{Failure::LimitExceeded, "asset.bytes"});
    auto compressed = Compress(w.bytes, limits.compressedAssetBytes, 3);
    if (!compressed) return std::unexpected(compressed.error());

    auto digest = Hash(*compressed);
    if (!digest) return std::unexpected(digest.error());
    return PreparedAsset{
        std::move(asset),
        *digest,
        std::make_shared<const Bytes>(std::move(*compressed)),
        static_cast<std::uint32_t>(w.bytes.size())
    };
  }

  Result<ValidatedAsset> ReadAsset(std::span<const std::uint8_t> compressed, std::uint32_t rawBytes, const Limits& limits)
  {
    if (compressed.size() > limits.compressedAssetBytes) return std::unexpected(Error{Failure::LimitExceeded, "asset.compressed"});
    auto raw = Decompress(compressed, limits.assetBytes, rawBytes);
    if (!raw) return std::unexpected(raw.error());
    Reader r(*raw);
    if (r.Get<std::uint32_t>() != AssetMagic || r.Get<std::uint32_t>() != AssetVersion)
      return Fail(Failure::InvalidFormat, "asset.version");
    Asset asset;
    asset.nif = r.Data(r.Count(limits.assetBytes));
    if (auto end = r.End(); !end) return std::unexpected(end.error());
    return ValidatedAsset::Parse(std::move(asset), limits);
  }

  static Result<Bytes> WriteSnapshotBytes(const Snapshot& snapshot, const ValidatedAsset& asset, const Limits& limits)
  {
    const std::uint64_t size = 60 + snapshot.channels.size() * 23ULL + snapshot.bounds.size() * 16ULL;
    if (size > limits.poseBytes) return Fail(Failure::LimitExceeded, "pose.bytes");
    if (auto valid = ValidateSnapshot(snapshot, asset.Layout()); !valid) return std::unexpected(valid.error());

    Writer w;
    w.bytes.reserve(static_cast<std::size_t>(size));
    w.Put(PoseMagic);
    w.Put(PoseVersion);
    w.Put(snapshot.generation.value);
    w.Put(snapshot.sequence.value);
    w.Put(snapshot.context);
    w.Put(snapshot.sampledAtUs);
    w.Vector(snapshot.origin);
    w.Put(static_cast<std::uint32_t>(snapshot.channels.size()));
    w.Put(static_cast<std::uint32_t>(snapshot.bounds.size()));

    for (const auto& channel : snapshot.channels)
    {
      if (auto position = PutPosition(w, channel.world.position, snapshot.origin); !position) return std::unexpected(position.error());
      const auto& q = channel.world.rotation;
      for (auto component : {q.x, q.y, q.z, q.w})
      {
        auto value = Quantize(component, 1.0f / 32767);
        if (!value) return std::unexpected(value.error());
        w.Put(*value);
      }
      const auto scale = std::round(channel.world.scale * 1024.0);
      if (!std::isfinite(scale) || scale < 1 || scale > 65535) return Fail(Failure::LimitExceeded, "pose.scale-range");
      w.Put(static_cast<std::uint16_t>(scale));
      w.Put<std::uint8_t>(channel.hidden ? 1 : 0);
    }

    for (const auto& bound : snapshot.bounds)
    {
      if (auto position = PutPosition(w, bound.center, snapshot.origin); !position) return std::unexpected(position.error());
      w.Put(bound.radius);
    }

    auto data = std::span(w.bytes);
    BytePlanes<23>(data.subspan(60, snapshot.channels.size() * 23));
    BytePlanes<16>(data.subspan(60 + snapshot.channels.size() * 23, snapshot.bounds.size() * 16));
    return std::move(w.bytes);
  }

  Result<Bytes> WriteSnapshot(const Snapshot& snapshot, const ValidatedAsset& asset, const Limits& limits)
  {
    auto raw = WriteSnapshotBytes(snapshot, asset, limits);
    if (!raw) return std::unexpected(raw.error());
    return Compress(*raw, limits.compressedPoseBytes, 1);
  }

#ifdef DREAMSLEEVE_DIAGNOSTICS
  Result<Bytes> SnapshotBytes(const Snapshot& snapshot, const ValidatedAsset& asset, const Limits& limits)
  {
    return WriteSnapshotBytes(snapshot, asset, limits);
  }
#endif

  static Result<Snapshot> DecodeSnapshot(
    std::span<const std::uint8_t> compressed,
    const ValidatedAsset&         asset,
    const Limits&                 limits,
    bool                          archived)
  {
    if (compressed.size() > limits.compressedPoseBytes) return std::unexpected(Error{Failure::LimitExceeded, "pose.compressed"});
    auto raw = Decompress(compressed, limits.poseBytes);
    if (!raw) return std::unexpected(raw.error());
    Reader r(*raw);
    if (r.Get<std::uint32_t>() != PoseMagic) return Fail(Failure::InvalidFormat, "pose.magic");
    const auto version = r.Get<std::uint32_t>();
    if (version != PoseVersion && !(archived && version == 2)) return Fail(Failure::InvalidFormat, "pose.version");

    Snapshot snapshot;
    snapshot.generation  = {r.Get<std::uint64_t>()};
    snapshot.sequence    = {r.Get<std::uint64_t>()};
    snapshot.context     = r.Get<std::uint64_t>();
    snapshot.sampledAtUs = r.Get<std::uint64_t>();
    snapshot.origin      = r.Vector();
    const auto channels  = r.Count(limits.nodes, 23);
    const auto bounds    = r.Count(limits.nodes, 16);
    if (auto status = r.Check(); !status) return std::unexpected(status.error());
    if (channels != asset.Layout().requiredChannels.size() || bounds != asset.Layout().bounds.size())
      return Fail(Failure::InvalidFormat, "pose.counts");
    if (r.Remaining() != channels * 23ULL + bounds * 16ULL) return Fail(Failure::InvalidFormat, "pose.size");

    if (version == PoseVersion)
    {
      auto data = std::span(*raw);
      BytePlanes<23, true>(data.subspan(60, channels * 23ULL));
      BytePlanes<16, true>(data.subspan(60 + channels * 23ULL, bounds * 16ULL));
    }

    snapshot.channels.reserve(channels);
    for (std::uint32_t i = 0; i < channels; ++i)
    {
      Channel channel;
      channel.world.position = GetPosition(r, snapshot.origin);
      auto& q                = channel.world.rotation;
      q                      = {
          r.Get<std::int16_t>() / 32767.0f,
          r.Get<std::int16_t>() / 32767.0f,
          r.Get<std::int16_t>() / 32767.0f,
          r.Get<std::int16_t>() / 32767.0f
      };
      const auto length = std::sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
      if (!std::isfinite(length) || length < 0.99f || length > 1.01f) return Fail(Failure::InvalidNumber, "pose.rotation");
      q.x                 /= length;
      q.y                 /= length;
      q.z                 /= length;
      q.w                 /= length;
      channel.world.scale  = r.Get<std::uint16_t>() / 1024.f;
      const auto hidden    = r.Get<std::uint8_t>();
      if (hidden > 1) return Fail(Failure::InvalidFormat, "pose.hidden");
      channel.hidden = hidden != 0;
      snapshot.channels.push_back(channel);
    }

    for (std::uint32_t i = 0; i < bounds; ++i)
      snapshot.bounds.push_back({GetPosition(r, snapshot.origin), r.Get<float>()});

    if (auto end = r.End(); !end) return std::unexpected(end.error());
    if (auto valid = ValidateSnapshot(snapshot, asset.Layout()); !valid) return std::unexpected(valid.error());
    return snapshot;
  }

  Result<Snapshot> ReadSnapshot(std::span<const std::uint8_t> compressed, const ValidatedAsset& asset, const Limits& limits)
  {
    return DecodeSnapshot(compressed, asset, limits, false);
  }
#ifdef DREAMSLEEVE_DIAGNOSTICS
  Result<Snapshot> ReadRecordedSnapshot(std::span<const std::uint8_t> compressed, const ValidatedAsset& asset, const Limits& limits)
  {
    return DecodeSnapshot(compressed, asset, limits, true);
  }
#endif

}
