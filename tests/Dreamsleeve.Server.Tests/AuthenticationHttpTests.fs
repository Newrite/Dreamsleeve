module Dreamsleeve.Server.Tests.AuthenticationHttpTests

open System
open System.Collections.Concurrent
open System.IO
open System.Net.Http
open System.Net.Sockets
open System.Net.Http.Json
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Dreamsleeve.Agent
open Dreamsleeve.Server
open Dreamsleeve.Server.Domain
open Dreamsleeve.Server.Infrastructure
open Dreamsleeve.Server.Web.Authentication
open Dreamsleeve.Server.Web
open Microsoft.AspNetCore.Http
open Expecto
open AgentTests
open BackgroundTests

let private ok = function Ok value -> value | Error error -> failwithf "%A" error
let private password = "Boundary-Password-2026!"
let private profile = PlayerData.create (PlayerId.create 42UL |> ok)
                          (Username.create 32 "player" |> ok) (DisplayName.create 64 "Player" |> ok) NameColor.unknown
let private ticket = String('a', 43)
let private signedIn = Ok (AccountAccessResult.SignedIn { Profile = profile; SessionTicket = ticket; ExpiresInSeconds = 60; RememberToken = "" })

let private moderation = Moderation.create { Words = ["badword"]; Substrings = []; Exceptions = [] }

// steam replaces the Steam ports of the composition root.
let private withHostUsingPorts (alter: AuthPorts -> AuthPorts) (logs: ConcurrentQueue<Serilog.Events.LogEvent>) (steam: SteamPorts option) customize execute run = task {
    let received = ConcurrentQueue<AccountAccessCommand>()
    let handle (_: AgentContext<AuthMessage>) message = task {
        match message with
        | AuthMessage.Access(command, reply) ->
            received.Enqueue command
            execute command reply
        | AuthMessage.Start | AuthMessage.Finished _ | AuthMessage.ConsumeTicket _
        | AuthMessage.WorkersStopped _ | AuthMessage.SetChangeTarget _ | AuthMessage.ChangeFailed _ | AuthMessage.Stop
        | AuthMessage.ChangeProfile _ | AuthMessage.Moderate _ -> failwith "Unexpected test authentication control."
    }
    use auth = Agent.Start(AgentOptions.create "http-test-auth", handle)
    let sink = { new Serilog.Core.ILogEventSink with member _.Emit entry = logs.Enqueue entry }
    use logger = Serilog.LoggerConfiguration().MinimumLevel.Debug().WriteTo.Sink(sink).CreateLogger()
    let initial = {
        Configuration.defaults with
            Authentication = { Configuration.defaults.Authentication with
                                   Listener = { Configuration.defaults.Authentication.Listener with ListenUrl = "http://127.0.0.1:0" } }
    }
    let settings = customize initial
    // The host exactly as Program builds it: the same settings mapping and ports.
    let ports = WebPorts.auth settings auth
    let ports = (match steam with Some steam -> { ports with Steam = steam } | None -> ports) |> alter
    let app = AuthRoutes.buildWithPhantoms (fun () -> None) 128 (WebPorts.authListener settings) (WebPorts.authRoutes settings) moderation ports logger
    let! outcome = task {
        try
            do! app.StartAsync()
            use http = new HttpClient(BaseAddress = Uri(Seq.head app.Urls), Timeout = guard)
            do! run http received
            return Ok ()
        with failure -> return Error failure
    }
    do! app.StopAsync()
    do! app.DisposeAsync().AsTask()
    auth.Complete() |> ignore
    do! awaitUnit auth.Completion
    match outcome with Ok () -> () | Error failure -> return raise failure
}

let private withHostUsing steam customize execute run = withHostUsingPorts id (ConcurrentQueue()) steam customize execute run
let private withHost customize execute run = withHostUsing None customize execute run

let private reply result _ (response: ReplyChannel<_>) = response.Reply result
let private credentials = {| username = "player"; password = password |}
let private post (http: HttpClient) (path: string) body = http.PostAsJsonAsync(path, body)
let private status expected (response: HttpResponseMessage) = equal expected (int response.StatusCode)
let private code (response: HttpResponseMessage) = task {
    let! bytes = response.Content.ReadAsByteArrayAsync()
    use body = JsonDocument.Parse(ReadOnlyMemory<byte>(bytes))
    return body.RootElement.GetProperty("code").GetString()
}

type private FailedBody(error: exn) =
    inherit MemoryStream()
    override _.ReadAsync(_: Memory<byte>, _: CancellationToken) = ValueTask<int>(Task.FromException<int> error)

let tests = testSequenced (testList "Authentication HTTP" [
    case "body dependency adapters retain I/O faults and only classify cancellation with an owning token" (fun () -> task {
        let original = IOException("request-stream-failure")
        let context = DefaultHttpContext()
        use body = new FailedBody(original)
        context.Request.Body <- body
        let! io = WebHost.readBody context 4096 CancellationToken.None
        match io with
        | Error (WebHost.BodyError.Io actual) -> check (obj.ReferenceEquals(original, actual)) "Original stream I/O error is retained."
        | other -> failtestf "Expected I/O failure, received %A" other
        for statusCode, expected in [ 413, WebHost.BodyError.TooLarge; 400, WebHost.BodyError.BadRequest ] do
            use bad = new FailedBody(BadHttpRequestException("bad framing", statusCode))
            context.Request.Body <- bad
            let! framing = WebHost.readBody context 4096 CancellationToken.None
            match framing with Error actual -> equal expected actual | Ok _ -> failtest "Invalid framing was accepted."
        use canceled = new CancellationTokenSource()
        canceled.Cancel()
        use cancelBody = new FailedBody(OperationCanceledException())
        context.Request.Body <- cancelBody
        let! deadline = WebHost.readBody context 4096 canceled.Token
        match deadline with Error WebHost.BodyError.Deadline -> () | other -> failtestf "%A" other
        context.RequestAborted <- canceled.Token
        let! caller = WebHost.readBody context 4096 canceled.Token
        match caller with Error WebHost.BodyError.CallerCanceled -> () | other -> failtestf "%A" other
        context.RequestAborted <- CancellationToken.None
        let unexplained = OperationCanceledException("unowned cancellation")
        use unexpectedBody = new FailedBody(unexplained)
        context.Request.Body <- unexpectedBody
        let work = WebHost.readBody context 4096 CancellationToken.None
        let! fault = terminal work
        check (fault |> Option.exists (fun actual -> obj.ReferenceEquals(unexplained, actual))) "Unowned cancellation remains a lifetime fault."
    })

    case "deferred JSON Unicode decoding is validated before authentication admission" (fun () ->
        withHost id (reply signedIn) (fun http received -> task {
            for json in [
                """{"username":"pl\uD800ayer","password":"Boundary-Password-2026!"}"""
                """{"username":"player","password":"Boundary-\uD800Password-2026!"}"""
                """{"username":"player","password":"Boundary-Password-2026!","device":"\uD800"}"""
                """{"us\uD800ername":"player","password":"Boundary-Password-2026!"}"""
                """{"username":"player","password":"Boundary-Password-2026!","extra":{"\uD800":["value"]}}"""
                """{"username":"player","password":"Boundary-Password-2026!","extra":["\uDC00"]}"""
            ] do
                // Parse accepts these escapes; decoding names/string values is deferred.
                use parsed = JsonDocument.Parse json
                equal JsonValueKind.Object parsed.RootElement.ValueKind
                use content = new StringContent(json, Encoding.UTF8, "application/json")
                use! response = http.PostAsync("auth/login", content)
                status 400 response
                let! actual = code response
                equal "invalid_request" actual
                equal 0 received.Count
            use valid = new StringContent("""{"username":"player","password":"Boundary-\uD83D\uDE00-Password-2026!","extra":{"\uD83D\uDE00":["\uD83D\uDE00"]}}""", Encoding.UTF8, "application/json")
            use! accepted = http.PostAsync("auth/login", valid)
            status 200 accepted
            equal 1 received.Count
        }))

    case "typed owner failure returns unavailable but unexpected port exceptions remain Kestrel failures" (fun () -> task {
        for original in [ InvalidOperationException("unexpected-account-port") :> exn; JsonException("unexpected-business-json") :> exn ] do
            let logs = ConcurrentQueue<Serilog.Events.LogEvent>()
            let alter ports = { ports with Access = fun _ _ _ -> Task.FromException<_> original }
            do! withHostUsingPorts alter logs None id (reply signedIn) (fun http received -> task {
                use! response = post http "auth/login" credentials
                status 500 response
                equal 0 received.Count
                check (logs.ToArray() |> Array.exists (fun entry -> obj.ReferenceEquals(original, entry.Exception))) "Kestrel retains the original unexpected fault."
            })
        let original = InvalidOperationException("typed-account-owner-fault")
        let logs = ConcurrentQueue<Serilog.Events.LogEvent>()
        let alter ports = { ports with Access = fun _ _ _ -> Task.FromResult(AgentAskResult.Faulted original) }
        do! withHostUsingPorts alter logs None id (reply signedIn) (fun http received -> task {
            use! response = post http "auth/login" credentials
            status 503 response
            let! actual = code response
            equal "unavailable" actual
            equal 0 received.Count
            check (logs.ToArray() |> Array.exists (fun entry -> obj.ReferenceEquals(original, entry.Exception))) "Typed owner failure preserves the original diagnostic."
        })
    })

    case "remember resume logout and reset map to dedicated public commands" (fun () ->
        let execute command (response: ReplyChannel<_>) =
            match command with
            | AccountAccessCommand.RememberLogin _ | AccountAccessCommand.Resume _ -> response.Reply signedIn
            | AccountAccessCommand.Logout _ | AccountAccessCommand.ResetPassword _ -> response.Reply (Ok AccountAccessResult.Completed)
            | AccountAccessCommand.Register _ | AccountAccessCommand.Login _
            | AccountAccessCommand.CreatePasswordReset _ | AccountAccessCommand.RevokeAccount _
            | AccountAccessCommand.RenamePlayer _ | AccountAccessCommand.ChangeOwnDisplayName _ | AccountAccessCommand.ChangeOwnNameColor _
            | AccountAccessCommand.Sanction _ | AccountAccessCommand.LiftSanction _
            | AccountAccessCommand.Kick _ | AccountAccessCommand.ListSanctions | AccountAccessCommand.RecordModeration _
            | AccountAccessCommand.CreateAccount _ | AccountAccessCommand.ReadRegistration | AccountAccessCommand.SetRegistration _
            | AccountAccessCommand.BanAddresses _ | AccountAccessCommand.LiftAddressBan _ | AccountAccessCommand.ListAddressBans
            | AccountAccessCommand.AddressHistory _ | AccountAccessCommand.PlayersInRange _ | AccountAccessCommand.DeviceHistory _
            | AccountAccessCommand.BeginSteam _ | AccountAccessCommand.CompleteSteam _ | AccountAccessCommand.PollSteam _
            | AccountAccessCommand.SignInSteam _ -> failtest "Unexpected public command"
        withHost id execute (fun http received -> task {
            use! remembered = post http "auth/login" {| username = "player"; password = password; rememberMe = true |}
            status 200 remembered
            use! resumed = post http "auth/resume" {| token = ticket |}
            status 200 resumed
            use! loggedOut = post http "auth/logout" {| token = ticket |}
            status 204 loggedOut
            use! reset = post http "auth/reset-password" {| code = ticket; password = password |}
            status 204 reset
            // The host passes the client address it saw: loopback here.
            let local = SignInOrigin.ofAddress Net.IPAddress.Loopback
            equal [| AccountAccessCommand.RememberLogin(Username.create 32 "player" |> ok, password, local)
                     AccountAccessCommand.Resume(ticket, local); AccountAccessCommand.Logout ticket
                     AccountAccessCommand.ResetPassword(ticket, password) |] (received.ToArray())
            use! invalidRemember = post http "auth/login" {| username = "player"; password = password; rememberMe = "true" |}
            status 400 invalidRemember
            use! admin = post http "auth/create-password-reset" {| username = "player" |}
            status 404 admin
        }))

    case "register and login validate domain input and return profiles without credential diagnostics" (fun () ->
        let execute command (response: ReplyChannel<_>) =
            match command with
            | AccountAccessCommand.Register _ -> response.Reply(Ok (AccountAccessResult.Registered profile))
            | AccountAccessCommand.Login _ -> response.Reply signedIn
            | AccountAccessCommand.RememberLogin _ | AccountAccessCommand.Resume _ | AccountAccessCommand.Logout _
            | AccountAccessCommand.ResetPassword _ | AccountAccessCommand.CreatePasswordReset _ | AccountAccessCommand.RevokeAccount _
            | AccountAccessCommand.RenamePlayer _ | AccountAccessCommand.ChangeOwnDisplayName _ | AccountAccessCommand.ChangeOwnNameColor _
            | AccountAccessCommand.Sanction _ | AccountAccessCommand.LiftSanction _
            | AccountAccessCommand.Kick _ | AccountAccessCommand.ListSanctions | AccountAccessCommand.RecordModeration _
            | AccountAccessCommand.CreateAccount _ | AccountAccessCommand.ReadRegistration | AccountAccessCommand.SetRegistration _
            | AccountAccessCommand.BanAddresses _ | AccountAccessCommand.LiftAddressBan _ | AccountAccessCommand.ListAddressBans
            | AccountAccessCommand.AddressHistory _ | AccountAccessCommand.PlayersInRange _ | AccountAccessCommand.DeviceHistory _
            | AccountAccessCommand.BeginSteam _ | AccountAccessCommand.CompleteSteam _ | AccountAccessCommand.PollSteam _
            | AccountAccessCommand.SignInSteam _ -> failtest "Unexpected command"
        withHost id execute (fun http received -> task {
            use! created = post http "auth/register" {|
                username = " PLAYER "; displayName = " e\u0301 "; password = password
            |}
            status 201 created
            let! createdText = created.Content.ReadAsStringAsync()
            check (not (createdText.Contains password)) "Registration response exposed the password."
            use body = JsonDocument.Parse createdText
            equal 42UL (body.RootElement.GetProperty("playerId").GetUInt64())
            match received.ToArray()[0] with
            | AccountAccessCommand.Register(username, displayName, actualPassword, _) ->
                equal "player" (Username.value username)
                equal "é" (DisplayName.value displayName)
                equal password actualPassword
            | AccountAccessCommand.Login _ | AccountAccessCommand.RememberLogin _ | AccountAccessCommand.Resume _ | AccountAccessCommand.Logout _
            | AccountAccessCommand.ResetPassword _ | AccountAccessCommand.CreatePasswordReset _ | AccountAccessCommand.RevokeAccount _
            | AccountAccessCommand.RenamePlayer _ | AccountAccessCommand.ChangeOwnDisplayName _ | AccountAccessCommand.ChangeOwnNameColor _
            | AccountAccessCommand.Sanction _ | AccountAccessCommand.LiftSanction _
            | AccountAccessCommand.Kick _ | AccountAccessCommand.ListSanctions | AccountAccessCommand.RecordModeration _
            | AccountAccessCommand.CreateAccount _ | AccountAccessCommand.ReadRegistration | AccountAccessCommand.SetRegistration _
            | AccountAccessCommand.BanAddresses _ | AccountAccessCommand.LiftAddressBan _ | AccountAccessCommand.ListAddressBans
            | AccountAccessCommand.AddressHistory _ | AccountAccessCommand.PlayersInRange _ | AccountAccessCommand.DeviceHistory _
            | AccountAccessCommand.BeginSteam _ | AccountAccessCommand.CompleteSteam _ | AccountAccessCommand.PollSteam _
            | AccountAccessCommand.SignInSteam _ -> failwith "Wrong registration command."

            use! loggedIn = post http "auth/login" credentials
            status 200 loggedIn
            check loggedIn.Headers.CacheControl.NoStore "Ticket response can be cached."
            let! loginText = loggedIn.Content.ReadAsStringAsync()
            use login = JsonDocument.Parse loginText
            equal ticket (login.RootElement.GetProperty("sessionTicket").GetString())
            equal 60 (login.RootElement.GetProperty("expiresInSeconds").GetInt32())
        }))

    case "registration refuses listed names and reserved placeholders before the auth agent" (fun () ->
        let execute command (response: ReplyChannel<_>) =
            match command with
            | AccountAccessCommand.Login _ -> response.Reply signedIn
            | _ -> failtest "A refused name reached the auth agent"
        withHost id execute (fun http received -> task {
            for username, displayName, expected in [ "bad_word_x", "Fine", "username_not_allowed"
                                                     "hidden.7", "Fine", "username_not_allowed"
                                                     "Server", "Fine", "username_not_allowed"
                                                     "system", "Fine", "username_not_allowed"
                                                     "steam.76561198000000000", "Fine", "username_not_allowed"
                                                     "player", "B4DW0RD", "display_name_not_allowed" ] do
                use! response = post http "auth/register" {| username = username; displayName = displayName; password = password |}
                status 400 response
                let! actual = code response
                equal expected actual
            equal 0 received.Count
            // Sign-in never re-checks names: stored accounts keep working.
            use! login = post http "auth/login" {| username = "badword"; password = password |}
            status 200 login
        }))

    case "malformed input and oversized known or chunked bodies never reach the auth agent" (fun () ->
        withHost id (reply signedIn) (fun http received -> task {
            for input in [ "{"; "null"; "[]"; "{}"; "{\"username\":42,\"password\":\"Boundary-Password-2026!\"}"
                           "{\"username\":\"player\",\"password\":\"short\"}" ] do
                use content = new StringContent(input, Encoding.UTF8, "application/json")
                use! response = http.PostAsync("auth/login", content)
                status 400 response
            use unsupported = new StringContent("test")
            use! unsupportedResponse = http.PostAsync("auth/login", unsupported)
            status 415 unsupportedResponse
            // Kestrel can reject before a client finishes writing an oversized body.
            // A raw request reads that response without HttpClient's concurrent writer race.
            for framing, body in [
                "Content-Length: 4097", ""
                "Transfer-Encoding: chunked", "1001\r\n" + String('x', 4097) + "\r\n0\r\n\r\n"
            ] do
                use client = new TcpClient()
                do! client.ConnectAsync(http.BaseAddress.Host, http.BaseAddress.Port)
                use stream = client.GetStream()
                let request = Encoding.ASCII.GetBytes(
                    "POST /auth/login HTTP/1.1\r\nHost: localhost\r\nContent-Type: application/json\r\n" + framing + "\r\n\r\n" + body)
                do! stream.WriteAsync(request).AsTask()
                let bytes = Array.zeroCreate<byte> 4096
                let! count = stream.ReadAsync(bytes).AsTask().WaitAsync guard
                let response = Encoding.ASCII.GetString(bytes, 0, count)
                check (response.StartsWith "HTTP/1.1 413") "Oversized body was not rejected."
            equal 0 received.Count
        }))

    case "service errors have stable safe HTTP responses" (fun () -> task {
        for failure, expectedStatus, expectedCode in [
            AccountAccessError.InvalidCredentials, 401, "invalid_credentials"
            AccountAccessError.UsernameTaken, 409, "username_taken"
            AccountAccessError.Busy, 503, "busy"
            AccountAccessError.Unavailable, 503, "unavailable"
        ] do
            do! withHost id (reply (Error failure)) (fun http _ -> task {
                use! response = post http "auth/login" credentials
                status expectedStatus response
                let! actual = code response
                equal expectedCode actual
            })
    })

    case "a closed registration answers 403 with the code of the mode in force" (fun () -> task {
        for mode, expected in [ RegistrationMode.Manual, "registration_closed"; RegistrationMode.Steam, "registration_steam_only" ] do
            do! withHost id (reply (Error (AccountAccessError.RegistrationClosed mode))) (fun http received -> task {
                use! response = post http "auth/register" {| username = "player"; displayName = "Player"; password = password |}
                status 403 response
                let! actual = code response
                equal expected actual
                // The account service owns the mode: the route asks it every time.
                equal 1 received.Count
            })
    })

    case "a banned address answers 403 with the reason and the end, like an account ban" (fun () ->
        let ban : AddressBan = {
            Id = 1L; Range = AddressRange.parse "127.0.0.0/8" |> ok; Reason = SanctionReason.create "Рейд" |> ok; IssuedBy = ValueNone
            IssuedAt = DateTimeOffset.UtcNow; Expires = ValueSome (DateTimeOffset.FromUnixTimeMilliseconds 1_800_000_000_000L)
        }
        withHost id (reply (Error (AccountAccessError.AddressBanned ban))) (fun http _ -> task {
            use! response = post http "auth/login" credentials
            status 403 response
            let! text = response.Content.ReadAsStringAsync()
            use body = JsonDocument.Parse text
            equal "address_banned" (body.RootElement.GetProperty("code").GetString())
            equal "Рейд" (body.RootElement.GetProperty("reason").GetString())
            equal 1_800_000_000_000L (body.RootElement.GetProperty("untilUnixMs").GetInt64())
        }))

    case "a device hash reaches the service with the address; a malformed one is refused; a banned device answers 403" (fun () ->
        let hash = String.replicate 4 "0123456789abcdef"
        let execute command (response: ReplyChannel<_>) =
            match command with
            | AccountAccessCommand.Login(_, _, origin) when origin.Device.IsSome -> response.Reply signedIn
            | _ -> failtest "Unexpected command"
        task {
            do! withHost id execute (fun http received -> task {
                use! accepted = post http "auth/login" {| username = "player"; password = password; device = hash |}
                status 200 accepted
                match received.ToArray() with
                | [| AccountAccessCommand.Login(_, _, origin) |] ->
                    equal (ValueSome (DeviceId.create hash |> ok)) origin.Device
                    equal (ValueSome Net.IPAddress.Loopback) origin.Address
                | other -> failtestf "%A" other
                for wrong in [ box (hash.ToUpperInvariant()); box "short"; box 42 ] do
                    use! refused = post http "auth/login" {| username = "player"; password = password; device = wrong |}
                    status 400 refused
                equal 1 received.Count
            })
            let ban = Sanction.issue (SanctionId.create 3L |> ok) DateTimeOffset.UtcNow
                          { Target = PlayerId.create 9UL |> ok; Kind = SanctionKind.Ban; Term = SanctionTerm.UntilLifted
                            Reason = SanctionReason.create "Спам" |> ok; IssuedBy = SanctionIssuer.Admin(AdminId.create 1L |> ok); Devices = true }
            do! withHost id (reply (Error (AccountAccessError.DeviceBanned ban))) (fun http _ -> task {
                use! response = post http "auth/register" {| username = "fresh"; displayName = "Fresh"; password = password; device = hash |}
                status 403 response
                let! actual = code response
                equal "device_banned" actual
            })
        })

    case "Steam sign-in starts a flow with Steam's URL, is polled by the client and is off unless enabled" (fun () -> task {
        let flow, secret = String('f', 43), String('s', 43)
        let steamOn (config: ApplicationConfig) =
            { config with Authentication = { config.Authentication with Steam = { Enabled = true; PublicUrl = "http://127.0.0.1:8779/"; ProxyUrls = [] } } }
        let execute command (response: ReplyChannel<_>) =
            match command with
            | AccountAccessCommand.BeginSteam(origin, true) when origin.Address.IsSome -> response.Reply(Ok (AccountAccessResult.SteamStarted(flow, secret, 600)))
            | AccountAccessCommand.BeginSteam(_, false) -> response.Reply(Ok (AccountAccessResult.SteamStarted(flow, secret, 600)))
            | AccountAccessCommand.PollSteam(f, s) when f = flow && s = secret -> response.Reply(Ok AccountAccessResult.SteamPending)
            | AccountAccessCommand.PollSteam _ -> response.Reply(Error AccountAccessError.FlowUnknown)
            | AccountAccessCommand.ReadRegistration -> response.Reply(Ok (AccountAccessResult.Registration RegistrationMode.Steam))
            | _ -> failtest "Unexpected command"
        do! withHost steamOn execute (fun http received -> task {
            use! started = post http "auth/steam/begin" {| device = String.replicate 4 "0123456789abcdef" |}
            status 200 started
            let! text = started.Content.ReadAsStringAsync()
            use body = JsonDocument.Parse text
            equal flow (body.RootElement.GetProperty("flow").GetString())
            equal secret (body.RootElement.GetProperty("secret").GetString())
            let url = body.RootElement.GetProperty("browserUrl").GetString()
            check (url.StartsWith "https://steamcommunity.com/openid/login?") "The browser goes to Steam."
            check (url.Contains(Uri.EscapeDataString $"http://127.0.0.1:8779/auth/steam/return?flow={flow}")) "Steam returns to this server's flow."
            check (url.Contains "openid.realm=http%3A%2F%2F127.0.0.1%3A8779&") "The realm is the public origin."
            use! once = post http "auth/steam/begin" {| rememberMe = false |}
            status 200 once
            use! pending = post http "auth/steam/poll" {| flow = flow; secret = secret |}
            status 202 pending
            use! unknown = post http "auth/steam/poll" {| flow = flow; secret = String('x', 43) |}
            status 410 unknown
            use! methods = http.GetAsync "auth/methods"
            status 200 methods
            let! methodsText = methods.Content.ReadAsStringAsync()
            use offered = JsonDocument.Parse methodsText
            equal "steam" (offered.RootElement.GetProperty("registration").GetString())
            check (offered.RootElement.GetProperty("steam").GetBoolean()) "Steam is offered."
            match received.ToArray() |> Array.head with
            | AccountAccessCommand.BeginSteam(origin, _) -> check origin.Device.IsSome "The device goes into the flow."
            | other -> failtestf "%A" other
        })
        do! withHost id execute (fun http received -> task {
            use! off = post http "auth/steam/begin" {| |}
            status 404 off
            let! actual = code off
            equal "steam_disabled" actual
            use! methods = http.GetAsync "auth/methods"
            let! methodsText = methods.Content.ReadAsStringAsync()
            use offered = JsonDocument.Parse methodsText
            check (not (offered.RootElement.GetProperty("steam").GetBoolean())) "Steam is not offered."
            equal [| AccountAccessCommand.ReadRegistration |] (received.ToArray())
        })
    })

    case "the browser's return is verified with Steam, named from a clean persona and answered with a page" (fun () -> task {
        let flow = String('f', 43)
        let steamOn (config: ApplicationConfig) =
            { config with Authentication = { config.Authentication with Steam = { Enabled = true; PublicUrl = "http://127.0.0.1:8779"; ProxyUrls = [] } } }
        let steamId = 76561198000000042UL
        let persona = ref "Довакин"
        let steam = {
            Verify = fun f fields _ ->
                let mode = fields |> List.tryFind (fun (key, _) -> key = "openid.mode") |> Option.map snd
                Task.FromResult(if f = flow && mode = Some "id_res" then Ok steamId elif mode = Some "cancel" then Error "canceled" else Error "bad")
            Profile = fun id _ -> Task.FromResult { SteamId = id; PersonaName = ValueSome persona.Value; Created = ValueNone }
        }
        let execute command (response: ReplyChannel<_>) =
            match command with
            | AccountAccessCommand.CompleteSteam(f, _, _) when f = flow -> response.Reply signedIn
            | AccountAccessCommand.CompleteSteam _ -> response.Reply(Error AccountAccessError.FlowUnknown)
            | _ -> failtest "Unexpected command"
        do! withHostUsing (Some steam) steamOn execute (fun http received -> task {
            use! done' = http.GetAsync $"auth/steam/return?flow={flow}&openid.mode=id_res"
            status 200 done'
            let! page = done'.Content.ReadAsStringAsync()
            check (page.Contains "Вернитесь в игру") "The page sends the player back to the game."
            check (done'.Content.Headers.ContentType.MediaType = "text/html") "A page for the browser."
            persona.Value <- "badword fan"
            use! clean = http.GetAsync $"auth/steam/return?flow={flow}&openid.mode=id_res"
            status 200 clean
            match received.ToArray() with
            | [| AccountAccessCommand.CompleteSteam(_, profile, first); AccountAccessCommand.CompleteSteam(_, _, second) |] ->
                equal steamId profile.SteamId
                equal "Довакин" (DisplayName.value first)
                equal "Steam 0042" (DisplayName.value second)
            | other -> failtestf "%A" other
            use! canceled = http.GetAsync $"auth/steam/return?flow={flow}&openid.mode=cancel"
            let! canceledPage = canceled.Content.ReadAsStringAsync()
            check (canceledPage.Contains "отменён") "A canceled sign-in says so."
            use! forged = http.GetAsync "auth/steam/return?flow=short&openid.mode=id_res"
            status 400 forged
            equal 2 received.Count
        })
    })

    case "per-IP rate limit has no waiting queue and ignores spoofed forwarded addresses" (fun () ->
        let limit config = { config with Authentication = { config.Authentication with Listener = { config.Authentication.Listener with RequestsPerMinute = 1 } } }
        withHost limit (reply signedIn) (fun http received -> task {
            use! first = post http "auth/login" credentials
            status 200 first
            http.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.7")
            use! second = post http "auth/login" credentials
            status 429 second
            let! actual = code second
            equal "rate_limited" actual
            equal 1 received.Count
        }))

    case "a proxy of the server speaks for its players; anyone else's forwarded address is ignored" (fun () ->
        let resolve trustLoopback proxies (peer: string) (forwarded: string list) =
            let ranges = proxies |> List.map (fun (range: string) -> AddressRange.parse range |> ok)
            let found = Dreamsleeve.Server.Web.Forwarding.resolve trustLoopback ranges (Net.IPAddress.Parse peer) forwarded
            found.Client |> ValueOption.map string, found.Proxy |> ValueOption.map string
        let proxies = [ "203.0.113.200" ]
        // A stranger's header is the stranger's own words.
        equal (ValueSome "198.51.100.9", ValueNone) (resolve false proxies "198.51.100.9" [ "192.0.2.1" ])
        // Through the proxy: the client it names; entries left of it are spoofable.
        equal (ValueSome "192.0.2.1", ValueSome "203.0.113.200") (resolve false proxies "203.0.113.200" [ "10.0.0.1, 192.0.2.1" ])
        equal (ValueSome "192.0.2.1", ValueSome "203.0.113.200") (resolve false proxies "::ffff:203.0.113.200" [ "192.0.2.1" ])
        // A proxy that names nobody: the client is unknown.
        equal (ValueNone, ValueSome "203.0.113.200") (resolve false proxies "203.0.113.200" [])
        equal (ValueNone, ValueSome "203.0.113.200") (resolve false proxies "203.0.113.200" [ "garbage" ])
        // A local proxy in front of the server, then the server's proxy.
        equal (ValueSome "192.0.2.1", ValueSome "203.0.113.200") (resolve true proxies "127.0.0.1" [ "192.0.2.1, 203.0.113.200" ])
        equal (ValueSome "203.0.113.200", ValueNone) (resolve true [] "127.0.0.1" [ "192.0.2.1, 203.0.113.200" ])
        equal (ValueSome "127.0.0.1", ValueNone) (resolve false proxies "127.0.0.1" [ "192.0.2.1" ])

        let throughProxy config =
            { config with Proxies = { Trusted = [ "127.0.0.1" ] }
                          Authentication = { config.Authentication with
                                                 Steam = { Enabled = true; PublicUrl = "https://auth.example.org"
                                                           ProxyUrls = [ "https://proxy.example.org" ] } } }
        let execute command (response: ReplyChannel<_>) =
            match command with
            | AccountAccessCommand.BeginSteam _ -> response.Reply (Ok (AccountAccessResult.SteamStarted(String('f', 43), String('s', 43), 300)))
            | _ -> response.Reply signedIn
        withHost throughProxy execute (fun http received -> task {
            use forwarded = new HttpRequestMessage(HttpMethod.Post, "auth/login", Content = JsonContent.Create credentials)
            forwarded.Headers.Add("X-Forwarded-For", "192.0.2.1")
            use! answer = http.SendAsync forwarded
            status 200 answer
            use! anonymous = post http "auth/login" credentials
            status 200 anonymous
            let origins =
                received.ToArray() |> Array.map (function
                    | AccountAccessCommand.Login(_, _, origin) -> origin.Address |> ValueOption.map string, origin.Proxy |> ValueOption.map string
                    | other -> failtestf "%A" other)
            equal [| ValueSome "192.0.2.1", ValueSome "127.0.0.1"; ValueNone, ValueSome "127.0.0.1" |] origins
            // A Steam sign-in begun through the proxy returns there, any other to the server.
            let steamUrl (host: string) = task {
                use request = new HttpRequestMessage(HttpMethod.Post, "auth/steam/begin", Content = JsonContent.Create {| |})
                request.Headers.Host <- host
                use! response = http.SendAsync request
                status 200 response
                let! body = response.Content.ReadFromJsonAsync<JsonElement>()
                return Uri.UnescapeDataString(body.GetProperty("browserUrl").GetString())
            }
            let! viaProxy = steamUrl "proxy.example.org"
            check (viaProxy.Contains "openid.return_to=https://proxy.example.org/auth/steam/return") viaProxy
            check (viaProxy.Contains "openid.realm=https://proxy.example.org&") viaProxy
            let! direct = steamUrl "localhost"
            check (direct.Contains "openid.return_to=https://auth.example.org/auth/steam/return") direct
        }))

    case "request deadline includes a client that never finishes its HTTP body" (fun () ->
        let limit config = { config with Authentication = { config.Authentication with Listener = { config.Authentication.Listener with RequestTimeoutSeconds = 1 } } }
        withHost limit (reply signedIn) (fun http received -> task {
            use client = new TcpClient()
            do! client.ConnectAsync(http.BaseAddress.Host, http.BaseAddress.Port)
            use stream = client.GetStream()
            let request = Encoding.ASCII.GetBytes("POST /auth/login HTTP/1.1\r\nHost: localhost\r\nContent-Type: application/json\r\nContent-Length: 100\r\n\r\n{")
            do! stream.WriteAsync(request).AsTask()
            let bytes = Array.zeroCreate<byte> 4096
            let! count = stream.ReadAsync(bytes).AsTask().WaitAsync guard
            let response = Encoding.ASCII.GetString(bytes, 0, count)
            check (response.StartsWith "HTTP/1.1 503") "Slow body escaped the request deadline."
            equal 0 received.Count
        }))

    case "HTTP timeout completes without stopping the authentication dependency" (fun () ->
        let limit config = { config with Authentication = { config.Authentication with Listener = { config.Authentication.Listener with RequestTimeoutSeconds = 1 } } }
        withHost limit (fun _ _ -> ()) (fun http received -> task {
            use! response = post http "auth/login" credentials
            status 503 response
            let! actual = code response
            equal "unavailable" actual
            equal 1 received.Count
        }))
])
