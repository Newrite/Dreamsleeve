namespace Dreamsleeve.Server.Domain

open System

/// Stable record identity and a display label; never a runtime pointer or load-order ID.
type NamedForm = private { form: FormKey; name: string } with
    member this.Form = this.form
    member this.Name = this.name

[<RequireQualifiedAccess>]
module NamedForm =
    let create maxLength form name =
        PrimitiveValidation.label "NamedForm.Name" maxLength name
        |> Result.map (fun label -> { form = form; name = label })

[<RequireQualifiedAccess>]
type ActivityKind =
    | Unknown | Exploring | Combat | Talking | Bartering | Training | Reading
    | Lockpicking | Crafting | UsingObject | Riding | Sneaking | Swimming | Flying
    | Dead | Ragdoll | Menu | NewGame | Loading

[<RequireQualifiedAccess>]
type LockDifficulty =
    | Unknown | Unlocked | VeryEasy | Easy | Average | Hard | VeryHard | RequiresKey

/// The optional target is a display label, not a remotely addressable game object.
type PlayerActivity = private {
    kind: ActivityKind
    targetName: string voption
    lockDifficulty: LockDifficulty
    menuKey: string voption
} with
    member this.Kind = this.kind
    member this.TargetName = this.targetName
    member this.LockDifficulty = this.lockDifficulty
    member this.MenuKey = this.menuKey

[<RequireQualifiedAccess>]
module PlayerActivity =
    let unknown = { kind = ActivityKind.Unknown; targetName = ValueNone
                    lockDifficulty = LockDifficulty.Unknown; menuKey = ValueNone }

    let private acceptsTarget = function
        | ActivityKind.Combat | ActivityKind.Talking | ActivityKind.Bartering
        | ActivityKind.Training | ActivityKind.Reading | ActivityKind.Lockpicking
        | ActivityKind.Crafting | ActivityKind.UsingObject | ActivityKind.Riding -> true
        | ActivityKind.Unknown | ActivityKind.Exploring | ActivityKind.Sneaking
        | ActivityKind.Swimming | ActivityKind.Flying | ActivityKind.Dead
        | ActivityKind.Ragdoll | ActivityKind.Menu | ActivityKind.NewGame | ActivityKind.Loading -> false

    let private optionalText validate = function
        | ValueNone -> Ok ValueNone
        | ValueSome value -> validate value |> Result.map ValueSome

    let private keyFormat value =
        if value |> Seq.forall (fun c -> PrimitiveValidation.asciiLetterOrDigit c || c = '_' || c = '-' || c = ':' || c = '.') then ValueNone
        else ValueSome TextError.InvalidCharacters

    let create textLimit keyLimit kind (targetName: string voption) difficulty (menuKey: string voption) =
        let invalid field = Error (DomainError.InvalidPlayerDetails field)
        if targetName.IsSome && not (acceptsTarget kind) then invalid "activity.target_name"
        elif kind <> ActivityKind.Lockpicking && difficulty <> LockDifficulty.Unknown then invalid "activity.lock_difficulty"
        elif (kind = ActivityKind.Menu) <> menuKey.IsSome then invalid "activity.menu_key"
        else
            let target = optionalText (PrimitiveValidation.text "Activity.TargetName" textLimit id false PrimitiveValidation.unrestricted) targetName
            let menu = optionalText (PrimitiveValidation.text "Activity.MenuKey" keyLimit PrimitiveValidation.asciiLower false keyFormat) menuKey
            match target, menu with
            | Ok target, Ok menu ->
                Ok { kind = kind; targetName = target; lockDifficulty = difficulty; menuKey = menu }
            | Error error, _ | _, Error error -> Error error

/// Descriptive place data is independent from the WRLD/CELL coordinate space.
/// Empty labels mean unknown; they are not identifiers and are never used for distance.
type PlaceDescription = private {
    worldspaceName: string
    locationName: string
    nearbyMarkerName: string
    markerKind: string
    isInterior: bool
} with
    member this.WorldspaceName = this.worldspaceName
    member this.LocationName = this.locationName
    member this.NearbyMarkerName = this.nearbyMarkerName
    member this.MarkerKind = this.markerKind
    member this.IsInterior = this.isInterior

[<RequireQualifiedAccess>]
module PlaceDescription =
    let create textLimit keyLimit worldspaceName locationName nearbyMarkerName markerKind isInterior =
        let label field = PrimitiveValidation.label field textLimit
        let marker =
            PrimitiveValidation.label "Place.MarkerKind" keyLimit markerKind
            |> Result.bind (fun value ->
                if value |> Seq.forall (fun c -> PrimitiveValidation.asciiLetterOrDigit c || c = '_' || c = '-' || c = ':' || c = '.') then
                    Ok (PrimitiveValidation.asciiLower value)
                else Error (DomainError.InvalidText("Place.MarkerKind", TextError.InvalidCharacters)))
        match label "Place.WorldspaceName" worldspaceName, label "Place.LocationName" locationName,
              label "Place.NearbyMarkerName" nearbyMarkerName, marker with
        | Ok world, Ok location, Ok nearby, Ok kind ->
            Ok { worldspaceName = world; locationName = location; nearbyMarkerName = nearby
                 markerKind = kind; isInterior = isInterior }
        | Error error, _, _, _ | _, Error error, _, _ | _, _, Error error, _ | _, _, _, Error error -> Error error

/// Slowly changing observations. The time is the client's reported game-process
/// start, not the ENet connection time or a server-authoritative elapsed duration.
type PlayerDetails = private {
    race: NamedForm voption
    level: uint32 voption
    activity: PlayerActivity
    place: PlaceDescription voption
    gameStartedAt: DateTimeOffset voption
} with
    member this.Race = this.race
    member this.Level = this.level
    member this.Activity = this.activity
    member this.Place = this.place
    member this.GameStartedAt = this.gameStartedAt

[<RequireQualifiedAccess>]
module PlayerDetails =
    let empty = { race = ValueNone; level = ValueNone; activity = PlayerActivity.unknown
                  place = ValueNone; gameStartedAt = ValueNone }

    let create race level activity place gameStartedAt =
        { race = race; level = level; activity = activity; place = place; gameStartedAt = gameStartedAt }
