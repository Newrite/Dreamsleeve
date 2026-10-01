namespace Dreamsleeve.Server.Domain

open System
open System.Collections.Generic

/// Scalar values and resources with a maximum are distinct shapes.
/// These are client observations, not server-authoritative gameplay constraints.
[<Struct; RequireQualifiedAccess>]
type ActorValueState =
    private
    | Scalar of value: ActorValue
    | Resource of current: int * maximum: int

[<RequireQualifiedAccess>]
module ActorValueState =
    let scalar value =
        ActorValue.create value |> Result.map ActorValueState.Scalar

    /// Whole points, as clients round them. Negative readings or a current
    /// value above the maximum are preserved.
    let resource (current: int) (maximum: int) = ActorValueState.Resource (current, maximum)

    /// Consume either case without exposing constructors that bypass validation.
    let fold onScalar onResource = function
        | ActorValueState.Scalar value -> onScalar value
        | ActorValueState.Resource (current, maximum) -> onResource current maximum

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

/// What changed between two published reading sets of one player. A kind is
/// a key with its label: a changed label removes the old kind and sets the new.
type ActorValuesPatch = {
    Removed: struct (ActorValueKey * ActorValueName) list
    Set: (ActorValueKey * ActorValueInfo) list
}

[<RequireQualifiedAccess>]
module ActorValuesPatch =
    /// ValueNone when nothing changed.
    let between (previous: Map<ActorValueKey, ActorValueInfo>) (latest: Map<ActorValueKey, ActorValueInfo>) =
        let removed = [
            for KeyValue(key, info) in previous do
                match Map.tryFind key latest with
                | Some next when next.DisplayName = info.DisplayName -> ()
                | Some _ | None -> struct (key, info.DisplayName)
        ]
        let set = [
            for KeyValue(key, info) in latest do
                match Map.tryFind key previous with
                | Some old when old = info -> ()
                | Some _ | None -> key, info
        ]
        if removed.IsEmpty && set.IsEmpty then ValueNone else ValueSome { Removed = removed; Set = set }

/// Mutable state owned by one agent. Never share this storage between agents.
/// Use snapshot or immutable individual readings to publish data.
[<NoEquality; NoComparison>]
type ActorValueStorage = private {
    values: Dictionary<ActorValueKey, ActorValueInfo>
    mutable projection: Map<ActorValueKey, ActorValueInfo> voption
}

[<RequireQualifiedAccess>]
module ActorValueStorage =
    let create () : ActorValueStorage =
        { values = Dictionary(); projection = ValueSome Map.empty }

    /// Retain the immutable input projection while making storage independently mutable.
    let ofSnapshot (entries: Map<ActorValueKey, ActorValueInfo>) : ActorValueStorage =
        let values = Dictionary<ActorValueKey, ActorValueInfo>(entries.Count)
        for KeyValue(key, info) in entries do values.Add(key, info)

        { values = values; projection = ValueSome entries }

    let count (storage: ActorValueStorage) = storage.values.Count

    let set key info (storage: ActorValueStorage) =
        storage.values[key] <- info
        storage.projection <- ValueNone

    /// Apply an already materialized, validated batch. The sender must finish
    /// building the array before handing it to the owning agent and must not
    /// mutate it afterwards. Repeated keys use the last supplied value.
    let setMany (entries: (ActorValueKey * ActorValueInfo) array) (storage: ActorValueStorage) =
        for key, info in entries do
            set key info storage

    let tryFind key (storage: ActorValueStorage) =
        match storage.values.TryGetValue key with
        | true, info -> ValueSome info
        | false, _ -> ValueNone

    let remove key (storage: ActorValueStorage) =
        let removed = storage.values.Remove key
        if removed then storage.projection <- ValueNone
        removed

    let clear (storage: ActorValueStorage) =
        storage.values.Clear()
        storage.projection <- ValueSome Map.empty

    /// Detached immutable snapshot; subsequent storage updates do not change it.
    let snapshot (storage: ActorValueStorage) : Map<ActorValueKey, ActorValueInfo> =
        match storage.projection with
        | ValueSome projection -> projection
        | ValueNone ->
            let projection = storage.values |> Seq.map (fun entry -> entry.Key, entry.Value) |> Map.ofSeq
            storage.projection <- ValueSome projection
            projection
