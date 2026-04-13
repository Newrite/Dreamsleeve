module;

#include <enet/enet.h>
#include <spdlog/spdlog.h>

export module DreamNet.Packet;

import std;

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
        return static_cast<Flag>(
            static_cast<enet_uint32>(lhs) | static_cast<enet_uint32>(rhs)
        );
    }
    
    [[nodiscard]] constexpr Flag operator&(const Flag lhs, const Flag rhs) noexcept
    {
        return static_cast<Flag>(
            static_cast<enet_uint32>(lhs) & static_cast<enet_uint32>(rhs)
        );
    }
    
    constexpr Flag AllPacketFlags =
        Flag::Reliable |
        Flag::Unsequenced |
        Flag::NoAllocate |
        Flag::UnreliableFragment;
    
    constexpr Flag operator~(const Flag value) noexcept
    {
        return static_cast<Flag>(
            (~static_cast<enet_uint32>(value)) &
            static_cast<enet_uint32>(AllPacketFlags)
        );
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
        const bool reliable = HasFlag(flags, Flag::Reliable);
        const bool unsequenced = HasFlag(flags, Flag::Unsequenced);
    
        return !(reliable && unsequenced);
    }
    
    [[nodiscard]] constexpr bool IsValidPacketFlags(const enet_uint32 flags) noexcept
    {
        return IsValidPacketFlags(FromRaw(flags));
    }
    
}

export using PacketFlag = PacketFlags::Flag;

export struct ENetPacketPtrDeleter
{
    void operator()(ENetPacket* packet) const noexcept
    {
        if (packet != nullptr)
        {
            enet_packet_destroy(packet);
        }
    }
};

export using ENetPacketPtr = std::unique_ptr<ENetPacket, ENetPacketPtrDeleter>;

export class DreamNetPacket
{
public:
    
    using DataSpan  = std::span<const enet_uint8>;
    using DataBytes = std::span<const std::byte>;
    
    enum class Error : std::uint8_t
    {
        NullPacket,
        InvalidFlags,
        CreateFailed,
    };
    
    DreamNetPacket(const DreamNetPacket& other) = delete;
    DreamNetPacket(DreamNetPacket&& other) noexcept = default;
    DreamNetPacket& operator=(const DreamNetPacket& other) = delete;
    DreamNetPacket& operator=(DreamNetPacket&& other) noexcept = default;
    ~DreamNetPacket() = default;
    
    [[nodiscard]] static std::expected<DreamNetPacket, Error> TryFromSpan(const DataBytes bytes, const PacketFlag flags = PacketFlag::Reliable)
    {
        const auto isValidFlags = PacketFlags::IsValidPacketFlags(flags);
        
        if (!isValidFlags)
        {
            spdlog::warn("DreamNetPacket::TryFromSpan: Invalid Packet Flags");
            return std::unexpected(Error::InvalidFlags);
        }
        
        if (!isValidFlags || PacketFlags::HasFlag(flags, PacketFlag::NoAllocate))
        {
            spdlog::warn("DreamNetPacket::TryFromSpan: NoAllocate flag not supported with TryFromSpan");
            return std::unexpected(Error::InvalidFlags);
        }

        ENetPacket* packet = enet_packet_create(
            bytes.data(),
            bytes.size(),
            ToNative(flags)
        );

        if (packet == nullptr)
        {
            spdlog::warn("DreamNetPacket::TryFromSpan: Failed create packet");
            return std::unexpected(Error::CreateFailed);
        }
        
        return DreamNetPacket(packet);
    }
    
    [[nodiscard]] static std::expected<DreamNetPacket, Error> TryFromSpan(const DataSpan span, const PacketFlag flags = PacketFlag::Reliable)
    {
        return TryFromSpan(std::as_bytes(span), flags);
    }
    
    [[nodiscard]] static std::expected<DreamNetPacket, Error> TryAdoptNative(ENetPacket* packet)
    {
        if (!packet)
        {
            return std::unexpected(Error::NullPacket);
        }
        
        if (!PacketFlags::IsValidPacketFlags(packet->flags))
        {
            spdlog::warn("DreamNetPacket::TryAdoptNative: Invalid Packet Flags");
            return std::unexpected(Error::InvalidFlags);
        }
        
        return DreamNetPacket(packet);
    }
    
    [[nodiscard]] bool IsValid() const noexcept
    {
        return packet ? true : false;
    }
    
    [[nodiscard]] ENetPacket* Native() const noexcept
    {
        return packet.get();
    }
    
    [[nodiscard]] PacketFlag Flags() const noexcept
    {
        if (packet)
        {
            return static_cast<PacketFlag>(packet->flags);
        }
        
        return PacketFlag::None;
    }
    
    [[nodiscard]] std::size_t Size() const noexcept
    {
        if (packet)
        {
            return packet->dataLength;
        }
        
        return 0;
    }
    
    [[nodiscard]] DataSpan Data() const noexcept
    {
        const auto dataSize = Size();
        if (packet && dataSize > 0)
        {
            return DataSpan(packet->data, dataSize);
        }
        
        return DataSpan{};
    }
    
    [[nodiscard]] DataBytes DataBytesView() const noexcept
    {
        return std::as_bytes(Data());
    }

private:
    explicit DreamNetPacket(ENetPacket* packet) : packet(ENetPacketPtr{packet}) {}
    
    ENetPacketPtr packet;
};