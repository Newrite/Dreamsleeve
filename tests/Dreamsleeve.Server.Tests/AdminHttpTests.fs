module Dreamsleeve.Server.Tests.AdminHttpTests

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Net
open System.Net.Http
open System.Text.Json
open System.Threading.Tasks
open Dreamsleeve.Agent
open Dreamsleeve.Server
open Dreamsleeve.Server.Core
open Dreamsleeve.Server.Domain
open Dreamsleeve.Server.Infrastructure
open Dreamsleeve.Server.Web.Admin
open Expecto
open AgentTests
open BackgroundTests

let private ok = function Ok value -> value | Error error -> failwithf "%A" error
let private setupCode = String('s', 43)
let private apiToken = String('t', 43)
let private password = "Admin-Password-2026"
let private root = { Id = AdminId.create 1L |> ok; Username = Username.create 32 "root" |> ok }

/// The admin service as the routes see it: codes, sessions and audit in memory.
type private FakeAdmin() =
    let sessions = ConcurrentDictionary<string, AdminAccount>()
    let mutable configured = false
    member val Audit = ConcurrentQueue<AdminAccount * AuditRecord>()
    member val Commands = ConcurrentQueue<AdminCommand>()
    member _.Configure() = configured <- true
    member this.Handle(command: AdminCommand) : Result<AdminReply, AdminServiceError> =
        this.Commands.Enqueue command
        let signIn () =
            let token = Guid.NewGuid().ToString("N")
            sessions[token] <- root
            Ok (AdminReply.SignedIn(root, token, DateTimeOffset.UtcNow.AddHours 1.))
        match command with
        | AdminCommand.Status -> Ok (AdminReply.Configured configured)
        | AdminCommand.Setup(code, _, _) ->
            if configured then Error AdminServiceError.AlreadyConfigured
            elif code <> setupCode then Error AdminServiceError.InvalidCredentials
            else
                configured <- true
                signIn ()
        | AdminCommand.Login(_, secret) -> if configured && secret = password then signIn () else Error AdminServiceError.InvalidCredentials
        | AdminCommand.Authenticate token ->
            match sessions.TryGetValue token with
            | true, admin -> Ok (AdminReply.Admin admin)
            | false, _ -> Error AdminServiceError.InvalidCredentials
        | AdminCommand.AuthenticateApi token -> if token = apiToken then Ok (AdminReply.Admin root) else Error AdminServiceError.InvalidCredentials
        | AdminCommand.Logout token ->
            sessions.TryRemove token |> ignore
            Ok AdminReply.Completed
        | AdminCommand.Record(admin, entry) ->
            this.Audit.Enqueue((admin, entry))
            Ok AdminReply.Completed
        | AdminCommand.RecentAudit _ -> Ok (AdminReply.Audit [])
        | AdminCommand.IssueSetupCode | AdminCommand.IssueResetCode _ | AdminCommand.ResetPassword _ | AdminCommand.CreateApiToken _
        | AdminCommand.ListApiTokens | AdminCommand.RevokeApiToken _ | AdminCommand.SetRole _ | AdminCommand.SearchPlayers _
        | AdminCommand.FindPlayer _ | AdminCommand.NameHistory _ -> Error AdminServiceError.Unavailable

// One hidden player online; its real names only the panel may show.
let private hiddenRow = {
    ConnectionId = Guid.NewGuid(); PlayerId = Some (PlayerId.create 7UL |> ok); Phase = RuntimeSessionPhase.Ready
    ConnectedAt = DateTimeOffset.UtcNow; Session = None
}

let private hiddenView =
    let stored = PlayerData.create (PlayerId.create 7UL |> ok) (Username.create 32 "alice.real" |> ok) (DisplayName.create 64 "Алиса Настоящая" |> ok)
    let player = Player.create stored |> Player.beginCharacter (CharacterName.create 64 "<b>Героиня</b>" |> ok)
    AdminPlayerView.create stored player false (ValueSome (Pseudonym.create "Страж" |> ok)) HiddenIdentity.Everywhere PlayerRole.Player
        AdminSessionPhase.Active DateTimeOffset.UtcNow

type private Panel = {
    Http: HttpClient
    Cookies: CookieContainer
    Admin: FakeAdmin
    Announcements: ConcurrentQueue<ServerAnnouncement>
}

let private withPanel customize run = task {
    let admin = FakeAdmin()
    let announcements = ConcurrentQueue<ServerAnnouncement>()
    let ports = {
        Admin = fun command _ _ -> Task.FromResult(AgentAskResult.Replied(admin.Handle command))
        Account = fun _ _ _ -> Task.FromResult(AgentAskResult.Replied(Error AccountAccessError.Unavailable))
        Snapshot = fun _ _ -> Task.FromResult(AgentAskResult.Replied { Connections = 1; Guests = 0; Ready = 1; Reservations = 1; Closing = 0; Stopping = false })
        Sessions = fun _ _ -> Task.FromResult(AgentAskResult.Replied [ hiddenRow ])
        Describe = fun _ row -> Task.FromResult(if row.ConnectionId = hiddenRow.ConnectionId then Some hiddenView else None)
        Announce = fun announcement -> announcements.Enqueue announcement; true
        ApplyRole = fun _ _ -> true
        ApplyProfile = fun _ -> true
        Configuration = fun () -> []
    }
    use logger = Serilog.LoggerConfiguration().MinimumLevel.Fatal().CreateLogger()
    let panel = Configuration.defaults.Admin
    let config = customize { Configuration.defaults with Admin = { panel with Listener = { panel.Listener with ListenUrl = "http://127.0.0.1:0" } } }
    let app = AdminRoutes.build (WebPorts.adminListener config) (WebPorts.adminRoutes config Moderation.empty) ports logger
    let! outcome = task {
        try
            do! app.StartAsync()
            let cookies = CookieContainer()
            use handler = new HttpClientHandler(CookieContainer = cookies, UseCookies = true, AllowAutoRedirect = false)
            use http = new HttpClient(handler, BaseAddress = Uri(Seq.head app.Urls), Timeout = guard)
            do! run { Http = http; Cookies = cookies; Admin = admin; Announcements = announcements }
            return Ok ()
        with failure -> return Error failure
    }
    do! app.StopAsync()
    do! app.DisposeAsync().AsTask()
    match outcome with Ok () -> () | Error failure -> return raise failure
}

let private form (fields: (string * string) list) = new FormUrlEncodedContent(fields |> List.map KeyValuePair)

/// A browser form of this origin; extra headers replace or remove Origin.
let private submit (panel: Panel) (path: string) fields (headers: (string * string option) list) = task {
    use request = new HttpRequestMessage(HttpMethod.Post, path, Content = form fields)
    let origin = panel.Http.BaseAddress.GetLeftPart(UriPartial.Authority)
    let headers = if headers |> List.exists (fun (name, _) -> name = "Origin") then headers else ("Origin", Some origin) :: headers
    for name, value in headers do
        value |> Option.iter (fun value -> request.Headers.TryAddWithoutValidation(name, value) |> ignore)
    return! panel.Http.SendAsync request
}

let private status expected (response: HttpResponseMessage) = equal expected (int response.StatusCode)
let private setCookie (response: HttpResponseMessage) =
    match response.Headers.TryGetValues "Set-Cookie" with
    | true, values -> String.concat "\n" values
    | false, _ -> ""

let private setupFields code = [ "code", code; "username", "root"; "password", password; "password2", password ]

let private signIn panel = task {
    panel.Admin.Configure()
    use! response = submit panel "/login" [ "username", "root"; "password", password ] []
    status 303 response
}

let tests = testSequenced (testList "Admin HTTP" [
    case "setup needs the console code, happens once and signs the administrator in" (fun () ->
        withPanel id (fun panel -> task {
            use! page = panel.Http.GetAsync "/setup"
            status 200 page
            use! missing = submit panel "/setup" (setupFields "") []
            status 400 missing
            use! foreign = submit panel "/setup" (setupFields (String('x', 43))) []
            status 403 foreign
            use! created = submit panel "/setup" (setupFields setupCode) []
            status 303 created
            equal "/" (created.Headers.Location.OriginalString)
            let cookie = (setCookie created).ToLowerInvariant()
            for flag in [ "dreamsleeve_admin="; "httponly"; "samesite=strict"; "path=/" ] do
                check (cookie.Contains flag) $"Cookie lacks {flag}: {cookie}"
            check (not (cookie.Contains "secure")) "Plain HTTP on loopback cannot set a Secure cookie."
            use! second = submit panel "/setup" (setupFields setupCode) []
            status 409 second
            use! overview = panel.Http.GetAsync "/"
            status 200 overview
        }))

    case "pages need a session and every mutation needs this origin" (fun () ->
        withPanel id (fun panel -> task {
            use! anonymous = panel.Http.GetAsync "/"
            status 303 anonymous
            equal "/login" anonymous.Headers.Location.OriginalString
            use htmx = new HttpRequestMessage(HttpMethod.Get, "/partials/online")
            htmx.Headers.Add("HX-Request", "true")
            use! partial = panel.Http.SendAsync htmx
            status 401 partial
            equal "/login" (partial.Headers.GetValues "HX-Redirect" |> Seq.head)
            do! signIn panel
            let announce = [ "text", "Hello"; "kind", "admin"; "confirm", "yes" ]
            use! noOrigin = submit panel "/announce" announce [ "Origin", None ]
            status 403 noOrigin
            use! foreign = submit panel "/announce" announce [ "Origin", Some "http://evil.example" ]
            status 403 foreign
            use! fetchSite = submit panel "/announce" announce [ "Origin", None; "Sec-Fetch-Site", Some "same-origin" ]
            status 303 fetchSite
            equal 1 panel.Announcements.Count
            // What a browser sends for its own form under Referrer-Policy: no-referrer.
            use! browser = submit panel "/announce" announce [ "Origin", Some "null"; "Sec-Fetch-Site", Some "same-origin" ]
            status 303 browser
            equal 2 panel.Announcements.Count
            use! opaque = submit panel "/announce" announce [ "Origin", Some "null" ]
            status 403 opaque
            use! crossSite = submit panel "/announce" announce [ "Origin", Some "null"; "Sec-Fetch-Site", Some "cross-site" ]
            status 403 crossSite
            equal 2 panel.Announcements.Count
            use! unconfirmed = submit panel "/announce" [ "text", "Hello"; "kind", "admin" ] []
            status 400 unconfirmed
            equal 2 panel.Announcements.Count
        }))

    case "an announcement goes to the runtime with the chosen kind and is audited" (fun () ->
        withPanel id (fun panel -> task {
            do! signIn panel
            use! sent = submit panel "/announce" [ "text", "Restart <soon>"; "kind", "event"; "confirm", "yes" ] []
            status 303 sent
            let announcement = panel.Announcements.ToArray() |> Array.exactlyOne
            equal AnnouncementKind.Event announcement.Kind
            equal "Restart <soon>" (ChatMessageText.value announcement.Text)
            let admin, entry = panel.Admin.Audit.ToArray() |> Array.exactlyOne
            equal root admin
            equal AdminAction.Announced entry.Action
            equal AuditTarget.Server entry.Target
            equal "event: Restart <soon>" entry.Details
            use! periodic = submit panel "/announce" [ "text", "x"; "kind", "periodic"; "confirm", "yes" ] []
            status 400 periodic
            equal 1 panel.Announcements.Count
        }))

    case "the online table and API show the real names of a hidden player, encoded, behind authentication" (fun () ->
        withPanel id (fun panel -> task {
            use! anonymous = panel.Http.GetAsync "/api/v1/online"
            status 401 anonymous
            let! body = anonymous.Content.ReadAsStringAsync()
            equal "unauthorized" (JsonDocument.Parse(body).RootElement.GetProperty("code").GetString())
            use request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/online")
            request.Headers.Authorization <- Headers.AuthenticationHeaderValue("Bearer", apiToken)
            use! online = panel.Http.SendAsync request
            status 200 online
            check online.Headers.CacheControl.NoStore "API answers are not cached."
            let! json = online.Content.ReadAsStringAsync()
            let row = JsonDocument.Parse(json).RootElement[0]
            equal "alice.real" (row.GetProperty("username").GetString())
            equal "Алиса Настоящая" (row.GetProperty("displayName").GetString())
            equal "Страж" (row.GetProperty("pseudonym").GetString())
            equal "everywhere" (row.GetProperty("hidden").GetString())
            equal 7UL (row.GetProperty("playerId").GetUInt64())
            use wrong = new HttpRequestMessage(HttpMethod.Get, "/api/v1/status")
            wrong.Headers.Authorization <- Headers.AuthenticationHeaderValue("Bearer", String('u', 43))
            use! refused = panel.Http.SendAsync wrong
            status 401 refused
            do! signIn panel
            use! page = panel.Http.GetAsync "/"
            let! html = page.Content.ReadAsStringAsync()
            check (html.Contains "alice.real" && html.Contains "~Страж") "The table shows the real name next to the pseudonym."
            check (html.Contains "&lt;b&gt;Героиня&lt;/b&gt;" && not (html.Contains "<b>Героиня")) "Player text is encoded."
            equal "default-src 'self'; frame-ancestors 'none'; form-action 'self'; base-uri 'none'" (page.Headers.GetValues "Content-Security-Policy" |> Seq.head)
            equal "nosniff" (page.Headers.GetValues "X-Content-Type-Options" |> Seq.head)
            equal "no-referrer" (page.Headers.GetValues "Referrer-Policy" |> Seq.head)
            check page.Headers.CacheControl.NoStore "Pages are not cached."
            check (html.Contains "\"allowEval\":false" || html.Contains "&quot;allowEval&quot;:false") "htmx runs without eval."
            use! script = panel.Http.GetAsync "/static/htmx.min.js"
            status 200 script
        }))

    case "sign-in attempts are limited per address and forwarded addresses are ignored unless trusted" (fun () ->
        let limit (config: ApplicationConfig) = { config with Admin = { config.Admin with Service = { config.Admin.Service with LoginAttemptsPerMinute = 2 } } }
        let wrong = [ "username", "root"; "password", "Wrong-Password-2026" ]
        task {
            do! withPanel limit (fun panel -> task {
                for index in 1 .. 2 do
                    use! refused = submit panel "/login" wrong [ "X-Forwarded-For", Some $"203.0.113.{index}" ]
                    status 401 refused
                use! limited = submit panel "/login" wrong [ "X-Forwarded-For", Some "203.0.113.9" ]
                status 429 limited
            })
            let trusted (config: ApplicationConfig) =
                limit { config with Admin = { config.Admin with Listener = { config.Admin.Listener with TrustForwardedHeaders = true } } }
            do! withPanel trusted (fun panel -> task {
                for index in 1 .. 3 do
                    use! refused = submit panel "/login" wrong [ "X-Forwarded-For", Some $"203.0.113.{index}" ]
                    status 401 refused
                panel.Admin.Configure()
                use! secure = submit panel "/login" [ "username", "root"; "password", password ]
                                     [ "X-Forwarded-For", Some "203.0.113.20"; "X-Forwarded-Proto", Some "https"; "Origin", Some ("https://" + panel.Http.BaseAddress.Authority) ]
                status 303 secure
                check ((setCookie secure).ToLowerInvariant().Contains "secure") "A trusted HTTPS proxy makes the cookie Secure."
            })
        })
])
