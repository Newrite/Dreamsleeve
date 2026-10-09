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
        // The hidden player has a card; the rest of it is not served here.
        | AdminCommand.FindPlayer playerId when PlayerId.value playerId = 7UL ->
            let stored = PlayerData.create playerId (Username.create 32 "alice.real" |> ok) (DisplayName.create 64 "Алиса Настоящая" |> ok) NameColor.unknown
            Ok (AdminReply.Player(Some { Profile = stored; Role = PlayerRole.Player }))
        | AdminCommand.IssueSetupCode | AdminCommand.IssueResetCode _ | AdminCommand.ResetPassword _ | AdminCommand.CreateApiToken _
        | AdminCommand.ListApiTokens | AdminCommand.RevokeApiToken _ | AdminCommand.SetRole _ | AdminCommand.SearchPlayers _
        | AdminCommand.FindPlayer _ -> Error AdminServiceError.Unavailable
        | AdminCommand.NameHistory _ -> Ok (AdminReply.Names [])
        | AdminCommand.PlayerSanctions _ -> Ok (AdminReply.Sanctions [])
        | AdminCommand.ActiveSanctions -> Ok (AdminReply.ActiveSanctions [])

// One hidden player online; its real names only the panel may show.
let private hiddenRow = {
    ConnectionId = Guid.NewGuid(); Address = IPAddress.Parse "203.0.113.7"; Proxy = None; PlayerId = Some (PlayerId.create 7UL |> ok)
    Phase = RuntimeSessionPhase.Ready
    ConnectedAt = DateTimeOffset.UtcNow; Session = None
}

let private hiddenView =
    let stored = PlayerData.create (PlayerId.create 7UL |> ok) (Username.create 32 "alice.real" |> ok) (DisplayName.create 64 "Алиса Настоящая" |> ok) NameColor.unknown
    let player = Player.create stored |> Player.beginCharacter (CharacterName.create 64 "<b>Героиня</b>" |> ok)
    AdminPlayerView.create stored player false (ValueSome (Pseudonym.create "Страж" |> ok)) HiddenIdentity.Everywhere PlayerRole.Player
        AdminSessionPhase.Active DateTimeOffset.UtcNow

type private Panel = {
    Http: HttpClient
    Cookies: CookieContainer
    Admin: FakeAdmin
    Announcements: ConcurrentQueue<ServerAnnouncement>
    /// Trusted account commands the panel sent, answered by AccountReply.
    Accounts: ConcurrentQueue<AccountAccessCommand>
    AccountReply: (AccountAccessCommand -> Result<AccountAccessResult, AccountAccessError>) ref
    /// Requests to the guild owner, answered by GuildReply.
    Guilds: ConcurrentQueue<GuildAdminCommand>
    GuildReply: (GuildAdminCommand -> GuildAdminResult) ref
    AdminReply: (AdminCommand -> Result<AdminReply, AdminServiceError>) ref
    SnapshotReply: AgentAskResult<ServerRuntimeSnapshot> ref
    SessionsReply: AgentAskResult<RuntimeSessionRow list> ref
    DescribeReply: (RuntimeSessionRow -> Result<AdminPlayerView option, SessionDescribeError>) ref
    Logs: ConcurrentQueue<Serilog.Events.LogEvent>
}

let private withPanel customize run = task {
    let admin = FakeAdmin()
    let announcements = ConcurrentQueue<ServerAnnouncement>()
    let accounts = ConcurrentQueue<AccountAccessCommand>()
    let accountReply = ref (function
        | AccountAccessCommand.AddressHistory _ -> Ok (AccountAccessResult.Addresses [])
        | AccountAccessCommand.DeviceHistory _ -> Ok (AccountAccessResult.Devices [])
        | _ -> Error AccountAccessError.Unavailable)
    let guildCommands = ConcurrentQueue<GuildAdminCommand>()
    let guildReply = ref (function
        | GuildAdminCommand.PlayerGuilds _ -> GuildAdminResult.PlayerGuilds []
        | _ -> GuildAdminResult.Refused GuildError.NotFound)
    let adminReply = ref admin.Handle
    let snapshotReply: AgentAskResult<ServerRuntimeSnapshot> ref = ref (AgentAskResult.Replied { Connections = 1; Guests = 0; Ready = 1; Reservations = 1; Closing = 0; Stopping = false })
    let sessionsReply = ref (AgentAskResult.Replied [ hiddenRow ])
    let describeReply = ref (fun (row: RuntimeSessionRow) -> Ok (if row.ConnectionId = hiddenRow.ConnectionId then Some hiddenView else None))
    let logs = ConcurrentQueue<Serilog.Events.LogEvent>()
    let ports = {
        Admin = fun command _ _ -> Task.FromResult(AgentAskResult.Replied(adminReply.Value command))
        Account = fun command _ _ ->
            accounts.Enqueue command
            Task.FromResult(AgentAskResult.Replied(accountReply.Value command))
        Snapshot = fun _ _ -> Task.FromResult snapshotReply.Value
        Guilds = fun command _ _ ->
            guildCommands.Enqueue command
            Task.FromResult(AgentAskResult.Replied(guildReply.Value command))
        Sessions = fun _ _ -> Task.FromResult sessionsReply.Value
        Describe = fun _ row -> Task.FromResult(describeReply.Value row)
        Announce = fun announcement -> announcements.Enqueue announcement; true
        ApplyRole = fun _ _ -> true
        ApplyProfile = fun _ -> true
        Configuration = fun () -> []
    }
    let sink = { new Serilog.Core.ILogEventSink with member _.Emit entry = logs.Enqueue entry }
    use logger = Serilog.LoggerConfiguration().MinimumLevel.Debug().WriteTo.Sink(sink).CreateLogger()
    let panel = Configuration.defaults.Admin
    let config = customize { Configuration.defaults with Admin = { panel with Listener = { panel.Listener with ListenUrl = "http://127.0.0.1:0" } } }
    let app = AdminRoutes.build (WebPorts.adminListener config) (WebPorts.adminRoutes config Moderation.empty) ports logger
    let! outcome = task {
        try
            do! app.StartAsync()
            let cookies = CookieContainer()
            use handler = new HttpClientHandler(CookieContainer = cookies, UseCookies = true, AllowAutoRedirect = false)
            use http = new HttpClient(handler, BaseAddress = Uri(Seq.head app.Urls), Timeout = guard)
            do! run { Http = http; Cookies = cookies; Admin = admin; Announcements = announcements; Accounts = accounts; AccountReply = accountReply
                      Guilds = guildCommands; GuildReply = guildReply; AdminReply = adminReply; SnapshotReply = snapshotReply
                      SessionsReply = sessionsReply; DescribeReply = describeReply; Logs = logs }
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

let private api panel (path: string) = task {
    use request = new HttpRequestMessage(HttpMethod.Get, path)
    request.Headers.Authorization <- Headers.AuthenticationHeaderValue("Bearer", apiToken)
    return! panel.Http.SendAsync request
}

let tests = testSequenced (testList "Admin HTTP" [
    case "description absence, timeout and original fault remain distinct from available player data" (fun () ->
        withPanel id (fun panel -> task {
            let missing = { hiddenRow with ConnectionId = Guid.NewGuid(); PlayerId = None; Phase = RuntimeSessionPhase.Guest }
            let slow = { hiddenRow with ConnectionId = Guid.NewGuid(); PlayerId = None }
            let failed = { hiddenRow with ConnectionId = Guid.NewGuid(); PlayerId = None }
            let failure = InvalidOperationException("description-owner-fault")
            panel.SessionsReply.Value <- AgentAskResult.Replied [ hiddenRow; missing; slow; failed ]
            panel.DescribeReply.Value <- fun row ->
                if row.ConnectionId = hiddenRow.ConnectionId then Ok (Some hiddenView)
                elif row.ConnectionId = missing.ConnectionId then Ok None
                elif row.ConnectionId = slow.ConnectionId then Error SessionDescribeError.TimedOut
                else Error (SessionDescribeError.Faulted failure)
            use! response = api panel "/api/v1/online"
            status 200 response
            let! body = response.Content.ReadAsStringAsync()
            use json = JsonDocument.Parse body
            let rows = json.RootElement.EnumerateArray() |> Seq.map (fun row -> row.GetProperty("connectionId").GetString(), row) |> dict
            for id, expected in [ hiddenRow.ConnectionId, "available"; missing.ConnectionId, "not_open"; slow.ConnectionId, "unavailable"; failed.ConnectionId, "unavailable" ] do
                let row = rows[string id]
                equal expected (row.GetProperty("descriptionStatus").GetString())
                equal (expected = "available") (row.GetProperty("described").GetBoolean())
            check (panel.Logs.ToArray() |> Array.exists (fun entry -> obj.ReferenceEquals(entry.Exception, failure))) "Original owner fault is logged, not replaced."
        }))

    case "runtime failure cannot claim empty sessions, offline players or a completed range check" (fun () ->
        withPanel id (fun panel -> task {
            do! signIn panel
            panel.SessionsReply.Value <- AgentAskResult.Closed
            use! online = api panel "/api/v1/online"
            status 503 online
            use! card = panel.Http.GetAsync "/players/7"
            status 503 card
            use! range = submit panel "/address-bans/check" [ "range", "203.0.113.0/24"; "term", ""; "reason", "Проверка" ] []
            status 503 range
            use! overview = panel.Http.GetAsync "/"
            status 200 overview
            let! html = overview.Content.ReadAsStringAsync()
            check (html.Contains "Онлайн (недоступно)" && not (html.Contains "Онлайн (0)")) "Unavailable does not assert zero online."
            let original = InvalidOperationException("snapshot-owner-fault")
            panel.SnapshotReply.Value <- AgentAskResult.Faulted original
            use! snapshot = api panel "/api/v1/status"
            status 503 snapshot
            check (panel.Logs.ToArray() |> Array.exists (fun entry -> obj.ReferenceEquals(entry.Exception, original))) "Original runtime fault is logged."
        }))

    case "required card histories distinguish genuine empty replies from failures and wrong reply shapes" (fun () ->
        withPanel id (fun panel -> task {
            do! signIn panel
            use! empty = api panel "/api/v1/players/7"
            status 200 empty
            let! body = empty.Content.ReadAsStringAsync()
            use json = JsonDocument.Parse body
            for field in [ "names"; "sanctions"; "addresses"; "devices"; "guilds" ] do
                equal 0 (json.RootElement.GetProperty(field).GetArrayLength())
            let original = panel.AdminReply.Value
            panel.AdminReply.Value <- function AdminCommand.NameHistory _ -> Error AdminServiceError.Unavailable | command -> original command
            use! names = api panel "/api/v1/players/7"
            status 503 names
            panel.AdminReply.Value <- function AdminCommand.NameHistory _ -> Ok AdminReply.Completed | command -> original command
            use! wrong = panel.Http.GetAsync "/players/7"
            status 503 wrong
            panel.AdminReply.Value <- original
            panel.AccountReply.Value <- function AccountAccessCommand.AddressHistory _ -> Error AccountAccessError.Unavailable | AccountAccessCommand.DeviceHistory _ -> Ok (AccountAccessResult.Devices []) | _ -> Error AccountAccessError.Unavailable
            use! addresses = api panel "/api/v1/players/7"
            status 503 addresses
            panel.AccountReply.Value <- function AccountAccessCommand.AddressHistory _ -> Ok (AccountAccessResult.Addresses []) | AccountAccessCommand.DeviceHistory _ -> Ok (AccountAccessResult.Devices []) | _ -> Error AccountAccessError.Unavailable
            panel.GuildReply.Value <- fun _ -> GuildAdminResult.Refused GuildError.NotFound
            use! guilds = api panel "/api/v1/players/7"
            status 503 guilds
        }))

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

    case "the registration page changes the mode and creates a player with a one-time code, both audited" (fun () ->
        withPanel id (fun panel -> task {
            let mode = ref RegistrationMode.Open
            let created = PlayerData.create (PlayerId.create 9UL |> ok) (Username.create 32 "newcomer" |> ok) (DisplayName.create 64 "Новичок" |> ok) NameColor.unknown
            panel.AccountReply.Value <- (function
                | AccountAccessCommand.ReadRegistration -> Ok (AccountAccessResult.Registration mode.Value)
                | AccountAccessCommand.SetRegistration(next, _) ->
                    mode.Value <- next
                    Ok (AccountAccessResult.Registration next)
                | AccountAccessCommand.CreateAccount(username, _) when Username.value username = "taken" -> Error AccountAccessError.UsernameTaken
                | AccountAccessCommand.CreateAccount _ -> Ok (AccountAccessResult.AccountCreated(created, setupCode))
                | _ -> Error AccountAccessError.Unavailable)
            do! signIn panel
            use! page = panel.Http.GetAsync "/registration"
            status 200 page
            use! unconfirmed = submit panel "/registration/mode" [ "mode", "manual" ] []
            status 400 unconfirmed
            use! unknown = submit panel "/registration/mode" [ "mode", "closed"; "confirm", "yes" ] []
            status 400 unknown
            use! changed = submit panel "/registration/mode" [ "mode", "manual"; "confirm", "yes" ] []
            status 303 changed
            equal RegistrationMode.Manual mode.Value
            // The panel stamps the change with the administrator.
            check (panel.Accounts.ToArray() |> Array.contains (AccountAccessCommand.SetRegistration(RegistrationMode.Manual, ValueSome root.Id))) "Mode change carries the admin."
            use! reserved = submit panel "/registration/players" [ "username", "hidden.3"; "displayName", "Кто-то"; "confirm", "yes" ] []
            status 400 reserved
            use! taken = submit panel "/registration/players" [ "username", "taken"; "displayName", "Кто-то"; "confirm", "yes" ] []
            status 409 taken
            use! made = submit panel "/registration/players" [ "username", " NewComer "; "displayName", "Новичок"; "confirm", "yes" ] []
            status 200 made
            check made.Headers.CacheControl.NoStore "The page with the code is not cached."
            let! html = made.Content.ReadAsStringAsync()
            check (html.Contains setupCode) "The setup code is shown once."
            check (panel.Accounts.ToArray() |> Array.contains (AccountAccessCommand.CreateAccount(Username.create 32 "newcomer" |> ok, DisplayName.create 64 "Новичок" |> ok)))
                "The canonical names reach the account service."
            let audit = panel.Admin.Audit.ToArray() |> Array.map (fun (_, entry) -> entry.Action, AuditTarget.key entry.Target, entry.Details)
            equal [| AdminAction.SetRegistrationMode, "server", "manual"; AdminAction.CreatedPlayer, "player:9", "newcomer" |] audit
        }))

    case "an IP range ban is first checked against whom it would hit, then confirmed and lifted" (fun () ->
        withPanel id (fun panel -> task {
            let bans = ref []
            let range = AddressRange.parse "203.0.113.0/24" |> ok
            let reason = SanctionReason.create "Рейд" |> ok
            let ban : AddressBan = { Id = 5L; Range = range; Reason = reason; IssuedBy = ValueSome root.Id; IssuedAt = DateTimeOffset.UtcNow; Expires = ValueNone }
            let bob = PlayerData.create (PlayerId.create 8UL |> ok) (Username.create 32 "bob" |> ok) (DisplayName.create 64 "Боб" |> ok) NameColor.unknown
            let seen : SignInAddress = { Address = IPAddress.Parse "203.0.113.9"; FirstSeen = DateTimeOffset.UtcNow; LastSeen = DateTimeOffset.UtcNow; SignIns = 3L }
            panel.AccountReply.Value <- (function
                | AccountAccessCommand.ListAddressBans -> Ok (AccountAccessResult.AddressBans bans.Value)
                | AccountAccessCommand.PlayersInRange _ -> Ok (AccountAccessResult.PlayersAt [ ({ Player = bob; Address = seen } : AddressMatch) ])
                | AccountAccessCommand.BanAddresses _ ->
                    bans.Value <- [ ban ]
                    Ok (AccountAccessResult.AddressesBanned ban)
                | AccountAccessCommand.LiftAddressBan(5L, _) when not bans.Value.IsEmpty ->
                    bans.Value <- []
                    Ok (AccountAccessResult.AddressBanLifted ban)
                | AccountAccessCommand.LiftAddressBan _ -> Error (AccountAccessError.SanctionRefused SanctionError.NotActive)
                | _ -> Error AccountAccessError.Unavailable)
            do! signIn panel
            use! page = panel.Http.GetAsync "/address-bans?range=203.0.113.7"
            status 200 page
            use! wide = submit panel "/address-bans/check" [ "range", "203.0.0.0/7"; "term", ""; "reason", "Рейд" ] []
            status 400 wide
            use! checkedRange = submit panel "/address-bans/check" [ "range", "203.0.113.77/24"; "term", ""; "reason", "Рейд" ] []
            status 200 checkedRange
            let! html = checkedRange.Content.ReadAsStringAsync()
            // The hidden player is online from 203.0.113.7; bob signed in from the range before.
            check (html.Contains "203.0.113.0/24" && html.Contains "alice.real" && html.Contains "Боб") "The check shows whom the ban would hit."
            use! unconfirmed = submit panel "/address-bans" [ "range", "203.0.113.0/24"; "term", ""; "reason", "Рейд" ] []
            status 400 unconfirmed
            use! confirmed = submit panel "/address-bans" [ "range", "203.0.113.0/24"; "term", ""; "reason", "Рейд"; "confirm", "yes" ] []
            status 303 confirmed
            check (panel.Accounts.ToArray() |> Array.contains (AccountAccessCommand.BanAddresses(range, reason, SanctionTerm.UntilLifted, root.Id))) "The order reaches the account service."
            use! listed = panel.Http.GetAsync "/address-bans"
            let! listing = listed.Content.ReadAsStringAsync()
            check (listing.Contains "/address-bans/5/lift") "The ban is listed with its lift form."
            use! lifted = submit panel "/address-bans/5/lift" [ "confirm", "yes" ] []
            status 303 lifted
            use! again = submit panel "/address-bans/5/lift" [ "confirm", "yes" ] []
            status 409 again
        }))

    case "a ban from the player card covers the devices only when the box is ticked" (fun () ->
        withPanel id (fun panel -> task {
            panel.AccountReply.Value <- (function
                | AccountAccessCommand.Sanction order ->
                    Ok (AccountAccessResult.Sanctioned (Sanction.issue (SanctionId.create 1L |> ok) DateTimeOffset.UtcNow order))
                | _ -> Error AccountAccessError.Unavailable)
            do! signIn panel
            let fields devices = [ "kind", "ban"; "term", ""; "reason", "Спам"; "confirm", "yes" ] @ (if devices then [ "devices", "yes" ] else [])
            use! plain = submit panel "/players/7/sanction" (fields false) []
            status 303 plain
            use! withDevices = submit panel "/players/7/sanction" (fields true) []
            status 303 withDevices
            let orders = panel.Accounts.ToArray() |> Array.choose (function AccountAccessCommand.Sanction order -> Some order.Devices | _ -> None)
            equal [| false; true |] orders
        }))

    case "guild pages go through the guild owner: a new master or a dissolution is audited, and the player card lists the guilds" (fun () ->
        withPanel id (fun panel -> task {
            let pid raw = PlayerId.create raw |> ok
            let guild = GuildId.create 5UL |> ok
            let name = GuildName.create 1 64 "Вороны" |> ok
            let at = DateTimeOffset.UtcNow
            let profile raw username display = PlayerData.create (pid raw) (Username.create 32 username |> ok) (DisplayName.create 64 display |> ok) NameColor.unknown
            let bob = profile 8UL "bob" "<i>Боб</i>"
            let carol = profile 9UL "carol" "Кэрол"
            let membership (player: PlayerData) role : GuildMember = { Player = player.PlayerId; Role = role; JoinedAt = at; Mute = ValueNone }
            // The master's account is gone: the guild waits for the panel.
            let summary master : GuildSummary = { Guild = guild; Name = name; CreatedAt = at; Members = 2; Master = master }
            let card bobRole master : GuildCard =
                { Summary = summary master
                  Members = [ { Membership = membership bob bobRole; Profile = bob; Online = true }
                              { Membership = membership carol GuildRole.Officer; Profile = carol; Online = false } ]
                  Invites = [] }
            panel.GuildReply.Value <- (function
                | GuildAdminCommand.Search _ -> GuildAdminResult.Page { Guilds = [ summary ValueNone ]; Total = 1; Page = 1 }
                | GuildAdminCommand.Card id when id = guild -> GuildAdminResult.Card(ValueSome(card GuildRole.Member ValueNone))
                | GuildAdminCommand.Card _ -> GuildAdminResult.Card ValueNone
                | GuildAdminCommand.PlayerGuilds _ -> GuildAdminResult.PlayerGuilds [ summary ValueNone, GuildRole.Officer ]
                | GuildAdminCommand.Appoint(_, player) when player = pid 10UL -> GuildAdminResult.Refused GuildError.TargetNotFound
                | GuildAdminCommand.Appoint _ -> GuildAdminResult.Appointed(card GuildRole.Master (ValueSome bob))
                | GuildAdminCommand.Dissolve _ ->
                    GuildAdminResult.Dissolved { Guild = guild; Name = name; Members = [ membership bob GuildRole.Master; membership carol GuildRole.Officer ]; Invites = [] })
            do! signIn panel
            use! list = panel.Http.GetAsync "/guilds?q=вор"
            status 200 list
            let! listing = list.Content.ReadAsStringAsync()
            check (listing.Contains "Вороны" && listing.Contains "/guilds/5" && listing.Contains "нет — назначьте") "The list shows the guild without a master."
            check (panel.Guilds.ToArray() |> Array.contains (GuildAdminCommand.Search("вор", 1))) "The search reaches the guild owner."
            use! page = panel.Http.GetAsync "/guilds/5"
            status 200 page
            let! html = page.Content.ReadAsStringAsync()
            check (html.Contains "&lt;i&gt;Боб&lt;/i&gt;" && not (html.Contains "<i>Боб")) "Member names are encoded."
            check (html.Contains "/guilds/5/appoint" && html.Contains "/guilds/5/dissolve") "The card offers both actions."
            use! missing = panel.Http.GetAsync "/guilds/6"
            status 404 missing
            use! unconfirmed = submit panel "/guilds/5/appoint" [ "player", "8" ] []
            status 400 unconfirmed
            use! stranger = submit panel "/guilds/5/appoint" [ "player", "10"; "confirm", "yes" ] []
            status 409 stranger
            use! appointed = submit panel "/guilds/5/appoint" [ "player", "8"; "confirm", "yes" ] []
            status 303 appointed
            equal "/guilds/5?done=appointed" appointed.Headers.Location.OriginalString
            use! dissolved = submit panel "/guilds/5/dissolve" [ "confirm", "yes" ] []
            status 303 dissolved
            equal "/guilds?done=dissolved" dissolved.Headers.Location.OriginalString
            let audit = panel.Admin.Audit.ToArray() |> Array.map (fun (_, entry) -> entry.Action, AuditTarget.key entry.Target, entry.Details)
            equal [| AdminAction.AppointedGuildMaster, "guild:5", "Вороны: player:8 <i>Боб</i>"; AdminAction.DissolvedGuild, "guild:5", "Вороны, участников: 2" |] audit

            use request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/guilds/5")
            request.Headers.Authorization <- Headers.AuthenticationHeaderValue("Bearer", apiToken)
            use! api = panel.Http.SendAsync request
            status 200 api
            let! json = api.Content.ReadAsStringAsync()
            let root = JsonDocument.Parse(json).RootElement
            equal "Вороны" (root.GetProperty("guild").GetProperty("name").GetString())
            equal JsonValueKind.Null (root.GetProperty("guild").GetProperty("masterId").ValueKind)
            let members = root.GetProperty("members")
            equal "officer" (members[1].GetProperty("role").GetString())
            check (not (members[0].GetProperty("muted").GetBoolean())) "Nobody is muted."

            use! player = panel.Http.GetAsync "/players/7"
            status 200 player
            let! card = player.Content.ReadAsStringAsync()
            check (card.Contains "/guilds/5" && card.Contains "офицер") "The player card lists the guild with the role."
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
