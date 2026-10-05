#include <doctest/doctest.h>
import std;
import Dreamsleeve.Host.PhantomArchive;
import Dreamsleeve.Host.PhantomReplay;

namespace
{

  PhantomArchive::Pose Identity(float x = 0)
  {
    PhantomArchive::Pose pose;
    pose.values[0] = pose.values[4] = pose.values[8] = pose.values[12] = 1;
    pose.values[9]                                                     = x;
    return pose;
  }

  PhantomArchive::Metadata Layout()
  {
    PhantomArchive::Metadata meta;
    meta.nodes.resize(6);
    meta.nodes[1].geometry = true;
    meta.nodes[2].geometry = true;
    meta.nodes[2].excluded = true;
    meta.skins.push_back({1, 3, {4}});
    meta.skins.push_back({2, 3, {5}});
    return meta;
  }

}

TEST_CASE("Phantom compact channels retain visible skins but omit excluded geometry and unused bones")
{
  auto meta = Layout();
  CHECK(PhantomReplay::Channels(meta) == std::vector<std::uint32_t>{0, 1, 3, 4});
  meta.skins[0].bones.push_back(6);
  CHECK_THROWS_AS(PhantomReplay::Channels(meta), std::runtime_error);
}

TEST_CASE("Phantom replay quaternion handles half turns and preserves rotation direction")
{
  auto pose      = Identity();
  pose.values[0] = 0;
  pose.values[1] = -1;
  pose.values[3] = 1;
  pose.values[4] = 0;
  auto q         = PhantomReplay::Quaternion(pose.values);
  CHECK(q[2] == doctest::Approx(std::sqrt(0.5f)));
  CHECK(q[3] == doctest::Approx(std::sqrt(0.5f)));
  CHECK(q[0] == 0);
  CHECK(q[1] == 0);
  for (const auto axis : {0, 1, 2})
  {
    pose = Identity();
    for (const auto i : {0, 1, 2})
      pose.values[i * 3 + i] = i == axis ? 1.0f : -1.0f;
    q = PhantomReplay::Quaternion(pose.values);
    CHECK(q[axis] == doctest::Approx(1.0f));
    CHECK(q[3] == 0);
    PhantomArchive::Pose reconstructed;
    PhantomReplay::Rotation(reconstructed, q);
    for (const auto i : {0, 1, 2})
      CHECK(reconstructed.values[i * 3 + i] == pose.values[i * 3 + i]);
  }
}

TEST_CASE("Phantom compact records actually roundtrip byte payloads with bounded quantization")
{
  const auto                         meta = Layout();
  std::vector<PhantomArchive::Frame> frames(2);
  for (auto& frame : frames)
    frame.poses.assign(6, Identity(10000.003f));
  frames[1].poses[1].values[9]  += 123.024f;
  frames[1].poses[1].values[10]  = -3.016f;
  frames[1].poses[1].values[12]  = 0.6012f;
  frames[1].poses[1].hidden      = true;
  for (const bool quantized : {false, true})
  {
    auto clip = PhantomReplay::Encode(meta, frames, quantized);
    REQUIRE(clip.channels.size() == 4);
    CHECK(clip.frames[0].size() == 4 * (quantized ? 23 : 33));
    const auto decoded = clip.Decode(1, 1);
    CHECK(decoded.hidden);
    CHECK(std::abs(decoded.values[9] - frames[1].poses[1].values[9]) <= (quantized ? 0.033 : 0.00001));
    CHECK(std::abs(decoded.values[10] + 3.016f) <= (quantized ? 0.032 : 0.00001));
    CHECK(std::abs(decoded.values[12] - 0.6012f) <= (quantized ? 0.00049 : 0.000001));
    clip.frames[1].pop_back();
    CHECK_THROWS_AS(clip.Decode(1, 1), std::runtime_error);
  }
  frames[0].poses[1].values[12] = 64;
  CHECK_THROWS_AS(PhantomReplay::Encode(meta, frames, true), std::runtime_error);
  frames[0].poses[1].values[12] = std::numeric_limits<float>::infinity();
  CHECK_THROWS_AS(PhantomReplay::Encode(meta, frames, false), std::runtime_error);
}

TEST_CASE("Phantom archive reader requires completion exact lengths and monotonic timestamps")
{
  const auto dir =
    std::filesystem::temp_directory_path() / std::format("PhantomRead-{}", std::chrono::steady_clock::now().time_since_epoch().count());
  std::filesystem::create_directory(dir);

  struct Cleanup
  {
    std::filesystem::path dir;

    ~Cleanup()
    {
      for (const auto name : {"complete.txt", "metadata.json", "poses.bin", "appearance.nif"})
        std::filesystem::remove(dir / name);
      std::filesystem::remove(dir);
    }
  } cleanup{dir};

  PhantomArchive::Metadata meta;
  meta.scenario   = "equipment";
  meta.rate       = 20;
  meta.frameCount = 2;
  meta.seconds    = 0.1;
  meta.cell       = 1;
  meta.nodes.resize(1);
  meta.appearanceBytes = 5;
  meta.filePoseBytes   = 24 + 2 * (9 + 73);
  {
    std::ofstream out(dir / "appearance.nif", std::ios::binary);
    out << "model";
  }
  PhantomArchive::MetadataFile(dir, meta);
  auto write = [&](double last) {
    std::ofstream out(dir / "poses.bin", std::ios::binary);
    PhantomArchive::Header(out, 1, 2, 20);
    for (const auto time : {0.0, last})
    {
      PhantomArchive::Scalar(out, time);
      PhantomArchive::Scalar(out, std::uint8_t{});
      PhantomArchive::WritePose(out, Identity());
    }
  };
  write(0.1);
  CHECK_THROWS_AS(PhantomArchive::Read(dir), std::runtime_error);
  {
    std::ofstream out(dir / "complete.txt");
    out << "complete";
  }
  const auto capture = PhantomArchive::Read(dir);
  REQUIRE(capture.frames.size() == 2);
  CHECK(capture.frames[1].time == 0.1);
  write(0);
  CHECK_THROWS_AS(PhantomArchive::Read(dir), std::runtime_error);
  write(0.1);
  {
    std::ofstream out(dir / "poses.bin", std::ios::binary | std::ios::app);
    out << "x";
  }
  CHECK_THROWS_AS(PhantomArchive::Read(dir), std::runtime_error);
  std::istringstream truncated("x");
  CHECK_THROWS_AS(PhantomArchive::ReadScalar<float>(truncated), std::runtime_error);
}

TEST_CASE("Phantom existing archives prepare compact replay without dropping geometry channels")
{
  const auto* base = std::getenv("DREAMSLEEVE_PHANTOM_ARCHIVES");
  if (!base) return;  // Explicit local measurement run; user captures are never a CI dependency.
  std::size_t archives = 0;
  for (const auto& directory : std::filesystem::directory_iterator(base))
  {
    if (!directory.is_directory() || !std::filesystem::is_regular_file(directory.path() / "complete.txt")) continue;
    auto prepared = PhantomReplay::Prepare(PhantomArchive::Read(directory.path()));
    REQUIRE(prepared.selected.channels == prepared.quantized.channels);
    double positionError = 0, scaleError = 0;
    bool   visibility = true;
    for (std::size_t f = 0; f < prepared.capture.frames.size(); ++f)
      for (std::size_t c = 0; c < prepared.selected.channels.size(); ++c)
      {
        const auto& original = prepared.capture.frames[f].poses[prepared.selected.channels[c]];
        const auto& decoded  = prepared.quantizedPoses[f][c];
        double      squared  = 0;
        for (std::size_t j = 9; j < 12; ++j)
          squared += std::pow(original.values[j] - decoded.values[j], 2);
        positionError = std::max(positionError, std::sqrt(squared));
        scaleError    = std::max(scaleError, static_cast<double>(std::abs(original.values[12] - decoded.values[12])));
        visibility    = visibility && original.hidden == decoded.hidden;
      }
    CHECK(visibility);
    CHECK(positionError < 0.06);
    CHECK(scaleError < 0.0005);
    std::cout << std::format(
      "{}: {} channels; float {} / quantized {} bytes; position error {:.6f}; scale error {:.6f}\n",
      directory.path().filename().string(),
      prepared.selected.channels.size(),
      prepared.selected.Bytes(),
      prepared.quantized.Bytes(),
      positionError,
      scaleError);
    ++archives;
  }
  REQUIRE(archives > 0);
}
