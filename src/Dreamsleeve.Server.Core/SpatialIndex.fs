namespace Dreamsleeve.Server.Core

open System.Collections.Generic
open Dreamsleeve.Server.Domain

/// Single-owner broad phase. Exact distance remains the owning agent's decision.
/// Double cell coordinates avoid overflow for every finite float32 world coordinate.
/// The key is whatever the owner indexes: connections for presence, mark IDs for marks.
[<RequireQualifiedAccess>]
module internal SpatialIndex =
    [<Struct>]
    type Cell = {
        Space: FormKey
        X: double
        Y: double
        Z: double
    }
    type State<'Key when 'Key: equality> = {
        CellSize: double
        Cells: Dictionary<Cell, HashSet<'Key>>
        Entries: Dictionary<'Key, Cell>
    }

    let create radius : State<'Key> = {
        CellSize = if radius > 0. then radius else 1.
        Cells = Dictionary()
        Entries = Dictionary()
    }

    let cellOf (state: State<'Key>) (space: FormKey) (position: Position) =
        let coordinate value = floor (float value / state.CellSize)

        {
            Space = space
            X = coordinate position.X
            Y = coordinate position.Y
            Z = coordinate position.Z
        }

    let private cell state (location: PlayerLocation) =
        cellOf state location.Location.LocationId location.Position

    let remove id (state: State<'Key>) =
        match state.Entries.TryGetValue id with
        | false, _ -> ()
        | true, key ->
            let bucket = state.Cells[key]
            bucket.Remove id |> ignore

            if bucket.Count = 0 then
                state.Cells.Remove key |> ignore

            state.Entries.Remove id |> ignore

    let setCell id (next: Cell voption) (state: State<'Key>) =
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
                        let created = HashSet<'Key>()
                        state.Cells.Add(key, created)
                        created

                bucket.Add id |> ignore
                state.Entries.Add(id, key)

    let set id location (state: State<'Key>) =
        setCell id (location |> ValueOption.map (cell state)) state

    /// Entries currently in this exact cell, not its neighbours.
    let countAt (key: Cell) (state: State<'Key>) =
        match state.Cells.TryGetValue key with
        | true, bucket -> bucket.Count
        | false, _ -> 0

    let neighborsOf (key: Cell) (state: State<'Key>) (result: HashSet<'Key>) =
        for dx in -1 .. 1 do
            for dy in -1 .. 1 do
                for dz in -1 .. 1 do
                    match state.Cells.TryGetValue({
                        Space = key.Space
                        X = key.X + float dx
                        Y = key.Y + float dy
                        Z = key.Z + float dz
                    }) with
                    | true, bucket -> result.UnionWith bucket
                    | false, _ -> ()

    let neighbors location (state: State<'Key>) (result: HashSet<'Key>) =
        match location with
        | ValueNone -> ()
        | ValueSome value -> neighborsOf (cell state value) state result
