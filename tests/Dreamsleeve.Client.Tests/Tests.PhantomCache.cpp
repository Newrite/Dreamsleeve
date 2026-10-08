#include <doctest/doctest.h>
import std;
import Dreamsleeve.Client.Phantom.Cache;
import Dreamsleeve.Client.Phantom.Worker;

#include "PhantomFixture.hpp"

namespace
{
  namespace P = Dreamsleeve::Client::Phantom;

  struct Directory
  {
    std::filesystem::path path = std::filesystem::temp_directory_path() /
                                 ("dreamsleeve-cache-" + std::to_string(std::chrono::steady_clock::now().time_since_epoch().count()));

    Directory()
    {
      std::filesystem::create_directories(path);
    }

    ~Directory()
    {
      std::error_code error;
      std::filesystem::remove_all(path, error);
    }
  };

  P::Digest Hash(unsigned value)
  {
    P::Digest hash{};
    hash.front() = static_cast<std::uint8_t>(value);
    return hash;
  }

  void Write(const std::filesystem::path& path, std::span<const std::uint8_t> bytes)
  {
    std::ofstream out(path, std::ios::binary);
    out.write(reinterpret_cast<const char*>(bytes.data()), static_cast<std::streamsize>(bytes.size()));
    out.close();
    REQUIRE(out);
  }

  template <class Test>
  bool Until(Test condition)
  {
    const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(2);
    while (!condition())
    {
      if (std::chrono::steady_clock::now() >= deadline) return false;
      std::this_thread::sleep_for(std::chrono::milliseconds(5));
    }
    return true;
  }

  P::PreparedAsset Model(std::size_t nodes = 2)
  {
    auto asset = P::ValidatedAsset::Parse(PhantomFixture::Model(nodes));
    REQUIRE(asset);
    auto prepared = P::Prepare(std::move(*asset));
    REQUIRE(prepared);
    return std::move(*prepared);
  }

  P::Wire::Descriptor Describe(const P::PreparedAsset& model, std::uint64_t generation = 1)
  {
    return {
        model.hash,
        {generation},
        P::AssetVersion,
        static_cast<std::uint32_t>(model.compressed->size()),
        model.rawBytes,
        static_cast<std::uint32_t>(model.asset.Layout().requiredChannels.size())
    };
  }

}

TEST_SUITE("Client.PhantomCache")
{
  TEST_CASE("Disabled and absent cache are intentional absence and skipped writes")
  {
    Directory      directory;
    P::Cache       disabled({}), absent(directory.path / "not-created");
    const P::Bytes bytes{1, 2, 3};
    for (const auto* cache : {&disabled, &absent})
    {
      auto read = cache->Read(Hash(1));
      REQUIRE(read);
      CHECK_FALSE(*read);
      REQUIRE(cache->Trim(100));
    }
    auto skipped = disabled.Save(Hash(1), bytes, 100);
    REQUIRE(skipped);
    CHECK(*skipped == P::CacheWrite::Skipped);
    CHECK_FALSE(std::filesystem::exists(directory.path / "not-created"));
  }

  TEST_CASE("Valid bytes and corrupt metadata remain distinct typed cache outcomes")
  {
    Directory      directory;
    P::Cache       cache(directory.path);
    const P::Bytes bytes{1, 2, 3};
    REQUIRE(cache.Save(Hash(1), bytes, 100));
    auto read = cache.Read(Hash(1), 3);
    REQUIRE(read);
    REQUIRE(*read);
    CHECK(**read == bytes);
    auto corrupt = cache.Read(Hash(1), 4);
    REQUIRE_FALSE(corrupt);
    CHECK(corrupt.error().reason == P::Failure::InvalidFormat);
    Write(directory.path / (P::Hex(Hash(1)) + ".zst"), {});
    auto empty = cache.Read(Hash(1));
    REQUIRE_FALSE(empty);
    CHECK(empty.error().reason == P::Failure::InvalidFormat);
  }

  TEST_CASE("A cache root that is a file is Storage failure rather than a miss")
  {
    Directory      directory;
    const P::Bytes bytes{9};
    const auto     root = directory.path / "blocked";
    Write(root, bytes);
    P::Cache cache(root);
    auto     read = cache.Read(Hash(1));
    REQUIRE_FALSE(read);
    CHECK(read.error().reason == P::Failure::Storage);
    auto trimmed = cache.Trim(100);
    REQUIRE_FALSE(trimmed);
    CHECK(trimmed.error().reason == P::Failure::Storage);
    auto saved = cache.Save(Hash(1), bytes, 100);
    REQUIRE_FALSE(saved);
    CHECK(saved.error().reason == P::Failure::Storage);
    CHECK(std::filesystem::file_size(root) == 1);
  }

  TEST_CASE("Existing-target rename atomically replaces content and removes its partial")
  {
    Directory      directory;
    P::Cache       cache(directory.path);
    const P::Bytes original{1, 2, 3}, replacement{4, 5, 6};
    REQUIRE(cache.Save(Hash(1), original, 100));
    auto replaced = cache.Save(Hash(1), replacement, 100);
    REQUIRE(replaced);
    CHECK(*replaced == P::CacheWrite::Stored);
    auto read = cache.Read(Hash(1));
    REQUIRE(read);
    REQUIRE(*read);
    CHECK(**read == replacement);
    CHECK_FALSE(std::filesystem::exists(directory.path / (P::Hex(Hash(1)) + ".partial")));
  }

  TEST_CASE("Failed cache removal is reported and leaves unrelated content intact")
  {
    Directory  directory;
    const auto path = directory.path / (P::Hex(Hash(1)) + ".zst");
    std::filesystem::create_directories(path);
    Write(path / "keep", P::Bytes{8});
    P::Cache cache(directory.path);
    auto     removed = cache.Erase(Hash(1));
    REQUIRE_FALSE(removed);
    CHECK(removed.error().reason == P::Failure::Storage);
    CHECK(std::filesystem::exists(path / "keep"));
    auto saved = cache.Save(Hash(1), P::Bytes{1, 2, 3}, 100);
    REQUIRE_FALSE(saved);
    CHECK(saved.error().reason == P::Failure::Storage);
    CHECK_FALSE(std::filesystem::exists(directory.path / (P::Hex(Hash(1)) + ".partial")));
    CHECK(std::filesystem::exists(path / "keep"));
  }

  TEST_CASE("Trimming uses captured age and size, clears partials and ignores Unicode files")
  {
    Directory  directory;
    P::Cache   cache(directory.path);
    const auto now = std::filesystem::file_time_type::clock::now();
    for (unsigned index = 1; index <= 3; ++index)
    {
      REQUIRE(cache.Save(Hash(index), P::Bytes(10 * index, 1), 100));
      std::filesystem::last_write_time(directory.path / (P::Hex(Hash(index)) + ".zst"), now + std::chrono::seconds(index));
    }
    const auto unrelated = directory.path / std::filesystem::path{L"чужой.txt"};
    Write(unrelated, P::Bytes{7});
    Write(directory.path / (P::Hex(Hash(4)) + ".partial"), P::Bytes{1});
    REQUIRE(cache.Trim(30));
    CHECK_FALSE(std::filesystem::exists(directory.path / (P::Hex(Hash(1)) + ".zst")));
    CHECK_FALSE(std::filesystem::exists(directory.path / (P::Hex(Hash(2)) + ".zst")));
    CHECK(std::filesystem::exists(directory.path / (P::Hex(Hash(3)) + ".zst")));
    CHECK_FALSE(std::filesystem::exists(directory.path / (P::Hex(Hash(4)) + ".partial")));
    CHECK(std::filesystem::exists(unrelated));
  }

  TEST_CASE("Optional disk failure keeps validated network replacement and its prior generation usable")
  {
    Directory  directory;
    const auto root = directory.path / "blocked";
    Write(root, P::Bytes{9});
    P::Exchange    exchange;
    P::Worker      worker(exchange, root);
    auto           first = Model();
    P::Wire::Offer offer{42, 1, Describe(first)};
    REQUIRE(exchange.Offer(offer));
    REQUIRE(worker.Queue(offer, first.compressed));
    REQUIRE(Until([&] { return exchange.Find(42)->Asset() != nullptr; }));
    CHECK(exchange.Stats().rejected == 0);
    CHECK_FALSE(exchange.Stats().error.empty());
    auto           second = Model(3);
    P::Wire::Offer replacement{42, 2, Describe(second, 2)};
    REQUIRE(exchange.Offer(replacement));
    REQUIRE(worker.Queue(replacement, second.compressed));
    REQUIRE(Until([&] { return exchange.Find(42)->Asset() != nullptr; }));
    auto remote = exchange.Find(42);
    REQUIRE(remote);
    REQUIRE(remote->previous);
    CHECK(remote->previous->Asset() != nullptr);
    CHECK(remote->Asset()->Value().nif == second.asset.Value().nif);
    CHECK(exchange.Stats().rejected == 0);
    CHECK_FALSE(exchange.Stats().error.empty());
  }

  TEST_CASE("Checksum-corrupt disk bytes are erased and request a full download without rejection")
  {
    Directory directory;
    auto      model    = Model();
    auto      corrupt  = *model.compressed;
    corrupt.front()   ^= 1;
    P::Cache cache(directory.path);
    REQUIRE(cache.Save(model.hash, corrupt, 1024 * 1024));
    P::Exchange    exchange;
    P::Worker      worker(exchange, directory.path);
    P::Wire::Offer offer{42, 1, Describe(model)};
    REQUIRE(exchange.Offer(offer));
    REQUIRE(worker.Queue(offer));
    REQUIRE(Until([&] {
      auto missing = worker.TakeMissing();
      if (missing.empty()) return false;
      CHECK(missing.front().offer.player == 42);
      CHECK_FALSE(missing.front().baseHash);
      return true;
    }));
    CHECK_FALSE(std::filesystem::exists(directory.path / (P::Hex(model.hash) + ".zst")));
    CHECK(exchange.Stats().rejected == 0);
    CHECK_FALSE(exchange.Stats().error.empty());
  }
}
