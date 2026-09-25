namespace Dreamsleeve.Server.Domain

open System.Collections.Generic
open FSharp.UMX

[<AutoOpen>]
module private DomainUMX =

    /// Identifies a player independently of a chat message or channel.
    [<Measure>]
    type playerId
    
    /// Identifies a chat message; cannot be used where PlayerId is required.
    [<Measure>]
    type chatMessageId
    
    /// Identifies an application chat channel, not an ENet transport channel.
    [<Measure>]
    type chatChannelId
    
    [<Measure>]
    type username
    
    [<Measure>]
    type displayName
    
    [<Measure>]
    type characterName
    
    [<Measure>]
    type chatMessageText
    
    [<Measure>]
    type chatChannelName
    
    [<Measure>]
    type locationName
    
    [<Measure>]
    type worldUnit
    
    [<Measure>]
    type radian
    
    [<Measure>]
    type actorValue
    
    [<Measure>]
    type actorValueName
    
    [<Measure>]
    type actorValueKey
    
    [<Measure>]
    type pluginName
    
    [<Measure>]
    type localFormId

type PluginName = string<pluginName>
type LocalFormId = uint32<localFormId>

type FormKey = {
    PluginName: PluginName
    LocalFormId: LocalFormId
}
    
/// Identifies the coordinate space:
/// WRLD FormKey for exteriors, CELL FormKey for interiors.
type LocationId = FormKey

type LocationName = string<locationName>

type Location = {
    LocationId: LocationId
    LocationName: LocationName
}

type WorldUnit = float32<worldUnit>

[<Struct>]
type Position = {
    X: WorldUnit
    Y: WorldUnit
    Z: WorldUnit
}

type Radian = float32<radian>

[<Struct>]
type Rotation = {
    X: Radian
    Y: Radian
    Z: Radian
}

type PlayerLocation = {
    Location: Location
    Position: Position
    Rotation: Rotation
}

type ActorValueKey = string<actorValueKey>

type ActorValue = float32<actorValue>

type ActorValueName = string<actorValueName>

[<Struct; RequireQualifiedAccess>]
type ActorValueState =
    | Scalar of value: ActorValue
    | Resource of current: ActorValue * maximum: ActorValue

type ActorValueInfo = {
    DisplayName: ActorValueName
    State: ActorValueState
}

type ActorValueStorage = Dictionary<ActorValueKey, ActorValueInfo>

type PlayerId = uint64<playerId>

type Username = string<username>

type DisplayName = string<displayName>

type CharacterName = string<characterName>

type PlayerData = {
    PlayerId: PlayerId
    Username: Username
    DisplayName: DisplayName
}

type Player = {
    Data: PlayerData
    CharacterName: CharacterName voption
    Location: PlayerLocation voption
    ActorValues: ActorValueStorage
}

type ChatMessageId = uint64<chatMessageId>

type ChatMessageText = string<chatMessageText>

type ChatChannelId = uint64<chatChannelId>

type ChatMessage = {
    MessageId: ChatMessageId
    ChannelId: ChatChannelId
    Author: PlayerData
    MessageText: ChatMessageText
    SentAt: System.DateTimeOffset
}
    
type Chat = {
    Players: HashSet<Player>
    Messages: Queue<ChatMessage>
}

type ChatChannelName = string<chatChannelName>

type ChatChannel = {
    ChannelId: ChatChannelId
    ChannelName: ChatChannelName
    ChannelChat: Chat
}