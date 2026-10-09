#include <zstd.h>
import std;
import Dreamsleeve.Client.Phantom.Delta;

namespace Dreamsleeve::Client::Phantom::Delta
{
  namespace
  {

    auto Fail(std::string field)
    {
      return std::unexpected(Error{Failure::InvalidFormat, std::move(field)});
    }

    Result<Bytes> Raw(std::span<const std::uint8_t> bytes, const Limits& limits)
    {
      if (bytes.empty() || bytes.size() > limits.compressedAssetBytes) return Fail("delta.compressed");
      const auto size = ZSTD_getFrameContentSize(bytes.data(), bytes.size());
      if (!size || size > limits.assetBytes || ZSTD_findFrameCompressedSize(bytes.data(), bytes.size()) != bytes.size())
        return Fail("delta.frame");
      Bytes                                                raw(static_cast<std::size_t>(size));
      std::unique_ptr<ZSTD_DCtx, decltype(&ZSTD_freeDCtx)> ctx(ZSTD_createDCtx(), ZSTD_freeDCtx);
      if (!ctx || ZSTD_isError(ZSTD_DCtx_setParameter(ctx.get(), ZSTD_d_windowLogMax, 27))) return Fail("delta.context");
      if (ZSTD_decompressDCtx(ctx.get(), raw.data(), raw.size(), bytes.data(), bytes.size()) != raw.size()) return Fail("delta.base");
      return raw;
    }

  }

  Result<Bytes> Create(std::span<const std::uint8_t> base, std::span<const std::uint8_t> target, const Limits& limits)
  {
    auto old = Raw(base, limits);
    if (!old) return std::unexpected(old.error());
    auto next = Raw(target, limits);
    if (!next) return std::unexpected(next.error());

    std::unique_ptr<ZSTD_CCtx, decltype(&ZSTD_freeCCtx)> ctx(ZSTD_createCCtx(), ZSTD_freeCCtx);
    if (!ctx) return Fail("delta.context");
    for (
      auto [parameter, value] : {
          std::pair{ZSTD_c_compressionLevel, 3},
          {ZSTD_c_windowLog, 27},
          {ZSTD_c_enableLongDistanceMatching, 1}
      })
      if (ZSTD_isError(ZSTD_CCtx_setParameter(ctx.get(), parameter, value))) return Fail("delta.parameter");
    if (ZSTD_isError(ZSTD_CCtx_refPrefix(ctx.get(), old->data(), old->size()))) return Fail("delta.prefix");

    Bytes      output(std::min<std::size_t>(ZSTD_compressBound(next->size()), limits.compressedAssetBytes));
    const auto count = ZSTD_compress2(ctx.get(), output.data(), output.size(), next->data(), next->size());
    if (ZSTD_isError(count)) return Fail("delta.encode");
    output.resize(count);
    return output;
  }

  Result<Bytes> Apply(
    std::span<const std::uint8_t> base,
    std::span<const std::uint8_t> patch,
    std::uint32_t                 targetRawBytes,
    const Limits&                 limits)
  {
    if (
      !targetRawBytes || targetRawBytes > limits.assetBytes || patch.empty() || patch.size() > limits.compressedAssetBytes ||
      ZSTD_getFrameContentSize(patch.data(), patch.size()) != targetRawBytes ||
      ZSTD_findFrameCompressedSize(patch.data(), patch.size()) != patch.size())
      return Fail("delta.frame");

    auto old = Raw(base, limits);
    if (!old) return std::unexpected(old.error());
    std::unique_ptr<ZSTD_DCtx, decltype(&ZSTD_freeDCtx)> ctx(ZSTD_createDCtx(), ZSTD_freeDCtx);
    if (
      !ctx || ZSTD_isError(ZSTD_DCtx_setParameter(ctx.get(), ZSTD_d_windowLogMax, 27)) ||
      ZSTD_isError(ZSTD_DCtx_refPrefix(ctx.get(), old->data(), old->size())))
      return Fail("delta.context");
    Bytes raw(targetRawBytes);
    if (ZSTD_decompressDCtx(ctx.get(), raw.data(), raw.size(), patch.data(), patch.size()) != raw.size()) return Fail("delta.decode");

    Bytes      output(std::min<std::size_t>(ZSTD_compressBound(raw.size()), limits.compressedAssetBytes));
    const auto size = ZSTD_compress(output.data(), output.size(), raw.data(), raw.size(), 3);
    if (ZSTD_isError(size)) return Fail("delta.canonical");
    output.resize(size);
    return output;
  }

}
