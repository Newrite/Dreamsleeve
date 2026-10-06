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

    constexpr std::uint32_t AssetMagic = 0x41504C44, PoseMagic = 0x50504C44;

    struct Invalid
    {
      Error error;
    };

    [[noreturn]] void Fail(Failure failure, std::string_view field)
    {
      throw Invalid{
          Error{failure, std::string(field)}
      };
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

  public:

      explicit Reader(std::span<const std::uint8_t> input) : bytes(input) {}

      std::size_t Remaining() const
      {
        return bytes.size() - at;
      }

      template <class T>
      T Get()
      {
        if (sizeof(T) > Remaining()) Fail(Failure::InvalidFormat, "truncated");
        std::array<std::uint8_t, sizeof(T)> value;
        std::ranges::copy(bytes.subspan(at, sizeof(T)), value.begin());
        at += sizeof(T);
        return std::bit_cast<T>(value);
      }

      std::uint32_t Count(std::uint32_t maximum, std::size_t stride = 1)
      {
        const auto count = Get<std::uint32_t>();
        if (count > maximum || count > Remaining() / stride) Fail(Failure::LimitExceeded, "count");
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
        if (count > Remaining()) Fail(Failure::InvalidFormat, "data");
        Bytes result(bytes.begin() + at, bytes.begin() + at + count);
        at += count;
        return result;
      }

      void End()
      {
        if (Remaining() != 0) Fail(Failure::InvalidFormat, "trailing");
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

    void ValidateSnapshot(const Snapshot& snapshot, const Asset& asset)
    {
      if (
        !snapshot.generation.value || !snapshot.sequence.value || !snapshot.context || snapshot.sampledAtUs > MaximumSampleTime ||
        !Finite(snapshot.origin) || snapshot.channels.size() != asset.nodes.size() || snapshot.bounds.size() != asset.geometry.size())
        Fail(Failure::InvalidFormat, "pose.shape");
      for (const auto& channel : snapshot.channels)
        if (!Valid(channel.world)) Fail(Failure::InvalidNumber, "pose.transform");
      for (const auto& bound : snapshot.bounds)
        if (!Finite(bound.center) || !std::isfinite(bound.radius) || bound.radius < 0 || bound.radius > 100000)
          Fail(Failure::InvalidNumber, "pose.bound");
      std::unordered_set<std::uint32_t> seen;
      for (const auto& deformation : snapshot.deformations)
      {
        if (deformation.geometry >= asset.geometry.size() || !seen.insert(deformation.geometry).second)
          Fail(Failure::InvalidLink, "deformation.geometry");
        const auto& mesh = asset.geometry[deformation.geometry];
        if (!mesh.dynamic || deformation.positions.size() != mesh.vertices.size() || deformation.normals.size() != mesh.vertices.size())
          Fail(Failure::InvalidGeometry, "deformation.shape");
        for (const auto& p : deformation.positions)
          if (!Finite(p)) Fail(Failure::InvalidNumber, "deformation.position");
        for (const auto& n : deformation.normals)
          if (!Finite(n)) Fail(Failure::InvalidNumber, "deformation.normal");
      }
      for (std::uint32_t i = 0; i < asset.geometry.size(); ++i)
        if (asset.geometry[i].dynamic && !seen.contains(i)) Fail(Failure::InvalidGeometry, "deformation.missing");
    }

    std::int16_t Quantize(float value, float step)
    {
      const auto quantized = std::round(value / step);
      if (!std::isfinite(quantized) || quantized < -32767 || quantized > 32767) Fail(Failure::LimitExceeded, "pose.range");
      return static_cast<std::int16_t>(quantized);
    }

    void PutPosition(Writer& writer, const Vec3& v, const Vec3& origin)
    {
      writer.Put(Quantize(v.x - origin.x, 0.125f));
      writer.Put(Quantize(v.y - origin.y, 0.125f));
      writer.Put(Quantize(v.z - origin.z, 0.125f));
    }

    Vec3 GetPosition(Reader& reader, const Vec3& origin)
    {
      return {
          origin.x + reader.Get<std::int16_t>() * 0.125f,
          origin.y + reader.Get<std::int16_t>() * 0.125f,
          origin.z + reader.Get<std::int16_t>() * 0.125f
      };
    }

  }

  Result<void> CheckSnapshot(const Snapshot& snapshot, const ValidatedAsset& asset)
  {
    try
    {
      ValidateSnapshot(snapshot, asset.Value());
      return {};
    }
    catch (const Invalid& invalid)
    {
      return std::unexpected(invalid.error);
    }
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
    w.Put(static_cast<std::uint32_t>(model.nodes.size()));
    w.Put(static_cast<std::uint32_t>(model.geometry.size()));
    for (const auto& node : model.nodes)
    {
      w.Put(node.parent.value);
      w.TransformValue(node.local);
    }
    for (const auto& mesh : model.geometry)
    {
      w.Put(mesh.node.value);
      w.Put(static_cast<std::uint32_t>(mesh.vertices.size()));
      w.Put(static_cast<std::uint32_t>(mesh.indices.size()));
      w.Put<std::uint8_t>(
        (mesh.skin ? 1 : 0) | (mesh.mask ? 2 : 0) | (mesh.alphaBlend ? 4 : 0) | (mesh.doubleSided ? 8 : 0) | (mesh.dynamic ? 16 : 0));
      w.Put(mesh.alphaThreshold);
      for (const auto& v : mesh.vertices)
      {
        w.Vector(v.position);
        w.Vector(v.normal);
        w.Vector(v.tangent);
        w.Put(v.u);
        w.Put(v.v);
        for (auto c : v.color)
          w.Put(c);
        for (auto weight : v.weights)
          w.Put(weight);
        for (auto bone : v.bones)
          w.Put(bone);
      }
      for (auto index : mesh.indices)
        w.Put(index);
      if (mesh.skin)
      {
        w.Put(mesh.skin->root.value);
        w.TransformValue(mesh.skin->worldToSkin);
        w.Put(static_cast<std::uint32_t>(mesh.skin->bones.size()));
        for (const auto& bone : mesh.skin->bones)
        {
          w.Put(bone.node.value);
          w.TransformValue(bone.bind);
          w.BoundValue(bone.bound);
        }
      }
      if (mesh.mask)
      {
        w.Put(mesh.mask->width);
        w.Put(mesh.mask->height);
        w.bytes.insert(w.bytes.end(), mesh.mask->pixels.begin(), mesh.mask->pixels.end());
      }
    }
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
    try
    {
      Reader r(*raw);
      if (r.Get<std::uint32_t>() != AssetMagic || r.Get<std::uint32_t>() != AssetVersion) Fail(Failure::InvalidFormat, "asset.version");
      Asset      asset;
      const auto nodes    = r.Count(limits.nodes, 36);
      const auto geometry = r.Count(limits.geometry, 14);
      asset.nodes.reserve(nodes);
      for (std::uint32_t i = 0; i < nodes; ++i)
        asset.nodes.push_back({NodeId{r.Get<std::uint32_t>()}, r.TransformValue()});
      asset.geometry.reserve(geometry);
      std::uint64_t totalVertices = 0, totalMasks = 0;
      for (std::uint32_t i = 0; i < geometry; ++i)
      {
        Geometry mesh;
        mesh.node            = {r.Get<std::uint32_t>()};
        const auto vertices  = r.Count(65535, 72);
        const auto indices   = r.Count(limits.assetBytes / 2, 2);
        totalVertices       += vertices;
        if (totalVertices > limits.vertices) Fail(Failure::LimitExceeded, "vertices");
        const auto flags = r.Get<std::uint8_t>();
        if (flags & ~31) Fail(Failure::InvalidFormat, "geometry.flags");
        mesh.alphaThreshold = r.Get<std::uint8_t>();
        mesh.alphaBlend     = (flags & 4) != 0;
        mesh.doubleSided    = (flags & 8) != 0;
        mesh.dynamic        = (flags & 16) != 0;
        mesh.vertices.reserve(vertices);
        for (std::uint32_t v = 0; v < vertices; ++v)
        {
          Vertex vertex;
          vertex.position = r.Vector();
          vertex.normal   = r.Vector();
          vertex.tangent  = r.Vector();
          vertex.u        = r.Get<float>();
          vertex.v        = r.Get<float>();
          for (auto& c : vertex.color)
            c = r.Get<std::uint8_t>();
          for (auto& weight : vertex.weights)
            weight = r.Get<float>();
          for (auto& bone : vertex.bones)
            bone = r.Get<std::uint16_t>();
          mesh.vertices.push_back(vertex);
        }
        mesh.indices.reserve(indices);
        for (std::uint32_t n = 0; n < indices; ++n)
          mesh.indices.push_back(r.Get<std::uint16_t>());
        if (flags & 1)
        {
          Skin skin;
          skin.root        = {r.Get<std::uint32_t>()};
          skin.worldToSkin = r.TransformValue();
          const auto bones = r.Count(limits.bonesPerSkin, 52);
          skin.bones.reserve(bones);
          for (std::uint32_t n = 0; n < bones; ++n)
            skin.bones.push_back({NodeId{r.Get<std::uint32_t>()}, r.TransformValue(), r.BoundValue()});
          mesh.skin = std::move(skin);
        }
        if (flags & 2)
        {
          AlphaMask mask;
          mask.width         = r.Get<std::uint32_t>();
          mask.height        = r.Get<std::uint32_t>();
          const auto pixels  = static_cast<std::uint64_t>(mask.width) * mask.height;
          totalMasks        += pixels;
          if (
            !mask.width || !mask.height || mask.width > limits.maskDimension || mask.height > limits.maskDimension ||
            totalMasks > limits.maskBytes)
            Fail(Failure::LimitExceeded, "mask");
          mask.pixels = r.Data(static_cast<std::uint32_t>(pixels));
          mesh.mask   = std::move(mask);
        }
        asset.geometry.push_back(std::move(mesh));
      }
      r.End();
      return ValidatedAsset::Parse(std::move(asset), limits);
    }
    catch (const Invalid& invalid)
    {
      return std::unexpected(invalid.error);
    }
  }

  Result<Bytes> WriteSnapshot(const Snapshot& snapshot, const ValidatedAsset& asset, const Limits& limits)
  {
    try
    {
      ValidateSnapshot(snapshot, asset.Value());
      Writer w;
      w.Put(PoseMagic);
      w.Put(AssetVersion);
      w.Put(snapshot.generation.value);
      w.Put(snapshot.sequence.value);
      w.Put(snapshot.context);
      w.Put(snapshot.sampledAtUs);
      w.Vector(snapshot.origin);
      w.Put(static_cast<std::uint32_t>(snapshot.channels.size()));
      w.Put(static_cast<std::uint32_t>(snapshot.bounds.size()));
      w.Put(static_cast<std::uint32_t>(snapshot.deformations.size()));
      for (const auto& channel : snapshot.channels)
      {
        PutPosition(w, channel.world.position, snapshot.origin);
        const auto& q = channel.world.rotation;
        for (auto component : {q.x, q.y, q.z, q.w})
          w.Put(Quantize(component, 1.0f / 32767));
        w.Put(channel.world.scale);
        w.Put<std::uint8_t>(channel.hidden ? 1 : 0);
      }
      for (const auto& bound : snapshot.bounds)
      {
        PutPosition(w, bound.center, snapshot.origin);
        w.Put(bound.radius);
      }
      for (const auto& deformation : snapshot.deformations)
      {
        w.Put(deformation.geometry);
        w.Put(static_cast<std::uint32_t>(deformation.positions.size()));
        for (const auto& p : deformation.positions)
          w.Vector(p);
        for (const auto& n : deformation.normals)
          w.Vector(n);
      }
      if (w.bytes.size() > limits.poseBytes) Fail(Failure::LimitExceeded, "pose.bytes");
      return Compress(w.bytes, limits.compressedPoseBytes, 1);
    }
    catch (const Invalid& invalid)
    {
      return std::unexpected(invalid.error);
    }
  }

  Result<Snapshot> ReadSnapshot(std::span<const std::uint8_t> compressed, const ValidatedAsset& asset, const Limits& limits)
  {
    if (compressed.size() > limits.compressedPoseBytes) return std::unexpected(Error{Failure::LimitExceeded, "pose.compressed"});
    auto raw = Decompress(compressed, limits.poseBytes);
    if (!raw) return std::unexpected(raw.error());
    try
    {
      Reader r(*raw);
      if (r.Get<std::uint32_t>() != PoseMagic || r.Get<std::uint32_t>() != AssetVersion) Fail(Failure::InvalidFormat, "pose.version");
      Snapshot snapshot;
      snapshot.generation     = {r.Get<std::uint64_t>()};
      snapshot.sequence       = {r.Get<std::uint64_t>()};
      snapshot.context        = r.Get<std::uint64_t>();
      snapshot.sampledAtUs    = r.Get<std::uint64_t>();
      snapshot.origin         = r.Vector();
      const auto channels     = r.Count(limits.nodes, 19);
      const auto bounds       = r.Count(limits.geometry, 10);
      const auto deformations = r.Count(limits.geometry, 8);
      if (channels != asset.Value().nodes.size() || bounds != asset.Value().geometry.size()) Fail(Failure::InvalidFormat, "pose.counts");
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
        if (!std::isfinite(length) || length < 0.99f || length > 1.01f) Fail(Failure::InvalidNumber, "pose.rotation");
        q.x                 /= length;
        q.y                 /= length;
        q.z                 /= length;
        q.w                 /= length;
        channel.world.scale  = r.Get<float>();
        const auto hidden    = r.Get<std::uint8_t>();
        if (hidden > 1) Fail(Failure::InvalidFormat, "pose.hidden");
        channel.hidden = hidden != 0;
        snapshot.channels.push_back(channel);
      }
      for (std::uint32_t i = 0; i < bounds; ++i)
        snapshot.bounds.push_back({GetPosition(r, snapshot.origin), r.Get<float>()});
      for (std::uint32_t i = 0; i < deformations; ++i)
      {
        Deformation d;
        d.geometry = r.Get<std::uint32_t>();
        if (d.geometry >= asset.Value().geometry.size()) Fail(Failure::InvalidLink, "deformation.geometry");
        const auto count = r.Count(static_cast<std::uint32_t>(asset.Value().geometry[d.geometry].vertices.size()), 24);
        if (count != asset.Value().geometry[d.geometry].vertices.size()) Fail(Failure::InvalidGeometry, "deformation.count");
        d.positions.reserve(count);
        d.normals.reserve(count);
        for (std::uint32_t n = 0; n < count; ++n)
          d.positions.push_back(r.Vector());
        for (std::uint32_t n = 0; n < count; ++n)
          d.normals.push_back(r.Vector());
        snapshot.deformations.push_back(std::move(d));
      }
      r.End();
      ValidateSnapshot(snapshot, asset.Value());
      return snapshot;
    }
    catch (const Invalid& invalid)
    {
      return std::unexpected(invalid.error);
    }
  }

}
