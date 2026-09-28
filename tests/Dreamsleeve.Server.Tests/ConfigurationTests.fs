module Dreamsleeve.Server.Tests.ConfigurationTests

open System
open System.IO
open Expecto
open Dreamsleeve.Server

let private withFile (text: string) action =
    let path = Path.Combine(Path.GetTempPath(), sprintf "dreamsleeve-config-%O.toml" (Guid.NewGuid()))
    try
        File.WriteAllText(path, text)
        action path
    finally
        File.Delete path

let tests = testList "Server configuration" [
    testCase "server display name loads from TOML and rejects invalid labels" <| fun _ ->
        withFile "[Server]\nServerName = 'Голоса Тамриэля'\n" (fun path ->
            match Configuration.parse [|"--config"; path|] with
            | Ok (LaunchCommand.Run config) -> Expect.equal config.Server.ServerName "Голоса Тамриэля" "name"
            | other -> failtestf "%A" other)
        for name in [""; "   "; String.replicate 129 "x"] do
            withFile (sprintf "[Server]\nServerName = '%s'\n" name) (fun path ->
                Expect.isError (Configuration.parse [|"--config"; path|]) "invalid name")

    testCase "TOML comments empty tables literal paths and inline tables are supported" <| fun _ ->
        withFile "# defaults\n" (fun path ->
            Expect.equal (Configuration.parse [|"--config"; path|]) (Ok (LaunchCommand.Run Configuration.defaults)) "Comments-only TOML keeps defaults.")
        withFile "" (fun path ->
            Expect.equal (Configuration.parse [|"--config"; path|]) (Ok (LaunchCommand.Run Configuration.defaults)) "Empty TOML keeps defaults.")
        withFile "Server = { Port = 9_001 } # inline override\n[Database]\nDatabasePath = 'C:\\Игры\\data.db'\n" (fun path ->
            match Configuration.parse [|"--config"; path|] with
            | Ok (LaunchCommand.Run config) ->
                Expect.equal config.Server.Port 9001us "TOML integer separators."
                Expect.equal config.Database.DatabasePath @"C:\Игры\data.db" "Literal strings preserve backslashes."
            | other -> failwithf "%A" other)

    testCase "TOML rejects duplicate keys coercions overflow nonfinite and old JSON" <| fun _ ->
        for source in [ "[Server]\nPort=9000\nPort=9001"; "[server]\nPort=9000";
                        "[Server]\nPort=9000.5"; "[Server]\nPort='9000'"; "[Server]\nPort=65536";
                        "[Server]\nPort=-1"; "[Runtime.Presence]\nVisibilityDistance=nan";
                        "[Runtime.Presence]\nVisibilityDistance=inf"; "{}"; String.replicate 65537 " " ] do
            withFile source (fun path -> Expect.isError (Configuration.parse [|"--config"; path|]) "Invalid TOML configuration.")

    testCase "partial nested settings retain defaults and CLI port takes precedence" <| fun _ ->
        withFile "[Runtime.Player]\nMaxPendingChat = 3\n\n[Server]\nPort = 9000\n" (fun path ->
            match Configuration.parse [|"--config"; path; "--port"; "9001"|] with
            | Ok (LaunchCommand.Run config) ->
                Expect.equal config.Server.Port 9001us "CLI override"
                Expect.equal config.Runtime.Player.MaxPendingChat 3 "nested override"
                Expect.equal config.Server.MaxOutgoingBytes Configuration.defaults.Server.MaxOutgoingBytes "omitted budget preserved"
            | other -> failwithf "Expected valid config: %A" other)

    testCase "movement target supports automatic MTU and rejects negative values" <| fun _ ->
        withFile "[Server]\nMovementPacketTargetBytes = 900\n" (fun path ->
            match Configuration.parse [|"--config"; path|] with
            | Ok (LaunchCommand.Run config) -> Expect.equal config.Server.MovementPacketTargetBytes 900 "Configured target."
            | other -> failwithf "%A" other)
        withFile "[Server]\nMovementPacketTargetBytes = 0\n" (fun path ->
            Expect.isOk (Configuration.parse [|"--config"; path|]) "Zero uses the negotiated MTU target.")
        withFile "[Server]\nMovementPacketTargetBytes = -1\n" (fun path ->
            Expect.isError (Configuration.parse [|"--config"; path|]) "Invalid target rejected at startup.")

    testCase "transport owner rejects unbounded or blocking configuration" <| fun _ ->
        for source in [
            "[Server]\nWorker = 0\n"
            "[Server.Worker]\nQueueCapacity = 0\n"
            "[Server.Worker]\nQueueBytes = 1\n"
            "[Server.Worker]\nSendCommandsPerPass = 0\n"
            "[Server.Worker]\nIdleWaitMs = 11\n"
            "[Server]\nServiceTimeoutMs = 1\n"
            "[Server]\nChannelLimit = 2\n"
        ] do
            withFile source (fun path ->
                Expect.isError (Configuration.parse [|"--config"; path|]) "invalid transport config")

    testCase "unknown properties, wrong types and non-table sections are rejected" <| fun _ ->
        for source in ["[Server]\nTypo = 1\n"; "Runtime = 0\n"; "Server = 0\n"; "[Server]\nBindAddress = 42\n"; "[]"] do
            withFile source (fun path ->
                Expect.isError (Configuration.parse [|"--config"; path|]) "invalid config must fail"
                Expect.isError (Configuration.parse [|"--config"; path; "--port"; "9001"|]) "CLI override cannot bypass section validation")

    testCase "cross-owner limits cannot make welcome exceed configured collection bounds" <| fun _ ->
        for source in ["[Server]\nMaxInitialPlayers = 1\n"; "[Server]\nMaxRecentMessages = 1\n"] do
            withFile source (fun path ->
                Expect.isError (Configuration.parse [|"--config"; path|]) "incompatible limits rejected")

    testCase "exported defaults load without losing any nested settings" <| fun _ ->
        withFile "" (fun path ->
            Configuration.writeDefaults path |> function Ok () -> () | Error error -> failwith error
            match Configuration.parse [|"--config"; path|] with
            | Ok (LaunchCommand.Run config) -> Expect.equal config Configuration.defaults "roundtrip all fields"
            | other -> failwithf "Cannot load exported defaults: %A" other)
    testCase "authentication requires TLS outside explicitly enabled literal loopback" <| fun _ ->
        for source in [
            "[Authentication]\nListenUrl = \"http://0.0.0.0:8779\"\n"
            "[Authentication]\nListenUrl = \"http://localhost:8779\"\n"
            "[Authentication]\nAllowInsecureLoopback = false\n"
            "[Authentication]\nListenUrl = \"https://user:secret@example.com\"\n"
            "[Authentication]\nListenUrl = \"https://example.com/auth\"\n"
            "[Authentication]\nListenUrl = 0\n"
        ] do
            withFile source (fun path ->
                Expect.isError (Configuration.parse [|"--config"; path|]) "unsafe or ambiguous binding rejected")
        withFile "[Authentication]\nListenUrl = \"https://example.com:9443\"\nAllowInsecureLoopback = false\n" (fun path ->
            Expect.isOk (Configuration.parse [|"--config"; path|]) "TLS endpoint accepted")

    testCase "account and logging limits and wrong section types fail before startup" <| fun _ ->
        for source in [
            "Authentication = 0\n"; "Database = 0\n"; "Logging = 0\n"
            "[Authentication]\nService = 0\n"
            "[Authentication.Service]\nMaxConcurrentOperations = 0\n"
            "[Authentication.Service]\nPasswordIterations = 1\n"
            "[Authentication]\nRequestsPerMinute = 0\n"
            "[Database]\nDatabasePath = \"\"\n"
            "[Logging]\nMinimumLevel = \"bogus\"\n"
            "[Logging]\nConsole = false\nFilePath = \"\"\n"
        ] do
            withFile source (fun path ->
                Expect.isError (Configuration.parse [|"--config"; path|]) "invalid settings rejected")

    testCase "player input sections and telemetry budgets are validated" <| fun _ ->
        for source in [
            "[Server]\nPlayerInput = 0\n"
            "[Server.PlayerInput]\nMaxActorValues = 0\n"
            "[Server.PlayerInput]\nDetailsText = 0\n"
            "[Server.PlayerInput]\nActivityKey = 0\n"
        ] do
            withFile source (fun path ->
                Expect.isError (Configuration.parse [|"--config"; path|]) "invalid telemetry input configuration rejected")
    testCase "visibility distance loads from TOML and rejects negative radius" <| fun _ ->
        withFile "[Runtime.Presence]\nVisibilityDistance = 0\n" (fun path ->
            match Configuration.parse [|"--config"; path|] with
            | Ok (LaunchCommand.Run config) -> Expect.equal config.Runtime.Presence.VisibilityDistance 0.0f "Zero is valid."
            | other -> failwithf "%A" other)
        withFile "[Runtime.Presence]\nVisibilityDistance = -1\n" (fun path ->
            Expect.isError (Configuration.parse [|"--config"; path|]) "Invalid distance rejected before startup.")
]
