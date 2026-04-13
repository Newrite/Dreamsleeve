module;

#include <enet/enet.h>

export module DreamNet.Address;

import std;

export using IpStr        = std::string;
export using IpStrView    = std::string_view;
export using HostName     = std::string;
export using HostNameView = std::string_view;
export using Port         = std::uint16_t;

export class DreamNetAddress
{
public:
    
    static constexpr IpStrView   LoopbackIp        = "127.0.0.1"; 
    static constexpr std::size_t BufferSize = 256;
    
    enum class Error : std::uint8_t
    {
        InvalidIp,
        ResolveFailed,
        FormatFailed
    };
    
    static std::expected<DreamNetAddress, Error> TryParseIp(IpStrView hostIp, Port port)
    {
        IpStr owned{hostIp};
        ENetAddress address{};
        address.port = port;
        
        if (owned.empty() || enet_address_set_host_ip(&address, owned.c_str()) != 0)
        {
            return std::unexpected(Error::InvalidIp);
        }
        
        return DreamNetAddress{address};
    }
    static std::expected<DreamNetAddress, Error> TryResolveHost(HostNameView hostName, Port port)
    {
        HostName owned{hostName};
        ENetAddress address{};
        address.port = port;
        
        if (owned.empty() || enet_address_set_host(&address, owned.c_str()) != 0)
        {
            return std::unexpected(Error::ResolveFailed);
        }
        
        return DreamNetAddress{address};
    }

    static DreamNetAddress Loopback(Port port)
    {
        return TryParseIp(LoopbackIp, port).value();
    }
    
    static DreamNetAddress Any(Port port) noexcept
    {
        ENetAddress address;
        address.port = port;
        address.host = ENET_HOST_ANY;
        return DreamNetAddress{address};
    }
    
    static DreamNetAddress Broadcast(Port port) noexcept
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

    std::expected<IpStr, Error> ToIpString() const
    {
        std::array<char, BufferSize> buffer{};
        
        if (enet_address_get_host_ip(&address, buffer.data(), buffer.size()) != 0)
        {
            return std::unexpected(Error::FormatFailed);
        }
        
        return IpStr{buffer.data()};
    }
    
    std::expected<HostName, Error> ToHostString() const
    {
        std::array<char, BufferSize> buffer{};
        
        if (enet_address_get_host(&address, buffer.data(), buffer.size()) != 0)
        {
            return std::unexpected(Error::FormatFailed);
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