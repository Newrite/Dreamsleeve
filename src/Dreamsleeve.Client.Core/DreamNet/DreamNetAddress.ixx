module;

#include <enet/enet.h>

export module DreamNet.Address;

import std;

import DreamNet.Core;

export class DreamNetAddress
{
  public:

  using Result = NetResult<DreamNetAddress>;

  static constexpr IpStrView   LoopbackIp = "127.0.0.1";
  static constexpr std::size_t BufferSize = 256;
  // DNS limits: a whole name and one dot-separated label.
  static constexpr std::size_t MaxHostNameLength = 253;
  static constexpr std::size_t MaxLabelLength    = 63;

  // Four dot-separated decimal parts 0..255, as enet_address_set_host_ip reads them.
  static constexpr bool IsIpv4Literal(const IpStrView text) noexcept
  {
    std::size_t start = 0;
    for (int part = 0; part < 4; ++part)
    {
      const auto end    = part < 3 ? text.find('.', start) : text.size();
      const auto digits = end == IpStrView::npos ? IpStrView{} : text.substr(start, end - start);
      if (digits.empty() || digits.size() > 3 || !std::ranges::all_of(digits, IsDigit)) return false;

      unsigned value = 0;
      for (const char digit : digits)
        value = value * 10 + static_cast<unsigned>(digit - '0');
      if (value > 255) return false;
      start = end + 1;
    }
    return true;
  }

  // A server host: an IPv4 literal, or a DNS name of dot-separated labels of
  // 1..63 ASCII letters, digits and inner hyphens, the last one not all digits.
  // International names go in punycode (xn--...).
  static constexpr bool IsHostSyntax(const HostNameView host) noexcept
  {
    if (IsIpv4Literal(host)) return true;
    if (host.empty() || host.size() > MaxHostNameLength) return false;

    std::size_t start   = 0;
    bool        numeric = false;
    while (true)
    {
      const auto dot   = host.find('.', start);
      const auto label = host.substr(start, dot == HostNameView::npos ? HostNameView::npos : dot - start);
      if (label.empty() || label.size() > MaxLabelLength || label.front() == '-' || label.back() == '-') return false;
      if (!std::ranges::all_of(label, [](char value) { return IsDigit(value) || IsAsciiLetter(value) || value == '-'; })) return false;

      numeric = std::ranges::all_of(label, IsDigit);
      if (dot == HostNameView::npos) break;
      start = dot + 1;
    }
    return !numeric;
  }

  // A configured server host: a literal as is, a name resolved to its first
  // IPv4 address. A name blocks on the system resolver: network owner only.
  static Result TryResolve(const HostNameView host, const Port port)
  {
    if (IsIpv4Literal(host)) return TryParseIp(host, port);
    return TryResolveHost(host, port);
  }

  static constexpr DreamNetAddress FromNative(const ENetAddress address) noexcept
  {
    return DreamNetAddress(address);
  }

  static Result TryParseIp(const IpStrView hostIp, const Port port)
  {
    IpStr       owned{hostIp};
    ENetAddress address{};
    address.port = port;

    if (owned.empty() || enet_address_set_host_ip(&address, owned.c_str()) != 0)
    {
      return DreamNetError::MakeUnexpected(DreamNetErrorCode::InvalidIp, "Invalid string IP when try make DreamNetAddress");
    }

    return DreamNetAddress{address};
  }

  static Result TryResolveHost(const HostNameView hostName, const Port port)
  {
    HostName    owned{hostName};
    ENetAddress address{};
    address.port = port;

    if (owned.empty() || enet_address_set_host(&address, owned.c_str()) != 0)
    {
      return DreamNetError::MakeUnexpected(DreamNetErrorCode::FailedResolveHost, std::format("Cannot resolve host {}", owned));
    }

    return DreamNetAddress{address};
  }

  static constexpr DreamNetAddress Loopback(const Port port) noexcept
  {
    return DreamNetAddress{
        ENetAddress{.host = 0x0100007Fu /* = 7F 00 00 01 little-endian */, .port = port}
    };  // 127.0.0.1
  }

  static constexpr DreamNetAddress Any(const Port port) noexcept
  {
    return DreamNetAddress{
        ENetAddress{.host = ENET_HOST_ANY, .port = port}
    };
  }

  static constexpr DreamNetAddress Broadcast(const Port port) noexcept
  {
    return DreamNetAddress{
        ENetAddress{.host = ENET_HOST_BROADCAST, .port = port}
    };
  }

  std::uint32_t HostRaw() const noexcept
  {
    return address.host;
  }

  Port GetPort() const noexcept
  {
    return address.port;
  }

  NetResult<IpStr> ToIpString() const
  {
    std::array<char, BufferSize> buffer{};

    if (enet_address_get_host_ip(&address, buffer.data(), buffer.size()) != 0)
    {
      return DreamNetError::MakeUnexpected(DreamNetErrorCode::FailedFormatAddress, "Failed to ip string with enet_address_get_host_ip");
    }

    return IpStr{buffer.data()};
  }

  // should never call from game thread or hot path
  NetResult<HostName> TryResolveHostNameBlocking() const
  {
    std::array<char, BufferSize> buffer{};

    if (enet_address_get_host(&address, buffer.data(), buffer.size()) != 0)
    {
      return DreamNetError::MakeUnexpected(DreamNetErrorCode::FailedFormatAddress, "Failed to host string with enet_address_get_host");
    }

    return HostName{buffer.data()};
  }

  // manual cast like enet
  std::string ToString() const
  {
    const auto raw = std::bit_cast<std::array<std::uint8_t, 4>>(address.host);
    return std::format("{}.{}.{}.{}:{}", raw[0], raw[1], raw[2], raw[3], address.port);
  }

  const ENetAddress& Native() const noexcept
  {
    return address;
  }

  private:

  static constexpr bool IsDigit(const char value) noexcept
  {
    return value >= '0' && value <= '9';
  }

  static constexpr bool IsAsciiLetter(const char value) noexcept
  {
    return (value >= 'a' && value <= 'z') || (value >= 'A' && value <= 'Z');
  }

  explicit constexpr DreamNetAddress(ENetAddress address) noexcept : address(address) {}

  ENetAddress address{};
};
