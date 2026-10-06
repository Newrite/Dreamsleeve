import std;
import Dreamsleeve.Client.Diagnostics.PhantomReplay;
import Dreamsleeve.Client.ProtocolCodec;

namespace Dreamsleeve::Client::Diagnostics
{
  namespace
  {

    struct Cursor
    {
      std::span<const std::uint8_t> bytes;
      std::size_t                   at{};

      std::span<const std::uint8_t> Take(std::size_t n)
      {
        if (n > bytes.size() - at) throw std::runtime_error("archive.truncated-record");
        const auto out  = bytes.subspan(at, n);
        at             += n;
        return out;
      }

      template <class T>
      T Get()
      {
        std::array<std::uint8_t, sizeof(T)> value;
        std::ranges::copy(Take(sizeof(T)), value.begin());
        return std::bit_cast<T>(value);
      }
    };

    P::Bytes ReadBytes(std::ifstream& input, std::size_t n)
    {
      P::Bytes bytes(n);
      if (!input.read(reinterpret_cast<char*>(bytes.data()), n)) throw std::runtime_error("archive.truncated-file");
      return bytes;
    }

    void Header(std::ifstream& input)
    {
      const auto                        bytes = ReadBytes(input, 20);
      Cursor                            r{bytes};
      const std::array<std::uint8_t, 8> magic{'D', 'L', 'P', 'D', 'I', 'A', 'G', '2'};
      if (!std::ranges::equal(r.Take(8), magic) || r.Get<std::uint32_t>() != 2) throw std::runtime_error("archive.version");
      const auto protocol = r.Get<std::uint32_t>();
      if ((protocol != 22 && protocol != Wire::Version) || r.Get<std::uint32_t>() != P::AssetVersion)
        throw std::runtime_error("archive.version");
    }

    struct Record
    {
      std::uint32_t kind{}, bytes{};
    };

    std::optional<Record> Next(std::ifstream& input)
    {
      if (input.peek() == std::char_traits<char>::eof()) return {};
      const auto bytes = ReadBytes(input, 8);
      Cursor     r{bytes};
      Record     record{r.Get<std::uint32_t>(), r.Get<std::uint32_t>()};
      if (record.kind < 1 || record.kind > 6 || record.bytes > P::Limits{}.compressedAssetBytes + 1024ULL)
        throw std::runtime_error("archive.record-header");
      return record;
    }

    std::filesystem::path SelectArchive(const std::filesystem::path& root, std::uint32_t scenario)
    {
      // Offline analysis may select one exact recording; the same Load path
      // still checks its header, every model hash and every production pose.
      if (std::filesystem::is_regular_file(root)) return root;
      if (std::filesystem::is_regular_file(root / "capture.phdiag")) return root / "capture.phdiag";
      std::vector<std::filesystem::path> files;
      if (std::filesystem::exists(root))
        for (const auto& entry : std::filesystem::directory_iterator(root))
          if (entry.is_directory() && entry.path().filename().string().contains(std::format("-{}-", Scenarios[scenario])))
          {
            auto file = entry.path() / "capture.phdiag";
            if (std::filesystem::is_regular_file(file)) files.push_back(std::move(file));
          }
      std::ranges::sort(files, std::greater{});
      for (const auto& file : files)
      {
        std::ifstream input(file, std::ios::binary);
        Header(input);
        while (auto record = Next(input))
        {
          if (record->kind == 2) return file;
          input.seekg(record->bytes, std::ios::cur);
          if (!input) throw std::runtime_error("archive.seek");
        }
      }
      throw std::runtime_error("Нет завершённой записи с позами для выбранного сценария");
    }

  }

  struct ReplayReader::State
  {
    mutable std::mutex      mutex;
    std::condition_variable wake;
    ReplayLoadStatus        status;
    std::deque<ReplayFrame> queue;
    std::filesystem::path   root;
    std::uint32_t           scenario{};
    bool                    requested{}, cancelled{}, shutdown{};
    std::jthread            thread;

    State() : thread([this] { Run(); }) {}

    bool Cancelled()
    {
      std::lock_guard lock(mutex);
      return cancelled || shutdown;
    }

    void Load()
    {
      const auto file = SelectArchive(root, scenario);
      {
        std::lock_guard lock(mutex);
        status.directory = file.parent_path().string();
      }
      std::ifstream input(file, std::ios::binary);
      Header(input);
      std::shared_ptr<const P::ValidatedAsset> asset;
      P::Generation                            generation;
      std::uint64_t                            lastTime = 0, frames = 0;
      while (!Cancelled())
      {
        auto record = Next(input);
        if (!record) break;
        if (record->kind != 1 && record->kind != 2)
        {
          input.seekg(record->bytes, std::ios::cur);
          if (!input) throw std::runtime_error("archive.seek");
          continue;
        }
        const auto bytes = ReadBytes(input, record->bytes);
        Cursor     r{bytes};
        const auto start = Clock::now();
        if (record->kind == 1)
        {
          generation     = {r.Get<std::uint64_t>()};
          const auto raw = r.Get<std::uint32_t>(), size = r.Get<std::uint32_t>();
          const auto digest = r.Take(32), compressed = r.Take(size);
          auto       hash = P::Hash(compressed);
          if (r.at != bytes.size() || !hash || !std::ranges::equal(digest, *hash)) throw std::runtime_error("archive.model-hash");
          auto decoded = P::ReadAsset(compressed, raw, CaptureLimits());
          if (!decoded) throw std::runtime_error(decoded.error().field);
          asset = std::make_shared<const P::ValidatedAsset>(std::move(*decoded));
          std::lock_guard lock(mutex);
          ++status.models;
        }
        else
        {
          if (!asset) throw std::runtime_error("archive.missing-model");
          r.Take(57);  // capture timing, camera and actor movement; not a rendering shortcut.
          const auto original = r.Get<std::uint32_t>(), raw = r.Get<std::uint32_t>(), size = r.Get<std::uint32_t>();
          r.Take(original);
          r.Take(raw);
          const auto compressed = r.Take(size);
          if (r.at != bytes.size()) throw std::runtime_error("archive.sample-length");
          auto decoded = P::ReadRecordedSnapshot(compressed, *asset, CaptureLimits());
          if (!decoded) throw std::runtime_error(decoded.error().field);
          if (decoded->generation != generation || (frames && decoded->sampledAtUs <= lastTime))
            throw std::runtime_error("archive.sample-order");
          lastTime                  = decoded->sampledAtUs;
          auto             pose     = std::make_shared<const P::Snapshot>(std::move(*decoded));
          const auto       decodeMs = std::chrono::duration<double, std::milli>(Clock::now() - start).count();
          std::unique_lock lock(mutex);
          wake.wait(lock, [&] { return cancelled || shutdown || queue.size() < 4; });
          if (cancelled || shutdown) return;
          queue.push_back({asset, std::move(pose)});
          ++status.frames;
          ++frames;
          status.decodeMs = std::max(status.decodeMs, decodeMs);
        }
      }
      if (!Cancelled() && !frames) throw std::runtime_error("archive.no-samples");
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
        std::string error;
        try
        {
          Load();
        }
        catch (const std::exception& e)
        {
          error = e.what();
        }
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

  bool ReplayReader::Start(std::filesystem::path root, std::uint32_t scenario)
  {
    std::lock_guard lock(state_->mutex);
    if (state_->shutdown || state_->status.busy || root.empty() || scenario >= Scenarios.size()) return false;
    state_->queue.clear();
    state_->status      = {};
    state_->status.busy = true;
    state_->root        = std::move(root);
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
