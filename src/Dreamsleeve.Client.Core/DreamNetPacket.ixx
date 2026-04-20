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

export struct IPacketUserData
{
    virtual ~IPacketUserData() = default;
};

export using IPacketUserDataPtr = std::unique_ptr<IPacketUserData>;

export class DreamNetPacket
{
public:
    
    using DataSpan  = std::span<const enet_uint8>;
    using DataBytes = std::span<const std::byte>;
    using Result = NetResult<DreamNetPacket>;
    
    DreamNetPacket(const DreamNetPacket& other)                = delete;
    DreamNetPacket(DreamNetPacket&& other) noexcept            = default;
    DreamNetPacket& operator=(const DreamNetPacket& other)     = delete;
    DreamNetPacket& operator=(DreamNetPacket&& other) noexcept = default;
    ~DreamNetPacket()                                          = default;
    
    static Result TryFromSpan(const DataBytes bytes, const PacketFlag flags = PacketFlag::Reliable)
    {
        const auto isValidFlags = PacketFlags::IsValidPacketFlags(flags);
        
        if (!isValidFlags)
        {
            return DreamNetError::MakeUnexpected(
                DreamNetErrorCode::InvalidPacketFlags,
                "Packet flags are invalid for ENet packet creation");
        }
        
        if (PacketFlags::HasFlag(flags, PacketFlag::NoAllocate))
        {
            return DreamNetError::MakeUnexpected(
                DreamNetErrorCode::InvalidPacketFlags,
                "PacketFlag::NoAllocate is not allowed when DreamNetPacket owns the packet memory");
        }

        ENetPacket* packet = enet_packet_create(
            bytes.data(),
            bytes.size(),
            ToNative(flags)
        );

        if (packet == nullptr)
        {
            return DreamNetError::MakeUnexpected(
                DreamNetErrorCode::FailedCreatePacket,
                "enet_packet_create returned nullptr");
        }
        
        return DreamNetPacket(ENetPacketPtr{packet});
    }
    
    static Result TryFromSpan(const DataSpan span, const PacketFlag flags = PacketFlag::Reliable)
    {
        return TryFromSpan(std::as_bytes(span), flags);
    }
    
    static Result TryAdoptNative(ENetPacket* packet)
    {
        if (!packet)
        {
            return DreamNetError::MakeUnexpected(
                DreamNetErrorCode::NullPacket,
                "Cannot adopt a null ENetPacket");
        }
        
        if (!PacketFlags::IsValidPacketFlags(packet->flags))
        {
            return DreamNetError::MakeUnexpected(
                DreamNetErrorCode::InvalidPacketFlags,
                "Cannot adopt ENetPacket with invalid flags");
        }
        
        return DreamNetPacket(ENetPacketPtr{packet});
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
    
    template <typename T, typename... Args>
    T& EmplaceUserData(Args&&... args)
    {
        auto data = std::make_unique<T>(std::forward<Args>(args)...);
        T& ref = *data;
        userData = std::move(data);
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
    explicit DreamNetPacket(ENetPacketPtr packet) : packet(std::move(packet)) {}
    
    ENetPacketPtr packet;
    IPacketUserDataPtr userData = nullptr;
};

export using DreamNetPacketPtr = std::unique_ptr<DreamNetPacket>;
