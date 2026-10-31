export module Dreamsleeve.Client.Phantom.NifOutput;
import std;

export namespace Dreamsleeve::Client::Phantom
{

  // Seekable output for native NiStream, which backpatches the block-size table.
  // The completed vector is transferred to Asset without a second full copy.
  class NifOutput
  {
public:

    enum class Error
    {
      None,
      Limit,
      Seek,
      UnsupportedOperation
    };

private:

    std::vector<std::uint8_t> bytes;
    std::uint32_t             position{};
    std::uint32_t             maximum;
    Error                     failure{Error::None};

public:

    explicit NifOutput(std::uint32_t limit, std::uint32_t previousSize = 0) : maximum(limit)
    {
      // Small equipment changes should not immediately invalidate the previous
      // size hint. This headroom affects allocation only, never the asset limit.
      const auto hint = std::uint64_t(previousSize) + previousSize / 16;
      bytes.reserve(static_cast<std::size_t>(std::min<std::uint64_t>(hint, limit)));
    }

    bool Good() const noexcept
    {
      return failure == Error::None;
    }

    Error Status() const noexcept
    {
      return failure;
    }

    void Reject() noexcept
    {
      if (Good()) failure = Error::UnsupportedOperation;
    }

    std::uint32_t Position() const noexcept
    {
      return position;
    }

    std::span<const std::uint8_t> Bytes() const noexcept
    {
      return bytes;
    }

    bool Seek(std::int32_t delta) noexcept
    {
      const auto next = std::int64_t(position) + delta;
      if (!Good()) return false;
      if (next < 0 || next > maximum)
      {
        failure = Error::Seek;
        return false;
      }
      position = static_cast<std::uint32_t>(next);
      return true;
    }

    std::uint32_t Write(std::span<const std::uint8_t> data)
    {
      if (!Good()) return 0;
      if (data.size() > maximum - position)
      {
        failure = Error::Limit;
        return 0;
      }
      if (data.empty()) return 0;

      const auto end = std::size_t(position) + data.size();
      if (end > bytes.capacity()) bytes.reserve(std::min<std::size_t>(maximum, std::max({end, bytes.capacity() * 2, std::size_t{1024}})));

      // Only gaps need zero initialization. Appended data is constructed directly
      // from the source; resize + memcpy would write every large block twice.
      if (position > bytes.size()) bytes.resize(position);
      const auto overlap = std::min(data.size(), bytes.size() - position);
      if (overlap) std::memcpy(bytes.data() + position, data.data(), overlap);
      bytes.insert(bytes.end(), data.begin() + overlap, data.end());
      position += static_cast<std::uint32_t>(data.size());
      return static_cast<std::uint32_t>(data.size());
    }

    std::vector<std::uint8_t> Take() &&
    {
      return std::move(bytes);
    }
  };

}
