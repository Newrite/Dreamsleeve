module Dreamsleeve.Server.Tests.Program

open Expecto

[<EntryPoint>]
let main argv =
    testList "Tests" [ DomainTests.tests; AgentTests.tests; CodecTests.tests; BackgroundTests.tests; OutboxTests.tests; AdmissionTests.tests; LifetimeTests.tests; ProfileStoreTests.tests; ChatAgentTests.tests; PlayerAgentTests.tests; SessionRegistryTests.tests; ChatFlowTests.tests; ChatRoomAgentTests.tests; PresenceAgentTests.tests; PlayerSessionTests.tests; ServerRuntimeTests.tests ]
    |> runTestsWithCLIArgs [] argv
