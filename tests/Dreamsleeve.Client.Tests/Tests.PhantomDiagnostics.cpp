#include <doctest/doctest.h>
#ifdef DREAMSLEEVE_DIAGNOSTICS
import std;
import Dreamsleeve.Client.Diagnostics.PhantomRecorder;
import Dreamsleeve.Client.Phantom.Wire;

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
      P::Asset a;
      a.nodes.push_back({});
      P::Geometry g;
      g.dynamic = true;
      g.vertices.resize(3);
      g.vertices[1].position = {1, 0, 0};
      g.vertices[2].position = {0, 1, 0};
      g.indices              = {0, 1, 2};
      a.geometry.push_back(std::move(g));
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
      p->channels.push_back({P::Transform{{0.04f, 0, 0}}, false});
      p->bounds.push_back({
          {0, 0, 0},
          2
      });
      p->deformations.push_back({
          0,
          {{.001f, 0, 0}, {1, 0, 0}, {0, 1, 0}},
          {{0, 0, 1},     {0, 0, 1}, {0, 0, 1}}
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
  recorder.Failed({P::Failure::Busy, "graphics.alpha-pending [mesh=Hair]"});
  recorder.Stop();
  const auto s = Finished(recorder);
  REQUIRE(s.phase == D::Phase::Complete);
  CHECK(s.samples == 3);
  CHECK(s.encoded == 1);
  CHECK(s.sent == 1);
  CHECK(s.movements == 1);
  CHECK(s.errors == 1);
  CHECK(s.lastCaptureError == "graphics.alpha-pending [mesh=Hair]");
  CHECK(s.queuedBytes == 0);
  CHECK(s.dropped == 0);
  auto   bytes = File(std::filesystem::path(s.directory) / "capture.phdiag");
  Reader r{bytes};
  r.Data(8);
  CHECK(r.Get<std::uint32_t>() == 1);
  r.Data(8);
  unsigned models = 0, samples = 0;
  while (r.at < bytes.size())
  {
    const auto type = r.Get<std::uint32_t>(), n = r.Get<std::uint32_t>();
    Reader     record{r.Data(n)};
    if (type == 1)
    {
      ++models;
      record.Get<std::uint64_t>();
      const auto raw = record.Get<std::uint32_t>(), compressed = record.Get<std::uint32_t>();
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
      const auto originalN = record.Get<std::uint32_t>(), rawN = record.Get<std::uint32_t>(), zstN = record.Get<std::uint32_t>();
      Reader     original{record.Data(originalN)};
      auto       raw = record.Data(rawN), compressed = record.Data(zstN);
      auto       decoded = P::ReadSnapshot(compressed, *f.asset);
      REQUIRE(decoded);
      if (samples++ == 0)
      {
        CHECK(first == 1);
        original.Data(56);
        CHECK(original.Get<float>() == .04f);
        auto expected = P::SnapshotBytes(*p, *f.asset);
        REQUIRE(expected);
        CHECK(std::ranges::equal(raw, *expected));
        CHECK(std::ranges::equal(compressed, *zst));
        CHECK(decoded->channels[0].world.position.x == 0);
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

TEST_CASE("Local recording preserves a complete pose above production network limits")
{
  Fixture  f;
  P::Asset data = f.asset->Value();
  data.geometry[0].vertices.resize(45000);
  auto asset = P::ValidatedAsset::Parse(std::move(data));
  REQUIRE(asset);
  f.asset   = std::make_shared<const P::ValidatedAsset>(*asset);
  auto pose = std::make_shared<P::Snapshot>(*f.Pose(1));
  pose->deformations[0].positions.resize(45000);
  pose->deformations[0].normals.resize(45000, P::Vec3{0, 0, 1});
  CHECK_FALSE(P::WriteSnapshot(*pose, *f.asset));
  auto encoded = P::WriteSnapshot(*pose, *f.asset, D::CaptureLimits());
  REQUIRE(encoded);
  REQUIRE(P::ReadSnapshot(*encoded, *f.asset, D::CaptureLimits()));
  D::Recorder recorder;
  REQUIRE(recorder.Start(f.root, 0, 15, 20));
  recorder.Partial(1, 2, "test partial geometry");
  recorder.Sample(f.asset, pose, {}, 1, false);
  recorder.Stop();
  auto status = Finished(recorder);
  CHECK(status.phase == D::Phase::Complete);
  CHECK(status.samples == 1);
  CHECK(status.errors == 0);
  CHECK(status.partialSamples == 1);
  CHECK(status.omittedGeometry == 1);
  CHECK(status.hiddenGeometry == 2);
  auto        summaryBytes = File(std::filesystem::path(status.directory) / "summary.json");
  std::string summary(summaryBytes.begin(), summaryBytes.end());
  CHECK(summary.find("test partial geometry") != std::string::npos);
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
  P::Asset data            = f.asset->Value();
  data.geometry[0].dynamic = false;
  data.geometry[0].vertices.resize(45000);
  auto asset = P::ValidatedAsset::Parse(std::move(data));
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
    pose->deformations.clear();
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
  pose->deformations.clear();
  recorder.Sample(f.asset, std::move(pose), {}, 1, false);
  recorder.Stop();
  CHECK(Finished(recorder).samples == 1);
}
#endif
