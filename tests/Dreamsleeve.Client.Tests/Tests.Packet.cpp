#include <doctest/doctest.h>
#include <enet/enet.h>
import DreamNet.Packet;
import DreamNet.Core;
import std;

// ==================== PacketFlags ====================

TEST_SUITE_BEGIN("DreamNet.Packet");

TEST_CASE("PacketFlag bitwise OR")
{
  auto combined = PacketFlag::Reliable | PacketFlag::NoAllocate;
  REQUIRE(PacketFlags::HasFlag(combined, PacketFlag::Reliable));
  REQUIRE(PacketFlags::HasFlag(combined, PacketFlag::NoAllocate));
  REQUIRE_FALSE(PacketFlags::HasFlag(combined, PacketFlag::Unsequenced));
}

TEST_CASE("PacketFlag bitwise AND")
{
  auto combined  = PacketFlag::Reliable | PacketFlag::Unsequenced;
  auto extracted = combined & PacketFlag::Reliable;
  REQUIRE(extracted == PacketFlag::Reliable);
}

TEST_CASE("PacketFlag bitwise NOT")
{
  auto inverted = ~PacketFlag::Reliable;
  REQUIRE_FALSE(PacketFlags::HasFlag(inverted, PacketFlag::Reliable));
}

TEST_CASE("PacketFlag operator|=")
{
  PacketFlag flags  = PacketFlag::None;
  flags            |= PacketFlag::Reliable;
  REQUIRE(PacketFlags::HasFlag(flags, PacketFlag::Reliable));
}

TEST_CASE("PacketFlag ToNative roundtrip")
{
  auto combined = PacketFlag::Reliable | PacketFlag::Unsequenced;
  auto raw      = PacketFlags::ToNative(combined);
  REQUIRE(raw == (static_cast<enet_uint32>(PacketFlag::Reliable) | static_cast<enet_uint32>(PacketFlag::Unsequenced)));
}

TEST_CASE("PacketFlag FromRaw")
{
  auto raw  = static_cast<enet_uint32>(PacketFlag::Reliable);
  auto flag = PacketFlags::FromRaw(raw);
  REQUIRE(flag == PacketFlag::Reliable);
}

TEST_CASE("PacketFlag IsValidPacketFlags - valid single flags")
{
  REQUIRE(PacketFlags::IsValidPacketFlags(PacketFlag::None));
  REQUIRE(PacketFlags::IsValidPacketFlags(PacketFlag::Reliable));
  REQUIRE(PacketFlags::IsValidPacketFlags(PacketFlag::Unsequenced));
  REQUIRE(PacketFlags::IsValidPacketFlags(PacketFlag::NoAllocate));
  REQUIRE(PacketFlags::IsValidPacketFlags(PacketFlag::UnreliableFragment));
}

TEST_CASE("PacketFlag IsValidPacketFlags - reliable + unsequenced is invalid")
{
  auto combined = PacketFlag::Reliable | PacketFlag::Unsequenced;
  REQUIRE_FALSE(PacketFlags::IsValidPacketFlags(combined));
}

TEST_CASE("PacketFlag IsValidPacketFlags - raw uint32")
{
  REQUIRE(PacketFlags::IsValidPacketFlags(0u));
  REQUIRE(PacketFlags::IsValidPacketFlags(static_cast<enet_uint32>(PacketFlag::Reliable)));

  const auto invalid = static_cast<enet_uint32>(PacketFlag::Reliable) | static_cast<enet_uint32>(PacketFlag::Unsequenced);
  REQUIRE_FALSE(PacketFlags::IsValidPacketFlags(invalid));
}

TEST_CASE("PacketFlag None value is zero")
{
  REQUIRE(static_cast<enet_uint32>(PacketFlag::None) == 0u);
}

// ==================== DreamNetPacket ====================

TEST_CASE("DreamNetPacket.TryFromSpan - reliable packet")
{
  std::array<std::byte, 4> data   = {std::byte{0x01}, std::byte{0x02}, std::byte{0x03}, std::byte{0x04}};
  auto                     result = DreamNetPacket::TryFromSpan(std::span{data.data(), data.size()}, PacketFlag::Reliable);

  REQUIRE(result.has_value());
  const auto& packet = result.value();
  REQUIRE(packet.IsValid());
  REQUIRE(packet.Size() == 4);
  REQUIRE(packet.Flags() == PacketFlag::Reliable);
}

TEST_CASE("DreamNetPacket.TryFromSpan - data copy matches")
{
  std::array<std::byte, 3> data   = {std::byte{0xAA}, std::byte{0xBB}, std::byte{0xCC}};
  auto                     result = DreamNetPacket::TryFromSpan(std::span{data.data(), data.size()}, PacketFlag::Reliable);
  REQUIRE(result.has_value());

  const auto& packet = result.value();
  REQUIRE(packet.DataBytesView().size() == 3);

  const auto& view = packet.DataBytesView();
  REQUIRE(view[0] == std::byte{0xAA});
  REQUIRE(view[1] == std::byte{0xBB});
  REQUIRE(view[2] == std::byte{0xCC});
}

TEST_CASE("DreamNetPacket.TryFromSpan - unsequenced flag")
{
  std::array<std::byte, 1> data   = {std::byte{0xFF}};
  auto                     result = DreamNetPacket::TryFromSpan(std::span{data.data(), data.size()}, PacketFlag::Unsequenced);

  REQUIRE(result.has_value());
  REQUIRE(result->Flags() == PacketFlag::Unsequenced);
}

TEST_CASE("DreamNetPacket.TryFromSpan - invalid flags (reliable | unsequenced)")
{
  std::array<std::byte, 4> data         = {};
  const auto               invalidFlags = PacketFlag::Reliable | PacketFlag::Unsequenced;
  auto                     result       = DreamNetPacket::TryFromSpan(std::span{data.data(), data.size()}, invalidFlags);

  REQUIRE_FALSE(result.has_value());
  REQUIRE(result.error().code == DreamNetErrorCode::InvalidPacketFlags);
}

TEST_CASE("DreamNetPacket.TryFromSpan - NoAllocate rejected")
{
  std::array<std::byte, 4> data   = {};
  auto                     result = DreamNetPacket::TryFromSpan(std::span{data.data(), data.size()}, PacketFlag::NoAllocate);

  REQUIRE_FALSE(result.has_value());
  REQUIRE(result.error().code == DreamNetErrorCode::InvalidPacketFlags);
}

TEST_CASE("DreamNetPacket.TryFromSpan - default flag is Reliable")
{
  std::array<std::byte, 2> data   = {std::byte{0x10}, std::byte{0x20}};
  auto                     result = DreamNetPacket::TryFromSpan(std::span{data.data(), data.size()});

  REQUIRE(result.has_value());
  REQUIRE(result->Flags() == PacketFlag::Reliable);
}

TEST_CASE("DreamNetPacket.TryAdoptNative - null pointer")
{
  auto result = DreamNetPacket::TryAdoptNative(nullptr);
  REQUIRE_FALSE(result.has_value());
  REQUIRE(result.error().code == DreamNetErrorCode::NullPacket);
}

TEST_CASE("DreamNetPacket move semantics")
{
  std::array<std::byte, 8> data   = {};
  auto                     result = DreamNetPacket::TryFromSpan(std::span{data.data(), data.size()}, PacketFlag::Reliable);
  REQUIRE(result.has_value());

  auto packet1 = std::move(result.value());
  REQUIRE(packet1.IsValid());
  REQUIRE(packet1.Size() == 8);

  auto packet2 = std::move(packet1);
  REQUIRE(packet2.IsValid());
  REQUIRE_FALSE(packet1.IsValid());
}

TEST_CASE("DreamNetPacket moved-from is invalid")
{
  std::array<std::byte, 4> data   = {};
  auto                     result = DreamNetPacket::TryFromSpan(std::span{data.data(), data.size()}, PacketFlag::Reliable);
  REQUIRE(result.has_value());

  auto packet1 = std::move(result.value());
  auto packet2 = std::move(packet1);

  REQUIRE_FALSE(packet1.IsValid());
  REQUIRE(packet1.Size() == 0);
  REQUIRE(packet1.Flags() == PacketFlag::None);
  REQUIRE(packet1.Data().empty());
}

TEST_CASE("DreamNetPacket.DataBytesView uses std::as_bytes")
{
  std::array<std::byte, 4> data   = {std::byte{0xDE}, std::byte{0xAD}, std::byte{0xBE}, std::byte{0xEF}};
  auto                     result = DreamNetPacket::TryFromSpan(std::span{data.data(), data.size()}, PacketFlag::Reliable);
  REQUIRE(result.has_value());

  auto bytesView = result->DataBytesView();
  REQUIRE(bytesView.size() == 4);
  REQUIRE(bytesView[0] == std::byte{0xDE});
  REQUIRE(bytesView[1] == std::byte{0xAD});
  REQUIRE(bytesView[2] == std::byte{0xBE});
  REQUIRE(bytesView[3] == std::byte{0xEF});
}

// ==================== TryAllocate / MutableData ====================

TEST_CASE("DreamNetPacket.TryAllocate - allocates requested size")
{
  auto result = DreamNetPacket::TryAllocate(16, PacketFlag::Reliable);
  REQUIRE(result.has_value());
  REQUIRE(result->IsValid());
  REQUIRE(result->Size() == 16);
  REQUIRE(result->Flags() == PacketFlag::Reliable);
  REQUIRE(result->MutableData().size() == 16);
}

TEST_CASE("DreamNetPacket.TryAllocate - write through as_writable_bytes round-trips")
{
  auto result = DreamNetPacket::TryAllocate(4, PacketFlag::Reliable);
  REQUIRE(result.has_value());

  auto buffer = std::as_writable_bytes(result->MutableData());
  REQUIRE(buffer.size() == 4);
  buffer[0] = std::byte{0xDE};
  buffer[1] = std::byte{0xAD};
  buffer[2] = std::byte{0xBE};
  buffer[3] = std::byte{0xEF};

  auto view = result->DataBytesView();
  REQUIRE(view.size() == 4);
  REQUIRE(view[0] == std::byte{0xDE});
  REQUIRE(view[1] == std::byte{0xAD});
  REQUIRE(view[2] == std::byte{0xBE});
  REQUIRE(view[3] == std::byte{0xEF});
}

TEST_CASE("DreamNetPacket.TryAllocate - write through MutableData round-trips")
{
  auto result = DreamNetPacket::TryAllocate(3, PacketFlag::Reliable);
  REQUIRE(result.has_value());

  auto buffer = result->MutableData();
  REQUIRE(buffer.size() == 3);
  buffer[0] = enet_uint8{1};
  buffer[1] = enet_uint8{2};
  buffer[2] = enet_uint8{3};

  auto view = result->Data();
  REQUIRE(view.size() == 3);
  REQUIRE(view[0] == enet_uint8{1});
  REQUIRE(view[1] == enet_uint8{2});
  REQUIRE(view[2] == enet_uint8{3});
}

TEST_CASE("DreamNetPacket.TryAllocate - NoAllocate rejected")
{
  auto result = DreamNetPacket::TryAllocate(8, PacketFlag::NoAllocate);
  REQUIRE_FALSE(result.has_value());
  REQUIRE(result.error().code == DreamNetErrorCode::InvalidPacketFlags);
}

TEST_CASE("DreamNetPacket.TryAllocate - reliable + unsequenced rejected")
{
  auto result = DreamNetPacket::TryAllocate(8, PacketFlag::Reliable | PacketFlag::Unsequenced);
  REQUIRE_FALSE(result.has_value());
  REQUIRE(result.error().code == DreamNetErrorCode::InvalidPacketFlags);
}

TEST_CASE("DreamNetPacket.TryAllocate - size above MaxDataSize rejected")
{
  auto result = DreamNetPacket::TryAllocate(DreamNetPacket::MaxDataSize + 1);
  REQUIRE_FALSE(result.has_value());
  REQUIRE(result.error().code == DreamNetErrorCode::InvalidPacket);
}

TEST_CASE("DreamNetPacket.TryAllocate - zero size yields empty packet")
{
  auto result = DreamNetPacket::TryAllocate(0);
  REQUIRE(result.has_value());
  REQUIRE(result->IsValid());
  REQUIRE(result->Size() == 0);
  REQUIRE(result->MutableData().empty());
}

TEST_CASE("DreamNetPacket.TryAllocateWith - writer fills the buffer")
{
  constexpr std::array<std::byte, 3> payload = {std::byte{1}, std::byte{2}, std::byte{3}};

  auto result = DreamNetPacket::TryAllocateWith(payload.size(), [&](std::span<std::byte> buffer) {
    if (buffer.size() != payload.size()) return false;
    std::ranges::copy(payload, buffer.begin());
    return true;
  });

  REQUIRE(result.has_value());
  REQUIRE(result->Size() == 3);

  auto view = result->DataBytesView();
  REQUIRE(view[0] == std::byte{1});
  REQUIRE(view[1] == std::byte{2});
  REQUIRE(view[2] == std::byte{3});
}

TEST_CASE("DreamNetPacket.TryAllocateWith - failing writer rejects the packet")
{
  auto result = DreamNetPacket::TryAllocateWith(8, [](std::span<std::byte>) { return false; });

  REQUIRE_FALSE(result.has_value());
  REQUIRE(result.error().code == DreamNetErrorCode::FailedCreatePacket);
}

TEST_CASE("DreamNetPacket.TryAllocateWith - propagates allocation error without invoking writer")
{
  bool writerInvoked = false;

  auto result = DreamNetPacket::TryAllocateWith(
    8,
    [&](std::span<std::byte>) {
      writerInvoked = true;
      return true;
    },
    PacketFlag::NoAllocate);

  REQUIRE_FALSE(result.has_value());
  REQUIRE(result.error().code == DreamNetErrorCode::InvalidPacketFlags);
  REQUIRE_FALSE(writerInvoked);
}

TEST_CASE("DreamNetPacket.MutableData - writable on a packet built from a span")
{
  std::array<std::byte, 3> data   = {std::byte{0x10}, std::byte{0x20}, std::byte{0x30}};
  auto                     result = DreamNetPacket::TryFromSpan(std::span{data.data(), data.size()});
  REQUIRE(result.has_value());

  auto buffer = std::as_writable_bytes(result->MutableData());
  REQUIRE(buffer.size() == 3);
  buffer[1] = std::byte{0xFF};

  CHECK(result->DataBytesView()[0] == std::byte{0x10});
  CHECK(result->DataBytesView()[1] == std::byte{0xFF});
  CHECK(result->DataBytesView()[2] == std::byte{0x30});

  // The source span is untouched - ENet copied the bytes on create.
  CHECK(data[1] == std::byte{0x20});
}

TEST_CASE("DreamNetPacket.MutableData - moved-from packet yields an empty span")
{
  auto result = DreamNetPacket::TryAllocate(8);
  REQUIRE(result.has_value());

  DreamNetPacket moved = std::move(result.value());

  CHECK(moved.MutableData().size() == 8);
  CHECK_FALSE(result->IsValid());
  CHECK(result->MutableData().empty());
}

TEST_CASE("DreamNetPacket.MutableData - aliases the same memory as Data")
{
  auto result = DreamNetPacket::TryAllocate(4);
  REQUIRE(result.has_value());

  CHECK(static_cast<const void*>(result->MutableData().data()) == static_cast<const void*>(result->Data().data()));
  CHECK(result->MutableData().size() == result->Data().size());
}

TEST_CASE("DreamNetPacket.TryAllocateWith - writer sees exactly the requested size")
{
  std::size_t observedSize = 0;

  auto result = DreamNetPacket::TryAllocateWith(12, [&](std::span<std::byte> buffer) {
    observedSize = buffer.size();
    std::ranges::fill(buffer, std::byte{0xAB});
    return true;
  });

  REQUIRE(result.has_value());
  CHECK(observedSize == 12);
  CHECK(result->Size() == 12);
  CHECK(std::ranges::all_of(result->DataBytesView(), [](std::byte b) { return b == std::byte{0xAB}; }));
}

TEST_CASE("DreamNetPacket.TryAllocateWith - honours non-default flags")
{
  auto result = DreamNetPacket::TryAllocateWith(
    2,
    [](std::span<std::byte> buffer) {
      std::ranges::fill(buffer, std::byte{0});
      return true;
    },
    PacketFlag::Unsequenced);

  REQUIRE(result.has_value());
  CHECK(PacketFlags::HasFlag(result->Flags(), PacketFlag::Unsequenced));
  CHECK_FALSE(PacketFlags::HasFlag(result->Flags(), PacketFlag::Reliable));
}

TEST_CASE("DreamNetPacket.TryAllocateWith - zero size gives the writer an empty span")
{
  bool writerInvoked = false;

  auto result = DreamNetPacket::TryAllocateWith(0, [&](std::span<std::byte> buffer) {
    writerInvoked = true;
    return buffer.empty();
  });

  REQUIRE(result.has_value());
  CHECK(writerInvoked);
  CHECK(result->Size() == 0);
}

TEST_SUITE_END();
