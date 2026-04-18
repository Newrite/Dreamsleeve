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

export using TimeOutMs = enet_uint32;

export class DreamNetHost
{
public:
    enum class Error : std::uint8_t
    {
        InvalidConfig,
        InvalidHost,
        FailedCreateServer,
        FailedCreateClient,
        FailedService,
        FailedServiceEvent,
    };
    
    static std::expected<DreamNetHost, Error> TryCreateClient(const ClientConfig config)
    {
        if (!IsValidConfig(config)) return std::unexpected(Error::InvalidConfig);
        
        auto host = enet_host_create(
            nullptr, 
            config.maxPeers, 
            config.channelLimit, 
            config.inBwLimit, 
            config.outBwLimit);
        
        
        if (!host)
        {
            return std::unexpected(Error::FailedCreateClient);
        }
        
        ENetHostPtr enetHost = ENetHostPtr(host);
        
        return DreamNetHost(std::move(enetHost));
    }
    
    static std::expected<DreamNetHost, Error> TryCreateServer(const ServerConfig config)
    {
        if (!IsValidConfig(config)) return std::unexpected(Error::InvalidConfig);
        
        auto& nativeAddress = config.address.Native();
        
        auto host = enet_host_create(
            std::addressof(nativeAddress), 
            config.maxPeers, 
            config.channelLimit, 
            config.inBwLimit, 
            config.outBwLimit);
        
        
        if (!host)
        {
            return std::unexpected(Error::FailedCreateServer);
        }
        
        ENetHostPtr enetHost = ENetHostPtr(host);
        
        return DreamNetHost(std::move(enetHost));
    }
    
    std::expected<std::optional<DreamNetEvent>, Error> Service(TimeOutMs timeoutMs)
    {
        if (!IsValid()) return std::unexpected(Error::InvalidHost);
        
        ENetEvent event;
        auto serviceResult = enet_host_service(Native(), &event, timeoutMs);
        if (serviceResult == 0) return std::nullopt;
        if (serviceResult < 0) return std::unexpected(Error::FailedService);
        
        auto dreamEvent = DreamNetEvent::TryFromNative(event);
        if (!dreamEvent) return std::unexpected(Error::FailedServiceEvent);
        
        return std::move(dreamEvent.value());
    }
    
    ENetHost* Native() const noexcept
    {
        return enetHost.get();
    }
    
    inline bool IsValid() const noexcept
    {
        return enetHost ? true : false;
    }
    
private:
    static bool IsValidConfig(const NetConfig& config) noexcept
    {
        if (config.maxPeers == 0 || config.maxPeers > ENET_PROTOCOL_MAXIMUM_PEER_ID)
        {
            return false;
        }

        if (config.channelLimit != 0 &&
            (config.channelLimit < ENET_PROTOCOL_MINIMUM_CHANNEL_COUNT ||
             config.channelLimit > ENET_PROTOCOL_MAXIMUM_CHANNEL_COUNT))
        {
            return false;
        }

        return true;
    }
    explicit DreamNetHost(ENetHostPtr enetHost) : enetHost(std::move(enetHost)) {}
    
    ENetHostPtr enetHost = nullptr;
};

export using DreamNetHostPtr = std::unique_ptr<DreamNetHost>;