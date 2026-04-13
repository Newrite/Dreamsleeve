module;

#include <enet/enet.h>

export module DreamNet.Runtime;

import std;

export class DreamNetRuntime
{
public:
    enum class Error
    {
        EnetInitializeError
    };

    static std::expected<DreamNetRuntime, Error> TryInitialize()
    {
        if (enet_initialize() != 0)
        {
            return std::unexpected(Error::EnetInitializeError);
        }

        return DreamNetRuntime(true);
    }

    DreamNetRuntime(const DreamNetRuntime&) = delete;
    DreamNetRuntime& operator=(const DreamNetRuntime&) = delete;

    DreamNetRuntime(DreamNetRuntime&& other) noexcept
        : ownsRuntime(other.ownsRuntime)
    {
        other.ownsRuntime = false;
    }

    DreamNetRuntime& operator=(DreamNetRuntime&& other) noexcept
    {
        if (this != &other)
        {
            if (ownsRuntime)
            {
                enet_deinitialize();
            }

            ownsRuntime = other.ownsRuntime;
            other.ownsRuntime = false;
        }

        return *this;
    }

    ~DreamNetRuntime()
    {
        Deinitialize();
    }

private:
    void Deinitialize()
    {
        if (ownsRuntime)
        {
            enet_deinitialize();
        }
    }
    explicit DreamNetRuntime(bool ownsRuntime) noexcept
        : ownsRuntime(ownsRuntime) {}

    bool ownsRuntime = false;
};