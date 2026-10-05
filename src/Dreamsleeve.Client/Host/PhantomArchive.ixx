module;

#include <glaze/glaze.hpp>

export module Dreamsleeve.Host.PhantomArchive;

import std;

// Diagnostic files only. This is not a network protocol or a NiStream reader.
export namespace PhantomArchive
{

  constexpr std::uint32_t NoParent         = 0xFFFFFFFF;
  constexpr std::uint32_t Version          = 1;
  constexpr std::size_t   PoseBytes        = 73;  // 17 float32 + hidden uint8 + live parent uint32
  constexpr std::size_t   HeaderBytes      = 24;
  constexpr std::size_t   FrameHeaderBytes = 9;   // float64 time + camera/equipment flags

  bool Scenario(std::string_view value)
  {
    constexpr auto values = std::to_array<std::string_view>({"idle", "movement", "combat", "camera", "equipment", "mixed"});
    return std::ranges::find(values, value) != values.end();
  }

  struct Node
  {
    std::string   name, type, sourceName, sourceType, channel, boneName;
    std::uint32_t parent{NoParent}, boneIndex{}, vertices{}, triangles{};
    bool          geometry{}, excluded{};
  };

  struct Skin
  {
    std::uint32_t              geometry{}, root{NoParent};
    std::vector<std::uint32_t> bones;
  };

  struct Metadata
  {
    std::uint32_t                        version{Version};
    std::string                          runtime, scenario, stopReason;
    std::uint32_t                        rate{}, frameCount{}, cell{}, world{};
    double                               seconds{}, buildMs{}, sampleMedianMs{}, sampleP95Ms{}, sampleMaxMs{};
    std::uint64_t                        appearanceBytes{}, memoryPoseBytes{}, filePoseBytes{};
    std::map<std::string, std::uint64_t> cppSizes;
    std::map<std::string, std::uint64_t> observedOffsets;
    std::vector<Node>                    nodes;
    std::vector<Skin>                    skins;
  };

  struct Pose
  {
    std::array<float, 17> values{};  // rotation row-major, translation, scale, bound center, radius
    bool                  hidden{};
    std::uint32_t         parent{NoParent};
  };

  struct Frame
  {
    double            time{};
    std::vector<Pose> poses;
    std::uint8_t      flags{};
  };

  struct Capture
  {
    Metadata           metadata;
    std::vector<char>  appearance;
    std::vector<Frame> frames;
    std::string        path;
  };

  template <class T>
  T ReadScalar(std::istream& in)
  {
    std::array<char, sizeof(T)> bytes{};
    in.read(bytes.data(), bytes.size());
    if (!in) throw std::runtime_error("Truncated archive scalar");
    if constexpr (std::endian::native == std::endian::big) std::ranges::reverse(bytes);
    return std::bit_cast<T>(bytes);
  }

  std::vector<char> ReadFile(const std::filesystem::path& path, std::size_t limit)
  {
    const auto size = std::filesystem::file_size(path);
    if (!size || size > limit) throw std::runtime_error("Archive file size exceeds limit");
    std::vector<char> bytes(static_cast<std::size_t>(size));
    std::ifstream     in(path, std::ios::binary);
    in.exceptions(std::ios::failbit | std::ios::badbit);
    in.read(bytes.data(), bytes.size());
    return bytes;
  }

  Capture Read(const std::filesystem::path& directory)
  {
    if (!std::filesystem::is_regular_file(directory / "complete.txt")) throw std::runtime_error("Incomplete archive");
    Capture result;
    result.path     = directory.string();
    const auto json = ReadFile(directory / "metadata.json", 16 * 1024 * 1024);
    if (glz::read_json(result.metadata, std::string_view{json.data(), json.size()})) throw std::runtime_error("Invalid archive metadata");
    const auto& m = result.metadata;
    if (
      m.version != Version || m.nodes.empty() || m.nodes.size() > 4096 || m.frameCount < 2 || m.frameCount > 1001 ||
      (m.rate != 20 && m.rate != 40) || !Scenario(m.scenario) || !std::isfinite(m.seconds) || m.seconds <= 0 || m.seconds > 15.5)
      throw std::runtime_error("Invalid archive limits");
    if (m.nodes.size() * m.frameCount * sizeof(Pose) > 128 * 1024 * 1024) throw std::runtime_error("Archive poses exceed memory limit");
    for (std::size_t i = 0; i < m.nodes.size(); ++i)
      if (m.nodes[i].parent != NoParent && m.nodes[i].parent >= i) throw std::runtime_error("Invalid archive hierarchy");
    for (const auto& skin : m.skins)
    {
      if (
        skin.geometry >= m.nodes.size() || !m.nodes[skin.geometry].geometry || (skin.root != NoParent && skin.root >= m.nodes.size()) ||
        skin.bones.size() > 4096 || !std::ranges::all_of(skin.bones, [&](auto i) { return i < m.nodes.size(); }))
        throw std::runtime_error("Invalid archive skin links");
    }
    result.appearance = ReadFile(directory / "appearance.nif", 64 * 1024 * 1024);
    if (result.appearance.size() != m.appearanceBytes) throw std::runtime_error("Appearance length mismatch");
    const auto expected = HeaderBytes + m.frameCount * (FrameHeaderBytes + m.nodes.size() * PoseBytes);
    if (expected != m.filePoseBytes || std::filesystem::file_size(directory / "poses.bin") != expected)
      throw std::runtime_error("Pose length mismatch");
    std::ifstream in(directory / "poses.bin", std::ios::binary);
    in.exceptions(std::ios::failbit | std::ios::badbit);
    std::array<char, 8> magic;
    in.read(magic.data(), magic.size());
    if (
      std::string_view{magic.data(), magic.size()} != "DSPPOSE1" || ReadScalar<std::uint32_t>(in) != Version ||
      ReadScalar<std::uint32_t>(in) != m.nodes.size() || ReadScalar<std::uint32_t>(in) != m.frameCount ||
      ReadScalar<std::uint32_t>(in) != m.rate)
      throw std::runtime_error("Pose header mismatch");
    result.frames.reserve(m.frameCount);
    double previous = -1;
    for (std::uint32_t f = 0; f < m.frameCount; ++f)
    {
      Frame frame;
      frame.time  = ReadScalar<double>(in);
      frame.flags = ReadScalar<std::uint8_t>(in);
      if (!std::isfinite(frame.time) || frame.time < 0 || frame.time <= previous || frame.time > 15.5 || frame.flags > 3)
        throw std::runtime_error("Invalid frame time/flags");
      previous = frame.time;
      frame.poses.resize(m.nodes.size());
      for (auto& pose : frame.poses)
      {
        for (auto& value : pose.values)
          value = ReadScalar<float>(in);
        const auto hidden = ReadScalar<std::uint8_t>(in);
        pose.hidden       = hidden != 0;
        pose.parent       = ReadScalar<std::uint32_t>(in);
        if (
          !std::ranges::all_of(pose.values, [](auto v) { return std::isfinite(v); }) || hidden > 1 ||
          (pose.parent != NoParent && pose.parent >= m.nodes.size()))
          throw std::runtime_error("Invalid pose values");
      }
      result.frames.push_back(std::move(frame));
    }
    if (std::abs(previous - m.seconds) > 0.00001) throw std::runtime_error("Duration mismatch");
    return result;
  }

  Capture Latest(const std::filesystem::path& base, std::string_view scenario, std::uint32_t rate)
  {
    if (!Scenario(scenario) || (rate != 20 && rate != 40)) throw std::runtime_error("Invalid archive selection");
    std::vector<std::filesystem::path> candidates;
    const auto                         root = std::filesystem::weakly_canonical(base);
    for (const auto& item : std::filesystem::directory_iterator(base))
    {
      const auto directory = std::filesystem::weakly_canonical(item.path());
      if (item.is_directory() && directory.parent_path() == root && std::filesystem::is_regular_file(directory / "complete.txt"))
        candidates.push_back(directory);
    }
    std::ranges::sort(candidates, std::greater<>{});
    for (const auto& directory : candidates)
    {
      const auto json = ReadFile(directory / "metadata.json", 16 * 1024 * 1024);
      Metadata   metadata;
      if (glz::read_json(metadata, std::string_view{json.data(), json.size()})) continue;
      if (metadata.scenario == scenario && metadata.rate == rate) return Read(directory);
    }
    throw std::runtime_error("No completed archive for this scenario/rate");
  }

  void Bytes(std::ostream& out, std::span<const char> bytes)
  {
    out.write(bytes.data(), static_cast<std::streamsize>(bytes.size()));
  }

  template <class T>
  void Scalar(std::ostream& out, T value)
  {
    static_assert(std::is_arithmetic_v<T> && !std::is_same_v<T, bool>);
    auto bytes = std::bit_cast<std::array<char, sizeof(T)>>(value);
    if constexpr (std::endian::native == std::endian::big) std::ranges::reverse(bytes);
    Bytes(out, bytes);
  }

  void Header(std::ostream& out, std::uint32_t nodes, std::uint32_t frames, std::uint32_t rate)
  {
    Bytes(out, std::span<const char>{"DSPPOSE1", 8});
    for (const auto value : {Version, nodes, frames, rate})
      Scalar(out, value);
  }

  void WritePose(std::ostream& out, const Pose& pose)
  {
    if (!std::ranges::all_of(pose.values, [](float value) { return std::isfinite(value); }))
      throw std::runtime_error("Non-finite captured pose");
    for (const auto value : pose.values)
      Scalar(out, value);
    Scalar(out, static_cast<std::uint8_t>(pose.hidden));
    Scalar(out, pose.parent);
  }

  void MetadataFile(const std::filesystem::path& directory, const Metadata& metadata)
  {
    const auto json = glz::write_json(metadata);
    if (!json) throw std::runtime_error("Cannot encode phantom metadata");
    std::ofstream out(directory / "metadata.json", std::ios::binary);
    out.exceptions(std::ios::failbit | std::ios::badbit);
    Bytes(out, *json);
    out.close();
  }

  struct Result
  {
    std::string path, error;
    double      milliseconds{};
  };

  class Writer
  {
    std::mutex            mutex;
    std::jthread          thread;
    bool                  busy{}, closed{};
    std::optional<Result> completed;

public:

    bool Busy()
    {
      std::scoped_lock lock(mutex);
      return busy;
    }

    // The callback owns immutable plain data, never live scene objects.
    std::expected<void, std::string> Submit(
      const std::filesystem::path&                      base,
      std::string                                       scenario,
      std::function<void(const std::filesystem::path&)> write)
    {
      if (!Scenario(scenario)) return std::unexpected{"Unknown capture scenario"};
      {
        std::scoped_lock lock(mutex);
        if (closed || busy) return std::unexpected{"Capture writer is unavailable"};
        busy = true;
        completed.reset();
      }
      try
      {
        // A previous finished writer may still be returning. Never join while
        // holding its completion mutex, or wait for file I/O in a game frame.
        if (thread.joinable()) thread.join();
        thread = std::jthread([this, base, scenario = std::move(scenario), write = std::move(write)] {
          const auto started = std::chrono::steady_clock::now();
          Result     result;
          try
          {
            std::filesystem::create_directories(base);
            const auto stamp =
              std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::system_clock::now().time_since_epoch()).count();
            std::filesystem::path directory;
            for (std::uint32_t attempt = 0; attempt < 1024; ++attempt)
            {
              directory = base / std::format("{}-{}-{}", stamp, scenario, attempt);
              if (std::filesystem::create_directory(directory)) break;
              if (attempt == 1023) throw std::runtime_error("Cannot allocate capture directory");
            }
            result.path = directory.string();
            write(directory);
            // A marker appears only after every file was successfully closed.
            std::ofstream marker(directory / "complete.txt", std::ios::binary);
            marker.exceptions(std::ios::failbit | std::ios::badbit);
            marker << "Dreamsleeve local phantom archive v1\n";
            marker.close();
          }
          catch (const std::exception& error)
          {
            result.error = error.what();
          }
          result.milliseconds = std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - started).count();
          std::scoped_lock lock(mutex);
          completed = std::move(result);
          busy      = false;
        });
      }
      catch (const std::exception& error)
      {
        std::scoped_lock lock(mutex);
        busy = false;
        return std::unexpected{error.what()};
      }
      return {};
    }

    std::optional<Result> Poll()
    {
      std::scoped_lock lock(mutex);
      return std::exchange(completed, std::nullopt);
    }

    void Shutdown()
    {
      {
        std::scoped_lock lock(mutex);
        closed = true;
      }
      if (thread.joinable()) thread.join();
    }

    ~Writer()
    {
      Shutdown();
    }
  };

}
