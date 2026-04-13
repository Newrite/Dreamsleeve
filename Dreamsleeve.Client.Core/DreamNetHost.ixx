module;

#include <enet/enet.h>

export module DreamNet.Host;

import std;

export class DreamNetHost
{
public:
    enum class Error : std::uint8_t
    {
        EnetHostCreationError,
    };
    
    struct EnetHostDeleter
    {
        void operator()(ENetHost* host) const noexcept
        {
            if (host != nullptr)
            {
                enet_host_destroy(host);
            }
        }
    };

    using EnetHostPtr = std::unique_ptr<ENetHost, EnetHostDeleter>;
    
    static std::expected<DreamNetHost, Error> TryCreate()
    {
        
        EnetHostPtr enetHost = EnetHostPtr(enet_host_create(nullptr,1,2,0,0));
        
        if (!enetHost)
        {
            return std::unexpected(Error::EnetHostCreationError);
        }
        
        return DreamNetHost(std::move(enetHost));
    }
    
private:
    explicit DreamNetHost(EnetHostPtr enetHost) : enetHost(std::move(enetHost)) {}
    
    EnetHostPtr enetHost = nullptr;
};

export using DreamNetHostPtr = std::unique_ptr<DreamNetHost>;