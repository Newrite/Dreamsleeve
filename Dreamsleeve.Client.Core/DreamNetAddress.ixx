module;

#include <enet/enet.h>

export module DreamNet.Address;

import std;

import DreamNet.Core;

export class DreamNetAddress
{
public:
    
    using Result = NetResult<DreamNetAddress>;
    
    static constexpr IpStrView   LoopbackIp        = "127.0.0.1"; 
    static constexpr std::size_t BufferSize = 256;
    
    static DreamNetAddress FromNative(const ENetAddress address) noexcept
    {
        return DreamNetAddress(address);
    }
    
    static Result TryParseIp(const IpStrView hostIp, const Port port)
    {
        IpStr owned{hostIp};
        ENetAddress address{};
        address.port = port;
        
        if (owned.empty() || enet_address_set_host_ip(&address, owned.c_str()) != 0)
        {
            return DreamNetError::MakeUnexpected(
                DreamNetErrorCode::InvalidIp, 
                "Invalid string IP when try make DreamNetAddress");
        }
        
        return DreamNetAddress{address};
    }
    static Result TryResolveHost(const HostNameView hostName, const Port port)
    {
        HostName owned{hostName};
        ENetAddress address{};
        address.port = port;
        
        if (owned.empty() || enet_address_set_host(&address, owned.c_str()) != 0)
        {
            return DreamNetError::MakeUnexpected(
                DreamNetErrorCode::FailedResolveHost, 
                "Failed resolve string Host when try make DreamNetAddress");
        }
        
        return DreamNetAddress{address};
    }

    static DreamNetAddress Loopback(const Port port)
    {
        return TryParseIp(LoopbackIp, port).value();
    }
    
    static DreamNetAddress Any(const Port port) noexcept
    {
        ENetAddress address;
        address.port = port;
        address.host = ENET_HOST_ANY;
        return DreamNetAddress{address};
    }
    
    static DreamNetAddress Broadcast(const Port port) noexcept
    {
        ENetAddress address;
        address.port = port;
        address.host = ENET_HOST_BROADCAST;
        return DreamNetAddress{address};
    }

    std::uint32_t HostRaw() const noexcept
    {
        return address.host;
    }
    
    Port GetPort() const noexcept
    {
        return address.port;
    }

    NetResult<IpStr> ToIpString() const
    {
        std::array<char, BufferSize> buffer{};
        
        if (enet_address_get_host_ip(&address, buffer.data(), buffer.size()) != 0)
        {
            return DreamNetError::MakeUnexpected(
                DreamNetErrorCode::FailedFormatAddress, 
                "Failed to ip string with enet_address_get_host_ip");
        }
        
        return IpStr{buffer.data()};
    }
    
    NetResult<HostName> ToHostString() const
    {
        std::array<char, BufferSize> buffer{};
        
        if (enet_address_get_host(&address, buffer.data(), buffer.size()) != 0)
        {
            return DreamNetError::MakeUnexpected(
                DreamNetErrorCode::FailedFormatAddress, 
                "Failed to host string with enet_address_get_host");
        }
        
        return HostName{buffer.data()};
    }

    const ENetAddress& Native() const noexcept
    {
        return address;
    }
    
private:
    explicit DreamNetAddress(ENetAddress address) noexcept : address(address) {}

    ENetAddress address{};
};

export using DreamNetAddressPtr = std::unique_ptr<DreamNetAddress>;