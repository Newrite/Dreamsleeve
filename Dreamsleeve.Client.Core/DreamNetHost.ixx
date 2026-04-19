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

export struct HostBandwidthLimitConfig final
{
    BandwidthLimit incoming;
    BandwidthLimit outgoing;

    static constexpr HostBandwidthLimitConfig Unlimited() noexcept
    {
        return
        {
            .incoming = 0,
            .outgoing = 0,
        };
    }
};

export struct HostRuntimeConfig final
{
    std::optional<HostBandwidthLimitConfig> bandwidthLimit;
    std::optional<ChannelLimit>             channelLimit;

    static constexpr HostRuntimeConfig Defaults() noexcept
    {
        return
        {
            .bandwidthLimit = HostBandwidthLimitConfig::Unlimited(),
            .channelLimit   = NetConfig::Default().channelLimit,
        };
    }
};

export struct HostInfo final
{
    DreamNetAddress address;
    size_t          peerCount;
    size_t          connectedPeers;
    size_t          duplicatePeers;
    ChannelLimit    channelLimit;
    enet_uint32     incomingBandwidth;
    enet_uint32     outgoingBandwidth;
};

export struct HostTelemetry final
{
    size_t      connectedPeers;
    enet_uint32 totalSentData;
    enet_uint32 totalSentPackets;
    enet_uint32 totalReceivedData;
    enet_uint32 totalReceivedPackets;
    enet_uint32 serviceTime;
};

export class DreamNetHost
{
public:
    using Result      = NetResult<DreamNetHost>;
    using EventResult = NetResult<std::optional<DreamNetEvent>>;
    
    DreamNetHost(const DreamNetHost&)                = delete;
    DreamNetHost(DreamNetHost&&) noexcept            = default;
    DreamNetHost& operator=(const DreamNetHost&)     = delete;
    DreamNetHost& operator=(DreamNetHost&&) noexcept = default;
    ~DreamNetHost()                                  = default;

    static Result TryCreateClient(
        const ClientConfig config,
        const std::optional<HostRuntimeConfig>& runtimeConfig = std::nullopt)
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
        auto dreamHost = DreamNetHost(std::move(enetHost));

        if (runtimeConfig)
        {
            auto result = dreamHost.ApplyRuntimeConfig(runtimeConfig.value());
            if (!result)
            {
                return DreamNetError::WrapUnexpected(
                    DreamNetErrorCode::InvalidConfig,
                    std::move(result.error()),
                    "Failed to apply host runtime config during client DreamNetHost creation");
            }
        }

        return dreamHost;
    }
    
    static Result TryCreateServer(
        const ServerConfig config,
        const std::optional<HostRuntimeConfig>& runtimeConfig = std::nullopt)
    {
        auto validationResult = ValidateConfig(config);
        if (!validationResult)
        {
            return DreamNetError::WrapUnexpected(
                DreamNetErrorCode::InvalidConfig,
                std::move(validationResult.error()),
                "Invalid server config when creating DreamNetHost");
        }
        
        const auto& nativeAddress = config.address.Native();
        
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
        auto dreamHost = DreamNetHost(std::move(enetHost));

        if (runtimeConfig)
        {
            auto result = dreamHost.ApplyRuntimeConfig(runtimeConfig.value());
            if (!result)
            {
                return DreamNetError::WrapUnexpected(
                    DreamNetErrorCode::InvalidConfig,
                    std::move(result.error()),
                    "Failed to apply host runtime config during server DreamNetHost creation");
            }
        }

        return dreamHost;
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

    NetOperationResult ApplyRuntimeConfig(const HostRuntimeConfig& config)
    {
        if (!IsValid())
        {
            return DreamNetError::MakeUnexpected(
                DreamNetErrorCode::InvalidHost,
                "Cannot apply runtime config to invalid DreamNetHost");
        }

        if (config.channelLimit)
        {
            auto validationResult = ValidateChannelLimit(
                *config.channelLimit,
                "HostRuntimeConfig.channelLimit");
            if (!validationResult)
            {
                return validationResult;
            }
        }

        if (config.bandwidthLimit)
        {
            const auto& bandwidthLimit = *config.bandwidthLimit;
            enet_host_bandwidth_limit(
                Native(),
                bandwidthLimit.incoming,
                bandwidthLimit.outgoing);
        }

        if (config.channelLimit)
        {
            enet_host_channel_limit(Native(), *config.channelLimit);
        }

        return {};
    }
    
    DreamNetPeer::Result Connect(const DreamNetAddress& address, ChannelLimit channelCount, enet_uint32 data = 0)
    {
        if (!IsValid()) 
            return DreamNetError::MakeUnexpected(
                DreamNetErrorCode::InvalidHost, 
                "Cannot initiate connect on invalid host");
        
        auto channelLimitValidationResult = ValidateChannelLimit(channelCount, "DreamNetPeer::Result Connect");
        if (!channelLimitValidationResult)
        {
            auto message = std::format("Invalid channelCount with count {}", channelCount);
            return DreamNetError::WrapUnexpected(
                DreamNetErrorCode::FailedCreateConnect, 
                std::move(channelLimitValidationResult.error()), 
                message);
        }
        
        const auto& nativeAddress = address.Native();
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
            auto message = 
                std::format("Can't configure DreamNetPeer after success create connect peer with address: {}", address.ToString());
            return DreamNetError::WrapUnexpected(DreamNetErrorCode::InvalidPeer, std::move(peer.error()), message);
        }
        
        return peer.value();
    }
    
    void FlushPackets() noexcept
    {
        if (!IsValid()) return;
        enet_host_flush(Native());
    }
    
    std::optional<DreamNetAddress> TryGetSocketAddress() const noexcept
    {
        if (!IsValid()) return std::nullopt;
        return DreamNetAddress::FromNative(Native()->address);
    }
    
    void BandwidthThrottle() noexcept
    {
        if (!IsValid()) return;
        enet_host_bandwidth_throttle(Native());
    }
    std::optional<DreamNetPeer> TryGetPeer(const size_t slot) const noexcept
    {
        if (!IsValid()) return std::nullopt;
        
        if (auto hostInfo = GetHostInfo())
        {
            if (slot >= hostInfo->peerCount) return std::nullopt;
            
            auto& nativePeer = Native()->peers[slot];
            auto peer = DreamNetPeer::TryFromNative(std::addressof(nativePeer));
            if (!peer) return std::nullopt;
            
            return peer.value();
        }

        return std::nullopt;
    }
    
    template <typename TCallback>
    void ForEachPeerSlot(TCallback&& callback)
    {
        if (!IsValid()) return;
        
        auto hostInfo = GetHostInfo();
        if (!hostInfo) return;

        for (const size_t peerIndex : std::views::iota(size_t{0}, hostInfo->peerCount))
        {
            auto& nativePeer = Native()->peers[peerIndex];
            auto peer = DreamNetPeer::TryFromNative(std::addressof(nativePeer));
            if (!peer) continue;
            if (!callback(peer.value())) return;
        }
    }

    std::optional<HostInfo> GetHostInfo() const noexcept
    {
        if (!IsValid())
        {
            return std::nullopt;
        }

        return HostInfo
        {
            .address           = DreamNetAddress::FromNative(host->address),
            .peerCount         = host->peerCount,
            .connectedPeers    = host->connectedPeers,
            .duplicatePeers    = host->duplicatePeers,
            .channelLimit      = host->channelLimit,
            .incomingBandwidth = host->incomingBandwidth,
            .outgoingBandwidth = host->outgoingBandwidth,
        };
    }

    std::optional<HostTelemetry> GetHostTelemetry() const noexcept
    {
        if (!IsValid())
        {
            return std::nullopt;
        }

        return HostTelemetry
        {
            .connectedPeers       = host->connectedPeers,
            .totalSentData        = host->totalSentData,
            .totalSentPackets     = host->totalSentPackets,
            .totalReceivedData    = host->totalReceivedData,
            .totalReceivedPackets = host->totalReceivedPackets,
            .serviceTime          = host->serviceTime,
        };
    }
    
    inline ENetHost* Native() const noexcept
    {
        return host.get();
    }
    
    inline bool IsValid() const noexcept
    {
        return host ? true : false;
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
        auto maxPeersValidationResult = ValidateMaxPeers(config.maxPeers);
        if (!maxPeersValidationResult)
        {
            return maxPeersValidationResult;
        }

        auto channelLimitValidationResult = ValidateChannelLimit(
            config.channelLimit,
            "NetConfig.channelLimit");
        if (!channelLimitValidationResult)
        {
            return channelLimitValidationResult;
        }

        return {};
    }

    static NetOperationResult ValidateMaxPeers(const size_t maxPeers)
    {
        if (maxPeers == 0)
        {
            return DreamNetError::MakeUnexpected(
                DreamNetErrorCode::InvalidConfig,
                "NetConfig.maxPeers must be greater than 0");
        }

        if (maxPeers > ENET_PROTOCOL_MAXIMUM_PEER_ID)
        {
            return DreamNetError::MakeUnexpected(
                DreamNetErrorCode::InvalidConfig,
                std::format(
                    "NetConfig.maxPeers must be less than or equal to {}, got {}",
                    static_cast<std::size_t>(ENET_PROTOCOL_MAXIMUM_PEER_ID),
                    maxPeers));
        }

        return {};
    }

    static NetOperationResult ValidateChannelLimit(
        const ChannelLimit channelLimit,
        const std::string_view fieldName)
    {
        if (channelLimit != 0 &&
            (channelLimit < ENET_PROTOCOL_MINIMUM_CHANNEL_COUNT ||
             channelLimit > ENET_PROTOCOL_MAXIMUM_CHANNEL_COUNT))
        {
            return DreamNetError::MakeUnexpected(
                DreamNetErrorCode::InvalidConfig,
                std::format(
                    "{} must be 0 or in range [{}, {}], got {}",
                    fieldName,
                    static_cast<std::size_t>(ENET_PROTOCOL_MINIMUM_CHANNEL_COUNT),
                    static_cast<std::size_t>(ENET_PROTOCOL_MAXIMUM_CHANNEL_COUNT),
                    static_cast<std::size_t>(channelLimit)));
        }

        return {};
    }
    explicit DreamNetHost(ENetHostPtr enetHost) : host(std::move(enetHost)) {}
    
    ENetHostPtr host = nullptr;
};

export using DreamNetHostPtr = std::unique_ptr<DreamNetHost>;
