module;

#include <enet/enet.h>

export module DreamNet.Host;

import std;

import DreamNet.Packet;
import DreamNet.Address;
import DreamNet.Peer;
import DreamNet.Event;
import DreamNet.Core;

export struct ENetHostDeleter
{
    void operator()(ENetHost* host) const noexcept
    {
        if (host != nullptr)
        {
            enet_host_destroy(host);
        }
    }
};

export using ENetHostPtr = std::unique_ptr<ENetHost, ENetHostDeleter>;

export struct NetConfig
{
    size_t         maxPeers;
    size_t         channelLimit;
    BandwidthLimit inBwLimit;
    BandwidthLimit outBwLimit;
    
    static constexpr NetConfig Default() noexcept
    {
        return
        {
            .maxPeers = 32,
            .channelLimit = 2,
            .inBwLimit = 0,
            .outBwLimit = 0,
        };
    }
};

export struct ServerConfig final : NetConfig 
{
    DreamNetAddress address;
    
    static ServerConfig Default() noexcept
    {
        constexpr Port loopbackPort = 8778;
        ServerConfig   config{.address = DreamNetAddress::Loopback(loopbackPort)};
        NetConfig      defaultNet = NetConfig::Default();
        
        config.maxPeers         = defaultNet.maxPeers;
        config.channelLimit     = defaultNet.channelLimit;
        config.inBwLimit        = defaultNet.inBwLimit;
        config.outBwLimit       = defaultNet.outBwLimit;
        return config;
    }
};

export using ClientConfig = NetConfig;

export class DreamNetHost
{
public:
    using Result      = NetResult<DreamNetHost>;
    using EventResult = NetResult<std::optional<DreamNetEvent>>;

    static Result TryCreateClient(const ClientConfig config)
    {
        auto validationResult = ValidateConfig(config);
        if (!validationResult)
        {
            return DreamNetError::WrapUnexpected(
                DreamNetErrorCode::InvalidConfig,
                std::move(validationResult.error()),
                "Invalid client config when creating DreamNetHost");
        }
        
        auto host = enet_host_create(
            nullptr, 
            config.maxPeers, 
            config.channelLimit, 
            config.inBwLimit, 
            config.outBwLimit);
        
        
        if (!host)
        {
            return DreamNetError::MakeUnexpected(
                DreamNetErrorCode::FailedCreateClient,
                "enet_host_create failed for client host");
        }
        
        ENetHostPtr enetHost = ENetHostPtr(host);
        
        return DreamNetHost(std::move(enetHost));
    }
    
    static Result TryCreateServer(const ServerConfig config)
    {
        auto validationResult = ValidateConfig(config);
        if (!validationResult)
        {
            return DreamNetError::WrapUnexpected(
                DreamNetErrorCode::InvalidConfig,
                std::move(validationResult.error()),
                "Invalid server config when creating DreamNetHost");
        }
        
        auto& nativeAddress = config.address.Native();
        
        auto host = enet_host_create(
            std::addressof(nativeAddress), 
            config.maxPeers, 
            config.channelLimit, 
            config.inBwLimit, 
            config.outBwLimit);
        
        
        if (!host)
        {
            return DreamNetError::MakeUnexpected(
                DreamNetErrorCode::FailedCreateServer,
                "enet_host_create failed for server host");
        }
        
        ENetHostPtr enetHost = ENetHostPtr(host);
        
        return DreamNetHost(std::move(enetHost));
    }
    
    EventResult Service(const TimeOutMs timeoutMs)
    {
        if (!IsValid())
        {
            return DreamNetError::MakeUnexpected(
                DreamNetErrorCode::InvalidHost,
                "DreamNetHost::Service called on invalid host");
        }
        
        ENetEvent event{};
        auto serviceResult = enet_host_service(Native(), &event, timeoutMs);
        return EventResultImpl(event, serviceResult, "enet_host_service");
    }
    
    EventResult CheckEvents()
    {
        if (!IsValid())
        {
            return DreamNetError::MakeUnexpected(
                DreamNetErrorCode::InvalidHost,
                "DreamNetHost::CheckEvents called on invalid host");
        }
        
        ENetEvent event{};
        auto checkResult = enet_host_check_events(Native(), &event);
        return EventResultImpl(event, checkResult, "enet_host_check_events");
    }
    
    NetOperationResult BroadcastPushPacket(DreamNetPacket&& packet, const ChannelId channelId)
    {
        if (!IsValid())
        {
            return DreamNetError::MakeUnexpected(
                DreamNetErrorCode::InvalidHost,
                "Cannot broadcast packet through invalid DreamNetHost");
        }

        if (!packet.IsValid())
        {
            return DreamNetError::MakeUnexpected(
                DreamNetErrorCode::InvalidPacket,
                "Cannot broadcast invalid DreamNetPacket");
        }

        enet_host_broadcast(Native(), channelId, packet.ReleaseNative());
        return {};
    }
    
    NetOperationResult BroadcastPushPacketSpan(
        const DreamNetPacket::DataBytes bytes,
        const ChannelId                 channelId,
        const PacketFlag                flags = PacketFlag::Reliable)
    {
        auto packet = DreamNetPacket::TryFromSpan(bytes, flags);
        if (!packet)
        {
            return DreamNetError::WrapUnexpected(
                DreamNetErrorCode::FailedPushPacket,
                std::move(packet.error()),
                "Failed to prepare packet for host broadcast from byte span");
        }
        
        return BroadcastPushPacket(std::move(packet.value()), channelId);
    }
    
    NetOperationResult BroadcastPushPacketSpan(
        const DreamNetPacket::DataSpan data,
        const ChannelId                channelId,
        const PacketFlag               flags = PacketFlag::Reliable)
    {
        return BroadcastPushPacketSpan(std::as_bytes(data), channelId, flags);
    }
    
    DreamNetPeer::Result Connect(const DreamNetAddress& address, size_t channelCount, enet_uint32 data = 0)
    {
        if (!IsValid()) 
            return DreamNetError::MakeUnexpected(
                DreamNetErrorCode::InvalidHost, 
                "Can't create connect, host invalid");
        
        auto& nativeAddress = address.Native();
        auto nativePeer = enet_host_connect(Native(), std::addressof(nativeAddress), channelCount, data);
        
        if (!nativePeer)
        {
            auto message = 
                std::format("Peer is null when try create connect with address: {}", address.ToString());
            return DreamNetError::MakeUnexpected(DreamNetErrorCode::FailedCreateConnect, message);
        }
        
        auto peer = DreamNetPeer::TryFromNative(nativePeer);
        if (!peer)
        {
            return DreamNetError::WrapUnexpected(
                DreamNetErrorCode::InvalidPeer, 
                std::move(peer.error()), 
                "Can't configure DreamNetPeer after success create connect peer with address: {}", address.ToString());
        }
        
        return peer.value();
    }
    
    void FlushPackets() noexcept
    {
        if (!IsValid()) return;
        enet_host_flush(Native());
    }
    
    inline ENetHost* Native() const noexcept
    {
        return enetHost.get();
    }
    
    inline bool IsValid() const noexcept
    {
        return enetHost ? true : false;
    }
    
private:
    static EventResult EventResultImpl(const ENetEvent& event, const int result, std::string_view function)
    {
        if (result == 0) return std::nullopt;
        if (result < 0)
        {
            return DreamNetError::MakeUnexpected(
                DreamNetErrorCode::FailedHostService,
                std::format("{} returned a negative result", function));
        }
        
        auto dreamEvent = DreamNetEvent::TryFromNative(event);
        if (!dreamEvent)
        {
            return DreamNetError::WrapUnexpected(
                DreamNetErrorCode::FailedEventBuild,
                std::move(dreamEvent.error()),
                "Failed to build DreamNetEvent from ENetEvent");
        }
        
        return std::optional<DreamNetEvent>{std::move(dreamEvent.value())};
    }
    
    static NetOperationResult ValidateConfig(const NetConfig& config)
    {
        if (config.maxPeers == 0)
        {
            return DreamNetError::MakeUnexpected(
                DreamNetErrorCode::InvalidConfig,
                "NetConfig.maxPeers must be greater than 0");
        }

        if (config.maxPeers > ENET_PROTOCOL_MAXIMUM_PEER_ID)
        {
            return DreamNetError::MakeUnexpected(
                DreamNetErrorCode::InvalidConfig,
                std::format(
                    "NetConfig.maxPeers must be less than or equal to {}, got {}",
                    static_cast<std::size_t>(ENET_PROTOCOL_MAXIMUM_PEER_ID),
                    config.maxPeers));
        }

        if (config.channelLimit != 0 &&
            (config.channelLimit < ENET_PROTOCOL_MINIMUM_CHANNEL_COUNT ||
             config.channelLimit > ENET_PROTOCOL_MAXIMUM_CHANNEL_COUNT))
        {
            return DreamNetError::MakeUnexpected(
                DreamNetErrorCode::InvalidConfig,
                std::format(
                    "NetConfig.channelLimit must be 0 or in range [{}, {}], got {}",
                    static_cast<std::size_t>(ENET_PROTOCOL_MINIMUM_CHANNEL_COUNT),
                    static_cast<std::size_t>(ENET_PROTOCOL_MAXIMUM_CHANNEL_COUNT),
                    config.channelLimit));
        }

        return {};
    }
    explicit DreamNetHost(ENetHostPtr enetHost) : enetHost(std::move(enetHost)) {}
    
    ENetHostPtr enetHost = nullptr;
};

export using DreamNetHostPtr = std::unique_ptr<DreamNetHost>;
