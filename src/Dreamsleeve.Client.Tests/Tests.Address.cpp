#include <doctest/doctest.h>
#include <enet/enet.h>
import DreamNet.Address;
import DreamNet.Core;
import DreamNet.Runtime;
import std;

TEST_CASE("DreamNetAddress.TryParseIp - valid IP", "[address]")
{
  auto result = DreamNetAddress::TryParseIp("192.168.1.1", 7777);
  REQUIRE(result.has_value());

  const auto& addr = result.value();
  REQUIRE(addr.GetPort() == 7777);
  REQUIRE(addr.HostRaw() != 0);
}

TEST_CASE("DreamNetAddress.TryParseIp - invalid IP", "[address]")
{
  auto result = DreamNetAddress::TryParseIp("not.an.ip", 7777);
  REQUIRE_FALSE(result.has_value());
  REQUIRE(result.error().code == DreamNetErrorCode::InvalidIp);
}

TEST_CASE("DreamNetAddress.TryParseIp - empty string", "[address]")
{
  auto result = DreamNetAddress::TryParseIp("", 7777);
  REQUIRE_FALSE(result.has_value());
}

TEST_CASE("DreamNetAddress.TryParseIp - port 0", "[address]")
{
  auto result = DreamNetAddress::TryParseIp("10.0.0.1", 0);
  REQUIRE(result.has_value());
  REQUIRE(result.value().GetPort() == 0);
}

TEST_CASE("DreamNetAddress.TryParseIp - max port", "[address]")
{
  auto result = DreamNetAddress::TryParseIp("10.0.0.1", 65535);
  REQUIRE(result.has_value());
  REQUIRE(result.value().GetPort() == 65535);
}

TEST_CASE("DreamNetAddress.Loopback", "[address]")
{
  auto addr = DreamNetAddress::Loopback(8080);
  REQUIRE(addr.GetPort() == 8080);

  auto ipStr = addr.ToIpString();
  REQUIRE(ipStr.has_value());
  REQUIRE(ipStr.value() == "127.0.0.1");
}

TEST_CASE("DreamNetAddress.Any", "[address]")
{
  auto addr = DreamNetAddress::Any(1234);
  REQUIRE(addr.GetPort() == 1234);
  REQUIRE(addr.HostRaw() == ENET_HOST_ANY);
}

TEST_CASE("DreamNetAddress.Broadcast", "[address]")
{
  auto addr = DreamNetAddress::Broadcast(9999);
  REQUIRE(addr.GetPort() == 9999);
  REQUIRE(addr.HostRaw() == ENET_HOST_BROADCAST);
}

TEST_CASE("DreamNetAddress.ToIpString - roundtrip", "[address]")
{
  auto addr = DreamNetAddress::TryParseIp("10.20.30.40", 5000);
  REQUIRE(addr.has_value());

  auto ipStr = addr->ToIpString();
  REQUIRE(ipStr.has_value());
  REQUIRE(ipStr.value() == "10.20.30.40");
}

TEST_CASE("DreamNetAddress.Native access", "[address]")
{
  auto        addr   = DreamNetAddress::Loopback(3000);
  const auto& native = addr.Native();
  REQUIRE(native.port == 3000);
}

// ==================== constexpr factories ====================

TEST_CASE("DreamNetAddress.Loopback/Any/Broadcast are usable in constant expressions", "[address][constexpr]")
{
  // Fails to compile if the private constructor stops being constexpr.
  constexpr DreamNetAddress loopback  = DreamNetAddress::Loopback(8080);
  constexpr DreamNetAddress any       = DreamNetAddress::Any(1234);
  constexpr DreamNetAddress broadcast = DreamNetAddress::Broadcast(9999);

  CHECK(loopback.GetPort() == 8080);
  CHECK(any.GetPort() == 1234);
  CHECK(broadcast.GetPort() == 9999);
}

TEST_CASE("DreamNetAddress.Loopback constant matches what ENet parses", "[address][constexpr]")
{
  // Loopback() hardcodes the network-order bytes instead of calling into ENet.
  // This pins that constant to enet_address_set_host_ip so the two cannot diverge.
  auto parsed = DreamNetAddress::TryParseIp(DreamNetAddress::LoopbackIp, 7777);
  REQUIRE(parsed.has_value());

  const auto hardcoded = DreamNetAddress::Loopback(7777);

  CHECK(hardcoded.HostRaw() == parsed->HostRaw());
  CHECK(hardcoded.GetPort() == parsed->GetPort());
}

// ==================== ToString ====================

TEST_CASE("DreamNetAddress.ToString - loopback", "[address][tostring]")
{
  CHECK(DreamNetAddress::Loopback(8080).ToString() == "127.0.0.1:8080");
}

TEST_CASE("DreamNetAddress.ToString - parsed address", "[address][tostring]")
{
  auto addr = DreamNetAddress::TryParseIp("10.20.30.40", 5000);
  REQUIRE(addr.has_value());
  CHECK(addr->ToString() == "10.20.30.40:5000");
}

TEST_CASE("DreamNetAddress.ToString - any and broadcast", "[address][tostring]")
{
  CHECK(DreamNetAddress::Any(0).ToString() == "0.0.0.0:0");
  CHECK(DreamNetAddress::Broadcast(1).ToString() == "255.255.255.255:1");
}

TEST_CASE("DreamNetAddress.ToString - agrees with ToIpString", "[address][tostring]")
{
  auto addr = DreamNetAddress::TryParseIp("192.168.1.1", 7777);
  REQUIRE(addr.has_value());

  auto ipStr = addr->ToIpString();
  REQUIRE(ipStr.has_value());

  CHECK(addr->ToString() == std::format("{}:{}", ipStr.value(), addr->GetPort()));
}

TEST_CASE("DreamNetAddress.ToString - needs no Winsock", "[address][tostring]")
{
  // ToString is pure formatting, unlike ToIpString (inet_ntoa) and
  // TryResolveHostNameBlocking (gethostbyaddr), so it stays usable in error
  // paths and logs before DreamNetRuntime::TryInitialize has run.
  CHECK(DreamNetAddress::Loopback(1).ToString() == "127.0.0.1:1");
}

// ==================== reverse resolve ====================

TEST_CASE("DreamNetAddress.TryResolveHostNameBlocking - falls back to the IP string", "[address][resolve]")
{
  // enet_address_get_host returns enet_address_get_host_ip when gethostbyaddr
  // finds no PTR record, and still reports success - so a non-empty result is
  // all this can promise, and it may well just be the IP back again.
  auto runtime = DreamNetRuntime::TryInitialize();
  REQUIRE(runtime.has_value());

  auto resolved = DreamNetAddress::Loopback(0).TryResolveHostNameBlocking();
  REQUIRE(resolved.has_value());
  CHECK_FALSE(resolved.value().empty());
}
