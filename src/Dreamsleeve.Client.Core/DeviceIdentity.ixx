module;
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <bcrypt.h>

export module Dreamsleeve.Client.Device;

import std;

// The device hash sent with every sign-in and registration
// (docs/AuthenticationRu.md, «Устройства»): SHA-256 of the board UUID from
// SMBIOS together with the server, so each server sees an unrelated value and
// the UUID itself never leaves the computer. A board without a real UUID
// (zeros, ones, the "To be filled by O.E.M." sequence, Wine) sends none.
export namespace Dreamsleeve::Client::Device
{

  using Uuid = std::array<std::uint8_t, 16>;

  // Values many boards share, so a ban of one would hit them all.
  bool Placeholder(const Uuid& uuid)
  {
    // As stored: the first three fields little-endian, "03000200-0400-0500-0006-000700080009".
    constexpr Uuid Sequence{0x00, 0x02, 0x00, 0x03, 0x00, 0x04, 0x00, 0x05, 0x00, 0x06, 0x00, 0x07, 0x00, 0x08, 0x00, 0x09};
    return uuid == Sequence || std::ranges::all_of(uuid, [&](std::uint8_t value) { return value == uuid.front(); });
  }

  // The UUID of the System Information structure (type 1) in a firmware table
  // as GetSystemFirmwareTable('RSMB') returns it: an 8-byte header (calling
  // method, versions, revision, table length), then the SMBIOS structures.
  std::optional<Uuid> SystemUuid(std::span<const std::uint8_t> table)
  {
    if (table.size() < 8) return std::nullopt;
    const std::size_t length = table[4] | (table[5] << 8) | (table[6] << 16) | (std::size_t{table[7]} << 24);
    auto              data   = table.subspan(8);
    if (length < data.size()) data = data.first(length);
    std::size_t at = 0;
    while (at + 4 <= data.size())
    {
      const auto type      = data[at];
      const auto formatted = std::size_t{data[at + 1]};
      if (formatted < 4 || at + formatted > data.size()) return std::nullopt;
      // The UUID is at offset 8 since SMBIOS 2.1, which made the structure 0x19 bytes long.
      if (type == 1 && formatted >= 0x19)
      {
        Uuid uuid{};
        std::ranges::copy(data.subspan(at + 8, uuid.size()), uuid.begin());
        if (Placeholder(uuid)) return std::nullopt;
        return uuid;
      }
      if (type == 127) return std::nullopt;  // End of table.
      // The strings follow the formatted area and end with an empty one.
      auto next = at + formatted;
      while (next + 1 < data.size() && (data[next] != 0 || data[next + 1] != 0))
        ++next;
      at = next + 2;
    }
    return std::nullopt;
  }

  // SHA-256 of a version tag, the server scope and the UUID, as 64 lowercase hex characters.
  std::string Hash(std::wstring_view scope, const Uuid& uuid)
  {
    constexpr std::string_view Tag = "dreamsleeve-device-v1";
    std::vector<std::uint8_t>  input(Tag.begin(), Tag.end());
    input.push_back(0);
    const auto* bytes = reinterpret_cast<const std::uint8_t*>(scope.data());
    input.insert(input.end(), bytes, bytes + scope.size() * sizeof(wchar_t));
    input.push_back(0);
    input.insert(input.end(), uuid.begin(), uuid.end());
    std::array<std::uint8_t, 32> digest{};
    const auto                   status = BCryptHash(
      BCRYPT_SHA256_ALG_HANDLE,
      nullptr,
      0,
      input.data(),
      static_cast<ULONG>(input.size()),
      digest.data(),
      static_cast<ULONG>(digest.size()));
    if (!BCRYPT_SUCCESS(status)) return {};
    std::string text;
    text.reserve(digest.size() * 2);
    for (const auto value : digest)
      text += std::format("{:02x}", value);
    return text;
  }

  std::optional<Uuid> BoardUuid()
  {
    constexpr DWORD Rsmb = 0x52534D42;  // 'RSMB': the raw SMBIOS table.
    const auto      size = GetSystemFirmwareTable(Rsmb, 0, nullptr, 0);
    if (size == 0) return std::nullopt;
    std::vector<std::uint8_t> table(size);
    if (GetSystemFirmwareTable(Rsmb, 0, table.data(), size) != size) return std::nullopt;
    return SystemUuid(table);
  }

  // The hash for one server, named by its credential scope; none without a real board UUID.
  std::optional<std::string> Identify(std::wstring_view scope)
  {
    const auto uuid = BoardUuid();
    if (!uuid) return std::nullopt;
    auto hash = Hash(scope, *uuid);
    if (hash.empty()) return std::nullopt;
    return hash;
  }

}
