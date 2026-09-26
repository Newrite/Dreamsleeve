namespace Dreamsleeve.Server.Domain

open System
open System.Collections.Generic

/// Scalar values and resources with a maximum are distinct shapes.
/// These are client observations, not server-authoritative gameplay constraints.
[<Struct; RequireQualifiedAccess>]
type ActorValueState =
    private
    | Scalar of value: ActorValue
    | Resource of current: ActorValue * maximum: ActorValue

[<RequireQualifiedAccess>]
module ActorValueState =
    let scalar value =
        ActorValue.create value |> Result.map ActorValueState.Scalar

    /// Negative readings or a current value above the maximum are preserved.
    let resource (current: float32) (maximum: float32) =
        if not (Single.IsFinite current) then Error (DomainError.NonFiniteNumber "ActorValue.current")
        elif not (Single.IsFinite maximum) then Error (DomainError.NonFiniteNumber "ActorValue.maximum")
        else
            Ok (ActorValueState.Resource (
                LanguagePrimitives.Float32WithMeasure<actorValue> current,
                LanguagePrimitives.Float32WithMeasure<actorValue> maximum))

    let current = function
        | ActorValueState.Scalar value -> value
        | ActorValueState.Resource (value, _) -> value

    let tryMaximum = function
        | ActorValueState.Scalar _ -> ValueNone
        | ActorValueState.Resource (_, maximum) -> ValueSome maximum

    /// Consume either case without exposing constructors that bypass validation.
    let fold onScalar onResource = function
        | ActorValueState.Scalar value -> onScalar value
        | ActorValueState.Resource (current, maximum) -> onResource current maximum

    let withCurrent value state =
        ActorValue.create value
        |> Result.map (fun current ->
            match state with
            | ActorValueState.Scalar _ -> ActorValueState.Scalar current
            | ActorValueState.Resource (_, maximum) -> ActorValueState.Resource (current, maximum))

type ActorValueInfo = private {
    displayName: ActorValueName
    state: ActorValueState
} with
    member this.DisplayName = this.displayName
    member this.State = this.state

[<RequireQualifiedAccess>]
module ActorValueInfo =
    let create displayName state : ActorValueInfo =
        { displayName = displayName; state = state }

    let withState state (info: ActorValueInfo) = { info with state = state }

/// Mutable state owned by one agent. Never share this storage between agents.
/// Use snapshot or immutable individual readings to publish data.
[<NoEquality; NoComparison>]
type ActorValueStorage = private {
    values: Dictionary<ActorValueKey, ActorValueInfo>
}

[<RequireQualifiedAccess>]
module ActorValueStorage =
    let create () : ActorValueStorage = { values = Dictionary<ActorValueKey, ActorValueInfo>() }

    let count (storage: ActorValueStorage) = storage.values.Count

    let set key info (storage: ActorValueStorage) = storage.values[key] <- info

    /// Apply an already materialized, validated batch. The sender must finish
    /// building the array before handing it to the owning agent and must not
    /// mutate it afterwards. Repeated keys use the last supplied value.
    let setMany (entries: (ActorValueKey * ActorValueInfo) array) (storage: ActorValueStorage) =
        for key, info in entries do
            storage.values[key] <- info

    let tryFind key (storage: ActorValueStorage) =
        match storage.values.TryGetValue key with
        | true, info -> ValueSome info
        | false, _ -> ValueNone

    let remove key (storage: ActorValueStorage) = storage.values.Remove key

    let clear (storage: ActorValueStorage) = storage.values.Clear()

    /// Detached immutable snapshot; subsequent storage updates do not change it.
    let snapshot (storage: ActorValueStorage) : Map<ActorValueKey, ActorValueInfo> =
        storage.values
        |> Seq.map (fun entry -> entry.Key, entry.Value)
        |> Map.ofSeq
