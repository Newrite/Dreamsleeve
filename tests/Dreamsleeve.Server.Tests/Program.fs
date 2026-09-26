module Dreamsleeve.Server.Tests.Program

open Expecto

[<EntryPoint>]
let main argv =
    testList "Tests" [ DomainTests.tests; AgentTests.tests; CodecTests.tests; BackgroundTests.tests; ProfileStoreTests.tests ]
    |> runTestsWithCLIArgs [] argv
