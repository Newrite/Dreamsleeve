#include <doctest/doctest.h>
#include <glaze/glaze.hpp>
import std;
import Dreamsleeve.Host.Bridge;
import Dreamsleeve.Game.PhantomCapture;
import Dreamsleeve.Host.PhantomArchive;
using namespace Dreamsleeve::Host;

TEST_CASE("Phantom controls admit only bounded local actions and sample rates")
{
  for (const auto action : {"query", "record", "stop", "play", "clear", "load"})
    for (const auto rate : {20, 40})
    {
      auto parsed = Bridge::ParseCommand(std::format(R"({{"type":"phantom","action":"{}","rate":{}}})", action, rate));
      REQUIRE(parsed);
      REQUIRE(std::holds_alternative<Bridge::Commands::Phantom>(*parsed));
      CHECK(std::get<Bridge::Commands::Phantom>(*parsed).rate == rate);
    }
  CHECK_FALSE(Bridge::ParseCommand(R"({"type":"phantom","action":"upload","rate":20})"));
  CHECK_FALSE(Bridge::ParseCommand(R"({"type":"phantom","action":"play","rate":20,"poseMode":"bad"})"));
  CHECK_FALSE(Bridge::ParseCommand(R"({"type":"phantom","action":"play","rate":20,"modelMode":"bad"})"));
  CHECK_FALSE(Bridge::ParseCommand(R"({"type":"phantom","action":"record","rate":20,"scenario":"../outside"})"));
  for (const auto scenario : {"idle", "movement", "combat", "camera", "equipment", "mixed"})
  {
    auto parsed = Bridge::ParseCommand(std::format(R"({{"type":"phantom","action":"record","rate":20,"scenario":"{}"}})", scenario));
    REQUIRE(parsed);
    CHECK(std::get<Bridge::Commands::Phantom>(*parsed).scenario == scenario);
  }
  CHECK_FALSE(Bridge::ParseCommand(R"({"type":"phantom","action":"record","rate":0})"));
  CHECK_FALSE(Bridge::ParseCommand(R"({"type":"phantom","action":"record","rate":1000000})"));
}

TEST_CASE("Phantom status crosses the bridge as data including failures")
{
  Bridge::PhantomEvent status{.supported = true, .ready = true, .frames = 300, .appearanceBytes = 1024, .status = "NiStream: <failed>"};
  auto                 encoded = Bridge::Encode(Bridge::HostEvent{status});
  REQUIRE(encoded);
  CHECK(encoded->find(R"("type":"phantom")") != std::string::npos);
  glz::generic json;
  REQUIRE_FALSE(glz::read_json(json, *encoded));
  CHECK(json["status"].get_string() == status.status);
  CHECK(json["frames"].get_number() == 300);
}

TEST_CASE("Phantom capture tolerates weapon reparenting and temporary removal")
{
  struct Node
  {
    Node* parent{};
    bool  hidden{};
  };

  Node       root, sheath{&root}, hand{&root}, weapon{&sheath};
  const auto locate = [&](Node* node) {
    return PhantomCapture::Locate(&root, node, [](Node* n) { return n->parent; }, [](Node* n) { return n->hidden; });
  };
  CHECK(locate(&weapon).present);
  weapon.parent = &hand;
  CHECK(locate(&weapon).present);
  Node extra{&hand};
  CHECK(locate(&extra).present);
  CHECK(locate(&weapon).present);
  hand.hidden = true;
  CHECK(locate(&weapon).hidden);
  weapon.parent = nullptr;
  CHECK_FALSE(locate(&weapon).present);
  weapon.parent = &sheath;
  CHECK(locate(&weapon).present);
  CHECK_FALSE(locate(&weapon).hidden);
  Node replacedRoot;
  CHECK_FALSE(PhantomCapture::Locate(&replacedRoot, &weapon, [](Node* n) { return n->parent; }, [](Node* n) { return n->hidden; }).present);
}

TEST_CASE("Phantom capture rejects cyclic attachment chains")
{
  struct Node
  {
    Node* parent{};
  };

  Node root, a, b;
  a.parent = &b;
  b.parent = &a;
  CHECK_FALSE(PhantomCapture::Locate(&root, &a, [](Node* n) { return n->parent; }, [](Node*) { return false; }).present);
}

TEST_CASE("Phantom bone channels resolve fresh relocated and reordered arrays by name")
{
  struct Bone
  {
    std::string name;
    int         pose;
  };

  auto name = [](const Bone& bone) {
    return std::string_view{bone.name};
  };
  const std::vector<Bone> original{
      {"Pelvis", 1},
      {"Hand",   2}
  };
  REQUIRE(PhantomCapture::FindBone(std::span<const Bone>{original}, "Hand", 1, name) == 1);
  // Keep the old allocation alive so the fresh array cannot reuse its address.
  const std::vector<Bone> current{
      {"Hand",   12},
      {"Pelvis", 11},
      {"Weapon", 13}
  };
  const auto resolved = PhantomCapture::FindBone(std::span<const Bone>{current}, "Hand", 1, name);
  REQUIRE(resolved);
  CHECK(current[*resolved].pose == 12);
  CHECK_FALSE(PhantomCapture::FindBone(std::span<const Bone>{current}, "Missing", 500, name));
  CHECK_FALSE(PhantomCapture::FindBone(std::span<const Bone>{}, "Hand", 1, name));
  const std::vector<Bone> ambiguous{
      {"Hand", 1},
      {"Hand", 2}
  };
  CHECK_FALSE(PhantomCapture::FindBone(std::span<const Bone>{ambiguous}, "Hand", 500, name));
}

TEST_CASE("Phantom camera culling does not become recorded appearance visibility")
{
  PhantomCapture::Visibility visible, hidden{true};
  CHECK_FALSE(visible.Sample(true, true, true));
  CHECK(hidden.Sample(true, false, true));
  CHECK(visible.Sample(false, false, true));
  CHECK_FALSE(visible.Sample(true, false, true));
  CHECK(visible.Sample(true, true, false));
  CHECK(visible.Sample(true, false, true));
  CHECK_FALSE(visible.Sample(true, false, false));
  CHECK_FALSE(visible.Sample(true, true, true));
}

TEST_CASE("Phantom capture ignores the camera-hidden character root")
{
  struct Node
  {
    Node* parent{};
    bool  hidden{};
  };

  Node       root{nullptr, true}, body{&root};
  const auto a = PhantomCapture::Locate(&root, &body, [](Node* n) { return n->parent; }, [](Node* n) { return n->hidden; });
  CHECK(a.present);
  CHECK_FALSE(a.hidden);
  body.hidden = true;
  CHECK(PhantomCapture::Locate(&root, &body, [](Node* n) { return n->parent; }, [](Node* n) { return n->hidden; }).hidden);
}

TEST_CASE("Phantom recording can cross exterior cells but not worlds or interiors")
{
  const PhantomCapture::Space exterior{10, 100}, interior{20, 0};
  CHECK(exterior.Contains({11, 100}));
  CHECK_FALSE(exterior.Contains({11, 101}));
  CHECK_FALSE(exterior.Contains({10, 0}));
  CHECK(interior.Contains({20, 0}));
  CHECK_FALSE(interior.Contains({21, 0}));
  CHECK_FALSE(interior.Contains({20, 100}));
  CHECK_FALSE(exterior.Contains({0, 100}));
}

namespace
{

  struct PhantomPoint
  {
    float x{}, y{}, z{};

    PhantomPoint operator-(PhantomPoint b) const
    {
      return {x - b.x, y - b.y, z - b.z};
    }

    PhantomPoint operator+(PhantomPoint b) const
    {
      return {x + b.x, y + b.y, z + b.z};
    }

    PhantomPoint operator*(float k) const
    {
      return {x * k, y * k, z * k};
    }
  };

  struct PhantomBound
  {
    PhantomPoint center;
    float        radius{};
  };

  bool Contains(const PhantomBound& parent, const PhantomBound& child)
  {
    const auto d = child.center - parent.center;
    return std::sqrt(d.x * d.x + d.y * d.y + d.z * d.z) + child.radius <= parent.radius + 0.001f;
  }

}

TEST_CASE("Phantom replay bounds enclose drawn weapons despite empty live sheath bounds")
{
  const PhantomBound sheathed{
      {0, 0, 0},
      4
  },
    drawn{{80, 30, 20}, 4}, body{{0, 0, 40}, 20};
  CHECK_FALSE(Contains(sheathed, drawn));
  // The weapon stays under the original replay sheath while its world pose
  // moves into the hand. The live sheath now has no children and radius zero.
  PhantomBound replaySheath{};
  PhantomCapture::Enclose(replaySheath, drawn);
  REQUIRE(Contains(replaySheath, drawn));
  PhantomBound replaySkeleton = body;
  PhantomCapture::Enclose(replaySkeleton, replaySheath);
  CHECK(Contains(replaySkeleton, body));
  CHECK(Contains(replaySkeleton, drawn));
  // Sheathing again must not retain a huge historical bounding sphere.
  replaySheath = {};
  PhantomCapture::Enclose(replaySheath, sheathed);
  CHECK(replaySheath.radius == sheathed.radius);
  CHECK(replaySheath.center.x == sheathed.center.x);
}

TEST_CASE("Phantom replay bound union handles containment coincident centers and empty nodes")
{
  PhantomBound bound{
      {1, 2, 3},
      10
  };
  PhantomCapture::Enclose(
    bound,
    PhantomBound{
        {1, 2, 3},
        2
  });
  CHECK(bound.radius == 10);
  PhantomCapture::Enclose(
    bound,
    PhantomBound{
        {1, 2, 3},
        20
  });
  CHECK(bound.radius == 20);
  PhantomCapture::Enclose(
    bound,
    PhantomBound{
        {10000, 10000, 10000},
        0
  });
  CHECK(bound.radius == 20);
  const PhantomBound outside{
      {-20, 40, -10},
      5
  };
  const auto previous = bound;
  PhantomCapture::Enclose(bound, outside);
  CHECK(Contains(bound, previous));
  CHECK(Contains(bound, outside));
}

TEST_CASE("Phantom VR occlusion box encloses the moving replay sphere")
{
  struct Box
  {
    PhantomPoint center;
    PhantomPoint halfExtents;
  };

  Box box{
      {10000, 10000, 10000},
      {1,     1,     1    }
  };
  PhantomBound sphere{
      {-80, 30, 20},
      4
  };
  PhantomCapture::OcclusionBox(sphere, box);
  for (
    const auto direction : {
        PhantomPoint{1,  0,  0 },
        PhantomPoint{-1, 0,  0 },
        PhantomPoint{0,  1,  0 },
        PhantomPoint{0,  -1, 0 },
        PhantomPoint{0,  0,  1 },
        PhantomPoint{0,  0,  -1}
  })
  {
    const auto edge = sphere.center + direction * sphere.radius;
    CHECK(std::abs(edge.x - box.center.x) <= box.halfExtents.x);
    CHECK(std::abs(edge.y - box.center.y) <= box.halfExtents.y);
    CHECK(std::abs(edge.z - box.center.z) <= box.halfExtents.z);
  }
  sphere = {
      {1, 2, 3},
      0
  };
  PhantomCapture::OcclusionBox(sphere, box);
  CHECK(box.center.x == 1);
  CHECK(box.center.y == 2);
  CHECK(box.center.z == 3);
  CHECK(box.halfExtents.x == 0);
  CHECK(box.halfExtents.y == 0);
  CHECK(box.halfExtents.z == 0);
}

TEST_CASE("Phantom archive uses a fixed little endian layout without native padding")
{
  std::ostringstream out(std::ios::binary);
  PhantomArchive::Header(out, 1, 2, 20);
  PhantomArchive::Scalar(out, 1.25);
  PhantomArchive::Scalar(out, std::uint8_t{1});
  PhantomArchive::Pose pose;
  pose.values[0] = 1.0f;
  pose.hidden    = true;
  pose.parent    = PhantomArchive::NoParent;
  PhantomArchive::WritePose(out, pose);
  const auto bytes = out.str();
  REQUIRE(bytes.size() == PhantomArchive::HeaderBytes + PhantomArchive::FrameHeaderBytes + PhantomArchive::PoseBytes);
  CHECK(bytes.substr(0, 8) == "DSPPOSE1");
  CHECK(static_cast<unsigned char>(bytes[8]) == 1);
  CHECK(static_cast<unsigned char>(bytes[12]) == 1);
  CHECK(static_cast<unsigned char>(bytes[16]) == 2);
  CHECK(static_cast<unsigned char>(bytes[20]) == 20);
  // IEEE float32 1.0 in little endian; an independent byte-level check.
  CHECK(static_cast<unsigned char>(bytes[33]) == 0);
  CHECK(static_cast<unsigned char>(bytes[34]) == 0);
  CHECK(static_cast<unsigned char>(bytes[35]) == 128);
  CHECK(static_cast<unsigned char>(bytes[36]) == 63);
  CHECK(static_cast<unsigned char>(bytes[101]) == 1);
  CHECK(bytes.substr(102) == std::string(4, static_cast<char>(255)));
  pose.values[7] = std::numeric_limits<float>::quiet_NaN();
  CHECK_THROWS_AS(PhantomArchive::WritePose(out, pose), std::runtime_error);
}

TEST_CASE("Phantom archive writes on a worker and marks only complete results")
{
  const auto             base = std::filesystem::temp_directory_path() /
                                std::format("DreamsleevePhantomTests-{}", std::chrono::steady_clock::now().time_since_epoch().count());
  PhantomArchive::Writer writer;
  std::promise<void>     release;

  struct Release
  {
    std::promise<void>& promise;
    bool                done{};

    void Open()
    {
      if (!done)
      {
        done = true;
        promise.set_value();
      }
    }

    ~Release()
    {
      Open();
    }
  } gate{release};

  const auto wait = release.get_future().share();
  REQUIRE(writer.Submit(base, "equipment", [wait](const auto& directory) {
    wait.wait();
    std::ofstream model(directory / "appearance.nif", std::ios::binary);
    model << "local";
  }));
  CHECK(writer.Busy());
  CHECK_FALSE(writer.Submit(base, "mixed", [](const auto&) {}));
  gate.Open();
  writer.Shutdown();
  const auto result = writer.Poll();
  REQUIRE(result);
  CHECK(result->error.empty());
  CHECK(std::filesystem::is_regular_file(std::filesystem::path(result->path) / "complete.txt"));
  CHECK_FALSE(writer.Submit(base, "mixed", [](const auto&) {}));
  // Delete only explicitly created files and empty directories; no recursive deletion.
  std::filesystem::remove(std::filesystem::path(result->path) / "appearance.nif");
  std::filesystem::remove(std::filesystem::path(result->path) / "complete.txt");
  std::filesystem::remove(result->path);
  std::filesystem::remove(base);
}

TEST_CASE("Phantom archive errors preserve partial files without a complete marker")
{
  const auto             base = std::filesystem::temp_directory_path() /
                                std::format("DreamsleevePhantomFailure-{}", std::chrono::steady_clock::now().time_since_epoch().count());
  PhantomArchive::Writer writer;
  CHECK_FALSE(writer.Submit(base, "../../bad", [](const auto&) {}));
  REQUIRE(writer.Submit(base, "idle", [](const auto&) { throw std::runtime_error("simulated write failure"); }));
  writer.Shutdown();
  const auto result = writer.Poll();
  REQUIRE(result);
  CHECK(result->error == "simulated write failure");
  CHECK_FALSE(std::filesystem::exists(std::filesystem::path(result->path) / "complete.txt"));
  std::filesystem::remove(result->path);
  std::filesystem::remove(base);
}
