namespace Dreamsleeve.Server.Core

open System
open System.Threading.Tasks

/// Lifetime adapters for explicitly owned resources. Operation errors belong to their own boundaries.
[<RequireQualifiedAccess>]
module OwnedCleanup =
    let private causes (work: Task) (caught: exn) =
        if work.IsFaulted then List.ofSeq work.Exception.InnerExceptions
        else [caught]

    /// Observe the actual returned task before another awaiter can select only one fault.
    let captureResult (action: unit -> Task<'Value>) : Task<Result<'Value, exn list>> = task {
        let started =
            try Ok(action ())
            with error -> Error [error]
        match started with
        | Error errors -> return Error errors
        | Ok work ->
            try
                let! value = work
                return Ok value
            with error -> return Error(causes work error)
    }

    let capture (action: unit -> Task) : Task<exn list> = task {
        let started =
            try Ok(action ())
            with error -> Error [error]
        match started with
        | Error errors -> return errors
        | Ok work ->
            try
                do! work
                return []
            with error -> return causes work error
    }

    /// The owner supplies a materialized list in its required release order.
    let release (actions: (unit -> Task) list) : Task<exn list> = task {
        let errors = ResizeArray<exn>()
        for action in actions do
            let! failures = capture action
            errors.AddRange failures
        return List.ofSeq errors
    }

    /// Preserve a singleton's identity; several lifetime faults stay ordered and observable.
    let finish (errors: exn list) : Task<unit> =
        match errors with
        | [] -> Task.FromResult()
        | [error] -> Task.FromException<unit> error
        | errors -> Task.FromException<unit>(AggregateException("Owned resources failed during their lifetime.", errors))
