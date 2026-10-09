#include <doctest/doctest.h>
#ifdef DREAMSLEEVE_DIAGNOSTICS
import std;
import Dreamsleeve.Client.Diagnostics.PhantomRecorder;
import Dreamsleeve.Client.Diagnostics.PhantomReplay;
import Dreamsleeve.Client.Diagnostics.PhantomTrace;
import Dreamsleeve.Client.Phantom.Wire;

#include "PhantomFixture.hpp"

namespace
{
  namespace D = Dreamsleeve::Client::Diagnostics;
  namespace P = Dreamsleeve::Client::Phantom;

  struct Fixture
  {
    std::filesystem::path                    root = std::filesystem::path("build/phantom-diagnostics-fixtures") /
                                                    std::to_string(std::chrono::steady_clock::now().time_since_epoch().count());
    std::shared_ptr<const P::ValidatedAsset> asset;

    Fixture()
    {
      auto a       = PhantomFixture::Model();
      auto checked = P::ValidatedAsset::Parse(std::move(a));
      REQUIRE(checked);
      asset = std::make_shared<const P::ValidatedAsset>(std::move(*checked));
    }

    std::shared_ptr<const P::Snapshot> Pose(std::uint64_t sequence, std::uint64_t generation = 1)
    {
      auto p         = std::make_shared<P::Snapshot>();
      p->generation  = {generation};
      p->sequence    = {sequence};
      p->context     = 1;
      p->sampledAtUs = sequence * 50000;
      p->channels.resize(2, {P::Transform{{0.04f, 0, 0}}, false});
      p->bounds.push_back({
          {0, 0, 0},
          2
      });
      return p;
    }
  };

  D::Status Finished(D::Recorder& recorder, int timeoutSeconds = 10)
  {
    const auto until = std::chrono::steady_clock::now() + std::chrono::seconds(timeoutSeconds);
    while (std::chrono::steady_clock::now() < until)
    {
      auto s = recorder.Read();
      if (s.phase == D::Phase::Complete || s.phase == D::Phase::Failed) return s;
      std::this_thread::sleep_for(std::chrono::milliseconds(10));
    }
    FAIL("recorder did not finish");
    return recorder.Read();
  }

  struct Reader
  {
    std::span<const std::uint8_t> bytes;
    std::size_t                   at{};

    template <class T>
    T Get()
    {
      REQUIRE(at + sizeof(T) <= bytes.size());
      std::array<std::uint8_t, sizeof(T)> data;
      std::ranges::copy(bytes.subspan(at, sizeof(T)), data.begin());
      at += sizeof(T);
      return std::bit_cast<T>(data);
    }

    std::span<const std::uint8_t> Data(std::size_t n)
    {
      REQUIRE(at + n <= bytes.size());
      auto out  = bytes.subspan(at, n);
      at       += n;
      return out;
    }
  };

  P::Bytes File(std::filesystem::path path)
  {
    std::ifstream in(path, std::ios::binary);
    REQUIRE(in);
    return {std::istreambuf_iterator<char>(in), std::istreambuf_iterator<char>()};
  }

}

TEST_CASE("Diagnostic recorder is opt-in and rejects invalid controls")
{
  Fixture     f;
  D::Recorder recorder;
  CHECK_FALSE(recorder.Active());
  recorder.Sample(f.asset, f.Pose(1), {}, 1, false);
  CHECK_FALSE(std::filesystem::exists(f.root));
  CHECK_FALSE(recorder.Start(f.root, 6, 15, 20));
  CHECK_FALSE(recorder.Start(f.root, 0, 999, 20));
  REQUIRE(recorder.Start(f.root, 0, 15, 20));
  CHECK_FALSE(recorder.Start(f.root, 0, 15, 20));
  recorder.Stop();
  CHECK(Finished(recorder).phase == D::Phase::Complete);
}

TEST_CASE("Diagnostic archive keeps original floats production bytes models and packet stages")
{
  Fixture     f;
  D::Recorder recorder;
  REQUIRE(recorder.Start(f.root, 1, 15, 20));
  auto p = f.Pose(1);
  recorder.Sample(
    f.asset,
    p,
    {
        0,
        0,
        p->sampledAtUs,
        {1, 2, 3 },
        {0, 0, .5}
  },
    .25,
    true);
  recorder.Sample(f.asset, f.Pose(2), {}, .3, false);
  recorder.Sample(f.asset, f.Pose(3, 2), {}, .4, false);
  recorder.Encoded(*p, .12, 99);
  auto zst = P::WriteSnapshot(*p, *f.asset);
  REQUIRE(zst);
  auto packet = P::Wire::Encode(P::Wire::Pose{p->generation, p->context, p->sequence, p->sampledAtUs, *zst});
  REQUIRE(packet);
  recorder.Sent(*packet);
  recorder.MovementSent(
    {
        7,
        9,
        50000,
        {1, 2, 3 },
        {0, 0, .5}
  },
    std::array<std::uint8_t, 2>{1, 2});
  recorder.Failed({P::Failure::Busy, "native.asset-pending [mesh=Hair]"});
  recorder.Stop();
  const auto s = Finished(recorder);
  REQUIRE(s.phase == D::Phase::Complete);
  CHECK(s.samples == 3);
  CHECK(s.encoded == 1);
  CHECK(s.sent == 1);
  CHECK(s.movements == 1);
  CHECK(s.errors == 1);
  CHECK(s.lastCaptureError == "native.asset-pending [mesh=Hair]");
  CHECK(s.queuedBytes == 0);
  CHECK(s.dropped == 0);

  auto   bytes = File(std::filesystem::path(s.directory) / "capture.phdiag");
  Reader r{bytes};
  r.Data(8);
  CHECK(r.Get<std::uint32_t>() == 2);
  r.Data(8);
  unsigned models  = 0;
  unsigned samples = 0;
  while (r.at < bytes.size())
  {
    const auto type = r.Get<std::uint32_t>();
    const auto n    = r.Get<std::uint32_t>();
    Reader     record{r.Data(n)};
    if (type == 1)
    {
      ++models;
      record.Get<std::uint64_t>();
      const auto raw        = record.Get<std::uint32_t>();
      const auto compressed = record.Get<std::uint32_t>();
      auto       digest = record.Data(32);
      auto       model  = record.Data(compressed);
      auto       hash   = P::Hash(model);
      REQUIRE(hash);
      CHECK(std::ranges::equal(digest, *hash));
      CHECK(P::ReadAsset(model, raw));
    }
    if (type == 2)
    {
      record.Get<double>();
      const auto first = record.Get<std::uint8_t>();
      record.Data(48);
      const auto originalN = record.Get<std::uint32_t>();
      const auto rawN      = record.Get<std::uint32_t>();
      const auto zstN      = record.Get<std::uint32_t>();
      Reader     original{record.Data(originalN)};
      auto       raw        = record.Data(rawN);
      auto       compressed = record.Data(zstN);
      auto       decoded = P::ReadSnapshot(compressed, *f.asset);
      REQUIRE(decoded);
      if (samples++ == 0)
      {
        CHECK(first == 1);
        original.Data(52);
        CHECK(original.Get<float>() == .04f);
        auto expected = P::SnapshotBytes(*p, *f.asset);
        REQUIRE(expected);
        CHECK(std::ranges::equal(raw, *expected));
        CHECK(std::ranges::equal(compressed, *zst));
        CHECK(decoded->channels[0].world.position.x == 0.0625f);
      }
    }
  }
  CHECK(models == 2);
  CHECK(samples == 3);
  CHECK_FALSE(std::filesystem::exists(std::filesystem::path(s.directory) / "capture.phdiag.partial"));
  CHECK(std::filesystem::exists(std::filesystem::path(s.directory) / "summary.json"));
}

TEST_CASE("Diagnostic admission stops at queue budget without blocking publication")
{
  Fixture   f;
  D::Budget budget;
  budget.queueBytes = 1;
  D::Recorder recorder(budget);
  REQUIRE(recorder.Start(f.root, 0, 15, 20));
  recorder.Sample(f.asset, f.Pose(1), {}, 0, false);
  CHECK_FALSE(recorder.Active());
  const auto s = Finished(recorder);
  CHECK(s.reason == "queue-limit");
  CHECK(s.dropped == 1);
  CHECK(s.queuedBytes == 0);
}

TEST_CASE("Prior recordings over one GiB do not consume a recorder quota")
{
  Fixture f;
  std::filesystem::create_directories(f.root);
  const auto priorPath = f.root / "prior.bin";
  {
    std::ofstream prior(priorPath, std::ios::binary);
    prior << "preserve";
    prior.seekp(1024ULL * 1024 * 1024);
    prior.put('x');
    REQUIRE(prior);
  }
  D::Recorder recorder;
  REQUIRE(recorder.Start(f.root, 0, 15, 20));
  recorder.Sample(f.asset, f.Pose(1), {}, .1, false);
  recorder.Stop();
  const auto s = Finished(recorder);
  CHECK(s.phase == D::Phase::Complete);
  CHECK(s.samples == 1);
  CHECK(s.reason == "manual");
  CHECK(std::filesystem::file_size(priorPath) == 1024ULL * 1024 * 1024 + 1);
  std::ifstream       prior(priorPath, std::ios::binary);
  std::array<char, 8> prefix;
  prior.read(prefix.data(), prefix.size());
  CHECK(std::string(prefix.data(), prefix.size()) == "preserve");
  prior.close();
  // Only this test's synthetic large file, never a user's recording.
  std::filesystem::remove(priorPath);
}

TEST_CASE("Insufficient actual free space reports a distinct reason and preserves prior files")
{
  Fixture f;
  std::filesystem::create_directories(f.root);
  {
    std::ofstream prior(f.root / "prior.bin");
    prior << "preserve";
  }
  D::Budget budget;
  budget.freeReserveBytes = std::numeric_limits<std::uint64_t>::max();
  D::Recorder recorder(budget);
  REQUIRE(recorder.Start(f.root, 0, 15, 20));
  const auto s = Finished(recorder);
  CHECK(s.phase == D::Phase::Failed);
  CHECK(s.reason == "disk-space-low");
  CHECK(File(f.root / "prior.bin").size() == 8);
}

TEST_CASE("Diagnostic shutdown drains data and completed recordings can restart")
{
  Fixture     f;
  D::Recorder recorder;
  REQUIRE(recorder.Start(f.root, 0, 15, 20));
  recorder.Sample(f.asset, f.Pose(1), {}, .1, false);
  recorder.Stop();
  const auto first = Finished(recorder);
  REQUIRE(first.phase == D::Phase::Complete);

  REQUIRE(recorder.Start(f.root, 2, 30, 40));
  recorder.Sample(f.asset, f.Pose(2), {}, .1, false);
  recorder.Shutdown();
  const auto second = recorder.Read();
  CHECK(second.phase == D::Phase::Complete);
  CHECK(second.reason == "shutdown");
  CHECK(second.samples == 1);
  CHECK(first.directory != second.directory);
  CHECK_FALSE(recorder.Start(f.root, 0, 15, 20));
}

TEST_CASE("Diagnostic duration ends on writer clock without another game frame")
{
  Fixture     f;
  D::Recorder recorder;
  REQUIRE(recorder.Start(f.root, 0, 15, 20));
  recorder.Sample(f.asset, f.Pose(1), {}, .1, false);
  const auto s = Finished(recorder, 20);
  CHECK(s.phase == D::Phase::Complete);
  CHECK(s.reason == "duration");
  CHECK(s.samples == 1);
  recorder.Sample(f.asset, f.Pose(2), {}, .1, false);
  CHECK(recorder.Read().phase == D::Phase::Complete);
}

TEST_CASE("Diagnostic record limit finishes a complete archive and releases queued charges")
{
  Fixture   f;
  D::Budget budget;
  budget.records = 1;
  D::Recorder recorder(budget);
  REQUIRE(recorder.Start(f.root, 0, 15, 20));
  recorder.Sample(f.asset, f.Pose(1), {}, .1, false);
  const auto s = Finished(recorder);
  CHECK(s.phase == D::Phase::Complete);
  CHECK(s.reason == "record-limit");
  CHECK(s.samples == 0);
  CHECK(s.dropped == 1);
  CHECK(s.queuedBytes == 0);
  CHECK(std::filesystem::exists(std::filesystem::path(s.directory) / "capture.phdiag"));
}

TEST_CASE("Temporary recorder backpressure drops observations without ending the session")
{
  Fixture   f;
  D::Budget budget;
  budget.queueJobs = 1;
  D::Recorder recorder(budget);
  REQUIRE(recorder.Start(f.root, 0, 15, 20));
  for (unsigned i = 1; i <= 1000; ++i)
    recorder.Sample(f.asset, f.Pose(i), {}, 1, false);
  CHECK(recorder.Active());
  recorder.Stop();
  const auto result = Finished(recorder);
  CHECK(result.phase == D::Phase::Complete);
  CHECK(result.reason == "manual");
  CHECK(result.samples > 0);
  CHECK(result.dropped > 0);
}

TEST_CASE("Diagnostic queue retains shared model storage once for a burst of distinct poses")
{
  Fixture  f;
  P::Asset data = f.asset->Value();
  data          = PhantomFixture::Model(2, 45000);
  auto asset    = P::ValidatedAsset::Parse(std::move(data));
  REQUIRE(asset);
  f.asset = std::make_shared<const P::ValidatedAsset>(*asset);
  // A wrapper copy still owns the same immutable Asset storage.
  auto      wrapper = std::make_shared<const P::ValidatedAsset>(*asset);
  D::Budget budget;
  budget.queueBytes = asset->MemoryBytes() + 256 * 1024;
  budget.queueJobs  = 128;
  D::Recorder recorder(budget);
  REQUIRE(recorder.Start(f.root, 0, 15, 20));
  for (unsigned i = 1; i <= 64; ++i)
  {
    auto pose = std::make_shared<P::Snapshot>(*f.Pose(i, i > 32 ? 2 : 1));
    recorder.Sample(i % 2 ? f.asset : wrapper, std::move(pose), {}, 1, false);
  }
  recorder.Stop();
  const auto result = Finished(recorder);
  CHECK(result.phase == D::Phase::Complete);
  CHECK(result.samples == 64);
  CHECK(result.dropped == 0);
  CHECK(result.queuedBytes == 0);

  REQUIRE(recorder.Start(f.root, 0, 15, 20));
  auto pose = std::make_shared<P::Snapshot>(*f.Pose(1));
  recorder.Sample(f.asset, std::move(pose), {}, 1, false);
  recorder.Stop();
  CHECK(Finished(recorder).samples == 1);
}

namespace
{

  std::vector<D::ReplayFrame> Replay(D::ReplayReader& reader, int timeoutSeconds = 20)
  {
    std::vector<D::ReplayFrame> frames;
    const auto                  until = std::chrono::steady_clock::now() + std::chrono::seconds(timeoutSeconds);
    while (std::chrono::steady_clock::now() < until)
    {
      if (auto frame = reader.Take())
      {
        frames.push_back(std::move(*frame));
        continue;
      }
      if (!reader.Read().busy)
      {
        // The writer may have published its final frame between Take and Read.
        if (auto frame = reader.Take())
        {
          frames.push_back(std::move(*frame));
          continue;
        }
        return frames;
      }
      std::this_thread::sleep_for(std::chrono::milliseconds(1));
    }
    FAIL("replay reader did not finish");
    return frames;
  }

}

TEST_CASE("Diagnostic replay decodes archived wire bytes and can cancel a full read-ahead queue")
{
  Fixture     f;
  D::Recorder recorder;
  REQUIRE(recorder.Start(f.root, 0, 15, 20));
  for (unsigned i = 1; i <= 32; ++i)
    recorder.Sample(f.asset, f.Pose(i, i > 16 ? 2 : 1), {}, 1, false);
  recorder.Stop();
  REQUIRE(Finished(recorder).samples == 32);
  D::ReplayReader reader;
  REQUIRE(reader.Start(f.root / "new-empty-location", 0, f.root));
  auto frames = Replay(reader);
  REQUIRE(reader.Read().error.empty());
  CHECK(reader.Read().complete);
  REQUIRE(frames.size() == 32);
  CHECK(reader.Read().models == 2);
  for (std::size_t i = 0; i < frames.size(); ++i)
  {
    CHECK(frames[i].pose->sequence.value == i + 1);
    CHECK(frames[i].pose->generation.value == (i >= 16 ? 2 : 1));
    // Original .04 differs: rendering must consume the quantized wire pose.
    CHECK(frames[i].pose->channels[0].world.position.x == 0.0625f);
    CHECK(P::CheckSnapshot(*frames[i].pose, *frames[i].asset));
  }

  REQUIRE(reader.Start(f.root, 0));
  const auto until = std::chrono::steady_clock::now() + std::chrono::seconds(5);
  while (reader.Read().frames < 4 && std::chrono::steady_clock::now() < until)
    std::this_thread::sleep_for(std::chrono::milliseconds(1));
  CHECK(reader.Read().frames == 4);
  reader.Stop();
  CHECK(Replay(reader).empty());
  REQUIRE(reader.Start(f.root, 0));
  CHECK(Replay(reader).size() == 32);
  const auto directory = std::filesystem::path(reader.Read().directory);
  REQUIRE(reader.Start(directory, 1));  // An explicit recording does not select a different scenario.
  CHECK(Replay(reader).size() == 32);
  REQUIRE(reader.Start(directory / "capture.phdiag", 1));
  CHECK(Replay(reader).size() == 32);
}

TEST_CASE("Diagnostic replay rejects a damaged model before returning a renderable frame")
{
  Fixture     f;
  D::Recorder recorder;
  REQUIRE(recorder.Start(f.root, 0, 15, 20));
  recorder.Sample(f.asset, f.Pose(1), {}, 1, false);
  recorder.Stop();
  const auto saved = Finished(recorder);
  auto       file  = std::filesystem::path(saved.directory) / "capture.phdiag";
  auto       bytes = File(file);
  REQUIRE(bytes.size() > 44);
  bytes[44] ^= 1;  // SHA256 field of first model record.
  {
    std::ofstream out(file, std::ios::binary | std::ios::trunc);
    out.write(reinterpret_cast<const char*>(bytes.data()), bytes.size());
  }
  D::ReplayReader reader;
  REQUIRE(reader.Start(f.root, 0));
  CHECK(Replay(reader).empty());
  CHECK(reader.Read().error == "archive.model-hash");
}

TEST_CASE("Diagnostic recorder reports a blocked directory and releases queued reservations")
{
  Fixture f;
  std::filesystem::create_directories(f.root.parent_path());
  {
    std::ofstream blocker(f.root);
    blocker << "preserve";
  }
  D::Recorder recorder;
  REQUIRE(recorder.Start(f.root, 0, 15, 20));
  recorder.Sample(f.asset, f.Pose(1), {}, .1, false);
  const auto failed = Finished(recorder);
  CHECK(failed.phase == D::Phase::Failed);
  CHECK_FALSE(failed.reason.empty());
  CHECK(failed.queuedBytes == 0);
  CHECK_FALSE(recorder.Active());
  CHECK(File(f.root).size() == 8);
  REQUIRE(recorder.Start(f.root.parent_path() / (f.root.filename().native() + std::filesystem::path("-recovered").native()), 0, 15, 20));
  recorder.Sample(f.asset, f.Pose(2), {}, .1, false);
  recorder.Stop();
  CHECK(Finished(recorder).phase == D::Phase::Complete);
}

TEST_CASE("Diagnostic replay preserves missing archive and rejects truncated header")
{
  Fixture         f;
  D::ReplayReader reader;
  REQUIRE(reader.Start(f.root, 0));
  CHECK(Replay(reader).empty());
  CHECK_FALSE(reader.Read().complete);
  CHECK(reader.Read().error == "Нет завершённой записи с позами для выбранного сценария");
  std::filesystem::create_directories(f.root);
  const auto file = f.root / "truncated.phdiag";
  {
    std::ofstream out(file, std::ios::binary);
    out << "DLPDIAG2";
  }
  REQUIRE(reader.Start(file, 0));
  CHECK(Replay(reader).empty());
  CHECK_FALSE(reader.Read().complete);
  CHECK(reader.Read().error == "archive.truncated-file");
}

TEST_CASE("Diagnostic replay cannot skip a truncated trailing record as clean EOF")
{
  Fixture     f;
  D::Recorder recorder;
  REQUIRE(recorder.Start(f.root, 0, 15, 20));
  recorder.Sample(f.asset, f.Pose(1), {}, .1, false);
  recorder.Stop();
  const auto saved = Finished(recorder);
  REQUIRE(saved.phase == D::Phase::Complete);
  const auto file = std::filesystem::path(std::u8string(saved.directory.begin(), saved.directory.end())) / "capture.phdiag";
  {
    std::ofstream       out(file, std::ios::binary | std::ios::app);
    const std::uint32_t kind = 3;
    const std::uint32_t size = 7;
    out.write(reinterpret_cast<const char*>(&kind), sizeof(kind));
    out.write(reinterpret_cast<const char*>(&size), sizeof(size));
    out << "xx";
  }
  D::ReplayReader reader;
  REQUIRE(reader.Start(file, 0));
  CHECK(Replay(reader).size() == 1);
  CHECK_FALSE(reader.Read().complete);
  CHECK(reader.Read().error == "archive.truncated-file");
}

TEST_CASE("Diagnostic recorder and replay retain native Unicode paths and UTF8 status")
{
  Fixture f;
  f.root /= std::filesystem::path(u8"Запись-фантом");
  D::Recorder recorder;
  REQUIRE(recorder.Start(f.root, 0, 15, 20));
  recorder.Sample(f.asset, f.Pose(1), {}, .1, false);
  recorder.Stop();
  const auto saved = Finished(recorder);
  REQUIRE(saved.phase == D::Phase::Complete);
  CHECK(saved.directory.find("Запись-фантом") != std::string::npos);
  D::ReplayReader reader;
  REQUIRE(reader.Start(f.root, 0));
  CHECK(Replay(reader).size() == 1);
  CHECK(reader.Read().complete);
  CHECK(reader.Read().error.empty());
  CHECK(reader.Read().directory == saved.directory);
}

TEST_CASE("Diagnostic replay decodes the recorded full character archive when supplied")
{
  const char* root = std::getenv("DREAMSLEEVE_PHANTOM_REPLAY_ROOT");
  if (!root)
  {
    MESSAGE("Optional real replay archive not configured");
    return;
  }
  D::ReplayReader reader;
  REQUIRE(reader.Start(std::filesystem::path(root), 1));
  // Consume without retaining the entire user's archive in the test process.
  const auto    until  = std::chrono::steady_clock::now() + std::chrono::seconds(60);
  std::uint64_t frames = 0;
  std::uint64_t last   = 0;
  std::ofstream measurements;
  const char*   output = std::getenv("DREAMSLEEVE_PHANTOM_REPLAY_MEASUREMENTS");
  if (output)
  {
    measurements.open(std::filesystem::path(output) / "poses.csv");
    REQUIRE(measurements.is_open());
    measurements << "generation,sequence,payload,packet,encodeMs,decodeMs\n";
  }
  while (std::chrono::steady_clock::now() < until)
  {
    if (auto frame = reader.Take())
    {
      REQUIRE(P::CheckSnapshot(*frame->pose, *frame->asset));
      CHECK(frame->pose->sampledAtUs > last);
      last = frame->pose->sampledAtUs;
      if (output)
      {
        const auto start   = std::chrono::steady_clock::now();
        auto       encoded = P::WriteSnapshot(*frame->pose, *frame->asset, D::CaptureLimits());
        REQUIRE(encoded);
        const auto encodedAt = std::chrono::steady_clock::now();
        auto       decoded   = P::ReadSnapshot(*encoded, *frame->asset, D::CaptureLimits());
        REQUIRE(decoded);
        const auto decodedAt = std::chrono::steady_clock::now();
        for (std::size_t i = 0; i < decoded->channels.size(); ++i)
        {
          CHECK(decoded->channels[i].world.position == frame->pose->channels[i].world.position);
          CHECK(decoded->channels[i].hidden == frame->pose->channels[i].hidden);
          CHECK(decoded->channels[i].world.scale == frame->pose->channels[i].world.scale);
          const auto a = decoded->channels[i].world.rotation;
          const auto b = frame->pose->channels[i].world.rotation;
          CHECK(std::abs(a.x - b.x) + std::abs(a.y - b.y) + std::abs(a.z - b.z) + std::abs(a.w - b.w) < 0.00013f);
        }
        CHECK(decoded->bounds == frame->pose->bounds);
        const auto& p      = *decoded;
        auto        packet = P::Wire::Encode(P::Wire::Pose{p.generation, p.context, p.sequence, p.sampledAtUs, *encoded});
        REQUIRE(packet);
        measurements << p.generation.value << ',' << p.sequence.value << ',' << encoded->size() << ',' << packet->size() << ','
                     << std::chrono::duration<double, std::milli>(encodedAt - start).count() << ','
                     << std::chrono::duration<double, std::milli>(decodedAt - encodedAt).count() << '\n';
        if (frames < 20)
        {
          std::ofstream fixture(std::filesystem::path(output) / ("pose-" + std::to_string(frames) + ".zst"), std::ios::binary);
          fixture.write(reinterpret_cast<const char*>(encoded->data()), encoded->size());
        }
      }
      ++frames;
      continue;
    }
    const auto status = reader.Read();
    if (!status.busy && (status.complete || !status.error.empty())) break;
    std::this_thread::sleep_for(std::chrono::milliseconds(1));
  }
  INFO(reader.Read().error);
  CHECK_FALSE(reader.Read().busy);
  CHECK(reader.Read().error.empty());
  CHECK(reader.Read().complete);
  CHECK(frames > 0);
  CHECK(frames == reader.Read().frames);
  MESSAGE("Real archive decoded frames: ", frames, ", models: ", reader.Read().models);
}

TEST_CASE("continuous trace retains packet slices and excludes control payloads")
{
  namespace T = Dreamsleeve::Client::Diagnostics::Trace;
  const auto path =
    std::filesystem::path("build/phantom-trace-tests") / std::to_string(std::chrono::steady_clock::now().time_since_epoch().count());
  REQUIRE(T::Start(path, 1024));
  const std::vector<std::uint8_t> bytes(33000, 0xab);
  T::Asset(std::string(64, 'a'), bytes);
  T::Packet(true, 3, bytes);
  T::Packet(false, 4, std::span(bytes).first(3));
  T::Packet(false, 0, bytes);
  T::Observe(T::Metric::CapturePose, 2.5);
  T::FlushMetrics();
  REQUIRE(T::Stop() == T::StopOutcome::Stopped);
  CHECK_FALSE(T::Enabled());
  std::string                        content;
  std::vector<std::filesystem::path> parts;
  for (const auto& entry : std::filesystem::recursive_directory_iterator(path))
    if (entry.is_regular_file() && entry.path().extension() == ".jsonl") parts.push_back(entry.path());
  REQUIRE_FALSE(parts.empty());
  auto session = parts.front().parent_path();
  CHECK(std::filesystem::file_size(session / "models" / (std::string(64, 'a') + ".zst")) == bytes.size());
  std::ranges::sort(parts);
  CHECK(parts.size() >= 3);
  for (const auto& part : parts)
  {
    std::ifstream input(part);
    content.append(std::istreambuf_iterator<char>(input), {});
  }
  std::istringstream file(content);
  std::string        line;
  int                packets = 0;
  int                metrics = 0;
  while (std::getline(file, line))
  {
    if (line.find("\"event\":\"packet\"") == std::string::npos)
    {
      if (line.find("\"name\":\"capture_pose\"") != std::string::npos)
      {
        CHECK(line.find("\"count\":1") != std::string::npos);
        ++metrics;
      }
      continue;
    }
    ++packets;
    CHECK(line.find("\"lane\":0") == std::string::npos);
    const auto start = line.find("\"hex\":\"") + 7;
    const auto end   = line.find('"', start);
    REQUIRE(end != std::string::npos);
    auto size = end - start;
    CHECK(size <= 32768);
    for (std::size_t i = 0; i < size; i += 2)
      CHECK(line.substr(start + i, 2) == "ab");
  }
  CHECK(packets == 4);
  CHECK(metrics == 1);

  REQUIRE(T::Start(path));
  REQUIRE(T::Stop() == T::StopOutcome::Stopped);
  std::filesystem::remove_all(path);
}

TEST_CASE("continuous trace cleanup launch failure retains its writer and prevents replacement")
{
  namespace T = Dreamsleeve::Client::Diagnostics::Trace;
  Fixture f;
  REQUIRE(T::Start(f.root));
  T::Testing::FailNextCleanupLaunch();
  const auto failed = T::Stop();
  REQUIRE_FALSE(failed);
  CHECK(failed.error().kind == T::TraceFailure::CleanupLaunch);
  CHECK(T::Enabled());
  T::Event("after-failed-stop");
  T::Testing::FailNextCleanupLaunch();
  const auto replacement = T::Start(f.root);
  REQUIRE_FALSE(replacement);
  CHECK(replacement.error().kind == T::TraceFailure::CleanupLaunch);
  CHECK(T::Enabled());
  CHECK(std::distance(std::filesystem::directory_iterator(f.root), std::filesystem::directory_iterator{}) == 1);
  REQUIRE(T::Stop() == T::StopOutcome::Stopped);
  CHECK_FALSE(T::Enabled());
  std::string content;
  for (const auto& entry : std::filesystem::recursive_directory_iterator(f.root))
    if (entry.path().extension() == ".jsonl")
    {
      const auto bytes = File(entry.path());
      content.append(bytes.begin(), bytes.end());
    }
  CHECK(content.find("after-failed-stop") != std::string::npos);
}

TEST_CASE("continuous trace pending cleanup bounds the session until the worker releases ownership")
{
  namespace T = Dreamsleeve::Client::Diagnostics::Trace;

  Fixture f;

  struct Gate
  {
    std::promise<void> value;
    bool               opened{};

    void Open()
    {
      if (!std::exchange(opened, true)) value.set_value();
    }

    ~Gate()
    {
      Open();
    }
  } gate;

  REQUIRE(T::Start(f.root));
  T::Event("before-gated-cleanup");
  T::Testing::GateNextCleanup(gate.value.get_future().share());
  auto       stopping = std::async(std::launch::async, [] { return T::Stop(); });
  const auto ready    = stopping.wait_for(std::chrono::seconds(3));
  if (ready != std::future_status::ready) gate.Open();  // A failing implementation must not hang the test process.
  REQUIRE(ready == std::future_status::ready);
  REQUIRE(stopping.get() == T::StopOutcome::CleanupPending);
  CHECK_FALSE(T::Enabled());
  for (int i = 0; i < 3; ++i)
  {
    const auto refused = T::Start(f.root);
    REQUIRE_FALSE(refused);
    CHECK(refused.error().kind == T::TraceFailure::CleanupPending);
  }
  CHECK(std::distance(std::filesystem::directory_iterator(f.root), std::filesystem::directory_iterator{}) == 1);
  REQUIRE(T::Stop() == T::StopOutcome::CleanupPending);
  gate.Open();
  const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(5);
  while (T::Stop() == T::StopOutcome::CleanupPending && std::chrono::steady_clock::now() < deadline)
    std::this_thread::sleep_for(std::chrono::milliseconds(1));
  REQUIRE(T::Stop() == T::StopOutcome::Stopped);
  REQUIRE(T::Start(f.root));
  REQUIRE(T::Stop() == T::StopOutcome::Stopped);
  CHECK_FALSE(T::Enabled());
}

TEST_CASE("continuous trace preserves expected storage failures and failed-open partial ownership")
{
  namespace T = Dreamsleeve::Client::Diagnostics::Trace;
  Fixture f;

  struct DirectoryCleanup
  {
    std::filesystem::path root;

    ~DirectoryCleanup()
    {
      // Never remove an active/pending writer's files, including assertion failure.
      const auto stopped = T::Stop();
      if (stopped && *stopped == T::StopOutcome::Stopped)
      {
        std::error_code error;
        std::filesystem::remove_all(root, error);
      }
    }
  } cleanup{f.root};

  std::filesystem::create_directories(f.root);
  const auto blocked = f.root / "blocked";
  {
    std::ofstream blocker(blocked);
    blocker << "preserve";
  }
  const auto unavailable = T::Start(blocked);
  REQUIRE_FALSE(unavailable);
  CHECK(unavailable.error().kind == T::TraceFailure::Storage);
  CHECK_FALSE(T::Enabled());
  CHECK(File(blocked).size() == 8);
  const auto root = f.root / "trace";
  REQUIRE(T::Start(root));
  const auto session = std::filesystem::directory_iterator(root)->path();
  const auto models  = session / "models";
  const auto hash    = std::string(64, 'a');
  const auto partial = models / (hash + ".zst.partial");
  std::filesystem::create_directories(partial);
  {
    std::ofstream prior(partial / "preserve.txt");
    prior << "preserve";
  }
  T::Asset(hash, std::array<std::uint8_t, 2>{1, 2});
  CHECK(File(partial / "preserve.txt").size() == 8);
  CHECK_FALSE(std::filesystem::exists(models / (hash + ".zst")));
  T::FlushMetrics();
  REQUIRE(T::Stop() == T::StopOutcome::Stopped);
  std::string content;
  for (const auto& entry : std::filesystem::directory_iterator(session))
    if (entry.path().extension() == ".jsonl")
    {
      const auto bytes = File(entry.path());
      content.append(bytes.begin(), bytes.end());
    }
  CHECK(content.find("\"write_errors_total\":1") != std::string::npos);
}

TEST_CASE("continuous trace rejects invalid replacement before stopping the active writer")
{
  namespace T = Dreamsleeve::Client::Diagnostics::Trace;
  Fixture f;
  REQUIRE(T::Start(f.root));
  const auto invalid = T::Start(f.root, 1023);
  REQUIRE_FALSE(invalid);
  CHECK(invalid.error().kind == T::TraceFailure::InvalidConfiguration);
  CHECK(T::Enabled());
  T::Event("after-invalid-replacement");
  REQUIRE(T::Stop() == T::StopOutcome::Stopped);
  CHECK(std::distance(std::filesystem::directory_iterator(f.root), std::filesystem::directory_iterator{}) == 1);
  std::string content;
  for (const auto& entry : std::filesystem::recursive_directory_iterator(f.root))
    if (entry.path().extension() == ".jsonl")
    {
      const auto bytes = File(entry.path());
      content.append(bytes.begin(), bytes.end());
    }
  CHECK(content.find("after-invalid-replacement") != std::string::npos);
}
#endif
