#include <doctest/doctest.h>
#include <enet/enet.h>
import DreamNet.Packet;
import std;

// ==================== PacketFlags ====================

TEST_CASE("PacketFlag bitwise OR", "[packet][flags]")
{
    auto combined = PacketFlag::Reliable | PacketFlag::NoAllocate;
    REQUIRE(PacketFlags::HasFlag(combined, PacketFlag::Reliable));
    REQUIRE(PacketFlags::HasFlag(combined, PacketFlag::NoAllocate));
    REQUIRE_FALSE(PacketFlags::HasFlag(combined, PacketFlag::Unsequenced));
}

TEST_CASE("PacketFlag bitwise AND", "[packet][flags]")
{
    auto combined = PacketFlag::Reliable | PacketFlag::Unsequenced;
    auto extracted = combined & PacketFlag::Reliable;
    REQUIRE(extracted == PacketFlag::Reliable);
}

TEST_CASE("PacketFlag bitwise NOT", "[packet][flags]")
{
    auto inverted = ~PacketFlag::Reliable;
    REQUIRE_FALSE(PacketFlags::HasFlag(inverted, PacketFlag::Reliable));
}

TEST_CASE("PacketFlag operator|=", "[packet][flags]")
{
    PacketFlag flags = PacketFlag::None;
    flags |= PacketFlag::Reliable;
    REQUIRE(PacketFlags::HasFlag(flags, PacketFlag::Reliable));
}

TEST_CASE("PacketFlag ToNative roundtrip", "[packet][flags]")
{
    auto combined = PacketFlag::Reliable | PacketFlag::Unsequenced;
    auto raw = PacketFlags::ToNative(combined);
    REQUIRE(raw == (static_cast<enet_uint32>(PacketFlag::Reliable) | static_cast<enet_uint32>(PacketFlag::Unsequenced)));
}

TEST_CASE("PacketFlag FromRaw", "[packet][flags]")
{
    auto raw = static_cast<enet_uint32>(PacketFlag::Reliable);
    auto flag = PacketFlags::FromRaw(raw);
    REQUIRE(flag == PacketFlag::Reliable);
}

TEST_CASE("PacketFlag IsValidPacketFlags - valid single flags", "[packet][flags]")
{
    REQUIRE(PacketFlags::IsValidPacketFlags(PacketFlag::None));
    REQUIRE(PacketFlags::IsValidPacketFlags(PacketFlag::Reliable));
    REQUIRE(PacketFlags::IsValidPacketFlags(PacketFlag::Unsequenced));
    REQUIRE(PacketFlags::IsValidPacketFlags(PacketFlag::NoAllocate));
    REQUIRE(PacketFlags::IsValidPacketFlags(PacketFlag::UnreliableFragment));
}

TEST_CASE("PacketFlag IsValidPacketFlags - reliable + unsequenced is invalid", "[packet][flags]")
{
    auto combined = PacketFlag::Reliable | PacketFlag::Unsequenced;
    REQUIRE_FALSE(PacketFlags::IsValidPacketFlags(combined));
}

TEST_CASE("PacketFlag IsValidPacketFlags - raw uint32", "[packet][flags]")
{
    REQUIRE(PacketFlags::IsValidPacketFlags(0u));
    REQUIRE(PacketFlags::IsValidPacketFlags(static_cast<enet_uint32>(PacketFlag::Reliable)));

    const auto invalid = static_cast<enet_uint32>(PacketFlag::Reliable) | static_cast<enet_uint32>(PacketFlag::Unsequenced);
    REQUIRE_FALSE(PacketFlags::IsValidPacketFlags(invalid));
}

TEST_CASE("PacketFlag None value is zero", "[packet][flags]")
{
    REQUIRE(static_cast<enet_uint32>(PacketFlag::None) == 0u);
}

// ==================== DreamNetPacket ====================

TEST_CASE("DreamNetPacket.TryFromSpan - reliable packet", "[packet]")
{
    std::array<std::byte, 4> data = {std::byte{0x01}, std::byte{0x02}, std::byte{0x03}, std::byte{0x04}};
    auto result = DreamNetPacket::TryFromSpan(std::span{data.data(), data.size()}, PacketFlag::Reliable);

    REQUIRE(result.has_value());
    const auto& packet = result.value();
    REQUIRE(packet.IsValid());
    REQUIRE(packet.Size() == 4);
    REQUIRE(packet.Flags() == PacketFlag::Reliable);
}

TEST_CASE("DreamNetPacket.TryFromSpan - data copy matches", "[packet]")
{
    std::array<std::byte, 3> data = {std::byte{0xAA}, std::byte{0xBB}, std::byte{0xCC}};
    auto result = DreamNetPacket::TryFromSpan(std::span{data.data(), data.size()}, PacketFlag::Reliable);
    REQUIRE(result.has_value());

    const auto& packet = result.value();
    REQUIRE(packet.DataBytesView().size() == 3);

    const auto& view = packet.DataBytesView();
    REQUIRE(view[0] == std::byte{0xAA});
    REQUIRE(view[1] == std::byte{0xBB});
    REQUIRE(view[2] == std::byte{0xCC});
}

TEST_CASE("DreamNetPacket.TryFromSpan - unsequenced flag", "[packet]")
{
    std::array<std::byte, 1> data = {std::byte{0xFF}};
    auto result = DreamNetPacket::TryFromSpan(std::span{data.data(), data.size()}, PacketFlag::Unsequenced);

    REQUIRE(result.has_value());
    REQUIRE(result->Flags() == PacketFlag::Unsequenced);
}

TEST_CASE("DreamNetPacket.TryFromSpan - invalid flags (reliable | unsequenced)", "[packet]")
{
    std::array<std::byte, 4> data = {};
    const auto invalidFlags = PacketFlag::Reliable | PacketFlag::Unsequenced;
    auto result = DreamNetPacket::TryFromSpan(std::span{data.data(), data.size()}, invalidFlags);

    REQUIRE_FALSE(result.has_value());
    REQUIRE(result.error() == DreamNetPacket::Error::InvalidFlags);
}

TEST_CASE("DreamNetPacket.TryFromSpan - NoAllocate rejected", "[packet]")
{
    std::array<std::byte, 4> data = {};
    auto result = DreamNetPacket::TryFromSpan(std::span{data.data(), data.size()}, PacketFlag::NoAllocate);

    REQUIRE_FALSE(result.has_value());
    REQUIRE(result.error() == DreamNetPacket::Error::InvalidFlags);
}

TEST_CASE("DreamNetPacket.TryFromSpan - default flag is Reliable", "[packet]")
{
    std::array<std::byte, 2> data = {std::byte{0x10}, std::byte{0x20}};
    auto result = DreamNetPacket::TryFromSpan(std::span{data.data(), data.size()});

    REQUIRE(result.has_value());
    REQUIRE(result->Flags() == PacketFlag::Reliable);
}

TEST_CASE("DreamNetPacket.TryAdoptNative - null pointer", "[packet]")
{
    auto result = DreamNetPacket::TryAdoptNative(nullptr);
    REQUIRE_FALSE(result.has_value());
    REQUIRE(result.error() == DreamNetPacket::Error::NullPacket);
}

TEST_CASE("DreamNetPacket move semantics", "[packet]")
{
    std::array<std::byte, 8> data = {};
    auto result = DreamNetPacket::TryFromSpan(std::span{data.data(), data.size()}, PacketFlag::Reliable);
    REQUIRE(result.has_value());

    auto packet1 = std::move(result.value());
    REQUIRE(packet1.IsValid());
    REQUIRE(packet1.Size() == 8);

    auto packet2 = std::move(packet1);
    REQUIRE(packet2.IsValid());
    REQUIRE_FALSE(packet1.IsValid());
}

TEST_CASE("DreamNetPacket moved-from is invalid", "[packet]")
{
    std::array<std::byte, 4> data = {};
    auto result = DreamNetPacket::TryFromSpan(std::span{data.data(), data.size()}, PacketFlag::Reliable);
    REQUIRE(result.has_value());

    auto packet1 = std::move(result.value());
    auto packet2 = std::move(packet1);

    REQUIRE_FALSE(packet1.IsValid());
    REQUIRE(packet1.Size() == 0);
    REQUIRE(packet1.Flags() == PacketFlag::None);
    REQUIRE(packet1.Data().empty());
}

TEST_CASE("DreamNetPacket.DataBytesView uses std::as_bytes", "[packet]")
{
    std::array<std::byte, 4> data = {std::byte{0xDE}, std::byte{0xAD}, std::byte{0xBE}, std::byte{0xEF}};
    auto result = DreamNetPacket::TryFromSpan(std::span{data.data(), data.size()}, PacketFlag::Reliable);
    REQUIRE(result.has_value());

    auto bytesView = result->DataBytesView();
    REQUIRE(bytesView.size() == 4);
    REQUIRE(bytesView[0] == std::byte{0xDE});
    REQUIRE(bytesView[1] == std::byte{0xAD});
    REQUIRE(bytesView[2] == std::byte{0xBE});
    REQUIRE(bytesView[3] == std::byte{0xEF});
}
