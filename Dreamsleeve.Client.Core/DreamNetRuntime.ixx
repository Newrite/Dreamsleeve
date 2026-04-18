module;

#include <enet/enet.h>

export module DreamNet.Runtime;

import std;
import DreamNet.Core;

export class DreamNetRuntime
{
public:
    using Result = NetResult<DreamNetRuntime>;

    static Result TryInitialize()
    {
        if (enet_initialize() != 0)
        {
            return DreamNetError::MakeUnexpected(
                DreamNetErrorCode::FailedENetInitialize,
                "enet_initialize returned a non-zero result");
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
