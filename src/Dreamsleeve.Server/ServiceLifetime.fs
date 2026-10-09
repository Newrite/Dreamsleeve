namespace Dreamsleeve.Server

open System.Threading.Tasks
open Dreamsleeve.Agent
open Dreamsleeve.Server.Core
open Dreamsleeve.Server.Infrastructure

[<RequireQualifiedAccess>]
type ServiceFailureStage =
    | StartOrServe
    | StopAdmin
    | StopAuthentication

[<RequireQualifiedAccess>]
type ServiceFailureReason =
    | StartRejected of AgentStartError
    | Faulted of exn

type ServiceFailure = { Stage: ServiceFailureStage; Reason: ServiceFailureReason }

/// Composition owns every returned service until its completion is observed.
[<RequireQualifiedAccess>]
module ServiceLifetime =
    let run (authentication: ReliableAgent<AuthMessage>)
            (startAdmin: unit -> Task<Result<ReliableAgent<AdminMessage> option, AgentStartError>>)
            (serve: ReliableAgent<AdminMessage> option -> Task<int>)
            (stopAdmin: ReliableAgent<AdminMessage> option -> Task<unit>)
            (stopAuthentication: ReliableAgent<AuthMessage> -> Task<unit>) = task {
        let failures = ResizeArray<ServiceFailure>()
        let mutable admin = None
        let mutable exitCode = 1
        // These are lifetime boundaries: inspect the actual returned tasks before wrappers select one fault.
        let record (stage: ServiceFailureStage) (errors: exn list) =
            for error in errors do failures.Add { Stage = stage; Reason = ServiceFailureReason.Faulted error }
        match! OwnedCleanup.captureResult startAdmin with
        | Error errors -> record ServiceFailureStage.StartOrServe errors
        | Ok(Error error) -> failures.Add { Stage = ServiceFailureStage.StartOrServe; Reason = ServiceFailureReason.StartRejected error }
        | Ok(Ok started) ->
            admin <- started
            match! OwnedCleanup.captureResult(fun () -> serve admin) with
            | Ok result -> exitCode <- result
            | Error errors -> record ServiceFailureStage.StartOrServe errors

        let! adminErrors = OwnedCleanup.capture(fun () -> stopAdmin admin :> Task)
        record ServiceFailureStage.StopAdmin adminErrors
        let! authenticationErrors = OwnedCleanup.capture(fun () -> stopAuthentication authentication :> Task)
        record ServiceFailureStage.StopAuthentication authenticationErrors

        if failures.Count = 0 then return Ok exitCode
        else return Error(List.ofSeq failures)
    }
