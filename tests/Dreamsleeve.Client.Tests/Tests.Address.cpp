#include <doctest/doctest.h>
#include <enet/enet.h>
import DreamNet.Address;
import DreamNet.Core;
import DreamNet.Runtime;
import std;

TEST_SUITE_BEGIN("DreamNet.Address");

TEST_CASE("DreamNetAddress.TryParseIp - valid IP")
{
  auto result = DreamNetAddress::TryParseIp("192.168.1.1", 7777);
  REQUIRE(result.has_value());

  const auto& addr = result.value();
  REQUIRE(addr.GetPort() == 7777);
  REQUIRE(addr.HostRaw() != 0);
}

TEST_CASE("DreamNetAddress.TryParseIp - invalid IP")
{
  auto result = DreamNetAddress::TryParseIp("not.an.ip", 7777);
  REQUIRE_FALSE(result.has_value());
  REQUIRE(result.error().code == DreamNetErrorCode::InvalidIp);
}

TEST_CASE("DreamNetAddress.IsIpv4Literal - four decimal parts up to 255")
{
  for (const auto text : {"127.0.0.1", "0.0.0.0", "255.255.255.255", "010.1.1.1"})
  {
    CAPTURE(std::string_view{text});
    CHECK(DreamNetAddress::IsIpv4Literal(text));
  }

  for (const auto text : {"", "256.0.0.1", "1.2.3", "1.2.3.4.5", "1..2.3", "a.b.c.d", "1.2.3.4 ", "1234.1.1.1"})
  {
    CAPTURE(std::string_view{text});
    CHECK_FALSE(DreamNetAddress::IsIpv4Literal(text));
  }
}

TEST_CASE("DreamNetAddress.IsHostSyntax - IPv4 literals and DNS names")
{
  static_assert(DreamNetAddress::IsHostSyntax("play.example.org"));
  for (const auto host : {"127.0.0.1", "localhost", "play.example.org", "Play.Example.Org", "xn--80ak6aa92e.com", "a-b.c1"})
  {
    CAPTURE(std::string_view{host});
    CHECK(DreamNetAddress::IsHostSyntax(host));
  }

  const std::string label(DreamNetAddress::MaxLabelLength, 'a');
  const auto        longest = label + "." + label + "." + label + "." + std::string(61, 'b');
  REQUIRE(longest.size() == DreamNetAddress::MaxHostNameLength);
  CHECK(DreamNetAddress::IsHostSyntax(longest));

  const auto             tooLong   = longest + "b";
  const auto             longLabel = label + "a.org";
  const std::string_view invalid[]{
      {},
      "bad host",
      "-x.org",
      "x-.org",
      "x..org",
      "x.org.",
      ".x.org",
      "999.1.1.1",
      "1.2.3",
      "a_b.org",
      "\xD0\xB8\xD0\xBC\xD1\x8F.\xD1\x80\xD1\x84",
      tooLong,
      longLabel
  };
  for (const auto host : invalid)
  {
    CAPTURE(host);
    CHECK_FALSE(DreamNetAddress::IsHostSyntax(host));
  }
}

TEST_CASE("DreamNetAddress.TryResolve - a literal is parsed, a name is resolved")
{
  auto runtime = DreamNetRuntime::TryInitialize();
  REQUIRE(runtime.has_value());

  auto literal = DreamNetAddress::TryResolve("10.0.0.1", 7777);
  REQUIRE(literal.has_value());
  CHECK(literal->ToString() == "10.0.0.1:7777");

  auto named = DreamNetAddress::TryResolve("localhost", 7777);
  REQUIRE(named.has_value());
  CHECK(named->ToString() == "127.0.0.1:7777");
}

TEST_CASE("DreamNetAddress.TryParseIp - empty string")
{
  auto result = DreamNetAddress::TryParseIp("", 7777);
  REQUIRE_FALSE(result.has_value());
}

TEST_CASE("DreamNetAddress.TryParseIp - port 0")
{
  auto result = DreamNetAddress::TryParseIp("10.0.0.1", 0);
  REQUIRE(result.has_value());
  REQUIRE(result.value().GetPort() == 0);
}

TEST_CASE("DreamNetAddress.TryParseIp - max port")
{
  auto result = DreamNetAddress::TryParseIp("10.0.0.1", 65535);
  REQUIRE(result.has_value());
  REQUIRE(result.value().GetPort() == 65535);
}

TEST_CASE("DreamNetAddress.Loopback")
{
  auto addr = DreamNetAddress::Loopback(8080);
  REQUIRE(addr.GetPort() == 8080);

  auto ipStr = addr.ToIpString();
  REQUIRE(ipStr.has_value());
  REQUIRE(ipStr.value() == "127.0.0.1");
}

TEST_CASE("DreamNetAddress.Any")
{
  auto addr = DreamNetAddress::Any(1234);
  REQUIRE(addr.GetPort() == 1234);
  REQUIRE(addr.HostRaw() == ENET_HOST_ANY);
}

TEST_CASE("DreamNetAddress.Broadcast")
{
  auto addr = DreamNetAddress::Broadcast(9999);
  REQUIRE(addr.GetPort() == 9999);
  REQUIRE(addr.HostRaw() == ENET_HOST_BROADCAST);
}

TEST_CASE("DreamNetAddress.ToIpString - roundtrip")
{
  auto addr = DreamNetAddress::TryParseIp("10.20.30.40", 5000);
  REQUIRE(addr.has_value());

  auto ipStr = addr->ToIpString();
  REQUIRE(ipStr.has_value());
  REQUIRE(ipStr.value() == "10.20.30.40");
}

TEST_CASE("DreamNetAddress.Native access")
{
  auto        addr   = DreamNetAddress::Loopback(3000);
  const auto& native = addr.Native();
  REQUIRE(native.port == 3000);
}

// ==================== constexpr factories ====================

TEST_CASE("DreamNetAddress.Loopback/Any/Broadcast are usable in constant expressions")
{
  // Fails to compile if the private constructor stops being constexpr.
  constexpr DreamNetAddress loopback  = DreamNetAddress::Loopback(8080);
  constexpr DreamNetAddress any       = DreamNetAddress::Any(1234);
  constexpr DreamNetAddress broadcast = DreamNetAddress::Broadcast(9999);

  CHECK(loopback.GetPort() == 8080);
  CHECK(any.GetPort() == 1234);
  CHECK(broadcast.GetPort() == 9999);
}

TEST_CASE("DreamNetAddress.Loopback constant matches what ENet parses")
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

TEST_CASE("DreamNetAddress.ToString - loopback")
{
  CHECK(DreamNetAddress::Loopback(8080).ToString() == "127.0.0.1:8080");
}

TEST_CASE("DreamNetAddress.ToString - parsed address")
{
  auto addr = DreamNetAddress::TryParseIp("10.20.30.40", 5000);
  REQUIRE(addr.has_value());
  CHECK(addr->ToString() == "10.20.30.40:5000");
}

TEST_CASE("DreamNetAddress.ToString - any and broadcast")
{
  CHECK(DreamNetAddress::Any(0).ToString() == "0.0.0.0:0");
  CHECK(DreamNetAddress::Broadcast(1).ToString() == "255.255.255.255:1");
}

TEST_CASE("DreamNetAddress.ToString - agrees with ToIpString")
{
  auto addr = DreamNetAddress::TryParseIp("192.168.1.1", 7777);
  REQUIRE(addr.has_value());

  auto ipStr = addr->ToIpString();
  REQUIRE(ipStr.has_value());

  CHECK(addr->ToString() == std::format("{}:{}", ipStr.value(), addr->GetPort()));
}

TEST_CASE("DreamNetAddress.ToString - needs no Winsock")
{
  // ToString is pure formatting, unlike ToIpString (inet_ntoa) and
  // TryResolveHostNameBlocking (gethostbyaddr), so it stays usable in error
  // paths and logs before DreamNetRuntime::TryInitialize has run.
  CHECK(DreamNetAddress::Loopback(1).ToString() == "127.0.0.1:1");
}

// ==================== reverse resolve ====================

TEST_CASE("DreamNetAddress.TryResolveHostNameBlocking - falls back to the IP string")
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

TEST_SUITE_END();
