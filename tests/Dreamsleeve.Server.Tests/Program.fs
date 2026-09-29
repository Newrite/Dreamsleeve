module Dreamsleeve.Server.Tests.Program

open Expecto

[<EntryPoint>]
let main argv =
    testList "Tests" [
        DomainTests.tests
        IdentityTests.tests
        GroundMarkDomainTests.tests
        ModerationTests.tests
        PlayerDetailsTests.tests
        AgentTests.tests
        CodecTests.tests
        BackgroundTests.tests
        OutboxTests.tests
        AsyncDispatcherTests.tests
        AdmissionTests.tests
        LifetimeTests.tests
        TickerTests.tests
        ProfileStoreTests.tests
        SqliteAccountStoreTests.tests
        AuthServiceTests.tests
        ChatRoomAgentTests.tests
        PresenceAgentTests.tests
        PlayerSessionTests.tests
        ServerRuntimeTests.tests
        ServerRuntimeTests.hiddenIdentityTests
        EnetTransportTests.tests
        TransportOwnerTests.tests
        ConfigurationTests.tests
        AnnouncementTests.tests
        GroundMarkTests.tests
        AuthenticationHttpTests.tests
    ]
    |> runTestsWithCLIArgs [] argv
