namespace Dreamsleeve.Server.Domain

/// An immutable profile. Username availability belongs to the owning registry,
/// not to this constructor; DisplayName is deliberately non-unique.
type PlayerData =
    private {
        playerId: PlayerId
        username: Username
        displayName: DisplayName
    }
    member this.PlayerId = this.playerId
    member this.Username = this.username
    member this.DisplayName = this.displayName

[<RequireQualifiedAccess>]
module PlayerData =
    /// Combine values already accepted by their primitive factories.
    let create playerId username displayName : PlayerData =
        { playerId = playerId; username = username; displayName = displayName }

    let withDisplayName displayName (profile: PlayerData) =
        { profile with displayName = displayName }

    /// The registry must reserve the new username before applying this update.
    let withUsername username (profile: PlayerData) =
        { profile with username = username }

/// A detached, immutable projection for other agents and outbound messages.
type PlayerSnapshot = {
    Data: PlayerData
    CharacterGeneration: uint64
    CharacterName: CharacterName voption
    Details: PlayerDetails
    Location: PlayerLocation voption
    ActorValues: Map<ActorValueKey, ActorValueInfo>
}

/// Independent observations from the current game instance. Membership, identity
/// and admission limits belong to the session owner.
[<RequireQualifiedAccess>]
type PlayerUpdate =
    | BeginCharacter of CharacterName
    | RenameCharacter of CharacterName
    | Move of PlayerLocation voption
    | SetActorValues of Map<ActorValueKey, ActorValueInfo>
    | SetDetails of PlayerDetails
    | LeaveGame

/// Owned by one agent. Profile/location updates return a replacement record,
/// while its private ActorValueStorage remains owned by that same agent.
[<NoEquality; NoComparison>]
type Player =
    private {
        data: PlayerData
        characterGeneration: uint64
        characterName: CharacterName voption
        details: PlayerDetails
        location: PlayerLocation voption
        actorValues: ActorValueStorage
    }
    member this.Data = this.data
    member this.CharacterGeneration = this.characterGeneration
    member this.CharacterName = this.characterName
    member this.Details = this.details
    member this.Location = this.location

[<RequireQualifiedAccess>]
module Player =
    let create data : Player =
        { data = data
          characterGeneration = 0UL
          characterName = ValueNone
          details = PlayerDetails.empty
          location = ValueNone
          actorValues = ActorValueStorage.create () }

    /// Changing a profile never changes the identity of an existing player.
    let withProfile (profile: PlayerData) (player: Player) =
        if profile.PlayerId <> player.Data.PlayerId then
            Error DomainError.PlayerIdentityMismatch
        else
            Ok { player with data = profile }

    let withDisplayName displayName (player: Player) =
        { player with data = PlayerData.withDisplayName displayName player.data }

    let withLocation location (player: Player) =
        { player with location = ValueSome location }

    /// Unknown position is not the same as disconnecting from the server.
    let clearLocation (player: Player) =
        { player with location = ValueNone }

    /// Rename the current character without interpreting its name as identity.
    let withCharacterName characterName (player: Player) =
        { player with characterName = ValueSome characterName }

    /// Clear telemetry when leaving the game or switching saves. Server profile
    /// and chat identity survive; the old character's storage is not reused.
    let clearGameState (player: Player) =
        { player with
            characterGeneration = player.characterGeneration + 1UL
            characterName = ValueNone
            details = PlayerDetails.empty
            location = ValueNone
            actorValues = ActorValueStorage.create () }

    /// Explicitly start a new character, even when its name matches the old one.
    let beginCharacter characterName player =
        clearGameState player |> withCharacterName characterName

    /// Build a replacement storage before publishing the replacement player. No
    /// observer can see a partially replaced set of readings.
    let replaceActorValues (entries: Map<ActorValueKey, ActorValueInfo>) (player: Player) =
        let values = ActorValueStorage.create ()

        for KeyValue(key, info) in entries do
            ActorValueStorage.set key info values

        { player with actorValues = values }

    let applyUpdate update player =
        match update with
        | PlayerUpdate.BeginCharacter name -> beginCharacter name player
        | PlayerUpdate.RenameCharacter name -> withCharacterName name player
        | PlayerUpdate.Move location -> { player with location = location }
        | PlayerUpdate.SetActorValues values -> replaceActorValues values player
        | PlayerUpdate.SetDetails details -> { player with details = details }
        | PlayerUpdate.LeaveGame -> clearGameState player

    let setActorValue key info (player: Player) =
        ActorValueStorage.set key info player.actorValues

    let setActorValues updates (player: Player) =
        ActorValueStorage.setMany updates player.actorValues

    let tryFindActorValue key (player: Player) =
        ActorValueStorage.tryFind key player.actorValues

    let removeActorValue key (player: Player) =
        ActorValueStorage.remove key player.actorValues

    let clearActorValues (player: Player) =
        ActorValueStorage.clear player.actorValues

    let actorValueCount (player: Player) =
        ActorValueStorage.count player.actorValues

    let actorValuesSnapshot (player: Player) =
        ActorValueStorage.snapshot player.actorValues

    /// Evaluate inside the owning agent. The resulting map shares no live
    /// dictionary and its values are immutable.
    let snapshot (player: Player) : PlayerSnapshot =
        { Data = player.data
          CharacterGeneration = player.characterGeneration
          CharacterName = player.characterName
          Details = player.details
          Location = player.location
          ActorValues = actorValuesSnapshot player }
