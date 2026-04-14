module;

#include <enet/enet.h>

export module DreamNet.Peer;

import std;
import Dreamsleeve.Protocol;

export struct ENetPeerPtrDeleter
{
    void operator()(ENetPeer*) const noexcept {}
};

export using ENetPeerPtr = std::unique_ptr<ENetPeer, ENetPeerPtrDeleter>;

export enum class DisconnectType : std::uint8_t
{
    Normal,
    Force,
    Later,
};

export class DreamNetPeer
{
public:
    enum class Error  : std::uint8_t {  };
    
    static void TEst(ENetPeer* peer)
    {
        ENetChannel*         channel            = nullptr;
        ENetIncomingCommand* incomingCommand    = nullptr;
        ENetOutgoingCommand* outcomingCommand   = nullptr;
        ENetPacket*          packet             = nullptr;
        const ENetProtocol*  protocolConst      = nullptr;
        const void*          void_              = nullptr;
        enet_uint32          uint32             = 1;
        enet_uint16          uint16             = 1;
        enet_uint8           uint8              = 1;
        enet_uint8*          channelIdUint8Ptr  = nullptr;
        size_t               size               = 0;
        
        enet_peer_disconnect                           (peer, uint32);
        enet_peer_disconnect_later                     (peer, uint32);
        enet_peer_disconnect_now                       (peer, uint32);
        enet_peer_dispatch_incoming_reliable_commands  (peer, channel, incomingCommand);
        enet_peer_dispatch_incoming_unreliable_commands(peer, channel, incomingCommand);
        enet_peer_on_disconnect                        (peer);
        enet_peer_on_connect                           (peer);
        enet_peer_ping                                 (peer);
        enet_peer_ping_interval                        (peer, uint32);
        enet_peer_reset                                (peer);
        enet_peer_reset_queues                         (peer);
        enet_peer_setup_outgoing_command               (peer, outcomingCommand);
        enet_peer_throttle_configure                   (peer, uint32, uint32, uint32);
        enet_peer_timeout                              (peer, uint32, uint32, uint32);
        
        int                  int_         = enet_peer_has_outgoing_commands (peer);
        ENetAcknowledgement* ack          = enet_peer_queue_acknowledgement (peer, protocolConst,       uint16);
        ENetIncomingCommand* inCommand    = enet_peer_queue_incoming_command(peer, protocolConst,       void_,  size,   uint32, uint32);
        ENetOutgoingCommand* outCommand   = enet_peer_queue_outgoing_command(peer, protocolConst,       packet, uint32, uint16);
        ENetPacket*          packetRecive = enet_peer_receive               (peer, channelIdUint8Ptr);
        int                  int_2        = enet_peer_send                  (peer, uint8,               packet);
        int                  int_3        = enet_peer_throttle              (peer, uint32);
        
    }
    
    void Disconnect(
        const DisconnectType                      type   = DisconnectType::Normal,
        const Protocol::Network::DisconnectReason reason = Protocol::Network::DisconnectReason::Unspecified) const
    {
        if (!peer) return;
        switch (type) {
            case DisconnectType::Normal: 
                return enet_peer_disconnect       (peer.get(), static_cast<enet_uint32>(reason));
            case DisconnectType::Force:  
                return enet_peer_disconnect_now   (peer.get(), static_cast<enet_uint32>(reason));
            case DisconnectType::Later:  
                return enet_peer_disconnect_later (peer.get(), static_cast<enet_uint32>(reason));
        }
        
        // fallback
        enet_peer_disconnect(peer.get(), static_cast<enet_uint32>(reason));
    }
    
    void Reset()
    {
    }
private:
    explicit DreamNetPeer(ENetPeerPtr peer) : peer(std::move(peer)) {}
    ENetPeerPtr peer;
};