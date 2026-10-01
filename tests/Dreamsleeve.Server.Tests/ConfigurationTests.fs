module Dreamsleeve.Server.Tests.ConfigurationTests

open System
open System.IO
open Expecto
open Dreamsleeve.Agent
open Dreamsleeve.Server

let private withFile (text: string) action =
    let path = Path.Combine(Path.GetTempPath(), sprintf "dreamsleeve-config-%O.toml" (Guid.NewGuid()))
    try
        File.WriteAllText(path, text)
        action path
    finally
        File.Delete path

let private parsed path =
    match Configuration.parse [|"--config"; path|] with
    | Ok (LaunchCommand.Run(config, _)) -> config
    | other -> failtestf "Expected a valid configuration: %A" other

let private example () =
    let rec find (directory: DirectoryInfo) =
        let candidate = Path.Combine(directory.FullName, "src", "Dreamsleeve.Server", "server.example.toml")
        if File.Exists candidate then candidate
        elif isNull directory.Parent then failtest "server.example.toml not found"
        else find directory.Parent
    find (DirectoryInfo AppContext.BaseDirectory)

// Every value path of a TOML document; table arrays and empty arrays name no setting.
let rec private keys prefix (table: Tomlyn.Model.TomlTable) = [
    for pair in table do
        match pair.Value with
        | :? Tomlyn.Model.TomlTable as nested -> yield! keys $"{prefix}{pair.Key}." nested
        | :? Tomlyn.Model.TomlTableArray -> ()
        | :? Tomlyn.Model.TomlArray as values when values.Count = 0 -> ()
        | _ -> $"{prefix}{pair.Key}"
]

let tests = testList "Server configuration" [
    testCase "moderation is on by default, switchable and loads a separate word list" <| fun _ ->
        Expect.isTrue Configuration.defaults.Moderation.Enabled "enabled by default"
        withFile "[Moderation]\nEnabled = false\nRulesPath = ''\n" (fun path ->
            match Configuration.parse [|"--config"; path|] with
            | Ok (LaunchCommand.Run(config, _)) ->
                Expect.isFalse config.Moderation.Enabled "switched off"
                match Configuration.loadModeration config.Moderation with
                | Ok (rules, warning) ->
                    Expect.isTrue rules.IsEmpty "disabled list"
                    Expect.isNone warning "no warning when disabled"
                | Error error -> failtest error
            | other -> failtestf "%A" other)
        withFile "[Moderation]\nRulesPath = ''\n" (fun path ->
            Expect.isError (Configuration.parse [|"--config"; path|]) "enabled moderation needs a path")
        match Configuration.loadModeration { Enabled = true; RulesPath = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid()}.toml") } with
        | Ok (rules, Some _) -> Expect.isTrue rules.IsEmpty "missing file warns and runs with an empty list"
        | other -> failtestf "%A" other
        withFile "words = ['badword']\nsubstrings = ['cunt']\nexceptions = ['Scunthorpe']\n" (fun path ->
            match Configuration.loadModeration { Enabled = true; RulesPath = path } with
            | Ok (rules, None) ->
                Expect.isFalse (Dreamsleeve.Server.Domain.Moderation.allows rules "b4dword") "word rule"
                Expect.isTrue (Dreamsleeve.Server.Domain.Moderation.allows rules "Scunthorpe") "exception"
            | other -> failtestf "%A" other)
        for invalid in ["words = 'badword'\n"; "words = [1]\n"; "phrases = ['x']\n"; "words = ['x'\n"] do
            Expect.isError (Configuration.parseModeration invalid) $"invalid rules: {invalid}"

    testCase "block and flag tiers load from sections; top-level keys stay the block tier" <| fun _ ->
        let text = String.concat "\n" [
            "words = ['legacyword']"
            "[block]"
            "words = ['blockword']"
            "[flag]"
            "words = ['flagword']"
            "substrings = ['xflag']"
            "exceptions = ['xflagok']"
            "" ]
        match Configuration.parseModeration text with
        | Ok rules ->
            Expect.isFalse (Dreamsleeve.Server.Domain.Moderation.allows rules "legacyword") "legacy block"
            Expect.isFalse (Dreamsleeve.Server.Domain.Moderation.allows rules "blockword") "block section"
            Expect.isTrue (Dreamsleeve.Server.Domain.Moderation.allows rules "flagword") "flag does not refuse"
            Expect.equal (Dreamsleeve.Server.Domain.Moderation.flag rules "a flagword").Length 1 "flag marks"
            Expect.isEmpty (Dreamsleeve.Server.Domain.Moderation.flag rules "xflagok") "flag exception"
        | Error error -> failtest error
        for invalid in ["[flag]\nphrases = ['x']\n"; "flag = 'x'\n"; "[other]\nwords = ['x']\n"] do
            Expect.isError (Configuration.parseModeration invalid) $"invalid tiers: {invalid}"

    testCase "bundled moderation example parses and blocks its sample words" <| fun _ ->
        let root = DirectoryInfo(AppContext.BaseDirectory)
        let rec find (directory: DirectoryInfo) =
            let candidate = Path.Combine(directory.FullName, "src", "Dreamsleeve.Server", "moderation.example.toml")
            if File.Exists candidate then candidate
            elif isNull directory.Parent then failtest "moderation.example.toml not found"
            else find directory.Parent
        match Configuration.parseModeration (File.ReadAllText(find root)) with
        | Ok rules ->
            let allows = Dreamsleeve.Server.Domain.Moderation.allows rules
            let flagged text = not (Dreamsleeve.Server.Domain.Moderation.flag rules text).IsEmpty
            // Refused for everyone.
            for text in ["k.y.s"; "just KILL YOURSELF"; "иди повесься"; "детское порно"] do
                Expect.isFalse (allows text) $"block tier: {text}"
            // Delivered but marked.
            for text in ["n1gg3r"; "ты чурка"; "Sieg Heil!"; "14/88"; "join discord.gg/abc"; "1xbet promo";
                         "скинь нюдсы"; "я знаю где ты живешь"; "купить мефедрон"; "проклятый хач"] do
                Expect.isTrue (allows text) $"flag tier does not refuse: {text}"
                Expect.isTrue (flagged text) $"flag tier marks: {text}"
            // Ordinary game chat, idioms and look-alike words stay untouched.
            for text in ["Hello, Dragonborn"; "Встретимся в Вайтране у Драконьего Предела"; "убью дракона и вернусь";
                         "хачапури в таверне"; "жидкость для зелья"; "Нигер и Чад — страны Африки"; "a chink in the armor";
                         "spic and span"; "niggardly pay"; "упорно качаю кузнечное дело"; "синдром дауна"; "Scunthorpe United";
                         "Skooma is bad for you"; "I'll find you at the College of Winterhold"; "раздача зелий у храма"] do
                Expect.isTrue (allows text) $"not refused: {text}"
                Expect.isFalse (flagged text) $"not flagged: {text}"
        | Error error -> failtest error

    testCase "server display name loads from TOML and rejects invalid labels" <| fun _ ->
        withFile "[Server]\nServerName = 'Голоса Тамриэля'\n" (fun path ->
            match Configuration.parse [|"--config"; path|] with
            | Ok (LaunchCommand.Run(config, _)) -> Expect.equal config.Server.ServerName "Голоса Тамриэля" "name"
            | other -> failtestf "%A" other)
        for name in [""; "   "; String.replicate 129 "x"] do
            withFile (sprintf "[Server]\nServerName = '%s'\n" name) (fun path ->
                Expect.isError (Configuration.parse [|"--config"; path|]) "invalid name")

    testCase "TOML comments empty tables literal paths and inline tables are supported" <| fun _ ->
        withFile "# defaults\n" (fun path -> Expect.equal (parsed path) Configuration.defaults "Comments-only TOML keeps defaults.")
        withFile "" (fun path -> Expect.equal (parsed path) Configuration.defaults "Empty TOML keeps defaults.")
        withFile "Server = { Port = 9_001 } # inline override\n[Database]\nDatabasePath = 'C:\\Игры\\data.db'\n" (fun path ->
            match Configuration.parse [|"--config"; path|] with
            | Ok (LaunchCommand.Run(config, _)) ->
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
            | Ok (LaunchCommand.Run(config, _)) ->
                Expect.equal config.Server.Port 9001us "CLI override"
                Expect.equal config.Runtime.Player.MaxPendingChat 3 "nested override"
                Expect.equal config.Server.MaxOutgoingBytes Configuration.defaults.Server.MaxOutgoingBytes "omitted budget preserved"
            | other -> failwithf "Expected valid config: %A" other)

    testCase "movement target supports automatic MTU and rejects negative values" <| fun _ ->
        withFile "[Server]\nMovementPacketTargetBytes = 900\n" (fun path ->
            match Configuration.parse [|"--config"; path|] with
            | Ok (LaunchCommand.Run(config, _)) -> Expect.equal config.Server.MovementPacketTargetBytes 900 "Configured target."
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
            | Ok (LaunchCommand.Run(config, _)) -> Expect.equal config Configuration.defaults "roundtrip all fields"
            | other -> failwithf "Cannot load exported defaults: %A" other)
    testCase "authentication requires TLS outside explicitly enabled literal loopback" <| fun _ ->
        for source in [
            "[Authentication.Listener]\nListenUrl = \"http://0.0.0.0:8779\"\n"
            "[Authentication.Listener]\nListenUrl = \"http://localhost:8779\"\n"
            "[Authentication.Listener]\nAllowInsecureLoopback = false\n"
            "[Authentication.Listener]\nListenUrl = \"https://user:secret@example.com\"\n"
            "[Authentication.Listener]\nListenUrl = \"https://example.com/auth\"\n"
            "[Authentication.Listener]\nListenUrl = 0\n"
        ] do
            withFile source (fun path ->
                Expect.isError (Configuration.parse [|"--config"; path|]) "unsafe or ambiguous binding rejected")
        withFile "[Authentication.Listener]\nListenUrl = \"https://example.com:9443\"\nAllowInsecureLoopback = false\n" (fun path ->
            Expect.isOk (Configuration.parse [|"--config"; path|]) "TLS endpoint accepted")

    testCase "remote HTTP requires explicit opt-in without weakening URL validation" <| fun _ ->
        for url in [ "http://0.0.0.0:8779"; "http://192.168.1.10:8779"; "http://auth.example.test:8779" ] do
            let source = sprintf "[Authentication.Listener]\nListenUrl = \"%s\"\nAllowInsecureLoopback = false\n" url
            withFile source (fun path ->
                Expect.isError (Configuration.parse [|"--config"; path|]) "default rejects HTTP")
            withFile (source + "AllowInsecureRemote = true\n") (fun path ->
                Expect.isOk (Configuration.parse [|"--config"; path|]) "explicit opt-in accepts HTTP")
        withFile "[Authentication.Listener]\nListenUrl = \"http://user:secret@example.test\"\nAllowInsecureRemote = true\n" (fun path ->
            Expect.isError (Configuration.parse [|"--config"; path|]) "credentials in URL remain forbidden")

    testCase "account and logging limits and wrong section types fail before startup" <| fun _ ->
        for source in [
            "Authentication = 0\n"; "Database = 0\n"; "Logging = 0\n"
            "[Authentication]\nService = 0\n"
            "[Authentication.Service]\nMaxConcurrentOperations = 0\n"
            "[Authentication.Service]\nPasswordIterations = 1\n"
            "[Authentication.Listener]\nRequestsPerMinute = 0\n"
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
            | Ok (LaunchCommand.Run(config, _)) -> Expect.equal config.Runtime.Presence.VisibilityDistance 0.0f "Zero is valid."
            | other -> failwithf "%A" other)
        withFile "[Runtime.Presence]\nVisibilityDistance = -1\n" (fun path ->
            Expect.isError (Configuration.parse [|"--config"; path|]) "Invalid distance rejected before startup.")
    testCase "identity settings load from TOML with defaults and are validated" <| fun _ ->
        let defaults = Configuration.defaults.Identity
        Expect.isTrue defaults.AllowHiddenIdentity "allowed by default"
        Expect.equal defaults.ToggleIntervalMs 30000 "30 s between switches"
        Expect.equal defaults.PseudonymsPath "pseudonyms.toml" "next to moderation.toml"
        withFile "[Identity]\nAllowHiddenIdentity = false\nToggleIntervalMs = 0\n" (fun path ->
            match Configuration.parse [|"--config"; path|] with
            | Ok (LaunchCommand.Run(config, _)) ->
                Expect.isFalse config.Identity.AllowHiddenIdentity "switched off"
                Expect.equal config.Identity.ToggleIntervalMs 0 "no limit"
                Expect.equal config.Identity.PseudonymsPath "pseudonyms.toml" "default path kept"
            | other -> failtestf "%A" other)
        for invalid in [ "[Identity]\nToggleIntervalMs = -1\n"; "[Identity]\nAllowHiddenIdentity = 1\n"; "[Identity]\nPseudonyms = 'x'\n" ] do
            withFile invalid (fun path -> Expect.isError (Configuration.parse [|"--config"; path|]) $"refused: {invalid}")

    testCase "the pseudonym file skips invalid entries and never stops the server" <| fun _ ->
        match Configuration.parsePseudonyms "version = 1\nnames = ['Бард', ' Страж', 'бард', '<b>x</b>', 7, 'Рыбак']\n" with
        | Ok (dictionary, skipped) ->
            Expect.equal (dictionary.Names |> List.map Dreamsleeve.Server.Domain.Pseudonym.value) [ "Бард"; "Рыбак" ] "valid, first spelling"
            Expect.equal skipped 4 "invalid and repeated entries counted"
        | Error error -> failtest error
        for invalid in [ "names = []\n"; "names = [' ']\n"; "version = 2\nnames = ['Бард']\n"; "names = 'Бард'\n"; "other = 1\nnames = ['Бард']\n"; "names = ['Бард'\n" ] do
            Expect.isError (Configuration.parsePseudonyms invalid) $"refused: {invalid}"
        let builtIn = Dreamsleeve.Server.Domain.PseudonymDictionary.builtIn
        let missing, warning = Configuration.loadPseudonyms { Configuration.defaults.Identity with PseudonymsPath = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid()}.toml") }
        Expect.equal missing.Names builtIn.Names "missing file uses the built-in list"
        Expect.isSome warning "with a warning"
        withFile "names = ['Бард'\n" (fun path ->
            let broken, warning = Configuration.loadPseudonyms { Configuration.defaults.Identity with PseudonymsPath = path }
            Expect.equal broken.Names builtIn.Names "broken file uses the built-in list"
            Expect.isSome warning "with a warning")
        withFile "version = 1\nnames = ['Бард']\n" (fun path ->
            let loaded, warning = Configuration.loadPseudonyms { Configuration.defaults.Identity with PseudonymsPath = path }
            Expect.equal (loaded.Names |> List.map Dreamsleeve.Server.Domain.Pseudonym.value) [ "Бард" ] "file replaces the list"
            Expect.isNone warning "no warning")

    testCase "bundled pseudonym example equals the built-in list" <| fun _ ->
        let rec find (directory: DirectoryInfo) =
            let candidate = Path.Combine(directory.FullName, "src", "Dreamsleeve.Server", "pseudonyms.example.toml")
            if File.Exists candidate then candidate
            elif isNull directory.Parent then failtest "pseudonyms.example.toml not found"
            else find directory.Parent
        let path = find (DirectoryInfo AppContext.BaseDirectory)
        match Configuration.parsePseudonyms (File.ReadAllText path) with
        | Ok (dictionary, 0) -> Expect.equal dictionary.Names Dreamsleeve.Server.Domain.PseudonymDictionary.builtIn.Names "same 24 names"
        | other -> failtestf "%A" other
    testCase "the admin panel section has loopback defaults and an old server.toml without it keeps them" <| fun _ ->
        let admin = Configuration.defaults.Admin
        Expect.isTrue admin.Enabled "enabled by default"
        Expect.equal admin.Listener.ListenUrl "http://127.0.0.1:8780" "loopback only"
        Expect.isFalse admin.Listener.TrustForwardedHeaders "forwarded headers are not trusted"
        Expect.isFalse Configuration.defaults.Authentication.Listener.TrustForwardedHeaders "same for authentication"
        withFile "[Authentication.Service]\nSetupLifetimeHours = 24\n" (fun path ->
            Expect.equal (parsed path).Admin admin "a file without [Admin] gets the defaults")
        // Who may register is the run-time registration mode, not a setting.
        withFile "[Authentication]\nAllowRegistration = false\n" (fun path ->
            Expect.isError (Configuration.parse [|"--config"; path|]) "AllowRegistration is an unknown setting")
        withFile "[Admin]\nEnabled = false\n[Admin.Listener]\nListenUrl = \"http://0.0.0.0:8780\"\n" (fun path ->
            Expect.isOk (Configuration.parse [|"--config"; path|]) "a disabled panel is not validated")

    testCase "the admin panel refuses remote HTTP without permission, a shared port and bad limits" <| fun _ ->
        for source in [
            "[Admin.Listener]\nListenUrl = \"http://0.0.0.0:8780\"\n"
            "[Admin.Listener]\nListenUrl = \"http://admin.example.test:8780\"\n"
            "[Admin.Listener]\nListenUrl = \"http://127.0.0.1:8779\"\n"
            "[Admin.Listener]\nListenUrl = \"https://example.test/admin\"\n"
            "[Admin.Listener]\nAllowInsecureLoopback = false\n"
            "[Admin.Service]\nSessionHours = 0\n"
            "[Admin.Service]\nCodeLifetimeMinutes = 61\n"
            "[Admin.Service]\nLoginAttemptsPerMinute = 0\n"
            "[Admin.Service]\nPasswordIterations = 1\n"
            "[Admin.Listener]\nRequestsPerMinute = 0\n"
            "[Admin.Listener]\nTrustForwardedHeaders = 1\n"
            "[Admin]\nMaxConnections = 0\n"
            "[Admin]\nPassword = \"x\"\n"
        ] do
            withFile source (fun path ->
                Expect.isError (Configuration.parse [|"--config"; path|]) $"refused: {source}")
        for source in [
            "[Admin.Listener]\nListenUrl = \"http://0.0.0.0:8780\"\nAllowInsecureRemote = true\n"
            "[Admin.Listener]\nListenUrl = \"https://admin.example.test:9443\"\n"
            "[Admin.Listener]\nListenUrl = \"http://[::1]:8780\"\nTrustForwardedHeaders = true\n"
        ] do
            withFile source (fun path ->
                Expect.isOk (Configuration.parse [|"--config"; path|]) $"accepted: {source}")

    testCase "restart settings are checked and become the supervisor's policy" <| fun _ ->
        withFile "[Recovery]\nInitialDelayMs = 500\nMaxDelayMs = 4000\nMaxRestarts = 0\nWindowSeconds = 60\n" (fun path ->
            let policy = Configuration.restartPolicy (parsed path).Recovery
            Expect.equal policy { InitialDelay = TimeSpan.FromMilliseconds 500.; MaxDelay = TimeSpan.FromSeconds 4.; MaxRestarts = 0; Window = TimeSpan.FromMinutes 1. }
                "0 restarts: the first failure stops the server")
        for invalid in [ "InitialDelayMs = -1"; "MaxDelayMs = 10\nInitialDelayMs = 20"; "MaxDelayMs = 3600001"
                         "MaxRestarts = -1"; "MaxRestarts = 1001"; "WindowSeconds = 0"; "WindowSeconds = 86401" ] do
            withFile $"[Recovery]\n{invalid}\n" (fun path ->
                Expect.isError (Configuration.parse [|"--config"; path|]) $"refused: {invalid}")

    testCase "the bundled server example names every setting with its default value" <| fun _ ->
        let path = example ()
        Expect.equal (parsed path) Configuration.defaults "every value in the example equals the default"
        let written = Tomlyn.TomlSerializer.Deserialize<Tomlyn.Model.TomlTable>(File.ReadAllText path)
        let defaults = Tomlyn.TomlSerializer.Deserialize<Tomlyn.Model.TomlTable>(Configuration.render Configuration.defaults)
        Expect.equal (keys "" written |> List.sort) (keys "" defaults |> List.sort) "the example names every setting"
]
