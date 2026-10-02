module Dreamsleeve.Server.Tests.Program

open Expecto

[<EntryPoint>]
let main argv =
    testList "Tests" [
        DomainTests.tests
        IdentityTests.tests
        GroundMarkDomainTests.tests
        GuildDomainTests.tests
        ModerationTests.tests
        AdminDomainTests.tests
        PlayerDetailsTests.tests
        AgentTests.tests
        CodecTests.tests
        BackgroundTests.tests
        OutboxTests.tests
        AsyncDispatcherTests.tests
        AdmissionTests.tests
        LifetimeTests.tests
        TickerTests.tests
        SupervisorTests.tests
        ProfileStoreTests.tests
        SqliteAccountStoreTests.tests
        AuthServiceTests.tests
        SteamOpenIdTests.tests
        SanctionTests.tests
        AdminStoreTests.tests
        AdminServiceTests.tests
        ChatRoomAgentTests.tests
        PresenceAgentTests.tests
        PlayerSessionTests.tests
        ServerRuntimeTests.tests
        ServerRuntimeTests.hiddenIdentityTests
        ServerRuntimeTests.adminTests
        ServerRuntimeTests.displayNameTests
        EnetTransportTests.tests
        TransportOwnerTests.tests
        ConfigurationTests.tests
        AnnouncementTests.tests
        GroundMarkTests.tests
        GuildTests.tests
        AuthenticationHttpTests.tests
        AdminHttpTests.tests
    ]
    |> runTestsWithCLIArgs [] argv
