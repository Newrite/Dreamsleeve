export module Dreamsleeve.Client.Phantom.Cache;

import std;
export import Dreamsleeve.Client.Phantom.Codec;

export namespace Dreamsleeve::Client::Phantom
{

  enum class CacheWrite
  {
    Skipped,
    Stored
  };

  // ModelRun owns this adapter. Missing files are absence; corrupt files and
  // expected filesystem failures remain typed errors for the caller's policy.
  class Cache final
  {
    struct Entry
    {
      std::filesystem::path           path;
      std::uint64_t                   bytes;
      std::filesystem::file_time_type touched;
    };

    std::filesystem::path        directory;
    static constexpr std::size_t FileLimit = 4096;

    static Error Storage(std::string operation, const std::error_code& error)
    {
      if (error) operation += ": " + error.message();
      return {Failure::Storage, std::move(operation)};
    }

    Result<bool> DirectoryPresent() const
    {
      std::error_code error;
      const auto      status = std::filesystem::status(directory, error);
      if (error == std::errc::no_such_file_or_directory) return false;
      if (error) return std::unexpected(Storage("cache.directory", error));
      if (!std::filesystem::exists(status)) return false;
      if (!std::filesystem::is_directory(status)) return std::unexpected(Storage("cache.directory", {}));
      return true;
    }

    Result<void> Remove(const std::filesystem::path& path) const
    {
      std::error_code error;
      std::filesystem::remove(path, error);
      if (error) return std::unexpected(Storage("cache.remove", error));
      return {};
    }

public:

    explicit Cache(std::filesystem::path path) : directory(std::move(path)) {}

    Result<std::optional<std::uint32_t>> Inspect(const Digest& hash, std::uint32_t expectedSize = 0) const
    {
      if (directory.empty()) return std::nullopt;
      auto present = DirectoryPresent();
      if (!present) return std::unexpected(present.error());
      if (!*present) return std::nullopt;
      std::error_code error;
      const auto      size = std::filesystem::file_size(directory / (Hex(hash) + ".zst"), error);
      if (error == std::errc::no_such_file_or_directory) return std::nullopt;
      if (error) return std::unexpected(Storage("cache.size", error));
      if (!size || size > Limits{}.compressedAssetBytes || (expectedSize && size != expectedSize))
        return std::unexpected(Error{Failure::InvalidFormat, "cache.size"});
      return static_cast<std::uint32_t>(size);
    }

    Result<std::optional<Bytes>> Read(const Digest& hash, std::uint32_t expectedSize = 0) const
    {
      auto size = Inspect(hash, expectedSize);
      if (!size) return std::unexpected(size.error());
      if (!*size) return std::nullopt;
      Bytes         bytes(**size);
      std::ifstream input(directory / (Hex(hash) + ".zst"), std::ios::binary);
      if (!input.is_open()) return std::unexpected(Storage("cache.open", {}));
      input.read(reinterpret_cast<char*>(bytes.data()), static_cast<std::streamsize>(bytes.size()));
      if (input.bad()) return std::unexpected(Storage("cache.read", {}));
      if (input.gcount() != static_cast<std::streamsize>(bytes.size()))
        return std::unexpected(Error{Failure::InvalidFormat, "cache.truncated"});
      return bytes;
    }

    Result<void> Touch(const Digest& hash) const
    {
      if (directory.empty()) return {};
      std::error_code error;
      std::filesystem::last_write_time(directory / (Hex(hash) + ".zst"), std::filesystem::file_time_type::clock::now(), error);
      if (error) return std::unexpected(Storage("cache.touch", error));
      return {};
    }

    Result<void> Erase(const Digest& hash) const
    {
      if (directory.empty()) return {};
      return Remove(directory / (Hex(hash) + ".zst"));
    }

    Result<void> Trim(std::uint64_t budget) const
    {
      if (directory.empty()) return {};
      auto present = DirectoryPresent();
      if (!present) return std::unexpected(present.error());
      if (!*present) return {};
      std::error_code                     error;
      std::filesystem::directory_iterator cursor(directory, error), end;
      if (error == std::errc::no_such_file_or_directory) return {};
      if (error) return std::unexpected(Storage("cache.list", error));
      std::vector<Entry> files;
      std::uint64_t      size{};
      while (cursor != end)
      {
        const auto& file = *cursor;
        const auto  path = file.path();
        const auto  name = path.stem().native();
        const bool  owned =
          name.size() == 64 && std::ranges::all_of(name, [](auto c) { return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'); });
        if (owned)
        {
          const bool regular = file.is_regular_file(error);
          if (error) return std::unexpected(Storage("cache.type", error));
          if (regular && (path.extension() == ".partial" || (path.extension() == ".zst" && files.size() >= FileLimit)))
          {
            if (auto removed = Remove(path); !removed) return removed;
          }
          else if (regular && path.extension() == ".zst")
          {
            const auto bytes = file.file_size(error);
            if (error) return std::unexpected(Storage("cache.size", error));
            const auto touched = file.last_write_time(error);
            if (error) return std::unexpected(Storage("cache.time", error));
            if (bytes > std::numeric_limits<std::uint64_t>::max() - size)
              return std::unexpected(Error{Failure::LimitExceeded, "cache.bytes"});
            size += bytes;
            files.push_back({path, bytes, touched});
          }
        }
        cursor.increment(error);
        if (error) return std::unexpected(Storage("cache.next", error));
      }
      std::ranges::sort(files, {}, &Entry::touched);
      for (const auto& file : files)
      {
        if (size <= budget) break;
        if (auto removed = Remove(file.path); !removed) return removed;
        size -= file.bytes;
      }
      return {};
    }

    Result<CacheWrite> Save(const Digest& hash, std::span<const std::uint8_t> bytes, std::uint64_t budget) const
    {
      if (directory.empty() || !budget || bytes.size() > budget) return CacheWrite::Skipped;
      std::error_code error;
      std::filesystem::create_directories(directory, error);
      if (error) return std::unexpected(Storage("cache.directory", error));
      const auto path = directory / (Hex(hash) + ".zst"), temporary = directory / (Hex(hash) + ".partial");
      bool       written{};
      {
        std::ofstream out(temporary, std::ios::binary | std::ios::trunc);
        out.write(reinterpret_cast<const char*>(bytes.data()), static_cast<std::streamsize>(bytes.size()));
        out.flush();
        out.close();
        written = static_cast<bool>(out);
      }
      if (!written)
      {
        auto failure = Storage("cache.write", {});
        if (auto removed = Remove(temporary); !removed) failure.field += "; " + removed.error().field;
        return std::unexpected(std::move(failure));
      }
      std::filesystem::rename(temporary, path, error);
      if (error)
      {
        auto failure = Storage("cache.rename", error);
        if (auto removed = Remove(temporary); !removed) failure.field += "; " + removed.error().field;
        return std::unexpected(std::move(failure));
      }
      if (auto trimmed = Trim(budget); !trimmed) return std::unexpected(trimmed.error());
      return CacheWrite::Stored;
    }
  };

}
