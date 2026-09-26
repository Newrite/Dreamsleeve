namespace Dreamsleeve.Server.Domain

open System

/// A plugin-relative key. PluginName.create supplies the canonical filename;
/// the client supplies the local ID of the plugin that originally defines the form.
type FormKey = private {
    pluginName: PluginName
    localFormId: LocalFormId
} with
    member this.PluginName = this.pluginName
    member this.LocalFormId = this.localFormId

[<RequireQualifiedAccess>]
module FormKey =
    let create pluginName localFormId : FormKey =
        { pluginName = pluginName; localFormId = localFormId }

/// Identifies the coordinate space: WRLD for exteriors, CELL for interiors.
/// BGSLocation is not the identity of a coordinate space.
type LocationId = FormKey

type Location = private {
    locationId: LocationId
    locationName: LocationName
} with
    member this.LocationId = this.locationId
    member this.LocationName = this.locationName

[<RequireQualifiedAccess>]
module Location =
    /// The name is a player's display label; it does not participate in space identity.
    let create locationId locationName : Location =
        { locationId = locationId; locationName = locationName }

[<Struct>]
type Position = private {
    x: WorldUnit
    y: WorldUnit
    z: WorldUnit
} with
    member this.X = this.x
    member this.Y = this.y
    member this.Z = this.z

[<RequireQualifiedAccess>]
module Position =
    /// Preserve native NiPoint3 coordinates. Reject only non-finite components.
    let create (x: float32) (y: float32) (z: float32) =
        if not (Single.IsFinite x) then
            Error (DomainError.NonFiniteNumber "Position.X")
        elif not (Single.IsFinite y) then
            Error (DomainError.NonFiniteNumber "Position.Y")
        elif not (Single.IsFinite z) then
            Error (DomainError.NonFiniteNumber "Position.Z")
        else
            Ok ({ x = LanguagePrimitives.Float32WithMeasure<worldUnit> x
                  y = LanguagePrimitives.Float32WithMeasure<worldUnit> y
                  z = LanguagePrimitives.Float32WithMeasure<worldUnit> z }: Position)

    let zero : Position = { x = 0.0f<worldUnit>; y = 0.0f<worldUnit>; z = 0.0f<worldUnit> }

    /// Convert before subtraction so every finite float32 input remains safe in float64.
    /// Callers must establish a common coordinate space before comparing positions.
    let distanceSquared (left: Position) (right: Position) : float<worldUnit^2> =
        let dx = float left.X - float right.X
        let dy = float left.Y - float right.Y
        let dz = float left.Z - float right.Z

        LanguagePrimitives.FloatWithMeasure<worldUnit^2> (dx * dx + dy * dy + dz * dz)

    let distance left right : float<worldUnit> =
        sqrt (distanceSquared left right)

[<Struct>]
type Rotation = private {
    x: Radian
    y: Radian
    z: Radian
} with
    member this.X = this.x
    member this.Y = this.y
    member this.Z = this.z

[<RequireQualifiedAccess>]
module Rotation =
    /// Preserve native Euler angles in radians without wrapping or clamping.
    let create (x: float32) (y: float32) (z: float32) =
        if not (Single.IsFinite x) then
            Error (DomainError.NonFiniteNumber "Rotation.X")
        elif not (Single.IsFinite y) then
            Error (DomainError.NonFiniteNumber "Rotation.Y")
        elif not (Single.IsFinite z) then
            Error (DomainError.NonFiniteNumber "Rotation.Z")
        else
            Ok ({ x = LanguagePrimitives.Float32WithMeasure<radian> x
                  y = LanguagePrimitives.Float32WithMeasure<radian> y
                  z = LanguagePrimitives.Float32WithMeasure<radian> z }: Rotation)

    let zero : Rotation = { x = 0.0f<radian>; y = 0.0f<radian>; z = 0.0f<radian> }

type PlayerLocation = private {
    location: Location
    position: Position
    rotation: Rotation
} with
    member this.Location = this.location
    member this.Position = this.position
    member this.Rotation = this.rotation

[<RequireQualifiedAccess>]
module PlayerLocation =
    let create location position rotation : PlayerLocation =
        { location = location; position = position; rotation = rotation }

    let isSameSpace (left: PlayerLocation) (right: PlayerLocation) =
        left.Location.LocationId = right.Location.LocationId

    let tryDistanceSquared left right =
        if isSameSpace left right then ValueSome (Position.distanceSquared left.Position right.Position)
        else ValueNone

    let tryDistance left right =
        tryDistanceSquared left right |> ValueOption.map sqrt

    /// The radius is in native world units. Different spaces are always outside the radius.
    let isWithinRadius (radius: WorldUnit) left right =
        if not (Single.IsFinite (float32 radius)) || radius < 0.0f<worldUnit> then
            Error DomainError.InvalidRadius
        else
            let radius64 = LanguagePrimitives.FloatWithMeasure<worldUnit> (float radius)

            match tryDistanceSquared left right with
            | ValueSome squared -> Ok (squared <= radius64 * radius64)
            | ValueNone -> Ok false
