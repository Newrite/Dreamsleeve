module Dreamsleeve.Server.Tests.OwnedCleanupTests

open System
open System.Threading
open System.Threading.Tasks
open Dreamsleeve.Server.Core
open Expecto
open AgentTests

let private same (expected: exn list) (actual: exn list) =
    equal (List.length expected) (List.length actual)
    List.iter2 (fun left right -> check (Object.ReferenceEquals(left, right)) "A lifetime cause was replaced.") expected actual

let tests = testList "Owned cleanup" [
    testTask "all actual joined task failures remain in acquisition order" {
        let first = InvalidOperationException("first") :> exn
        let second = InvalidOperationException("second") :> exn
        let combined = Task.WhenAll [Task.FromException first; Task.FromException second]
        let! (errors: exn list) = OwnedCleanup.capture(fun () -> combined)
        same [first; second] errors
        let! value = OwnedCleanup.captureResult(fun () -> Task.WhenAll [Task.FromException<int> first; Task.FromException<int> second])
        match value with
        | Error errors -> same [first; second] errors
        | Ok _ -> failtest "Faulted acquisition appeared successful."
    }
    testTask "reporter fault cannot skip any owned cleanup and joins actual completion" {
        let reporting = InvalidOperationException("reporter") :> exn
        let cleanup = InvalidOperationException("cleanup") :> exn
        let entered = gate<unit>()
        let release = gate<unit>()
        let calls = ResizeArray<string>()
        let! reports = OwnedCleanup.capture(fun () -> calls.Add "report"; Task.FromException reporting)
        let disposing = OwnedCleanup.release [
            (fun () -> calls.Add "first"; Task.FromException cleanup)
            (fun () -> task { calls.Add "second"; entered.TrySetResult() |> ignore; do! release.Task } :> Task)
            (fun () -> calls.Add "third"; Task.CompletedTask)
        ]
        do! awaitResult entered.Task
        check (not disposing.IsCompleted) "Cleanup did not wait for its actual owner."
        equal ["report"; "first"; "second"] (List.ofSeq calls)
        release.TrySetResult() |> ignore
        let! (errors: exn list) = awaitResult disposing
        same [reporting; cleanup] (reports @ errors)
        equal ["report"; "first"; "second"; "third"] (List.ofSeq calls)
    }
    testTask "synchronous lifetime fault is retained and later cleanup still runs" {
        let original = InvalidOperationException("sync") :> exn
        let mutable cleaned = false
        let! (errors: exn list) = OwnedCleanup.release [
            (fun () -> raise original)
            (fun () -> cleaned <- true; Task.CompletedTask)
        ]
        same [original] errors
        check cleaned "A synchronous fault skipped the next cleanup."
    }
    testTask "unexpected task cancellation stays observable" {
        let canceled = CancellationToken(true)
        let! (errors: exn list) = OwnedCleanup.capture(fun () -> Task.FromCanceled<unit>(canceled) :> Task)
        match errors with
        | [(:? OperationCanceledException)] -> ()
        | other -> failtestf "Cancellation disappeared: %A" other
    }
    testTask "finish preserves original singleton and ordered aggregate" {
        let original = InvalidOperationException("primary") :> exn
        let secondary = InvalidOperationException("secondary") :> exn
        let singleton = OwnedCleanup.finish [original]
        let! (errors: exn list) = OwnedCleanup.capture(fun () -> singleton)
        same [original] errors
        let multiple = OwnedCleanup.finish [original; secondary]
        let! (errors: exn list) = OwnedCleanup.capture(fun () -> multiple)
        match errors with
        | [(:? AggregateException as combined)] -> same [original; secondary] (List.ofSeq combined.InnerExceptions)
        | other -> failtestf "Lifetime faults lost their ordered aggregate: %A" other
    }
]
