namespace Dreamsleeve.Server.Core

open System
open System.Collections.Generic
open Dreamsleeve.Server.Domain

/// Single-owner broad phase. Exact distance remains the presence owner's decision.
/// Double cell coordinates avoid overflow for every finite float32 world coordinate.
[<RequireQualifiedAccess>]
module internal SpatialIndex =
    [<Struct>]
    type Cell = { Space: FormKey; X: double; Y: double; Z: double }
    type State = {
        CellSize: double
        Cells: Dictionary<Cell, HashSet<Guid>>
        Entries: Dictionary<Guid, Cell>
    }

    let create radius =
        { CellSize = if radius > 0. then radius else 1.
          Cells = Dictionary(); Entries = Dictionary() }

    let private cell state (location: PlayerLocation) =
        let coordinate value = floor (float value / state.CellSize)
        { Space = location.Location.LocationId; X = coordinate location.Position.X
          Y = coordinate location.Position.Y; Z = coordinate location.Position.Z }

    let remove id state =
        match state.Entries.TryGetValue id with
        | false, _ -> ()
        | true, key ->
            let bucket = state.Cells[key]
            bucket.Remove id |> ignore
            if bucket.Count = 0 then state.Cells.Remove key |> ignore
            state.Entries.Remove id |> ignore

    let set id location state =
        let next = location |> ValueOption.map (cell state)
        match state.Entries.TryGetValue id, next with
        | (true, previous), ValueSome key when previous = key -> ()
        | _, _ ->
            remove id state
            match next with
            | ValueNone -> ()
            | ValueSome key ->
                let bucket =
                    match state.Cells.TryGetValue key with
                    | true, existing -> existing
                    | false, _ ->
                        let created = HashSet<Guid>()
                        state.Cells.Add(key, created)
                        created
                bucket.Add id |> ignore
                state.Entries.Add(id, key)

    let neighbors location state (result: HashSet<Guid>) =
        match location with
        | ValueNone -> ()
        | ValueSome value ->
            let key = cell state value
            for dx in -1 .. 1 do
                for dy in -1 .. 1 do
                    for dz in -1 .. 1 do
                        match state.Cells.TryGetValue({ Space = key.Space; X = key.X + float dx; Y = key.Y + float dy; Z = key.Z + float dz }) with
                        | true, bucket -> result.UnionWith bucket
                        | false, _ -> ()
