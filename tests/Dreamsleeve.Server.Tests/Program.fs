module Dreamsleeve.Server.Tests.Program

open Expecto

[<EntryPoint>]
let main argv =
    testList "Tests" [ DomainTests.tests; AgentTests.tests; CodecTests.tests; BackgroundTests.tests; OutboxTests.tests; ProfileStoreTests.tests; ChatAgentTests.tests; PlayerAgentTests.tests; SessionRegistryTests.tests ]
    |> runTestsWithCLIArgs [] argv
