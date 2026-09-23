module;

#include <enet/enet.h>
#include <spdlog/spdlog.h>

export module DreamNet.Client;

import std;

import DreamNet.Address;
import DreamNet.Core;
import DreamNet.Event;
import DreamNet.Host;
import DreamNet.Packet;
import DreamNet.Peer;
import DreamNet.Runtime;

void EventReceiveHandle(DreamNetEvent event)
{
    if (!event.HasPacket())
    {
        spdlog::error("Receive Event without packet");
        return;
    }
    
    auto dreamNetPacket = event.AcquirePacket();
    auto data = dreamNetPacket->Data();
}

export void ClinetLoop(DreamNetHostPtr host)
{
    constexpr TimeOutMs timeoutms = TimeOutMs(20);
    while (auto event = host->Service(timeoutms))
    {
        if (!event)
        {
            spdlog::warn("Client Host Service Error: {}", event.error().ToLogString());
            continue;
        }
        
        auto dreamNetEvent = std::move(event.value());
        if (!dreamNetEvent) continue;
        
        switch (dreamNetEvent->Type()) {
        case EventType::None:
            break;
        case EventType::Connect:
            break;
        case EventType::Disconnect:
            break;
        case EventType::Receive:
            EventReceiveHandle(std::move(*dreamNetEvent));
            continue;
        }

        spdlog::error("Unknown event type: {}", static_cast<int>(dreamNetEvent->Type()));
    
    }
}