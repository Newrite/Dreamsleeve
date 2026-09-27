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
        AsyncDispatcherTests.tests
        AdmissionTests.tests
        LifetimeTests.tests
        ProfileStoreTests.tests
        SqliteAccountStoreTests.tests
        AuthServiceTests.tests
        ChatRoomAgentTests.tests
        PresenceAgentTests.tests
        PlayerSessionTests.tests
        ServerRuntimeTests.tests
        EnetTransportTests.tests
        ConfigurationTests.tests
        AuthenticationHttpTests.tests
    ]
    |> runTestsWithCLIArgs [] argv
