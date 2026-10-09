module Dreamsleeve.Server.Tests.ServiceLifetimeTests

open System
open System.Threading.Tasks
open Dreamsleeve.Agent
open Dreamsleeve.Server
open Dreamsleeve.Server.Core
open Dreamsleeve.Server.Infrastructure
open Expecto
open AgentTests
open BackgroundTests

let private authentication () : ReliableAgent<AuthMessage> =
    Agent.TryStartReliable(AgentOptions.create "lifetime-auth", fun _ _ -> Task.FromResult()) |> expectStarted

let private administrator () : ReliableAgent<AdminMessage> =
    Agent.TryStartReliable(AgentOptions.create "lifetime-admin", fun _ _ -> Task.FromResult()) |> expectStarted

let private stop (agent: ReliableAgent<'Message>) = task {
    agent.Complete() |> ignore
    do! awaitUnit agent.Completion
}

let private expectFailures expected result =
    match result with
    | Ok code -> failtestf "Unexpected successful exit code: %d" code
    | Error actual ->
        equal (List.map fst expected) (actual |> List.map (fun failure -> failure.Stage))
        List.iter2 (fun (_, error) failure ->
            match failure.Reason with
            | ServiceFailureReason.Faulted actual -> check (Object.ReferenceEquals(error, actual)) "The original lifecycle fault was replaced."
            | ServiceFailureReason.StartRejected actual -> failtestf "Unexpected startup refusal: %A" actual) expected actual

let private failedTask<'Value> (errors: exn list) =
    let completion = TaskCompletionSource<'Value>()
    completion.SetException errors
    completion.Task

let tests = testList "Server service lifetime" [
    case "compound serve and cleanup tasks retain every original cause at each stage" (fun () -> task {
        use auth = authentication ()
        let first = InvalidOperationException "serve first" :> exn
        let second = InvalidOperationException "serve second" :> exn
        let cleanupFirst = InvalidOperationException "cleanup first" :> exn
        let cleanupSecond = InvalidOperationException "cleanup second" :> exn
        let! result = ServiceLifetime.run auth (fun () -> Task.FromResult(Ok None))
                          (fun _ -> failedTask<int> [first; second])
                          (fun _ -> failedTask<unit> [cleanupFirst; cleanupSecond])
                          stop
        expectFailures [ServiceFailureStage.StartOrServe, first; ServiceFailureStage.StartOrServe, second
                        ServiceFailureStage.StopAdmin, cleanupFirst; ServiceFailureStage.StopAdmin, cleanupSecond] result
        check auth.Completion.IsCompletedSuccessfully "Compound failure skipped authentication cleanup."
    })

    case "typed admin startup refusal skips serving and joins authentication" (fun () -> task {
        use auth = authentication ()
        let refusal = AgentStartError.InvalidCapacity("capacity", 0)
        let calls = ResizeArray<string>()
        let! result = ServiceLifetime.run auth (fun () -> Task.FromResult(Error refusal))
                          (fun _ -> calls.Add "serve"; Task.FromResult 0)
                          (fun admin -> task { Expect.isNone admin "No refused owner."; calls.Add "stop-admin" })
                          (fun owner -> task { calls.Add "stop-auth"; do! stop owner })
        match result with
        | Error [{ Stage = ServiceFailureStage.StartOrServe; Reason = ServiceFailureReason.StartRejected actual }] -> equal refusal actual
        | other -> failtestf "Unexpected lifetime result: %A" other
        equal ["stop-admin"; "stop-auth"] (List.ofSeq calls)
        check auth.Completion.IsCompletedSuccessfully "Authentication cleanup did not finish."
    })

    case "normal shutdown stops admin before authentication and preserves the serve exit code" (fun () -> task {
        use auth = authentication ()
        use admin = administrator ()
        let calls = ResizeArray<string>()
        let! result =
            ServiceLifetime.run auth
                (fun () -> calls.Add "start"; Task.FromResult(Ok(Some admin)))
                (fun owned -> task {
                    check (owned |> Option.exists (fun value -> Object.ReferenceEquals(value, admin))) "Wrong admin ownership."
                    calls.Add "serve"
                    return 7
                })
                (fun owned -> task {
                    calls.Add "stop-admin"
                    match owned with
                    | Some service -> do! stop service
                    | None -> failtest "Admin ownership was lost."
                })
                (fun service -> task {
                    check admin.Completion.IsCompletedSuccessfully "Authentication stopped before admin completion."
                    calls.Add "stop-auth"
                    do! stop service
                })
        equal (Ok 7) result
        equal [ "start"; "serve"; "stop-admin"; "stop-auth" ] (List.ofSeq calls)
        check auth.Completion.IsCompletedSuccessfully "Authentication was not cleaned up."
    })

    case "disabled admin remains legitimate absence and authentication still stops" (fun () -> task {
        use auth = authentication ()
        let! result =
            ServiceLifetime.run auth (fun () -> Task.FromResult(Ok None))
                (fun admin -> task { Expect.isNone admin "Admin disabled."; return 0 })
                (fun admin -> task { Expect.isNone admin "No hidden admin resource." })
                stop
        equal (Ok 0) result
        check auth.Completion.IsCompletedSuccessfully "Authentication was not cleaned up."
    })

    case "failed admin startup skips listeners and cleans up returned authentication" (fun () -> task {
        use auth = authentication ()
        let failure = InvalidOperationException "admin startup" :> exn
        let calls = ResizeArray<string>()
        let! result =
            ServiceLifetime.run auth (fun () -> raise failure)
                (fun _ -> calls.Add "serve"; Task.FromResult 0)
                (fun admin -> task {
                    Expect.isNone admin "Failed construction returned no resource."
                    calls.Add "stop-admin"
                })
                (fun service -> task { calls.Add "stop-auth"; do! stop service })
        expectFailures [ ServiceFailureStage.StartOrServe, failure ] result
        equal [ "stop-admin"; "stop-auth" ] (List.ofSeq calls)
        check auth.Completion.IsCompletedSuccessfully "Authentication leaked after admin startup fault."
    })

    case "serve and both cleanup failures preserve original faults without skipping cleanup" (fun () -> task {
        use auth = authentication ()
        use admin = administrator ()
        let initial = InvalidOperationException "listener" :> exn
        let adminFailure = InvalidOperationException "admin cleanup" :> exn
        let authFailure = InvalidOperationException "auth cleanup" :> exn
        let calls = ResizeArray<string>()
        let! result =
            ServiceLifetime.run auth (fun () -> Task.FromResult(Ok(Some admin)))
                (fun _ -> Task.FromException<int> initial)
                (fun owned -> task {
                    calls.Add "stop-admin"
                    match owned with
                    | Some service -> do! stop service
                    | None -> failtest "Missing admin."
                    return raise adminFailure
                })
                (fun service -> task {
                    check admin.Completion.IsCompletedSuccessfully "Admin cleanup did not complete first."
                    calls.Add "stop-auth"
                    do! stop service
                    return raise authFailure
                })
        expectFailures [ ServiceFailureStage.StartOrServe, initial
                         ServiceFailureStage.StopAdmin, adminFailure
                         ServiceFailureStage.StopAuthentication, authFailure ] result
        equal [ "stop-admin"; "stop-auth" ] (List.ofSeq calls)
        check auth.Completion.IsCompletedSuccessfully "Second cleanup was skipped."
    })
]
