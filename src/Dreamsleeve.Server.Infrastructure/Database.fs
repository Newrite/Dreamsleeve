namespace Dreamsleeve.Server.Infrastructure

module Database =
    
    type Player = {
        PlayerId: uint64
    }
    
    let private player = { PlayerId = 0UL }
    
    let getPlayer() = player