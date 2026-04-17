module;

#include <enet/enet.h>

export module DreamNet.Event;

import std;

import DreamNet.Peer;
import DreamNet.Packet;

export enum class EventType : std::uint8_t
{
    None        = ENET_EVENT_TYPE_NONE,
    Connect     = ENET_EVENT_TYPE_CONNECT,
    Disconnect  = ENET_EVENT_TYPE_DISCONNECT,
    Receive     = ENET_EVENT_TYPE_RECEIVE,
};

export struct EventInfo final
{
private:
    enet_uint32 data{};
    ChannelId   channelID{};
    
    explicit EventInfo(const enet_uint32 data, const ChannelId channelID, const EventType type) 
        : data(data), channelID(channelID), type(type) {}
public:
    EventType   type{EventType::None};

    static EventInfo FromEvent(const ENetEvent& event) noexcept
    {
        return EventInfo(event.data, event.channelID, static_cast<EventType>(event.type));
    }

    bool IsNone() const noexcept
    {
        return type == EventType::None;
    }

    bool IsConnect() const noexcept
    {
        return type == EventType::Connect;
    }

    bool IsDisconnect() const noexcept
    {
        return type == EventType::Disconnect;
    }

    bool IsReceive() const noexcept
    {
        return type == EventType::Receive;
    }

    bool HasChannelId() const noexcept
    {
        return IsReceive();
    }

    bool HasData() const noexcept
    {
        return IsDisconnect();
    }

    std::optional<ChannelId> TryChannelId() const noexcept
    {
        if (!HasChannelId())
        {
            return std::nullopt;
        }

        return channelID;
    }

    std::optional<enet_uint32> TryData() const noexcept
    {
        if (!HasData())
        {
            return std::nullopt;
        }

        return data;
    }

    std::optional<DisconnectReason> TryDisconnectReason() const noexcept
    {
        if (!HasData())
        {
            return std::nullopt;
        }

        return static_cast<DisconnectReason>(data);
    }
};

export class DreamNetEvent
{
public:
    enum class Error : std::uint8_t
    {
        ReceivePacketFailed,
        InvalidPeer,
    };
    
    DreamNetEvent(const DreamNetEvent& other)                = delete;
    DreamNetEvent(DreamNetEvent&& other) noexcept            = default;
    DreamNetEvent& operator=(const DreamNetEvent& other)     = delete;
    DreamNetEvent& operator=(DreamNetEvent&& other) noexcept = default;
    ~DreamNetEvent()                                         = default;
    
    static std::expected<DreamNetEvent, Error> TryFromNative(const ENetEvent& event)
    {
        EventInfo info = EventInfo::FromEvent(event);

        std::optional<DreamNetPeer> peer = std::nullopt;
        if (event.peer)
        {
            if (auto peerResult = DreamNetPeer::TryFromNative(event.peer); peerResult)
            {
                peer = std::move(peerResult.value());
            } else
            {
                return std::unexpected(Error::InvalidPeer);
            }
        }

        std::optional<DreamNetPacket> packet = std::nullopt;
        if (event.type == ENET_EVENT_TYPE_RECEIVE && event.packet)
        {
            if (auto packetResult = DreamNetPacket::TryAdoptNative(event.packet); packetResult)
            {
                packet = std::move(packetResult.value());
            } else
            {
                return std::unexpected(Error::ReceivePacketFailed);
            }
        }

        return DreamNetEvent(std::move(info), std::move(peer), std::move(packet));
    }
    
    bool HasPeer() const noexcept
    {
        return peer.has_value();
    }

    bool HasPacket() const noexcept
    {
        return packet.has_value();
    }
    
    bool IsNone() const noexcept
    {
        return info.IsNone();
    }

    bool IsConnect() const noexcept
    {
        return info.IsConnect();
    }

    bool IsDisconnect() const noexcept
    {
        return info.IsDisconnect();
    }

    bool IsReceive() const noexcept
    {
        return info.IsReceive();
    }

    std::optional<ChannelId> TryChannelId() const noexcept
    {
        return info.TryChannelId();
    }

    std::optional<DisconnectReason> TryDisconnectReason() const noexcept
    {
        return info.TryDisconnectReason();
    }
    
    std::optional<DreamNetPeer> Peer() const noexcept
    {
        return peer;
    }
    
    const DreamNetPacket* ViewPacket() const noexcept
    {
        if (packet) return std::addressof(packet.value());
        return nullptr;
    }
    
    std::optional<DreamNetPacket> AcquirePacket() noexcept
    {
        auto result = std::move(packet);
        packet.reset();
        return result;
    }
    
    EventInfo Info() const noexcept
    {
        return info;
    }
    
    EventType Type() const noexcept
    {
        return info.type;
    }
private:
    explicit DreamNetEvent(
        EventInfo info,
        std::optional<DreamNetPeer> peer,
        std::optional<DreamNetPacket> packet) : 
    info(std::move(info)), peer(std::move(peer)), packet(std::move(packet)) {} 
    
    EventInfo                     info;
    std::optional<DreamNetPeer>   peer;
    std::optional<DreamNetPacket> packet;
};

export using DreamNetEventPtr = std::unique_ptr<DreamNetEvent>;