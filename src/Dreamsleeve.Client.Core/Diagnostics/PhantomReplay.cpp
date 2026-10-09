import std;
import Dreamsleeve.Client.Diagnostics.PhantomReplay;
import Dreamsleeve.Client.ProtocolCodec;

#include "PhantomFiles.hpp"

namespace Dreamsleeve::Client::Diagnostics
{
  namespace
  {

    struct Cursor
    {
      std::span<const std::uint8_t> bytes;
      std::size_t                   at{};
      bool                          valid{true};

      std::span<const std::uint8_t> Take(std::size_t n)
      {
        if (!valid || n > bytes.size() - at)
        {
          valid = false;
          return {};
        }
        const auto out  = bytes.subspan(at, n);
        at             += n;
        return out;
      }

      template <class T>
      T Get()
      {
        std::array<std::uint8_t, sizeof(T)> value{};
        std::ranges::copy(Take(sizeof(T)), value.begin());
        return std::bit_cast<T>(value);
      }
    };

    P::Error ReadFailure(const std::ifstream& input)
    {
      if (input.bad() || !input.eof()) return {P::Failure::Storage, "archive.read"};
      return {P::Failure::InvalidFormat, "archive.truncated-file"};
    }

    P::Result<P::Bytes> ReadBytes(std::ifstream& input, std::size_t n)
    {
      P::Bytes bytes(n);
      if (!input.read(reinterpret_cast<char*>(bytes.data()), static_cast<std::streamsize>(n))) return std::unexpected(ReadFailure(input));
      return bytes;
    }

    P::Result<void> SkipBytes(std::ifstream& input, std::uint32_t n)
    {
      input.ignore(n);
      if (input.gcount() != n) return std::unexpected(ReadFailure(input));
      return {};
    }

    P::Result<void> Header(std::ifstream& input)
    {
      const auto bytes = ReadBytes(input, 20);
      if (!bytes) return std::unexpected(bytes.error());
      Cursor                            r{*bytes};
      const std::array<std::uint8_t, 8> magic{'D', 'L', 'P', 'D', 'I', 'A', 'G', '2'};
      if (!std::ranges::equal(r.Take(8), magic) || r.Get<std::uint32_t>() != 2)
        return std::unexpected(P::Error{P::Failure::InvalidFormat, "archive.version"});
      const auto protocol = r.Get<std::uint32_t>();
      if (
        (protocol != 22 && protocol != 23 && protocol != 24 && protocol != 25 && protocol != Wire::Version) ||
        r.Get<std::uint32_t>() != P::AssetVersion)
        return std::unexpected(P::Error{P::Failure::InvalidFormat, "archive.version"});
      return {};
    }

    struct Record
    {
      std::uint32_t kind{};
      std::uint32_t bytes{};
    };

    P::Result<std::optional<Record>> Next(std::ifstream& input)
    {
      if (input.peek() == std::char_traits<char>::eof())
      {
        if (input.bad() || !input.eof()) return std::unexpected(P::Error{P::Failure::Storage, "archive.read"});
        return {};
      }
      const auto bytes = ReadBytes(input, 8);
      if (!bytes) return std::unexpected(bytes.error());
      Cursor r{*bytes};
      Record record{r.Get<std::uint32_t>(), r.Get<std::uint32_t>()};
      if (record.kind < 1 || record.kind > 6 || record.bytes > P::Limits{}.compressedAssetBytes + 1024ULL)
        return std::unexpected(P::Error{P::Failure::InvalidFormat, "archive.record-header"});
      return std::optional<Record>{record};
    }

    P::Result<std::optional<std::filesystem::path>> SelectArchive(const std::filesystem::path& root, std::uint32_t scenario)
    {
      // Exact archives follow the same production hash/NIF/pose decoder boundary.
      auto exact = Files::IsRegularFile(root);
      if (!exact) return std::unexpected(exact.error());
      if (*exact) return std::optional{root};

      auto captured = Files::IsRegularFile(root / "capture.phdiag");
      if (!captured) return std::unexpected(captured.error());
      if (*captured) return std::optional{root / "capture.phdiag"};

      std::error_code error;
      if (!std::filesystem::exists(root, error))
      {
        if (error) return std::unexpected(P::Error{P::Failure::Storage, error.message()});
        return {};
      }
      std::filesystem::path::string_type token(1, '-');
      for (const auto c : Scenarios[scenario])
        token.push_back(static_cast<std::filesystem::path::value_type>(c));
      token.push_back('-');
      std::vector<std::filesystem::path>  files;
      std::filesystem::directory_iterator cursor(root, error), end;
      if (error) return std::unexpected(P::Error{P::Failure::Storage, error.message()});
      while (cursor != end)
      {
        const auto& entry     = *cursor;
        const auto  directory = entry.is_directory(error);
        if (error) return std::unexpected(P::Error{P::Failure::Storage, error.message()});
        if (directory && entry.path().filename().native().find(token) != std::filesystem::path::string_type::npos)
        {
          auto file    = entry.path() / "capture.phdiag";
          auto regular = Files::IsRegularFile(file);
          if (!regular) return std::unexpected(regular.error());
          if (*regular) files.push_back(std::move(file));
        }
        cursor.increment(error);
        if (error) return std::unexpected(P::Error{P::Failure::Storage, error.message()});
      }
      std::ranges::sort(files, std::greater{});

      for (const auto& file : files)
      {
        std::ifstream input(file, std::ios::binary);
        if (!input) return std::unexpected(P::Error{P::Failure::Storage, "archive.open"});
        if (auto header = Header(input); !header) return std::unexpected(header.error());
        for (;;)
        {
          auto next = Next(input);
          if (!next) return std::unexpected(next.error());
          if (!*next) break;
          const auto& record = *next;
          if (record->kind == 2) return std::optional{file};
          if (auto skipped = SkipBytes(input, record->bytes); !skipped) return std::unexpected(skipped.error());
        }
      }
      return {};
    }

  }

  struct ReplayReader::State
  {
    mutable std::mutex      mutex;
    std::condition_variable wake;
    ReplayLoadStatus        status;
    std::deque<ReplayFrame> queue;
    std::filesystem::path   root;
    std::filesystem::path   fallback;
    std::uint32_t           scenario{};

    bool requested{};
    bool cancelled{};
    bool shutdown{};
    std::jthread            thread;

    State() : thread([this] { Run(); }) {}

    bool Cancelled()
    {
      std::lock_guard lock(mutex);
      return cancelled || shutdown;
    }

    P::Result<void> Load()
    {
      auto file = SelectArchive(root, scenario);
      if (!file) return std::unexpected(file.error());
      if (!*file && !fallback.empty()) file = SelectArchive(fallback, scenario);
      if (!file) return std::unexpected(file.error());
      if (!*file) return std::unexpected(P::Error{P::Failure::InvalidFormat, "Нет завершённой записи с позами для выбранного сценария"});

      auto display = Files::DisplayPath((*file)->parent_path());
      if (!display) return std::unexpected(display.error());
      {
        std::lock_guard lock(mutex);
        status.directory = std::move(*display);
      }

      std::ifstream input(**file, std::ios::binary);
      if (!input) return std::unexpected(P::Error{P::Failure::Storage, "archive.open"});
      if (auto header = Header(input); !header) return std::unexpected(header.error());

      std::shared_ptr<const P::ValidatedAsset> asset;
      P::Generation                            generation;
      std::uint64_t                            lastTime = 0;
      std::uint64_t                            frames   = 0;

      while (!Cancelled())
      {
        auto next = Next(input);
        if (!next) return std::unexpected(next.error());
        if (!*next) break;
        const auto& record = *next;
        if (record->kind != 1 && record->kind != 2)
        {
          if (auto skipped = SkipBytes(input, record->bytes); !skipped) return std::unexpected(skipped.error());
          continue;
        }

        const auto bytes = ReadBytes(input, record->bytes);
        if (!bytes) return std::unexpected(bytes.error());
        Cursor     r{*bytes};
        const auto start = Clock::now();
        if (record->kind == 1)
        {
          generation     = {r.Get<std::uint64_t>()};
          const auto raw = r.Get<std::uint32_t>(), size = r.Get<std::uint32_t>();
          const auto digest = r.Take(32), compressed = r.Take(size);
          auto       hash = P::Hash(compressed);
          if (!hash) return std::unexpected(hash.error());
          if (!r.valid || r.at != bytes->size() || !std::ranges::equal(digest, *hash))
            return std::unexpected(P::Error{P::Failure::InvalidFormat, "archive.model-hash"});
          auto decoded = P::ReadAsset(compressed, raw, CaptureLimits());
          if (!decoded) return std::unexpected(decoded.error());
          asset = std::make_shared<const P::ValidatedAsset>(std::move(*decoded));
          std::lock_guard lock(mutex);
          ++status.models;
        }
        else
        {
          if (!asset) return std::unexpected(P::Error{P::Failure::InvalidFormat, "archive.missing-model"});
          r.Take(57);  // capture timing, camera and actor movement; not a rendering shortcut.
          const auto original = r.Get<std::uint32_t>(), raw = r.Get<std::uint32_t>(), size = r.Get<std::uint32_t>();
          r.Take(original);
          r.Take(raw);
          const auto compressed = r.Take(size);
          if (!r.valid || r.at != bytes->size()) return std::unexpected(P::Error{P::Failure::InvalidFormat, "archive.sample-length"});
          auto decoded = P::ReadRecordedSnapshot(compressed, *asset, CaptureLimits());
          if (!decoded) return std::unexpected(decoded.error());
          if (decoded->generation != generation || (frames && decoded->sampledAtUs <= lastTime))
            return std::unexpected(P::Error{P::Failure::InvalidFormat, "archive.sample-order"});
          lastTime                  = decoded->sampledAtUs;
          auto             pose     = std::make_shared<const P::Snapshot>(std::move(*decoded));
          const auto       decodeMs = std::chrono::duration<double, std::milli>(Clock::now() - start).count();

          std::unique_lock lock(mutex);
          wake.wait(lock, [&] { return cancelled || shutdown || queue.size() < 4; });
          if (cancelled || shutdown) return {};
          queue.push_back({asset, std::move(pose)});
          ++status.frames;
          ++frames;
          status.decodeMs = std::max(status.decodeMs, decodeMs);
        }
      }
      if (!Cancelled() && !frames) return std::unexpected(P::Error{P::Failure::InvalidFormat, "archive.no-samples"});
      return {};
    }

    void Run()
    {
      for (;;)
      {
        {
          std::unique_lock lock(mutex);
          wake.wait(lock, [&] { return requested || shutdown; });
          if (shutdown) return;
          requested = false;
        }

        const auto      loaded = Load();
        std::string     error  = loaded ? std::string{} : loaded.error().field;
        std::lock_guard lock(mutex);
        status.busy     = false;
        status.complete = !cancelled && error.empty();
        if (!cancelled) status.error = std::move(error);
      }
    }
  };

  ReplayReader::ReplayReader() : state_(std::make_unique<State>()) {}

  ReplayReader::~ReplayReader()
  {
    Shutdown();
  }

  bool ReplayReader::Start(std::filesystem::path root, std::uint32_t scenario, std::filesystem::path fallback)
  {
    std::lock_guard lock(state_->mutex);
    if (state_->shutdown || state_->status.busy || root.empty() || scenario >= Scenarios.size()) return false;

    state_->queue.clear();
    state_->status      = {};
    state_->status.busy = true;
    state_->root        = std::move(root);
    state_->fallback    = std::move(fallback);
    state_->scenario    = scenario;
    state_->cancelled   = false;
    state_->requested   = true;
    state_->wake.notify_one();
    return true;
  }

  void ReplayReader::Stop()
  {
    std::lock_guard lock(state_->mutex);
    state_->cancelled = true;
    state_->queue.clear();
    state_->wake.notify_one();
  }

  void ReplayReader::Shutdown()
  {
    if (!state_->thread.joinable()) return;
    {
      std::lock_guard lock(state_->mutex);
      state_->shutdown = state_->cancelled = true;
      state_->queue.clear();
    }
    state_->wake.notify_one();
    state_->thread.join();
  }

  ReplayLoadStatus ReplayReader::Read() const
  {
    std::lock_guard lock(state_->mutex);
    auto            status = state_->status;
    status.complete        = status.complete && state_->queue.empty();
    return status;
  }

  std::optional<ReplayFrame> ReplayReader::Take()
  {
    std::lock_guard lock(state_->mutex);
    if (state_->queue.empty()) return {};
    auto frame = std::move(state_->queue.front());
    state_->queue.pop_front();
    state_->wake.notify_one();
    return frame;
  }

}
