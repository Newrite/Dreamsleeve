#include <string>
#include <doctest/doctest.h>
#include <cstdlib>
import std;
import Dreamsleeve.Client.Phantom.Nif;
import Dreamsleeve.Client.Phantom.NifOutput;
import Dreamsleeve.Client.Phantom.Wire;
#ifdef DREAMSLEEVE_DIAGNOSTICS
import Dreamsleeve.Client.Diagnostics.PhantomRecorder;
#endif
#include "PhantomFixture.hpp"

namespace
{

  std::string NativeFixture()
  {
    char*       text{};
    std::size_t size{};
    if (_dupenv_s(&text, &size, "DREAMSLEEVE_NATIVE_NIF") != 0) return {};
    std::unique_ptr<char, decltype(&std::free)> owner(text, std::free);
    return owner && size > 1 ? owner.get() : std::string{};
  }

}

TEST_CASE("Native NIF rejects truncated inputs before engine loading")
{
  namespace N = Dreamsleeve::Client::Phantom::Nif;
  CHECK_FALSE(N::Inspect({}));
  const std::string         header = "Gamebryo File Format, Version 20.2.0.7\n";
  std::vector<std::uint8_t> bytes(header.begin(), header.end());
  CHECK_FALSE(N::Inspect(bytes));
  bytes.resize(128);
  CHECK_FALSE(N::Inspect(bytes));
}

TEST_CASE("Native NIF real archive has stable complete pose bindings" * doctest::skip(NativeFixture().empty()))
{
  namespace N = Dreamsleeve::Client::Phantom::Nif;
  std::ifstream input(NativeFixture(), std::ios::binary);
  REQUIRE(input);
  std::vector<std::uint8_t> bytes{std::istreambuf_iterator<char>(input), {}};
  const auto                layout = N::Inspect(bytes);
  INFO((layout ? "valid" : layout.error().field));
  REQUIRE(layout);
  CHECK(layout->bounds.size() == 62);
  CHECK(layout->requiredChannels.size() == 327);
  CHECK(layout->nodes.front().parent == Dreamsleeve::Client::Phantom::NoNode);
  for (std::size_t i = 1; i < layout->nodes.size(); ++i)
    CHECK(layout->nodes[i].parent < i);
  auto truncated = bytes;
  truncated.pop_back();
  CHECK_FALSE(N::Inspect(truncated));
  bytes.push_back(0);
  CHECK_FALSE(N::Inspect(bytes));
}

#ifdef DREAMSLEEVE_DIAGNOSTICS
namespace
{

  template <class T>
  T Scalar(std::istream& in)
  {
    T v{};
    in.read(reinterpret_cast<char*>(&v), sizeof(v));
    REQUIRE(in);
    return v;
  }

  void SaveBytes(const std::filesystem::path& path, std::span<const std::uint8_t> bytes)
  {
    std::ofstream f(path, std::ios::binary);
    f.write(reinterpret_cast<const char*>(bytes.data()), bytes.size());
    REQUIRE(f);
  }

}

TEST_CASE("Native recorded poses traverse production codec and diagnostic replay archive" * doctest::skip(NativeFixture().empty()))
{
  namespace P    = Dreamsleeve::Client::Phantom;
  namespace D    = Dreamsleeve::Client::Diagnostics;
  using Clock    = std::chrono::steady_clock;
  const auto dir = std::filesystem::path(NativeFixture()).parent_path();
  if (!std::filesystem::exists(dir / "native-poses.bin")) return;
  std::ifstream nif(NativeFixture(), std::ios::binary);
  P::Asset      raw{
      {std::istreambuf_iterator<char>(nif), {}}
  };
  const auto start = Clock::now();
  auto       asset = P::ValidatedAsset::Parse(std::move(raw));
  REQUIRE(asset);
  const auto validateMs    = std::chrono::duration<double, std::milli>(Clock::now() - start).count();
  const auto compressStart = Clock::now();
  auto       model         = P::Prepare(*asset);
  REQUIRE(model);
  const auto compressMs  = std::chrono::duration<double, std::milli>(Clock::now() - compressStart).count();
  const auto decodeStart = Clock::now();
  auto       decoded     = P::ReadAsset(*model->compressed, model->rawBytes);
  REQUIRE(decoded);
  const auto decodeMs = std::chrono::duration<double, std::milli>(Clock::now() - decodeStart).count();
  CHECK(decoded->Value().nif == asset->Value().nif);
  SaveBytes(dir / "model.zst", *model->compressed);

  std::ifstream       poses(dir / "native-poses.bin", std::ios::binary);
  std::array<char, 8> magic{};
  poses.read(magic.data(), 8);
  REQUIRE(std::string_view(magic.data(), 8) == "NIFPOSE2");
  const auto channels = Scalar<std::uint32_t>(poses);
  const auto bounds   = Scalar<std::uint32_t>(poses);
  const auto frames   = Scalar<std::uint32_t>(poses);
  REQUIRE(channels == asset->Layout().requiredChannels.size());
  REQUIRE(bounds == asset->Layout().bounds.size());
  REQUIRE(frames <= 1001);

  auto        shared = std::make_shared<const P::ValidatedAsset>(*asset);
  D::Recorder recorder;
  REQUIRE(recorder.Start(dir / "replay", 1, 30, 20));
  std::ofstream csv(dir / "measurements.csv");
  csv << "sequence,compressed_bytes,packet_bytes,encode_ms,decode_ms,max_position_error,max_rotation_degrees,max_scale_error\n";
  double positionMax = 0;
  double rotationMax = 0;
  double scaleMax    = 0;
  for (std::uint32_t f = 0; f < frames; ++f)
  {
    P::Snapshot pose;
    pose.generation  = {1};
    pose.sequence    = {f + 1};
    pose.context     = 1;
    pose.sampledAtUs = Scalar<std::uint64_t>(poses);
    pose.origin      = Scalar<P::Vec3>(poses);
    pose.channels.resize(channels);
    pose.bounds.resize(bounds);
    for (auto& c : pose.channels)
    {
      c.world  = Scalar<P::Transform>(poses);
      c.hidden = Scalar<std::uint8_t>(poses) != 0;
    }
    for (auto& b : pose.bounds)
      b = Scalar<P::Bound>(poses);

    const auto e    = Clock::now();
    auto       wire = P::WriteSnapshot(pose, *asset);
    INFO((wire ? "encoded" : wire.error().field));
    REQUIRE(wire);
    const auto ems  = std::chrono::duration<double, std::milli>(Clock::now() - e).count();
    const auto d    = Clock::now();
    auto       read = P::ReadSnapshot(*wire, *decoded);
    REQUIRE(read);
    const auto dms = std::chrono::duration<double, std::milli>(Clock::now() - d).count();

    double     pe = 0;
    double     re = 0;
    double     se = 0;
    for (std::size_t i = 0; i < channels; ++i)
    {
      const auto& a = pose.channels[i].world;
      const auto& b = read->channels[i].world;
      pe            = std::max(
        pe,
        std::hypot(double(a.position.x) - b.position.x, double(a.position.y) - b.position.y, double(a.position.z) - b.position.z));
      const std::array<double, 4> qa{a.rotation.x, a.rotation.y, a.rotation.z, a.rotation.w};
      const std::array<double, 4> qb{b.rotation.x, b.rotation.y, b.rotation.z, b.rotation.w};
      double dot = 0;
      double na  = 0;
      double nb  = 0;
      for (unsigned q = 0; q < 4; ++q)
      {
        dot += qa[q] * qb[q];
        na  += qa[q] * qa[q];
        nb  += qb[q] * qb[q];
      }
      re = std::max(re, 2 * std::acos(std::clamp(std::abs(dot) / std::sqrt(na * nb), 0.0, 1.0)) * 180 / std::numbers::pi);
      se = std::max(se, std::abs(double(a.scale) - b.scale));
      CHECK(pose.channels[i].hidden == read->channels[i].hidden);
    }
    CHECK(pe <= 0.055);
    CHECK(re <= 0.005);
    CHECK(se <= 1.0 / 2048 + 0.000001);
    positionMax = std::max(positionMax, pe);
    rotationMax = std::max(rotationMax, re);
    scaleMax    = std::max(scaleMax, se);

    auto packet = P::Wire::Encode(P::Wire::Pose{pose.generation, pose.context, pose.sequence, pose.sampledAtUs, *wire});
    REQUIRE(packet);
    SaveBytes(dir / std::format("pose-{}.zst", f + 1), *wire);
    csv << f + 1 << ',' << wire->size() << ',' << packet->size() << ',' << ems << ',' << dms << ',' << pe << ',' << re << ',' << se << '\n';
    while (recorder.Read().queuedBytes > 32 * 1024 * 1024)
      std::this_thread::sleep_for(std::chrono::milliseconds(5));
    recorder.Sample(shared, std::make_shared<const P::Snapshot>(pose), {}, 0, false);
    recorder.Encoded(pose, ems, wire->size());
    recorder.Sent(*packet);
  }
  recorder.Stop();
  recorder.Shutdown();

  const auto status = recorder.Read();
  CHECK(status.samples == frames);
  CHECK(status.dropped == 0);
  std::ofstream report(dir / "production.txt");
  report << "native_raw_bytes=" << asset->Value().nif.size() << "\nraw_container_bytes=" << model->rawBytes
         << "\ncompressed_bytes=" << model->compressed->size() << "\nsha256=" << P::Hex(model->hash) << "\nchannels=" << channels
         << "\nbounds=" << bounds << "\nframes=" << frames << "\nvalidate_ms=" << validateMs << "\ncompress_hash_ms=" << compressMs
         << "\ndecompress_validate_ms=" << decodeMs << "\nmax_position_error=" << positionMax << "\nmax_rotation_degrees=" << rotationMax
         << "\nmax_scale_error=" << scaleMax << "\narchive=" << status.directory << '\n';
}
#endif

TEST_CASE("Native NIF rejects malformed typed links and buffers")
{
  namespace P = Dreamsleeve::Client::Phantom;
  auto asset  = PhantomFixture::Model();
  REQUIRE(P::ValidatedAsset::Parse(asset));
  constexpr std::array<std::string_view, 4> types{"NiNode", "BSTriShape", "BSLightingShaderProperty", "BSShaderTextureSet"};
  std::size_t offset = std::string_view("Gamebryo File Format, Version 20.2.0.7\n").size() + 4 + 1 + 4 + 4 + 4 + 3 + 2 + 4 * 6 + 12;
  for (auto t : types)
    offset += 4 + t.size();
  const auto set = [&](std::size_t where, std::uint32_t value) {
    std::memcpy(asset.nif.data() + where, &value, 4);
  };
  SUBCASE("tree cycle")
  {
    set(offset + 76, 0);
  }
  SUBCASE("extra data")
  {
    set(offset + 4, 1);
  }
  SUBCASE("active controller")
  {
    set(offset + 8, 1);
  }
  SUBCASE("collision")
  {
    set(offset + 68, 1);
  }
  SUBCASE("nonfinite transform")
  {
    set(offset + 16, 0x7fc00000);
  }
  SUBCASE("wrong shader reference")
  {
    set(offset + 84 + 92, 0);
  }
  SUBCASE("skin outside blocks")
  {
    set(offset + 84 + 88, 65535);
  }
  SUBCASE("vertex stride")
  {
    set(offset + 84 + 100, 0);
  }
  SUBCASE("buffer length")
  {
    set(offset + 84 + 112, 1);
  }
  SUBCASE("vertex index")
  {
    asset.nif[offset + 84 + 116 + 48] = 255;
  }
  CHECK_FALSE(P::ValidatedAsset::Parse(std::move(asset)));
}

TEST_CASE("Native NIF rejects every truncated prefix without throwing")
{
  namespace N      = Dreamsleeve::Client::Phantom::Nif;
  const auto asset = PhantomFixture::Model();
  REQUIRE(N::Inspect(asset.nif));
  for (std::size_t size = 0; size < asset.nif.size(); ++size)
  {
    CAPTURE(size);
    Dreamsleeve::Client::Phantom::Result<N::Layout> result;
    CHECK_NOTHROW(result = N::Inspect(std::span(asset.nif).first(size)));
    CHECK_FALSE(result);
  }
}

TEST_CASE("Native NIF output backpatch preserves tail and transfers its allocation")
{
  using Dreamsleeve::Client::Phantom::NifOutput;
  NifOutput                         output(64, 64);
  const std::array<std::uint8_t, 8> data{1, 2, 3, 4, 5, 6, 7, 8};
  REQUIRE(output.Write(data) == data.size());
  const auto* allocation = output.Bytes().data();
  REQUIRE(output.Seek(-6));
  REQUIRE(output.Write(std::span(data).first(2)) == 2);
  CHECK(output.Position() == 4);
  CHECK(output.Bytes().size() == 8);
  REQUIRE(output.Seek(6));
  REQUIRE(output.Write(std::span(data).first(2)) == 2);
  CHECK(output.Position() == 12);
  auto result = std::move(output).Take();
  CHECK(result.data() == allocation);
  CHECK(result == std::vector<std::uint8_t>{1, 2, 1, 2, 5, 6, 7, 8, 0, 0, 1, 2});
}

TEST_CASE("Native NIF output supports overwrite followed by extension")
{
  Dreamsleeve::Client::Phantom::NifOutput output(64, 2);
  const std::array<std::uint8_t, 4>       data{1, 2, 3, 4};
  REQUIRE(output.Write(data) == 4);
  REQUIRE(output.Seek(-2));
  REQUIRE(output.Write(data) == 4);
  CHECK(std::move(output).Take() == std::vector<std::uint8_t>{1, 2, 1, 2, 3, 4});
}

TEST_CASE("Native NIF output budget failure is atomic and sticky")
{
  using Dreamsleeve::Client::Phantom::NifOutput;
  NifOutput                         output(4, 100);
  const std::array<std::uint8_t, 4> data{1, 2, 3, 4};
  REQUIRE(output.Write(data) == 4);
  CHECK(output.Write(std::span(data).first(1)) == 0);
  CHECK_FALSE(output.Good());
  CHECK_FALSE(output.Seek(-4));
  CHECK(output.Write({}) == 0);
  CHECK(output.Position() == 4);
  auto result = std::move(output).Take();
  CHECK(result.capacity() <= 4);
  CHECK(result == std::vector<std::uint8_t>{1, 2, 3, 4});
  for (const auto delta : {-1, 5, std::numeric_limits<std::int32_t>::max(), std::numeric_limits<std::int32_t>::min()})
  {
    NifOutput invalid(4);
    CHECK_FALSE(invalid.Seek(delta));
    CHECK_FALSE(invalid.Good());
    CHECK(invalid.Bytes().empty());
  }
}

TEST_CASE("Native NIF output permits exact budget and empty writes without gaps")
{
  Dreamsleeve::Client::Phantom::NifOutput output(4);
  CHECK(output.Seek(4));
  CHECK(output.Write({}) == 0);
  CHECK(output.Good());
  CHECK(output.Bytes().empty());
  CHECK(output.Seek(-4));
  const std::array<std::uint8_t, 4> data{1, 2, 3, 4};
  CHECK(output.Write(data) == 4);
  CHECK(output.Good());
}

TEST_CASE("Native NIF recorded bytes survive seekable output and size backpatch" * doctest::skip(NativeFixture().empty()))
{
  namespace P = Dreamsleeve::Client::Phantom;
  std::ifstream input(NativeFixture(), std::ios::binary);
  REQUIRE(input);
  const std::vector<std::uint8_t> bytes{std::istreambuf_iterator<char>(input), {}};
  REQUIRE(bytes.size() > 4096);
  REQUIRE(P::Nif::Inspect(bytes));
  for (auto hint : {0u, static_cast<std::uint32_t>(bytes.size())})
  {
    P::NifOutput output(P::Limits{}.assetBytes, hint);
    const auto   view = std::span(bytes);
    // Small header/field writes followed by detached data blocks; the size
    // table is backpatched afterwards, leaving the final cursor inside NIF.
    for (std::size_t i = 0; i < 4096; i += 4)
      REQUIRE(output.Write(view.subspan(i, 4)) == 4);
    for (std::size_t i = 4096; i < bytes.size(); i += 65536)
    {
      const auto part = view.subspan(i, std::min<std::size_t>(65536, bytes.size() - i));
      REQUIRE(output.Write(part) == part.size());
    }
    REQUIRE(output.Seek(128 - static_cast<std::int32_t>(output.Position())));
    REQUIRE(output.Write(view.subspan(128, 256)) == 256);
    auto result = std::move(output).Take();
    CHECK(result == bytes);
    CHECK(P::Nif::Inspect(result));
  }
}

TEST_CASE("Native NIF output size hint absorbs small growth without another allocation")
{
  Dreamsleeve::Client::Phantom::NifOutput output(4096, 1024);
  const std::array<std::uint8_t, 1024>    data{};
  REQUIRE(output.Write(data) == data.size());
  const auto* allocation = output.Bytes().data();
  REQUIRE(output.Write(std::span(data).first(64)) == 64);
  CHECK(output.Bytes().data() == allocation);
  CHECK(output.Bytes().size() == 1088);
  output.Reject();
  CHECK_FALSE(output.Good());
  CHECK(output.Status() == Dreamsleeve::Client::Phantom::NifOutput::Error::UnsupportedOperation);
  CHECK(output.Write(data) == 0);
}
