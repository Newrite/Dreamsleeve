module;

#include <enet/enet.h>

export module DreamNet.Peer;

import std;

import DreamNet.Packet;
import DreamNet.Address;
import DreamNet.Core;

export struct ReceiveResult
{
    DreamNetPacket packet;
    ChannelId      channelId;
};

export struct PeerTelemetry final
{
    struct TransportInfo final
    {
        enet_uint32   mtu;
        enet_uint32   windowSize;
        enet_uint32   incomingDataTotal;
        enet_uint32   outgoingDataTotal;
        enet_uint32   reliableDataInTransit;
        enet_uint32   lastReceiveTime;
        enet_uint32   lastSendTime;
        ENetPeerState state;
    };
    
    struct RoundTripTimeInfo final
    {
        enet_uint32 lastRoundTripTime;
        enet_uint32 lowestRoundTripTime;
        enet_uint32 roundTripTime;
        enet_uint32 roundTripTimeVariance;
    };
    
    struct PacketStats final
    {
        enet_uint32 packetLoss;
        enet_uint32 packetLossVariance;  
        enet_uint32 packetsLost;  
        enet_uint32 packetsSent;  
    };
    
    struct PacketThrottleStats final
    {
        enet_uint32 packetThrottle;
        enet_uint32 packetThrottleLimit;
        enet_uint32 packetThrottleAcceleration;
        enet_uint32 packetThrottleDeceleration;
        enet_uint32 packetThrottleInterval;
    };
        
    TransportInfo       transportInfo;
    PacketStats         packetStats;
    PacketThrottleStats packetThrottleStats;
    RoundTripTimeInfo   roundTripTimeInfo;
    
    double PacketLossRatio() const noexcept
    {
        constexpr double scale = static_cast<double>(ENET_PEER_PACKET_LOSS_SCALE);
        return scale > 0.0
            ? static_cast<double>(packetStats.packetLoss) / scale
            : 0.0;
    }
    double PacketLossPercent() const noexcept
    {
        return PacketLossRatio() * 100.0;
    }
    
    double RoundTripTimeSeconds() const noexcept
    {
        return static_cast<double>(roundTripTimeInfo.roundTripTime) / 1000.0;
    }
    
    double LastRoundTripTimeSeconds() const noexcept
    {
        return static_cast<double>(roundTripTimeInfo.lastRoundTripTime) / 1000.0;
    }
    
    double LowestRoundTripTimeSeconds() const noexcept
    {
        return static_cast<double>(roundTripTimeInfo.lowestRoundTripTime) / 1000.0;
    }
};
    
export struct PeerInfo final
{
    ENetPeerState   state;
    DreamNetAddress address;
    size_t          channelCount;
    enet_uint32     incomingBandwidth;
    enet_uint32     outgoingBandwidth;
};

export struct TimeoutConfig final
{
    enet_uint32 limit;
    enet_uint32 minimumMs;
    enet_uint32 maximumMs;

    static constexpr TimeoutConfig Default() noexcept
    {
        return
        {
            .limit     = ENET_PEER_TIMEOUT_LIMIT,
            .minimumMs = ENET_PEER_TIMEOUT_MINIMUM,
            .maximumMs = ENET_PEER_TIMEOUT_MAXIMUM,
        };
    }
};

export struct ThrottleConfig final
{
    enet_uint32 intervalMs;
    enet_uint32 acceleration;
    enet_uint32 deceleration;

    static constexpr ThrottleConfig Default() noexcept
    {
        return
        {
            .intervalMs   = ENET_PEER_PACKET_THROTTLE_INTERVAL,
            .acceleration = ENET_PEER_PACKET_THROTTLE_ACCELERATION,
            .deceleration = ENET_PEER_PACKET_THROTTLE_DECELERATION,
        };
    }
};

export struct PeerRuntimeConfig final
{
    std::optional<TimeoutConfig>  timeout;
    std::optional<ThrottleConfig> throttle;
    std::optional<PingIntervalMs> pingIntervalMs;

    static constexpr PeerRuntimeConfig Defaults() noexcept
    {
        return
        {
            .timeout        = TimeoutConfig::Default(),
            .throttle       = ThrottleConfig::Default(),
            .pingIntervalMs = ENET_PEER_PING_INTERVAL,
        };
    }
};

export class DreamNetPeer
{
public:
    using Result                 = NetResult<DreamNetPeer>;
    using ReceiveOperationResult = NetResult<std::optional<ReceiveResult>>;

    static Result TryFromNative(ENetPeer* peer, const std::optional<PeerRuntimeConfig>& config = std::nullopt)
    {
        if (!peer)
        {
            return DreamNetError::MakeUnexpected(
                DreamNetErrorCode::InvalidPeer,
                "Cannot create DreamNetPeer from null ENetPeer");
        }
        
        auto dreamPeer = DreamNetPeer(peer);
        if (config)
        {
            auto result = dreamPeer.ApplyRuntimeConfig(config.value());
            if (!result)
            {
                return DreamNetError::WrapUnexpected(
                    DreamNetErrorCode::InvalidConfig,
                    std::move(result.error()),
                    "Failed to apply peer runtime config during DreamNetPeer creation");
            }
        }
        
        return dreamPeer;
    }
    
    NetOperationResult ApplyRuntimeConfig(const PeerRuntimeConfig& config) noexcept
    {
        if (!IsValid())
        {
            return DreamNetError::MakeUnexpected(
                DreamNetErrorCode::InvalidPeer,
                "Cannot apply runtime config to invalid DreamNetPeer");
        }

        if (config.timeout)
        {
            const auto& timeout = *config.timeout;
            if (timeout.limit == 0 || timeout.minimumMs > timeout.maximumMs)
            {
                return DreamNetError::MakeUnexpected(
                    DreamNetErrorCode::InvalidTimeoutConfig,
                    "TimeoutConfig must have non-zero limit and minimumMs <= maximumMs");
            }
        }

        if (config.throttle)
        {
            const auto& throttle = *config.throttle;
            if (throttle.intervalMs == 0)
            {
                return DreamNetError::MakeUnexpected(
                    DreamNetErrorCode::InvalidThrottleConfig,
                    "ThrottleConfig intervalMs must be non-zero");
            }
        }

        if (config.pingIntervalMs)
        {
            if (*config.pingIntervalMs == 0)
            {
                return DreamNetError::MakeUnexpected(
                    DreamNetErrorCode::InvalidPingInterval,
                    "Ping interval must be non-zero");
            }
        }

        if (config.timeout)
        {
            const auto& timeout = *config.timeout;
            enet_peer_timeout(Native(), timeout.limit, timeout.minimumMs, timeout.maximumMs);
        }

        if (config.throttle)
        {
            const auto& throttle = *config.throttle;
            enet_peer_throttle_configure(
                Native(),
                throttle.intervalMs,
                throttle.acceleration,
                throttle.deceleration
            );
        }

        if (config.pingIntervalMs)
        {
            enet_peer_ping_interval(Native(), *config.pingIntervalMs);
        }

        return std::monostate{};
    }
    
    NetOperationResult PushPacket(DreamNetPacket&& packet, const ChannelId channelId)
    {
        if (!CanSend())
        {
            return DreamNetError::MakeUnexpected(
                DreamNetErrorCode::InvalidPeerState,
                "Cannot send packet because peer is not connected");
        }

        return PushPacketImpl(std::move(packet), channelId);
    }
    
    NetOperationResult PushSpan(
        const DreamNetPacket::DataBytes bytes,
        const ChannelId                 channelId,
        const PacketFlag                flags = PacketFlag::Reliable)
    {
        auto packet = DreamNetPacket::TryFromSpan(bytes, flags);
        if (!packet)
        {
            return std::unexpected(std::move(packet.error()));
        }
        
        return PushPacket(std::move(packet.value()), channelId);
    }
    
    NetOperationResult PushSpan(
        const DreamNetPacket::DataSpan data, 
        const ChannelId                channelId, 
        const PacketFlag               flags = PacketFlag::Reliable)
    {
        return PushSpan(std::as_bytes(data), channelId, flags);
    }
    
    NetOperationResult PushPacketUnchecked(DreamNetPacket&& packet, const ChannelId channelId)
    {
        return PushPacketImpl(std::move(packet), channelId);
    }
    
    NetOperationResult PushSpanUnchecked(
        const DreamNetPacket::DataBytes bytes,
        const ChannelId                 channelId,
        const PacketFlag                flags = PacketFlag::Reliable)
    {
        auto packet = DreamNetPacket::TryFromSpan(bytes, flags);
        if (!packet)
        {
            return std::unexpected(std::move(packet.error()));
        }
        
        return PushPacketUnchecked(std::move(packet.value()), channelId);
    }
    
    NetOperationResult PushSpanUnchecked(
        const DreamNetPacket::DataSpan data, 
        const ChannelId                channelId, 
        const PacketFlag               flags = PacketFlag::Reliable)
    {
        return PushSpanUnchecked(std::as_bytes(data), channelId, flags);
    }
    
    void Disconnect(
        const DisconnectType   type   = DisconnectType::Normal,
        const DisconnectReason reason = DisconnectReason::Unspecified)
    {
        if (!IsValid()) return;
        switch (type) {
            case DisconnectType::Normal: 
                return enet_peer_disconnect       (Native(), static_cast<enet_uint32>(reason));
            case DisconnectType::Force:  
                return enet_peer_disconnect_now   (Native(), static_cast<enet_uint32>(reason));
            case DisconnectType::Later:  
                return enet_peer_disconnect_later (Native(), static_cast<enet_uint32>(reason));
        }
        
        // fallback
        enet_peer_disconnect(peer, static_cast<enet_uint32>(reason));
    }
    
    ReceiveOperationResult TryReceive(ChannelId channelId)
    {
        if (!IsValid())
        {
            return DreamNetError::MakeUnexpected(
                DreamNetErrorCode::InvalidPeer,
                "Cannot receive packet from invalid DreamNetPeer");
        }
        
        auto nativePacket = enet_peer_receive(Native(), std::addressof(channelId));
        if (!nativePacket) return std::nullopt;
        
        auto packet = 
            DreamNetPacket::TryAdoptNative(nativePacket);
        
        if (!packet)
        {
            return DreamNetError::WrapUnexpected(
                DreamNetErrorCode::FailedReceivePacket,
                std::move(packet.error()),
                "enet_peer_receive returned a packet that DreamNetPacket could not adopt");
        }
        
        return ReceiveResult 
            { .packet    = std::move(packet.value()),
              .channelId = channelId };
    }
    
    inline ENetPeer* Native() const noexcept
    {
        return peer;
    }
    
    inline bool IsValid() const noexcept
    {
        return peer ? true : false;
    }
    
    bool IsConnected() const noexcept
    {
        return State() == ENET_PEER_STATE_CONNECTED;
    }

    bool IsConnecting() const noexcept
    {
        switch (State())
        {
        case ENET_PEER_STATE_CONNECTING:
        case ENET_PEER_STATE_ACKNOWLEDGING_CONNECT:
        case ENET_PEER_STATE_CONNECTION_PENDING:
        case ENET_PEER_STATE_CONNECTION_SUCCEEDED:
            return true;

        default:
            return false;
        }
    }

    bool IsDisconnected() const noexcept
    {
        switch (State())
        {
        case ENET_PEER_STATE_DISCONNECTED:
        case ENET_PEER_STATE_ZOMBIE:
            return true;

        default:
            return false;
        }
    }

    bool IsDisconnecting() const noexcept
    {
        switch (State())
        {
        case ENET_PEER_STATE_DISCONNECT_LATER:
        case ENET_PEER_STATE_DISCONNECTING:
        case ENET_PEER_STATE_ACKNOWLEDGING_DISCONNECT:
            return true;

        default:
            return false;
        }
    }
    
    bool IsAlive() const noexcept
    {
        switch (State())
        {
        case ENET_PEER_STATE_CONNECTING:
        case ENET_PEER_STATE_ACKNOWLEDGING_CONNECT:
        case ENET_PEER_STATE_CONNECTION_PENDING:
        case ENET_PEER_STATE_CONNECTION_SUCCEEDED:
        case ENET_PEER_STATE_CONNECTED:
        case ENET_PEER_STATE_DISCONNECT_LATER:
        case ENET_PEER_STATE_DISCONNECTING:
        case ENET_PEER_STATE_ACKNOWLEDGING_DISCONNECT:
            return true;

        case ENET_PEER_STATE_DISCONNECTED:
        case ENET_PEER_STATE_ZOMBIE:
        default:
            return false;
        }
    }
    
    void Reset()
    {
        if (!IsValid()) return;
        enet_peer_reset(Native());
    }
    
    void Ping()
    {
        if (!IsValid()) return;
        enet_peer_ping(Native());
    }
    
    std::optional<PeerTelemetry> GetPeerTelemetry() const noexcept
    {
        if (!IsValid())
        {
            return std::nullopt;
        }

        return PeerTelemetry
        {
            .transportInfo = PeerTelemetry::TransportInfo
            {
                .mtu                   = peer->mtu,
                .windowSize            = peer->windowSize,
                .incomingDataTotal     = peer->incomingDataTotal,
                .outgoingDataTotal     = peer->outgoingDataTotal,
                .reliableDataInTransit = peer->reliableDataInTransit,
                .lastReceiveTime       = peer->lastReceiveTime,
                .lastSendTime          = peer->lastSendTime,
                .state                 = peer->state,
            },

            .packetStats = PeerTelemetry::PacketStats
            {
                .packetLoss         = peer->packetLoss,
                .packetLossVariance = peer->packetLossVariance,
                .packetsLost        = peer->packetsLost,
                .packetsSent        = peer->packetsSent,
            },

            .packetThrottleStats = PeerTelemetry::PacketThrottleStats
            {
                .packetThrottle             = peer->packetThrottle,
                .packetThrottleLimit        = peer->packetThrottleLimit,
                .packetThrottleAcceleration = peer->packetThrottleAcceleration,
                .packetThrottleDeceleration = peer->packetThrottleDeceleration,
                .packetThrottleInterval     = peer->packetThrottleInterval,
            },

            .roundTripTimeInfo = PeerTelemetry::RoundTripTimeInfo
            {
                .lastRoundTripTime     = peer->lastRoundTripTime,
                .lowestRoundTripTime   = peer->lowestRoundTripTime,
                .roundTripTime         = peer->roundTripTime,
                .roundTripTimeVariance = peer->roundTripTimeVariance,
            },
        };
    }
    
    std::optional<PeerInfo> GetPeerInfo() const noexcept
    {
        if (!IsValid())
        {
            return std::nullopt;
        }
        
        return PeerInfo
        {
            .state             = State(),
            .address           = DreamNetAddress::FromNative(peer->address),
            .channelCount      = peer->channelCount,
            .incomingBandwidth = peer->incomingBandwidth,
            .outgoingBandwidth = peer->outgoingBandwidth,
        };
    }
    
    ENetPeerState State() const noexcept
    {
        return IsValid() ? peer->state : ENET_PEER_STATE_DISCONNECTED;
    }
    
    void* RawUserData() const noexcept
    {
        return IsValid() ? peer->data : nullptr;
    }

    void SetRawUserData(void* value) noexcept
    {
        if (!IsValid())
        {
            return;
        }

        peer->data = value;
    }

    void ClearRawUserData() noexcept
    {
        if (!IsValid())
        {
            return;
        }

        peer->data = nullptr;
    }

    template <typename T>
    requires std::is_object_v<T>
    T* UserDataAs() const noexcept
    {
        return static_cast<T*>(RawUserData());
    }

    template <typename T>
    requires std::is_object_v<T>
    void SetUserData(T* value) noexcept
    {
        SetRawUserData(static_cast<void*>(value));
    }
private:
    NetOperationResult PushPacketImpl(DreamNetPacket&& packet, const ChannelId channelId)
    {
        if (!packet.IsValid())
        {
            return DreamNetError::MakeUnexpected(
                DreamNetErrorCode::InvalidPacket,
                "Cannot send invalid DreamNetPacket");
        }

        if (!IsValid())
        {
            return DreamNetError::MakeUnexpected(
                DreamNetErrorCode::InvalidPeer,
                "Cannot send packet through invalid DreamNetPeer");
        }
        
        if (enet_peer_send(Native(), channelId, packet.Native()) < 0)
        {
            return DreamNetError::MakeUnexpected(
                DreamNetErrorCode::FailedPushPacket,
                "enet_peer_send returned a negative result");
        }
        
        auto _ = packet.ReleaseNative();
        return {};
    }
    
    [[nodiscard]] bool CanSend() const noexcept
    {
        return IsValid() && peer->state == ENET_PEER_STATE_CONNECTED;
    }
    
    explicit DreamNetPeer(ENetPeer* peer) : peer(peer) {}
    
    ENetPeer* peer;
};

export using DreamNetPeerPtr = std::unique_ptr<DreamNetPeer>;
