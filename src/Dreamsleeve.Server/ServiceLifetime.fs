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
        // This is the server lifecycle boundary, not an operation adapter.
        try
            match! startAdmin () with
            | Error error -> failures.Add { Stage = ServiceFailureStage.StartOrServe; Reason = ServiceFailureReason.StartRejected error }
            | Ok started ->
                admin <- started
                let! result = serve admin
                exitCode <- result
        with error -> failures.Add { Stage = ServiceFailureStage.StartOrServe; Reason = ServiceFailureReason.Faulted error }

        try do! stopAdmin admin
        with error -> failures.Add { Stage = ServiceFailureStage.StopAdmin; Reason = ServiceFailureReason.Faulted error }

        try do! stopAuthentication authentication
        with error -> failures.Add { Stage = ServiceFailureStage.StopAuthentication; Reason = ServiceFailureReason.Faulted error }

        if failures.Count = 0 then return Ok exitCode
        else return Error(List.ofSeq failures)
    }
