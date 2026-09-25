namespace Dreamsleeve.Server.Domain


    
type PlayerId = uint64

type PlayerName = string

type Player = {
    PlayerId: PlayerId
    PlayerName: PlayerName
}

type ChatMessageId = uint64

type ChatMessageText = string

type ChatMessage = {
    MessageId: ChatMessageId
    MessageText: ChatMessageText
}
    
type Chat = {
    Players: seq<Player>
    Messages: seq<ChatMessage>
}

type ChatChannelId = uint64

type ChatChannelName = string

type ChatChannel = {
    ChannelId: ChatChannelId
    ChannelName: ChatChannelName
    ChannelChat: Chat
}