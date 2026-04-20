#include <doctest/doctest.h>
#include <enet/enet.h>
import DreamNet.Address;
import DreamNet.Core;
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
    auto addr = DreamNetAddress::Loopback(3000);
    const auto& native = addr.Native();
    REQUIRE(native.port == 3000);
}
