module;

#include <enet/enet.h>

export module DreamNet.Host;

import std;

export struct EnetHostDeleter
{
    void operator()(ENetHost* host) const noexcept
    {
        if (host != nullptr)
        {
            enet_host_destroy(host);
        }
    }
};

export using EnetHostPtr = std::unique_ptr<ENetHost, EnetHostDeleter>;

export class DreamNetHost
{
public:
    enum class Error : std::uint8_t
    {
        EnetHostCreationError,
    };
    
    static std::expected<DreamNetHost, Error> TryCreate()
    {
        
        EnetHostPtr enetHost = EnetHostPtr(enet_host_create(nullptr,1,2,0,0));
        
        if (!enetHost)
        {
            return std::unexpected(Error::EnetHostCreationError);
        }
        
        return DreamNetHost(std::move(enetHost));
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
    explicit DreamNetHost(EnetHostPtr enetHost) : enetHost(std::move(enetHost)) {}
    
    EnetHostPtr enetHost = nullptr;
};

export using DreamNetHostPtr = std::unique_ptr<DreamNetHost>;