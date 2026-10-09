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

let private authentication () : Agent<AuthMessage> =
    TestAgent.Start(AgentOptions.create "lifetime-auth", fun _ _ -> Task.FromResult())

let private administrator () : Agent<AdminMessage> =
    TestAgent.Start(AgentOptions.create "lifetime-admin", fun _ _ -> Task.FromResult())

let private stop (agent: Agent<'Message>) = task {
    agent.Complete() |> ignore
    do! awaitUnit agent.Completion
}

let private expectFailures expected result =
    match result with
    | Ok code -> failtestf "Unexpected successful exit code: %d" code
    | Error actual ->
        equal (List.map fst expected) (actual |> List.map (fun failure -> failure.Stage))
        List.iter2 (fun (_, error) failure ->
            check (Object.ReferenceEquals(error, failure.Error)) "The original lifecycle fault was replaced.") expected actual

let tests = testList "Server service lifetime" [
    case "normal shutdown stops admin before authentication and preserves the serve exit code" (fun () -> task {
        use auth = authentication ()
        use admin = administrator ()
        let calls = ResizeArray<string>()
        let! result =
            ServiceLifetime.run auth
                (fun () -> calls.Add "start"; Some admin)
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
            ServiceLifetime.run auth (fun () -> None)
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
            ServiceLifetime.run auth (fun () -> Some admin)
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
