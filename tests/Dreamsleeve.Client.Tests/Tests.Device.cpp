#include <doctest/doctest.h>

import std;
import Dreamsleeve.Client.Device;

namespace Device = Dreamsleeve::Client::Device;

namespace
{

  constexpr Device::Uuid      Real{0x4C, 0x4C, 0x45, 0x44, 0x00, 0x30, 0x10, 0x80, 0x80, 0x31, 0xB2, 0xC0, 0x4F, 0x51, 0x32, 0x33};
  constexpr std::wstring_view Scope = L"Dreamsleeve/Auth/v1/https/example.org/443";

  // A raw SMBIOS table as GetSystemFirmwareTable('RSMB') returns it: the header,
  // a BIOS structure (type 0) with one string, the System Information
  // structure (type 1) carrying uuid, and the end-of-table structure.
  std::vector<std::uint8_t> Table(const Device::Uuid& uuid)
  {
    std::vector<std::uint8_t> structures(0x18, 0);
    structures[1] = 0x18;
    for (const char c : std::string_view{"Vendor"})
      structures.push_back(static_cast<std::uint8_t>(c));
    structures.insert(structures.end(), {0, 0});

    std::vector<std::uint8_t> system(0x1B, 0);
    system[0] = 1;
    system[1] = 0x1B;
    std::ranges::copy(uuid, system.begin() + 8);
    structures.insert(structures.end(), system.begin(), system.end());
    structures.insert(structures.end(), {0, 0, 127, 4, 0, 0, 0, 0});

    std::vector<std::uint8_t> table{0, 3, 4, 0};
    const auto                length = static_cast<std::uint32_t>(structures.size());
    for (int shift = 0; shift < 32; shift += 8)
      table.push_back(static_cast<std::uint8_t>(length >> shift));
    table.insert(table.end(), structures.begin(), structures.end());
    return table;
  }

}

TEST_SUITE_BEGIN("Client.Device");

TEST_CASE("The board UUID comes from the System Information structure; placeholders and broken tables give none")
{
  CHECK(Device::SystemUuid(Table(Real)) == Real);
  CHECK_FALSE(Device::SystemUuid(Table(Device::Uuid{})));

  Device::Uuid ones{};
  ones.fill(0xFF);
  CHECK_FALSE(Device::SystemUuid(Table(ones)));

  constexpr Device::Uuid oem{0x00, 0x02, 0x00, 0x03, 0x00, 0x04, 0x00, 0x05, 0x00, 0x06, 0x00, 0x07, 0x00, 0x08, 0x00, 0x09};
  CHECK_FALSE(Device::SystemUuid(Table(oem)));

  const auto table = Table(Real);
  CHECK_FALSE(Device::SystemUuid(std::span{table}.first(table.size() - 30)));
  CHECK_FALSE(Device::SystemUuid({}));
}

TEST_CASE("The device hash is stable for one server, unrelated across servers and keeps its documented form")
{
  const auto hash = Device::Hash(Scope, Real);
  // SHA-256("dreamsleeve-device-v1" NUL scope-as-UTF-16LE NUL uuid): changing the
  // form would release every device ban already stored.
  CHECK(hash == "f7b00a79d03045c0ccc361c2a0a545f1cf528da7c5a2dfc640ecbe6ba39f1b6d");
  CHECK(hash != Device::Hash(L"Dreamsleeve/Auth/v1/https/other.example/443", Real));

  auto other    = Real;
  other.back() ^= 1;
  CHECK(hash != Device::Hash(Scope, other));
}

TEST_SUITE_END();
