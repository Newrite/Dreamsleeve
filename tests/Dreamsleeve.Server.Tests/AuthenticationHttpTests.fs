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
open Expecto
open AgentTests
open BackgroundTests

let private ok = function Ok value -> value | Error error -> failwithf "%A" error
let private password = "Boundary-Password-2026!"
let private profile = PlayerData.create (PlayerId.create 42UL |> ok)
                          (Username.create 32 "player" |> ok) (DisplayName.create 64 "Player" |> ok)
let private ticket = String('a', 43)
let private signedIn = Ok (AccountAccessResult.SignedIn { Profile = profile; SessionTicket = ticket; ExpiresInSeconds = 60; RememberToken = "" })

let private moderation = Moderation.create { Words = ["badword"]; Substrings = []; Exceptions = [] }

let private withHost customize execute run = task {
    let received = ConcurrentQueue<AccountAccessCommand>()
    let handle (_: AgentContext<AuthMessage>) message = task {
        match message with
        | AuthMessage.Access(command, reply) ->
            received.Enqueue command
            execute command reply
        | AuthMessage.Start | AuthMessage.Finished _ | AuthMessage.ConsumeTicket _
        | AuthMessage.WorkersStopped _ | AuthMessage.SetChangeTarget _ | AuthMessage.ChangeFailed _ | AuthMessage.Stop
        | AuthMessage.ChangeDisplayName _ | AuthMessage.Moderate _ -> failwith "Unexpected test authentication control."
    }
    use auth = Agent.Start(AgentOptions.create "http-test-auth", handle)
    use logger = Serilog.LoggerConfiguration().MinimumLevel.Fatal().CreateLogger()
    let initial = {
        Configuration.defaults with
            Authentication = { Configuration.defaults.Authentication with
                                   Listener = { Configuration.defaults.Authentication.Listener with ListenUrl = "http://127.0.0.1:0" } }
    }
    let settings = customize initial
    // The host exactly as Program builds it: the same settings mapping and ports.
    let app = AuthRoutes.build (WebPorts.authListener settings) (WebPorts.authRoutes settings) moderation (WebPorts.auth auth) logger
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

let private reply result _ (response: ReplyChannel<_>) = response.Reply result
let private credentials = {| username = "player"; password = password |}
let private post (http: HttpClient) (path: string) body = http.PostAsJsonAsync(path, body)
let private status expected (response: HttpResponseMessage) = equal expected (int response.StatusCode)
let private code (response: HttpResponseMessage) = task {
    let! bytes = response.Content.ReadAsByteArrayAsync()
    use body = JsonDocument.Parse(ReadOnlyMemory<byte>(bytes))
    return body.RootElement.GetProperty("code").GetString()
}

let tests = testSequenced (testList "Authentication HTTP" [
    case "remember resume logout and reset map to dedicated public commands" (fun () ->
        let execute command (response: ReplyChannel<_>) =
            match command with
            | AccountAccessCommand.RememberLogin _ | AccountAccessCommand.Resume _ -> response.Reply signedIn
            | AccountAccessCommand.Logout _ | AccountAccessCommand.ResetPassword _ -> response.Reply (Ok AccountAccessResult.Completed)
            | AccountAccessCommand.Register _ | AccountAccessCommand.Login _
            | AccountAccessCommand.CreatePasswordReset _ | AccountAccessCommand.RevokeAccount _
            | AccountAccessCommand.RenamePlayer _ | AccountAccessCommand.ChangeOwnDisplayName _
            | AccountAccessCommand.Sanction _ | AccountAccessCommand.LiftSanction _
            | AccountAccessCommand.Kick _ | AccountAccessCommand.ListSanctions | AccountAccessCommand.RecordModeration _ -> failtest "Unexpected public command"
        withHost id execute (fun http received -> task {
            use! remembered = post http "auth/login" {| username = "player"; password = password; rememberMe = true |}
            status 200 remembered
            use! resumed = post http "auth/resume" {| token = ticket |}
            status 200 resumed
            use! loggedOut = post http "auth/logout" {| token = ticket |}
            status 204 loggedOut
            use! reset = post http "auth/reset-password" {| code = ticket; password = password |}
            status 204 reset
            equal [| AccountAccessCommand.RememberLogin(Username.create 32 "player" |> ok, password)
                     AccountAccessCommand.Resume ticket; AccountAccessCommand.Logout ticket
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
            | AccountAccessCommand.RenamePlayer _ | AccountAccessCommand.ChangeOwnDisplayName _
            | AccountAccessCommand.Sanction _ | AccountAccessCommand.LiftSanction _
            | AccountAccessCommand.Kick _ | AccountAccessCommand.ListSanctions | AccountAccessCommand.RecordModeration _ -> failtest "Unexpected command"
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
            | AccountAccessCommand.Register(username, displayName, actualPassword) ->
                equal "player" (Username.value username)
                equal "é" (DisplayName.value displayName)
                equal password actualPassword
            | AccountAccessCommand.Login _ | AccountAccessCommand.RememberLogin _ | AccountAccessCommand.Resume _ | AccountAccessCommand.Logout _
            | AccountAccessCommand.ResetPassword _ | AccountAccessCommand.CreatePasswordReset _ | AccountAccessCommand.RevokeAccount _
            | AccountAccessCommand.RenamePlayer _ | AccountAccessCommand.ChangeOwnDisplayName _
            | AccountAccessCommand.Sanction _ | AccountAccessCommand.LiftSanction _
            | AccountAccessCommand.Kick _ | AccountAccessCommand.ListSanctions | AccountAccessCommand.RecordModeration _ -> failwith "Wrong registration command."

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

    case "disabled registration rejects before contacting the service" (fun () ->
        let disable config = { config with Authentication = { config.Authentication with AllowRegistration = false } }
        withHost disable (reply signedIn) (fun http received -> task {
            use! response = post http "auth/register" {| username = "player"; displayName = "Player"; password = password |}
            status 403 response
            let! actual = code response
            equal "registration_disabled" actual
            equal 0 received.Count
        }))

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
