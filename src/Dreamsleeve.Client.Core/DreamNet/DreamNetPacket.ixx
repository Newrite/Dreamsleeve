module;

#include <enet/enet.h>

export module DreamNet.Packet;

import std;
import DreamNet.Core;

export namespace PacketFlags
{

  enum class Flag : enet_uint32
  {
    None               = 0,
    Reliable           = ENET_PACKET_FLAG_RELIABLE,
    Unsequenced        = ENET_PACKET_FLAG_UNSEQUENCED,
    NoAllocate         = ENET_PACKET_FLAG_NO_ALLOCATE,
    UnreliableFragment = ENET_PACKET_FLAG_UNRELIABLE_FRAGMENT,
  };

  [[nodiscard]] constexpr Flag operator|(const Flag lhs, const Flag rhs) noexcept
  {
    return static_cast<Flag>(static_cast<enet_uint32>(lhs) | static_cast<enet_uint32>(rhs));
  }

  [[nodiscard]] constexpr Flag operator&(const Flag lhs, const Flag rhs) noexcept
  {
    return static_cast<Flag>(static_cast<enet_uint32>(lhs) & static_cast<enet_uint32>(rhs));
  }

  constexpr Flag AllPacketFlags = Flag::Reliable | Flag::Unsequenced | Flag::NoAllocate | Flag::UnreliableFragment;

  constexpr Flag operator~(const Flag value) noexcept
  {
    return static_cast<Flag>((~static_cast<enet_uint32>(value)) & static_cast<enet_uint32>(AllPacketFlags));
  }

  constexpr Flag& operator|=(Flag& lhs, const Flag rhs) noexcept
  {
    lhs = lhs | rhs;
    return lhs;
  }

  [[nodiscard]] constexpr bool HasFlag(const Flag value, const Flag flag) noexcept
  {
    return (static_cast<enet_uint32>(value) & static_cast<enet_uint32>(flag)) != 0;
  }

  [[nodiscard]] constexpr enet_uint32 ToNative(const Flag flags) noexcept
  {
    return static_cast<enet_uint32>(flags);
  }

  [[nodiscard]] constexpr Flag FromRaw(const enet_uint32 flags) noexcept
  {
    return static_cast<Flag>(flags);
  }

  [[nodiscard]] constexpr bool IsValidPacketFlags(const Flag flags) noexcept
  {
    const bool reliable    = HasFlag(flags, Flag::Reliable);
    const bool unsequenced = HasFlag(flags, Flag::Unsequenced);

    return !(reliable && unsequenced);
  }

  [[nodiscard]] constexpr bool IsValidPacketFlags(const enet_uint32 flags) noexcept
  {
    return IsValidPacketFlags(FromRaw(flags));
  }

}

export using PacketFlag = PacketFlags::Flag;

export struct IPacketUserData
{
  using Ptr = std::unique_ptr<IPacketUserData>;

  virtual ~IPacketUserData() = default;
};

export class DreamNetPacket
{
  public:

  using DataSpan     = std::span<const enet_uint8>;
  using DataBytes    = std::span<const std::byte>;
  using MutableSpan  = std::span<enet_uint8>;
  using MutableBytes = std::span<std::byte>;
  using Result       = NetResult<DreamNetPacket>;

  // ENet's fragmented-message length is uint32. The configured host limit is
  // checked on send; its default is not a fixed ceiling for packet allocation.
  static constexpr std::size_t MaxDataSize = (std::numeric_limits<enet_uint32>::max)();

  DreamNetPacket(const DreamNetPacket& other)                = delete;
  DreamNetPacket(DreamNetPacket&& other) noexcept            = default;
  DreamNetPacket& operator=(const DreamNetPacket& other)     = delete;
  DreamNetPacket& operator=(DreamNetPacket&& other) noexcept = default;
  ~DreamNetPacket()                                          = default;

  static Result TryFromSpan(const DataBytes bytes, const PacketFlag flags = PacketFlag::Reliable)
  {
    auto validationResult = ValidateOwningCreate(bytes.size(), flags);
    if (!validationResult)
    {
      return std::unexpected(std::move(validationResult.error()));
    }

    ENetPacket* packet = enet_packet_create(bytes.data(), bytes.size(), ToNative(flags));

    if (packet == nullptr)
    {
      return DreamNetError::MakeUnexpected(DreamNetErrorCode::FailedCreatePacket, "enet_packet_create returned nullptr");
    }

    return DreamNetPacket(NativePtr{packet});
  }

  static Result TryFromSpan(const DataSpan span, const PacketFlag flags = PacketFlag::Reliable)
  {
    return TryFromSpan(std::as_bytes(span), flags);
  }

  // Creates an owning packet of `size` bytes without copying anything into it.
  // enet_packet_create skips its memcpy when data is null, so the buffer comes
  // back allocated but UNINITIALISED - fill it through MutableData() before
  // sending, or prefer TryAllocateWith, which closes that window.
  static Result TryAllocate(const std::size_t size, const PacketFlag flags = PacketFlag::Reliable)
  {
    auto validationResult = ValidateOwningCreate(size, flags);
    if (!validationResult)
    {
      return std::unexpected(std::move(validationResult.error()));
    }

    ENetPacket* packet = enet_packet_create(nullptr, size, ToNative(flags));

    if (packet == nullptr)
    {
      return DreamNetError::MakeUnexpected(
        DreamNetErrorCode::FailedCreatePacket,
        "enet_packet_create returned nullptr for preallocated packet");
    }

    return DreamNetPacket(NativePtr{packet});
  }

  // Allocates the packet and hands its buffer straight to `writer`, so a
  // serializer writes into ENet memory instead of into a staging buffer.
  // `writer` returns false to reject the packet; it is then destroyed and
  // never reaches a peer.
  template <typename TWriter>
  requires std::is_invocable_r_v<bool, TWriter, MutableBytes>
  static Result TryAllocateWith(const std::size_t size, TWriter&& writer, const PacketFlag flags = PacketFlag::Reliable)
  {
    auto packet = TryAllocate(size, flags);
    if (!packet)
    {
      return packet;
    }

    if (!std::invoke(std::forward<TWriter>(writer), std::as_writable_bytes(packet->MutableData())))
    {
      return DreamNetError::MakeUnexpected(
        DreamNetErrorCode::FailedCreatePacket,
        std::format("Packet writer failed to fill preallocated buffer of {} bytes", size));
    }

    return packet;
  }

  static Result TryAdoptNative(ENetPacket* packet)
  {
    if (!packet)
    {
      return DreamNetError::MakeUnexpected(DreamNetErrorCode::NullPacket, "Cannot adopt a null ENetPacket");
    }

    if (!PacketFlags::IsValidPacketFlags(packet->flags))
    {
      return DreamNetError::MakeUnexpected(DreamNetErrorCode::InvalidPacketFlags, "Cannot adopt ENetPacket with invalid flags");
    }

    return DreamNetPacket(NativePtr{packet});
  }

  inline bool IsValid() const noexcept
  {
    return packet ? true : false;
  }

  inline ENetPacket* Native() const noexcept
  {
    return packet.get();
  }

  inline [[nodiscard]] ENetPacket* ReleaseNative() noexcept
  {
    userData.reset();
    return packet.release();
  }

  PacketFlag Flags() const noexcept
  {
    if (IsValid())
    {
      return static_cast<PacketFlag>(packet->flags);
    }

    return PacketFlag::None;
  }

  std::size_t Size() const noexcept
  {
    if (IsValid())
    {
      return packet->dataLength;
    }

    return 0;
  }

  DataSpan Data() const noexcept
  {
    const auto dataSize = Size();
    if (IsValid() && dataSize > 0)
    {
      return DataSpan(packet->data, dataSize);
    }

    return DataSpan{};
  }

  DataBytes DataBytesView() const noexcept
  {
    return std::as_bytes(Data());
  }

  // Writable view over the packet buffer. Non-const on purpose: it is only
  // meaningful before the packet is handed to a peer, and PushPacket takes the
  // packet by rvalue, so a sent packet can no longer be written through.
  // For a std::byte view, wrap it in std::as_writable_bytes at the call site.
  MutableSpan MutableData() noexcept
  {
    const auto dataSize = Size();
    if (IsValid() && dataSize > 0)
    {
      return MutableSpan(packet->data, dataSize);
    }

    return MutableSpan{};
  }

  template <typename T, typename... Args>
  T& EmplaceUserData(Args&&... args)
  {
    auto data = std::make_unique<T>(std::forward<Args>(args)...);
    T&   ref  = *data;
    userData  = std::move(data);
    return ref;
  }

  template <typename T>
  T* TryGetUserData() noexcept
  {
    if (!userData) return nullptr;
    return dynamic_cast<T*>(userData.get());
  }

  template <typename T>
  const T* TryGetUserData() const noexcept
  {
    if (!userData) return nullptr;
    return dynamic_cast<const T*>(userData.get());
  }

  bool HasUserData() const noexcept
  {
    return userData ? true : false;
  }

  void ClearUserData() noexcept
  {
    userData.reset();
  }

  IPacketUserData* RawUserData() noexcept
  {
    return userData.get();
  }

  const IPacketUserData* RawUserData() const noexcept
  {
    return userData.get();
  }

  private:

  struct NativeDeleter
  {
    void operator()(ENetPacket* nativePacket) const noexcept
    {
      if (nativePacket != nullptr)
      {
        enet_packet_destroy(nativePacket);
      }
    }
  };

  using NativePtr = std::unique_ptr<ENetPacket, NativeDeleter>;

  // Shared preconditions for every path where DreamNetPacket owns the buffer.
  static NetOperationResult ValidateOwningCreate(const std::size_t size, const PacketFlag flags)
  {
    if (!PacketFlags::IsValidPacketFlags(flags))
    {
      return DreamNetError::MakeUnexpected(DreamNetErrorCode::InvalidPacketFlags, "Packet flags are invalid for ENet packet creation");
    }

    if (PacketFlags::HasFlag(flags, PacketFlag::NoAllocate))
    {
      return DreamNetError::MakeUnexpected(
        DreamNetErrorCode::InvalidPacketFlags,
        "PacketFlag::NoAllocate is not allowed when DreamNetPacket owns the packet memory");
    }

    if (size > MaxDataSize)
    {
      return DreamNetError::MakeUnexpected(
        DreamNetErrorCode::InvalidPacket,
        std::format("Packet size must be less than or equal to {}, got {}", MaxDataSize, size));
    }

    return {};
  }

  explicit DreamNetPacket(NativePtr packet) : packet(std::move(packet)) {}

  NativePtr            packet;
  IPacketUserData::Ptr userData = nullptr;
};
