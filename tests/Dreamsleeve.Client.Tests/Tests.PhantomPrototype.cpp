#include <cstdlib>
#include <fstream>
#include <string>
#include <glaze/glaze.hpp>
#include <doctest/doctest.h>
import std;
import Dreamsleeve.Game.PhantomCaptureRules;

namespace
{

  std::string PrototypeFixturePath()
  {
    char*       text{};
    std::size_t size{};
    if (_dupenv_s(&text, &size, "DREAMSLEEVE_PHANTOM_PROTOTYPE_FIXTURES") != 0) return {};
    const auto owner = std::unique_ptr<char, decltype(&std::free)>(text, &std::free);
    return owner && size > 1 && size <= 32768 ? std::string(owner.get()) : std::string{};
  }

}

TEST_CASE("Saved prototype material exclusions survive the neutral capture port" * doctest::skip(PrototypeFixturePath().empty()))
{
  namespace C          = Dreamsleeve::Game::PhantomCapture;
  std::size_t archives = 0;
  std::size_t meshes = 0;

  for (const auto& entry : std::filesystem::directory_iterator(PrototypeFixturePath()))
  {
    if (entry.path().extension() != ".json") continue;
    REQUIRE(++archives <= 64);
    REQUIRE(entry.file_size() < 1024 * 1024);

    std::ifstream input(entry.path(), std::ios::binary);
    REQUIRE(input);
    std::string  text{std::istreambuf_iterator<char>(input), {}};
    glz::generic json;
    REQUIRE_FALSE(bool(glz::read_json(json, text)));

    const auto& records = json.get<glz::generic::array_t>();
    REQUIRE(records.size() <= 4096);
    for (const auto& record : records)
    {
      const auto& name = record["name"].get<std::string>();
      INFO(entry.path().filename().string(), ": ", name);
      C::Surface surface{
          record["effectMaterial"].get<bool>(),
          record["decalMaterial"].get<bool>(),
          record["skinned"].get<bool>(),
          record["dedicatedDecal"].get<bool>(),
          name,
          record["hasShader"].get<bool>(),
          float(record["shaderAlpha"].get<double>()),
          float(record["materialAlpha"].get<double>())
      };
      // Dedicated blood surfaces were excluded more strictly after the
      // prototype; every other saved selection must still match its oracle.
      const bool excluded = record["excluded"].get<bool>() || name == "EdgeBlood12";
      CHECK(surface.Auxiliary() == excluded);
      ++meshes;
    }
  }

  REQUIRE(archives > 0);
  REQUIRE(meshes > 0);
}
