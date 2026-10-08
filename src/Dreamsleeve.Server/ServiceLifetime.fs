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

type ServiceFailure = { Stage: ServiceFailureStage; Error: exn }

/// Composition owns every returned service until its completion is observed.
[<RequireQualifiedAccess>]
module ServiceLifetime =
    let run (authentication: Agent<AuthMessage>)
            (startAdmin: unit -> Agent<AdminMessage> option)
            (serve: Agent<AdminMessage> option -> Task<int>)
            (stopAdmin: Agent<AdminMessage> option -> Task<unit>)
            (stopAuthentication: Agent<AuthMessage> -> Task<unit>) = task {
        let failures = ResizeArray<ServiceFailure>()
        let mutable admin = None
        let mutable exitCode = 1
        // This is the server lifecycle boundary, not an operation adapter.
        try
            admin <- startAdmin ()
            let! result = serve admin
            exitCode <- result
        with error -> failures.Add { Stage = ServiceFailureStage.StartOrServe; Error = error }

        try do! stopAdmin admin
        with error -> failures.Add { Stage = ServiceFailureStage.StopAdmin; Error = error }

        try do! stopAuthentication authentication
        with error -> failures.Add { Stage = ServiceFailureStage.StopAuthentication; Error = error }

        if failures.Count = 0 then return Ok exitCode
        else return Error(List.ofSeq failures)
    }
