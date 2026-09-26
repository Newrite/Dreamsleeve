module Dreamsleeve.Server.Tests.Program

open Expecto

[<EntryPoint>]
let main argv =
    testList "Tests" [
        DomainTests.tests
        AgentTests.tests
        CodecTests.tests
        BackgroundTests.tests
        OutboxTests.tests
        AdmissionTests.tests
        LifetimeTests.tests
        ProfileStoreTests.tests
        ChatRoomAgentTests.tests
        PresenceAgentTests.tests
        PlayerSessionTests.tests
        ServerRuntimeTests.tests
    ]
    |> runTestsWithCLIArgs [] argv
