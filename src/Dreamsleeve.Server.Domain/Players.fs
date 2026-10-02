namespace Dreamsleeve.Server.Domain

/// An immutable profile. Username availability belongs to the owning registry,
/// not to this constructor; DisplayName is deliberately non-unique.
type PlayerData =
    private {
        playerId: PlayerId
        username: Username
        displayName: DisplayName
        nameColor: NameColor
    }
    member this.PlayerId = this.playerId
    member this.Username = this.username
    member this.DisplayName = this.displayName
    /// How the name is drawn in chat; the player chooses it, a new account gets a random one.
    member this.NameColor = this.nameColor

[<RequireQualifiedAccess>]
module PlayerData =
    /// Combine values already accepted by their primitive factories.
    let create playerId username displayName nameColor : PlayerData =
        { playerId = playerId; username = username; displayName = displayName; nameColor = nameColor }

    let withDisplayName displayName (profile: PlayerData) =
        { profile with displayName = displayName }

    let withNameColor nameColor (profile: PlayerData) =
        { profile with nameColor = nameColor }

    /// The registry must reserve the new username before applying this update.
    let withUsername username (profile: PlayerData) =
        { profile with username = username }

/// What other players see instead of the stored profile (DomainSpec §3.1):
/// the profile after moderation placeholders, or a server pseudonym that
/// replaces every real name. PlayerId is public either way and never changes.
[<RequireQualifiedAccess>]
type PublicIdentity =
    | Profile of PlayerData
    /// No username, display name, character name or name color leaves with it.
    | Pseudonymous of PlayerId * Pseudonym

    member this.PlayerId =
        match this with
        | Profile profile -> profile.PlayerId
        | Pseudonymous(playerId, _) -> playerId

[<RequireQualifiedAccess>]
module PublicIdentity =
    let pseudonym (identity: PublicIdentity) =
        match identity with
        | PublicIdentity.Profile _ -> ValueNone
        | PublicIdentity.Pseudonymous(_, name) -> ValueSome name

    /// The pseudonym when there is one, else the profile itself.
    let ofProfile (pseudonym: Pseudonym voption) (profile: PlayerData) =
        match pseudonym with
        | ValueSome name -> PublicIdentity.Pseudonymous(profile.PlayerId, name)
        | ValueNone -> PublicIdentity.Profile profile

/// Where other players see the pseudonym. Presence and chat always go together;
/// ground marks either follow them or keep showing the real profile.
[<RequireQualifiedAccess>]
type HiddenIdentity =
    | Shown
    | Everywhere
    | ExceptGroundMarks

[<RequireQualifiedAccess>]
module HiddenIdentity =
    /// Presence and chat carry the pseudonym.
    let isHidden hiding =
        match hiding with
        | HiddenIdentity.Shown -> false
        | HiddenIdentity.Everywhere | HiddenIdentity.ExceptGroundMarks -> true

    /// New ground marks carry the pseudonym too.
    let coversGroundMarks hiding =
        match hiding with
        | HiddenIdentity.Everywhere -> true
        | HiddenIdentity.Shown | HiddenIdentity.ExceptGroundMarks -> false

/// A detached, immutable projection for other agents and outbound messages.
type PlayerSnapshot = {
    Identity: PublicIdentity
    CharacterGeneration: uint64
    CharacterName: CharacterName voption
    /// The session withheld a character name that failed moderation;
    /// CharacterName is then ValueNone although a character is active.
    CharacterNameWithheld: bool
    Details: PlayerDetails
    Location: PlayerLocation voption
    MovementContext: uint64
    MovementSequence: uint64
    ViewRevision: uint64
    ActorValues: Map<ActorValueKey, ActorValueInfo>
}

/// Independent observations from the current game instance. Membership, identity
/// and admission limits belong to the session owner.
[<RequireQualifiedAccess>]
type PlayerUpdate =
    | BeginCharacter of CharacterName
    | RenameCharacter of CharacterName
    | SetLocation of contextRevision: uint64 * PlayerLocation voption
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
        movementContext: uint64
        movementSequence: uint64
        movementHighWater: uint64
        actorValues: ActorValueStorage
    }
    member this.Data = this.data
    member this.CharacterGeneration = this.characterGeneration
    member this.CharacterName = this.characterName
    member this.Details = this.details
    member this.Location = this.location
    member this.MovementContext = this.movementContext
    member this.MovementSequence = this.movementSequence
    member this.MovementHighWater = this.movementHighWater

[<RequireQualifiedAccess>]
module Player =
    let create data : Player =
        { data = data
          characterGeneration = 0UL
          characterName = ValueNone
          details = PlayerDetails.empty
          location = ValueNone
          movementContext = 0UL
          movementSequence = 0UL
          movementHighWater = 0UL
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
            movementContext = 0UL
            movementSequence = 0UL
            actorValues = ActorValueStorage.create () }

    /// Explicitly start a new character, even when its name matches the old one.
    let beginCharacter characterName player =
        clearGameState player |> withCharacterName characterName

    /// Build a replacement storage before publishing the replacement player. No
    /// observer can see a partially replaced set of readings.
    let replaceActorValues (entries: Map<ActorValueKey, ActorValueInfo>) (player: Player) =
        { player with actorValues = ActorValueStorage.ofSnapshot entries }

    let applyUpdate update player =
        match update with
        | PlayerUpdate.BeginCharacter name -> beginCharacter name player
        | PlayerUpdate.RenameCharacter name -> withCharacterName name player
        | PlayerUpdate.SetLocation(context, location) ->
            { player with
                location = location
                movementContext = if location.IsSome then context else 0UL
                movementSequence = 0UL
                movementHighWater = context }
        | PlayerUpdate.SetActorValues values -> replaceActorValues values player
        | PlayerUpdate.SetDetails details -> { player with details = details }
        | PlayerUpdate.LeaveGame -> clearGameState player

    /// Reordered, duplicated or early realtime samples are normal packet loss,
    /// not failed commands. Reliable transitions alone establish the location.
    let tryApplyMovement (sample: MovementSample) (player: Player) =
        match player.location with
        | ValueSome location when sample.ContextRevision <> 0UL
                                  && sample.ContextRevision = player.movementContext
                                  && sample.Sequence > player.movementSequence ->
            ValueSome { player with
                            location = ValueSome (MovementPose.apply sample.Pose location)
                            movementSequence = sample.Sequence }
        | ValueSome _ | ValueNone -> ValueNone

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
        { Identity = PublicIdentity.Profile player.data
          CharacterGeneration = player.characterGeneration
          CharacterName = player.characterName
          CharacterNameWithheld = false
          Details = player.details
          Location = player.location
          MovementContext = player.movementContext
          MovementSequence = player.movementSequence
          ViewRevision = 0UL
          ActorValues = actorValuesSnapshot player }

[<RequireQualifiedAccess>]
module PlayerSnapshot =
    /// What others see while the player hides their names: the pseudonym
    /// stands for the profile and no character name is published. Game
    /// state, position and the PlayerId stay as they are.
    let withPseudonym pseudonym (snapshot: PlayerSnapshot) =
        { snapshot with
            Identity = PublicIdentity.Pseudonymous(snapshot.Identity.PlayerId, pseudonym)
            CharacterName = ValueNone }
