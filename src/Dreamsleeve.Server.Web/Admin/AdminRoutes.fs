namespace Dreamsleeve.Server.Web.Admin

open System
open System.IO
open System.Net
open System.Reflection
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Falco
open Falco.Markup
open Falco.Routing
open Serilog
open Dreamsleeve.Agent
open Dreamsleeve.Server.Core
open Dreamsleeve.Server.Domain
open Dreamsleeve.Server.Infrastructure
open Dreamsleeve.Server.Web

/// The admin panel: SSR pages, htmx refresh of the online table and the
/// read-only REST API. Handlers only translate HTTP to port calls; every rule
/// stays with the owner behind the port (docs/TechnicalHandbookRu.MD §13, §15.5).
[<RequireQualifiedAccess>]
module AdminRoutes =
    /// HttpOnly, SameSite=Strict; Secure whenever the request arrived over HTTPS.
    [<Literal>]
    let CookieName = "dreamsleeve_admin"

    /// Forms carry at most one announcement; 64 KiB covers its encoding.
    [<Literal>]
    let MaxBodyBytes = 65536

    [<Literal>]
    let AuditLimit = 200

    type private Routes = {
        Settings: AdminRouteSettings
        Ports: AdminPorts
        Logger: ILogger
    }

    // --- Responses -------------------------------------------------------

    let private html status (node: XmlNode) : HttpHandler = fun context ->
        WebHost.noStore context
        context |> Response.withStatusCode status |> Response.ofHtml node

    let private fragment (node: XmlNode) : HttpHandler = fun context ->
        WebHost.noStore context
        context.Response.ContentType <- "text/html; charset=utf-8"
        context.Response.WriteAsync(renderNode node)

    /// 303 after a form: the browser reloads with GET and never repeats the POST.
    let private redirect (path: string) : HttpHandler = fun context ->
        WebHost.noStore context
        context.Response.StatusCode <- 303
        context.Response.Headers.Location <- path
        Task.CompletedTask

    let private apiError status code message : HttpHandler = fun context ->
        WebHost.noStore context
        WebHost.write context (WebHost.error status code message)

    let private apiJson (value: obj) : HttpHandler = fun context ->
        WebHost.noStore context
        WebHost.write context (WebHost.json 200 value)

    let private errorPage status message admin = html status (AdminViews.errorPage status message admin)

    let private serviceFailure admin error : HttpHandler =
        match error with
        | AdminServiceError.Busy -> errorPage 503 "Сервис занят, повторите позже." admin
        | AdminServiceError.RateLimited -> errorPage 429 "Слишком много попыток, подождите минуту." admin
        | AdminServiceError.NotFound -> errorPage 404 "Не найдено." admin
        | AdminServiceError.InvalidCredentials -> errorPage 401 "Требуется вход." admin
        | AdminServiceError.AlreadyConfigured -> errorPage 409 "Администратор уже создан." admin
        | AdminServiceError.Unavailable -> errorPage 503 "Сервис недоступен." admin

    let private accountFailure admin error : HttpHandler =
        match error with
        | AccountAccessError.Busy -> errorPage 503 "Сервис аккаунтов занят (выполняется другая операция), повторите." admin
        | AccountAccessError.InvalidCredentials | AccountAccessError.SanctionRefused SanctionError.PlayerNotFound -> errorPage 404 "Игрок не найден." admin
        | AccountAccessError.SanctionRefused SanctionError.NotActive -> errorPage 409 "У игрока нет такого действующего наказания." admin
        | AccountAccessError.SanctionRefused SanctionError.NotAllowed -> errorPage 403 "Это наказание нельзя выдать или снять." admin
        | AccountAccessError.UsernameTaken | AccountAccessError.Unavailable | AccountAccessError.TooSoon _ | AccountAccessError.Banned _
        | AccountAccessError.RegistrationClosed _ | AccountAccessError.AddressBanned _ | AccountAccessError.DeviceBanned _
        | AccountAccessError.FlowUnknown ->
            errorPage 503 "Сервис аккаунтов недоступен." admin

    // --- Ports -----------------------------------------------------------

    let private timeout routes = TimeSpan.FromSeconds(float routes.Settings.RequestTimeoutSeconds)

    let private ask routes (context: HttpContext) command = task {
        match! routes.Ports.Admin command (timeout routes) context.RequestAborted with
        | AgentAskResult.Replied result -> return result
        | AgentAskResult.Full | AgentAskResult.Dropped -> return Error AdminServiceError.Busy
        | AgentAskResult.Faulted failure ->
            routes.Logger.Error(failure, "Admin request failed")
            return Error AdminServiceError.Unavailable
        | AgentAskResult.Closed | AgentAskResult.TimedOut | AgentAskResult.Canceled -> return Error AdminServiceError.Unavailable
    }

    let private account routes (context: HttpContext) command = task {
        match! routes.Ports.Account command (timeout routes) context.RequestAborted with
        | AgentAskResult.Replied result -> return result
        | AgentAskResult.Full | AgentAskResult.Dropped -> return Error AccountAccessError.Busy
        | AgentAskResult.Faulted failure ->
            routes.Logger.Error(failure, "Account request from the panel failed")
            return Error AccountAccessError.Unavailable
        | AgentAskResult.Closed | AgentAskResult.TimedOut | AgentAskResult.Canceled -> return Error AccountAccessError.Unavailable
    }

    /// The guild owner answers through the runtime; while it restarts the panel reports it unavailable.
    let private guilds routes (context: HttpContext) command = task {
        match! routes.Ports.Guilds command (timeout routes) context.RequestAborted with
        | AgentAskResult.Replied result -> return Ok result
        | AgentAskResult.Full | AgentAskResult.Dropped -> return Error AdminServiceError.Busy
        | AgentAskResult.Faulted failure ->
            routes.Logger.Error(failure, "Guild request from the panel failed")
            return Error AdminServiceError.Unavailable
        | AgentAskResult.Closed | AgentAskResult.TimedOut | AgentAskResult.Canceled -> return Error AdminServiceError.Unavailable
    }

    let private snapshot routes (context: HttpContext) = task {
        match! routes.Ports.Snapshot (timeout routes) context.RequestAborted with
        | AgentAskResult.Replied value -> return Some (AdminModels.status value)
        | AgentAskResult.Faulted _ | AgentAskResult.Dropped | AgentAskResult.Full | AgentAskResult.Closed
        | AgentAskResult.TimedOut | AgentAskResult.Canceled -> return None
    }

    let private sessions routes (context: HttpContext) = task {
        match! routes.Ports.Sessions (timeout routes) context.RequestAborted with
        | AgentAskResult.Replied rows -> return Some rows
        | AgentAskResult.Faulted _ | AgentAskResult.Dropped | AgentAskResult.Full | AgentAskResult.Closed
        | AgentAskResult.TimedOut | AgentAskResult.Canceled -> return None
    }

    /// Sessions are asked in parallel; one that does not answer in time becomes
    /// a row without data and never fails the page.
    let private describe routes (rows: RuntimeSessionRow list) = task {
        let wait = TimeSpan.FromMilliseconds(float routes.Settings.DescribeTimeoutMs)
        let! views = rows |> List.map (routes.Ports.Describe wait) |> Task.WhenAll
        return
            List.map2 AdminModels.online rows (List.ofArray views)
            |> List.sortBy (fun row -> (if row.PlayerId.HasValue then row.PlayerId.Value else UInt64.MaxValue), row.ConnectedAt)
    }

    let private online routes context = task {
        match! sessions routes context with
        | Some rows ->
            let! models = describe routes rows
            return models, true
        | None -> return [], false
    }

    // --- Sign-in state ---------------------------------------------------

    let private cookieOptions (context: HttpContext) =
        CookieOptions(HttpOnly = true, SameSite = SameSiteMode.Strict, Secure = context.Request.IsHttps, Path = "/", IsEssential = true)

    let private setCookie (context: HttpContext) (token: string) (expires: DateTimeOffset) =
        let options = cookieOptions context
        options.Expires <- Nullable expires
        context.Response.Cookies.Append(CookieName, token, options)

    let private clearCookie (context: HttpContext) =
        context.Response.Cookies.Delete(CookieName, cookieOptions context)

    let private cookie (context: HttpContext) =
        match context.Request.Cookies.TryGetValue CookieName with
        | true, value when not (String.IsNullOrEmpty value) -> Some value
        | true, _ | false, _ -> None

    let private signedIn routes context = task {
        match cookie context with
        | None -> return Ok None
        | Some token ->
            match! ask routes context (AdminCommand.Authenticate token) with
            | Ok (AdminReply.Admin admin) -> return Ok (Some admin)
            | Ok _ | Error AdminServiceError.InvalidCredentials -> return Ok None
            | Error error -> return Error error
    }

    /// Pages that need an administrator. htmx requests get 401 and HX-Redirect.
    let private withAdmin routes (handler: AdminAccount -> HttpHandler) : HttpHandler = fun context -> task {
        match! signedIn routes context with
        | Ok (Some admin) -> return! handler admin context
        | Ok None ->
            if context.Request.Headers.ContainsKey "HX-Request" then
                context.Response.Headers["HX-Redirect"] <- "/login"
                return! errorPage 401 "Требуется вход." None context
            else return! redirect "/login" context
        | Error error -> return! serviceFailure None error context
    }

    /// Mutations come only from pages of this origin: Origin, or Sec-Fetch-Site
    /// when the browser gives no usable Origin. Pages are served with
    /// Referrer-Policy: no-referrer, and then browsers send "Origin: null" on their
    /// own form posts (Fetch, serializing a request origin); Sec-Fetch-Site is set
    /// by the browser and cannot be forged by a page. Anything else is refused
    /// before any work.
    let sameOrigin (context: HttpContext) =
        let origin = context.Request.Headers.Origin.ToString()
        if String.IsNullOrEmpty origin || origin = "null" then
            context.Request.Headers["Sec-Fetch-Site"].ToString() = "same-origin"
        else
            String.Equals(origin, $"{context.Request.Scheme}://{context.Request.Host.Value}", StringComparison.OrdinalIgnoreCase)

    let private readForm (context: HttpContext) = task {
        if not context.Request.HasFormContentType then return Error 415
        else
            try
                let! form = context.Request.ReadFormAsync(context.RequestAborted)
                return Ok form
            with
            | :? BadHttpRequestException as failure when failure.StatusCode = 413 -> return Error 413
            | :? InvalidDataException -> return Error 413
    }

    let private withForm admin (handler: IFormCollection -> HttpHandler) : HttpHandler = fun context -> task {
        if not (sameOrigin context) then
            return! errorPage 403 "Запрос не со страницы этой панели." admin context
        else
            match! readForm context with
            | Ok form -> return! handler form context
            | Error 415 -> return! errorPage 415 "Ожидается отправка формы." admin context
            | Error status -> return! errorPage status "Форма слишком велика." admin context
    }

    /// A form of a signed-in administrator. The origin is checked first.
    let private mutation routes (handler: AdminAccount -> IFormCollection -> HttpHandler) : HttpHandler = fun context -> task {
        if not (sameOrigin context) then
            return! errorPage 403 "Запрос не со страницы этой панели." None context
        else
            return! withAdmin routes (fun admin -> withForm (Some admin) (handler admin)) context
    }

    let private value (form: IFormCollection) (name: string) = form[name].ToString()
    let private confirmed form = value form "confirm" = "yes"

    let private record routes context admin action target details = task {
        match! ask routes context (AdminCommand.Record(admin, AuditRecord.create action target details)) with
        | Ok _ -> return true
        | Error error ->
            routes.Logger.Error("Audit line not written for {Admin}: {Action} ({Error})", Username.value admin.Username, AdminAction.key action, error)
            return false
    }

    // --- Public pages: sign-in, setup, reset -----------------------------

    let private loginPage routes : HttpHandler = fun context -> task {
        match! signedIn routes context with
        | Ok (Some _) -> return! redirect "/" context
        | Ok None | Error _ -> return! html 200 (AdminViews.login None "") context
    }

    let private login routes : HttpHandler =
        withForm None (fun form context -> task {
            let name = value form "username"
            let refused = html 401 (AdminViews.login (Some "Неверное имя или пароль.") name)
            match Username.create routes.Settings.Input.Username name with
            | Error _ -> return! refused context
            | Ok username ->
                match! ask routes context (AdminCommand.Login(username, value form "password")) with
                | Ok (AdminReply.SignedIn(_, token, expires)) ->
                    setCookie context token expires
                    return! redirect "/" context
                | Ok _ | Error AdminServiceError.InvalidCredentials | Error AdminServiceError.NotFound -> return! refused context
                | Error AdminServiceError.RateLimited ->
                    return! html 429 (AdminViews.login (Some "Слишком много попыток входа. Подождите минуту.") name) context
                | Error error -> return! serviceFailure None error context
        })

    let private logout routes : HttpHandler =
        withForm None (fun _ context -> task {
            match cookie context with
            | Some token ->
                let! _ = ask routes context (AdminCommand.Logout token)
                ()
            | None -> ()
            clearCookie context
            return! redirect "/login" context
        })

    let private setupPage routes : HttpHandler = fun context -> task {
        match! ask routes context AdminCommand.Status with
        | Ok (AdminReply.Configured configured) -> return! html 200 (AdminViews.setup configured None "") context
        | Ok _ -> return! serviceFailure None AdminServiceError.Unavailable context
        | Error error -> return! serviceFailure None error context
    }

    /// Password and repetition checks shared by setup and reset; the rule of the
    /// password itself belongs to the admin service (the player rule).
    let private newPassword (form: IFormCollection) =
        let password = value form "password"
        if password <> value form "password2" then Error "Пароли не совпадают."
        elif not (Secrets.validPassword password) then Error "Пароль должен занимать 12–128 байт UTF-8."
        else Ok password

    let private signInAfter context (reply: Result<AdminReply, AdminServiceError>) (refused: string -> HttpHandler) : Task =
        match reply with
        | Ok (AdminReply.SignedIn(_, token, expires)) ->
            setCookie context token expires
            redirect "/" context
        | Ok _ -> serviceFailure None AdminServiceError.Unavailable context
        | Error AdminServiceError.InvalidCredentials -> refused "Код неверен, уже использован или истёк." context
        | Error AdminServiceError.NotFound -> refused "Администратор этого кода больше не существует." context
        | Error error -> serviceFailure None error context

    let private setup routes : HttpHandler =
        withForm None (fun form context -> task {
            let name = value form "username"
            let refused status message = html status (AdminViews.setup false (Some message) name)
            let code = value form "code"
            match Username.create routes.Settings.Input.Username name, newPassword form with
            | _ when String.IsNullOrWhiteSpace code -> return! refused 400 "Нужен код настройки из консоли сервера." context
            | Error _, _ -> return! refused 400 "Имя: латиница, цифры, _ и точка." context
            | _, Error message -> return! refused 400 message context
            | Ok username, Ok password ->
                match! ask routes context (AdminCommand.Setup(code.Trim(), username, password)) with
                | Error AdminServiceError.AlreadyConfigured -> return! html 409 (AdminViews.setup true None "") context
                | reply -> return! signInAfter context reply (refused 403)
        })

    let private resetPage : HttpHandler = html 200 (AdminViews.reset None)

    let private reset routes : HttpHandler =
        withForm None (fun form context -> task {
            let refused status message = html status (AdminViews.reset (Some message))
            let code = value form "code"
            match newPassword form with
            | _ when String.IsNullOrWhiteSpace code -> return! refused 400 "Нужен код из команды admin-reset." context
            | Error message -> return! refused 400 message context
            | Ok password ->
                let! reply = ask routes context (AdminCommand.ResetPassword(code.Trim(), password))
                return! signInAfter context reply (refused 403)
        })

    // --- Overview and players ---------------------------------------------

    let private overview routes admin : HttpHandler = fun context -> task {
        let! status = snapshot routes context
        let! rows, available = online routes context
        return! html 200 (AdminViews.overview admin status rows available) context
    }

    let private onlinePartial routes (_: AdminAccount) : HttpHandler = fun context -> task {
        let! rows, available = online routes context
        return! fragment (AdminViews.online rows available) context
    }

    let private onlineIds (rows: RuntimeSessionRow list option) =
        let ids = rows |> Option.defaultValue [] |> List.choose _.PlayerId |> Set.ofList
        fun playerId -> ids.Contains playerId

    let private pageNumber (context: HttpContext) =
        match Int32.TryParse(context.Request.Query["page"].ToString()) with
        | true, number when number >= 1 && number <= 1000000 -> number
        | true, _ | false, _ -> 1

    let private searchQuery (context: HttpContext) =
        let text = context.Request.Query["q"].ToString().Trim()
        if text.Length > 128 then text.Substring(0, 128) else text

    let private playerPage routes context = task {
        let query = searchQuery context
        match! ask routes context (AdminCommand.SearchPlayers(query, pageNumber context)) with
        | Ok (AdminReply.Players page) ->
            let! rows = sessions routes context
            return Ok (AdminModels.page query (onlineIds rows) page)
        | Ok _ -> return Error AdminServiceError.Unavailable
        | Error error -> return Error error
    }

    let private players routes admin : HttpHandler = fun context -> task {
        match! playerPage routes context with
        | Ok model -> return! html 200 (AdminViews.players admin model) context
        | Error error -> return! serviceFailure (Some admin) error context
    }

    let private routePlayer (context: HttpContext) =
        match UInt64.TryParse(string context.Request.RouteValues["id"]) with
        | true, raw -> PlayerId.create raw |> Result.toOption
        | false, _ -> None

    let private findPlayer routes context playerId = task {
        match! ask routes context (AdminCommand.FindPlayer playerId) with
        | Ok (AdminReply.Player player) -> return Ok player
        | Ok _ -> return Error AdminServiceError.Unavailable
        | Error error -> return Error error
    }

    let private card routes context (record: PlayerRecord) = task {
        let! rows = sessions routes context
        let own = rows |> Option.defaultValue [] |> List.filter (fun row -> row.PlayerId = Some record.Profile.PlayerId)
        let! described = describe routes own
        let! names = ask routes context (AdminCommand.NameHistory record.Profile.PlayerId)
        let names = match names with Ok (AdminReply.Names changes) -> changes |> List.map AdminModels.nameChange | Ok _ | Error _ -> []
        let! sanctions = ask routes context (AdminCommand.PlayerSanctions record.Profile.PlayerId)
        let sanctions = match sanctions with Ok (AdminReply.Sanctions active) -> active |> List.map AdminModels.sanction | Ok _ | Error _ -> []
        let! addresses = account routes context (AccountAccessCommand.AddressHistory record.Profile.PlayerId)
        let addresses = match addresses with Ok (AccountAccessResult.Addresses entries) -> entries |> List.map AdminModels.signInAddress | Ok _ | Error _ -> []
        let! devices = account routes context (AccountAccessCommand.DeviceHistory record.Profile.PlayerId)
        let devices = match devices with Ok (AccountAccessResult.Devices entries) -> entries |> List.map AdminModels.signInDevice | Ok _ | Error _ -> []
        let! memberships = guilds routes context (GuildAdminCommand.PlayerGuilds record.Profile.PlayerId)
        let memberships = match memberships with Ok (GuildAdminResult.PlayerGuilds entries) -> entries |> List.map AdminModels.playerGuild | Ok _ | Error _ -> []
        return { Player = AdminModels.player (onlineIds rows) record; Sessions = described; Names = names; Sanctions = sanctions
                 Addresses = addresses; Devices = devices; Guilds = memberships }
    }

    let private notices =
        dict [
            "role", "Роль сохранена."
            "renamed", "Display name изменено."
            "revoked", "Доступ отозван."
            "sanctioned", "Наказание выдано."
            "lifted", "Наказание снято."
            "kicked", "Сессия игрока закрыта."
            "registration", "Режим регистрации сохранён."
            "address-banned", "Диапазон забанен; соединения из него закрыты."
            "address-lifted", "Бан диапазона снят."
            "announced", "Объявление отправлено."
            "token-revoked", "Токен отозван."
            "appointed", "Глава гильдии назначен."
            "dissolved", "Гильдия распущена."
        ]

    let private notice (context: HttpContext) =
        match notices.TryGetValue(context.Request.Query["done"].ToString()) with
        | true, text -> Some text
        | false, _ -> None

    let private showCard routes admin status (failure: string option) (resetCode: string option) (playerId: PlayerId) : HttpHandler = fun context -> task {
        match! findPlayer routes context playerId with
        | Ok (Some record) ->
            let! model = card routes context record
            let page = AdminViews.player admin model (if failure.IsNone && resetCode.IsNone then notice context else None) failure resetCode
                           routes.Settings.Input.DisplayName routes.Settings.AddressHistoryDays
            return! html status page context
        | Ok None -> return! errorPage 404 "Игрок не найден." (Some admin) context
        | Error error -> return! serviceFailure (Some admin) error context
    }

    let private player routes admin : HttpHandler = fun context ->
        match routePlayer context with
        | Some playerId -> showCard routes admin 200 None None playerId context
        | None -> errorPage 404 "Игрок не найден." (Some admin) context

    let private playerAction routes (handler: AdminAccount -> PlayerId -> IFormCollection -> HttpHandler) : HttpHandler =
        mutation routes (fun admin form context ->
            match routePlayer context with
            | None -> errorPage 404 "Игрок не найден." (Some admin) context
            | Some playerId when not (confirmed form) ->
                showCard routes admin 400 (Some "Отметьте подтверждение действия.") None playerId context
            | Some playerId -> handler admin playerId form context)

    let private setRole routes =
        playerAction routes (fun admin playerId form context -> task {
            match PlayerRole.ofKey (value form "role") with
            | None -> return! showCard routes admin 400 (Some "Неизвестная роль.") None playerId context
            | Some role ->
                match! ask routes context (AdminCommand.SetRole(admin, playerId, role)) with
                | Ok _ ->
                    // Stored and audited; the live session applies it without reconnecting.
                    if not (routes.Ports.ApplyRole playerId role) then
                        routes.Logger.Warning("Runtime did not take the role of player {PlayerId}", PlayerId.value playerId)
                    return! redirect $"/players/{PlayerId.value playerId}?done=role" context
                | Error error -> return! serviceFailure (Some admin) error context
        })

    let private rename routes =
        playerAction routes (fun admin playerId form context -> task {
            let refuse message = showCard routes admin 400 (Some message) None playerId context
            match DisplayName.create routes.Settings.Input.DisplayName (value form "displayName") with
            | Error _ -> return! refuse $"Display name: 1–{routes.Settings.Input.DisplayName} символов без управляющих."
            | Ok name when not (Moderation.allows routes.Settings.Moderation (DisplayName.value name)) ->
                return! refuse "Имя содержит недопустимые слова."
            | Ok name ->
                match! findPlayer routes context playerId with
                | Error error -> return! serviceFailure (Some admin) error context
                | Ok None -> return! errorPage 404 "Игрок не найден." (Some admin) context
                | Ok (Some before) ->
                    match! account routes context (AccountAccessCommand.RenamePlayer(playerId, name, admin.Id)) with
                    | Ok (AccountAccessResult.Renamed profile) ->
                        if not (routes.Ports.ApplyProfile profile) then
                            routes.Logger.Warning("Runtime did not take the new name of player {PlayerId}", PlayerId.value playerId)
                        let details = $"{DisplayName.value before.Profile.DisplayName} -> {DisplayName.value profile.DisplayName}"
                        let! audited = record routes context admin AdminAction.RenamePlayer (AuditTarget.Player playerId) details
                        if audited then return! redirect $"/players/{PlayerId.value playerId}?done=renamed" context
                        else return! errorPage 503 "Имя изменено, но строка аудита не записана; см. лог сервера." (Some admin) context
                    | Ok _ -> return! accountFailure (Some admin) AccountAccessError.Unavailable context
                    | Error error -> return! accountFailure (Some admin) error context
        })

    /// Reset and revoke go through the account service, as the console commands do.
    let private administer routes reset =
        playerAction routes (fun admin playerId _ context -> task {
            match! findPlayer routes context playerId with
            | Error error -> return! serviceFailure (Some admin) error context
            | Ok None -> return! errorPage 404 "Игрок не найден." (Some admin) context
            | Ok (Some stored) ->
                let command =
                    if reset then AccountAccessCommand.CreatePasswordReset stored.Profile.Username
                    else AccountAccessCommand.RevokeAccount stored.Profile.Username
                match! account routes context command with
                | Ok (AccountAccessResult.PasswordResetCreated code) when reset ->
                    let! audited = record routes context admin AdminAction.ResetPlayerPassword (AuditTarget.Player playerId) ""
                    let failure = if audited then None else Some "Код выдан, но строка аудита не записана; см. лог сервера."
                    return! showCard routes admin 200 failure (Some code) playerId context
                | Ok AccountAccessResult.Completed when not reset ->
                    let! audited = record routes context admin AdminAction.RevokePlayerAccess (AuditTarget.Player playerId) ""
                    if audited then return! redirect $"/players/{PlayerId.value playerId}?done=revoked" context
                    else return! errorPage 503 "Доступ отозван, но строка аудита не записана; см. лог сервера." (Some admin) context
                | Ok _ -> return! accountFailure (Some admin) AccountAccessError.Unavailable context
                | Error error -> return! accountFailure (Some admin) error context
        })

    // Term field "" is until lifted, "custom" takes minutes, a preset is its minutes.
    let private sanctionTerm (form: IFormCollection) =
        let minutes (text: string) =
            match Int32.TryParse text with
            | true, minutes -> SanctionTerm.create (ValueSome minutes)
            | false, _ -> Error(DomainError.InvalidLimit("SanctionTerm", 0))
        match value form "term" with
        | "" -> SanctionTerm.create ValueNone
        | "custom" -> minutes (value form "minutes")
        | preset -> minutes preset

    let private reasonHint = $"Причина обязательна: одна строка до {SanctionReason.MaxLength} символов."

    /// A mute or a ban from the panel: the account service stores it with its
    /// audit line and the runtime applies it to a live session, as for a moderator's.
    let private sanction routes =
        playerAction routes (fun admin playerId form context -> task {
            let refuse message = showCard routes admin 400 (Some message) None playerId context
            match SanctionKind.ofKey (value form "kind"), sanctionTerm form, SanctionReason.create (value form "reason") with
            | None, _, _ -> return! refuse "Неизвестный вид наказания."
            | _, Error _, _ -> return! refuse $"Срок: от 1 до {SanctionTerm.MaxMinutes} минут или бессрочно."
            | _, _, Error _ -> return! refuse reasonHint
            | Some kind, Ok term, Ok reason ->
                let order = { Target = playerId; Kind = kind; Term = term; Reason = reason; IssuedBy = SanctionIssuer.Admin admin.Id
                              Devices = value form "devices" = "yes" }
                match! account routes context (AccountAccessCommand.Sanction order) with
                | Ok (AccountAccessResult.Sanctioned _) -> return! redirect $"/players/{PlayerId.value playerId}?done=sanctioned" context
                | Ok _ -> return! accountFailure (Some admin) AccountAccessError.Unavailable context
                | Error error -> return! accountFailure (Some admin) error context
        })

    let private lift routes =
        playerAction routes (fun admin playerId form context -> task {
            match SanctionKind.ofKey (value form "kind") with
            | None -> return! showCard routes admin 400 (Some "Неизвестный вид наказания.") None playerId context
            | Some kind ->
                match! account routes context (AccountAccessCommand.LiftSanction(playerId, kind, SanctionIssuer.Admin admin.Id)) with
                | Ok (AccountAccessResult.SanctionLifted _) -> return! redirect $"/players/{PlayerId.value playerId}?done=lifted" context
                | Ok _ -> return! accountFailure (Some admin) AccountAccessError.Unavailable context
                | Error error -> return! accountFailure (Some admin) error context
        })

    /// Ends the live session now, as a moderator's kick; the player may sign in again at once.
    let private kick routes =
        playerAction routes (fun admin playerId form context -> task {
            match SanctionReason.create (value form "reason") with
            | Error _ -> return! showCard routes admin 400 (Some reasonHint) None playerId context
            | Ok reason ->
                match! account routes context (AccountAccessCommand.Kick(playerId, reason, SanctionIssuer.Admin admin.Id)) with
                | Ok AccountAccessResult.Kicked -> return! redirect $"/players/{PlayerId.value playerId}?done=kicked" context
                | Ok _ -> return! accountFailure (Some admin) AccountAccessError.Unavailable context
                | Error error -> return! accountFailure (Some admin) error context
        })

    let private activeSanctions routes context = task {
        match! ask routes context AdminCommand.ActiveSanctions with
        | Ok (AdminReply.ActiveSanctions records) -> return Ok (records |> List.map AdminModels.sanctionEntry)
        | Ok _ -> return Error AdminServiceError.Unavailable
        | Error error -> return Error error
    }

    let private sanctionsPage routes admin : HttpHandler = fun context -> task {
        match! activeSanctions routes context with
        | Ok entries -> return! html 200 (AdminViews.sanctions admin entries) context
        | Error error -> return! serviceFailure (Some admin) error context
    }

    // --- Guilds ------------------------------------------------------------

    let private guildPage routes context = task {
        let query = searchQuery context
        match! guilds routes context (GuildAdminCommand.Search(query, pageNumber context)) with
        | Ok (GuildAdminResult.Page page) -> return Ok (AdminModels.guildPage query page)
        | Ok _ -> return Error AdminServiceError.Unavailable
        | Error error -> return Error error
    }

    let private guildsPage routes admin : HttpHandler = fun context -> task {
        match! guildPage routes context with
        | Ok model -> return! html 200 (AdminViews.guilds admin model (notice context)) context
        | Error error -> return! serviceFailure (Some admin) error context
    }

    let private routeGuild (context: HttpContext) =
        match UInt64.TryParse(string context.Request.RouteValues["id"]) with
        | true, raw -> GuildId.create raw |> Result.toOption
        | false, _ -> None

    let private guildCard routes context guild = task {
        match! guilds routes context (GuildAdminCommand.Card guild) with
        | Ok (GuildAdminResult.Card card) -> return Ok (card |> ValueOption.map AdminModels.guildCard)
        | Ok _ -> return Error AdminServiceError.Unavailable
        | Error error -> return Error error
    }

    let private showGuild routes admin status (failure: string option) guild : HttpHandler = fun context -> task {
        match! guildCard routes context guild with
        | Ok (ValueSome card) -> return! html status (AdminViews.guild admin card (if failure.IsNone then notice context else None) failure) context
        | Ok ValueNone -> return! errorPage 404 "Гильдия не найдена." (Some admin) context
        | Error error -> return! serviceFailure (Some admin) error context
    }

    let private guildPageOf routes admin : HttpHandler = fun context ->
        match routeGuild context with
        | Some guild -> showGuild routes admin 200 None guild context
        | None -> errorPage 404 "Гильдия не найдена." (Some admin) context

    let private guildAction routes (handler: AdminAccount -> GuildId -> IFormCollection -> HttpHandler) : HttpHandler =
        mutation routes (fun admin form context ->
            match routeGuild context with
            | None -> errorPage 404 "Гильдия не найдена." (Some admin) context
            | Some guild when not (confirmed form) -> showGuild routes admin 400 (Some "Отметьте подтверждение действия.") guild context
            | Some guild -> handler admin guild form context)

    let private guildRefusal routes admin guild error : HttpHandler =
        match error with
        | GuildError.NotFound -> errorPage 404 "Гильдия не найдена." (Some admin)
        | GuildError.TargetNotFound -> showGuild routes admin 409 (Some "Этот игрок не состоит в гильдии.") guild
        | GuildError.NameTaken | GuildError.ServerFull | GuildError.PlayerLimit | GuildError.GuildFull | GuildError.InvitesFull
        | GuildError.AlreadyMember | GuildError.AlreadyInvited | GuildError.NotPermitted | GuildError.MasterStays ->
            showGuild routes admin 409 (Some "Владелец гильдий отказал в действии.") guild

    /// The guild owner hands the role over and tells the members; the panel audits it.
    let private appoint routes =
        guildAction routes (fun admin guild form context -> task {
            let target =
                match UInt64.TryParse(value form "player") with
                | true, raw -> PlayerId.create raw |> Result.toOption
                | false, _ -> None
            match target with
            | None -> return! showGuild routes admin 400 (Some "Выберите участника гильдии.") guild context
            | Some player ->
                match! guilds routes context (GuildAdminCommand.Appoint(guild, player)) with
                | Ok (GuildAdminResult.Appointed card) ->
                    let master = card.Members |> List.tryFind (fun entry -> entry.Profile.PlayerId = player)
                    let name = master |> Option.map (fun entry -> DisplayName.value entry.Profile.DisplayName) |> Option.defaultValue ""
                    let details = $"{GuildName.value card.Summary.Name}: player:{PlayerId.value player} {name}"
                    let! audited = record routes context admin AdminAction.AppointedGuildMaster (AuditTarget.Guild guild) details
                    if audited then return! redirect $"/guilds/{GuildId.value guild}?done=appointed" context
                    else return! errorPage 503 "Глава назначен, но строка аудита не записана; см. лог сервера." (Some admin) context
                | Ok (GuildAdminResult.Refused error) -> return! guildRefusal routes admin guild error context
                | Ok _ -> return! serviceFailure (Some admin) AdminServiceError.Unavailable context
                | Error error -> return! serviceFailure (Some admin) error context
        })

    let private dissolve routes =
        guildAction routes (fun admin guild _ context -> task {
            match! guilds routes context (GuildAdminCommand.Dissolve guild) with
            | Ok (GuildAdminResult.Dissolved gone) ->
                let details = $"{GuildName.value gone.Name}, участников: {gone.Members.Length}"
                let! audited = record routes context admin AdminAction.DissolvedGuild (AuditTarget.Guild guild) details
                if audited then return! redirect "/guilds?done=dissolved" context
                else return! errorPage 503 "Гильдия распущена, но строка аудита не записана; см. лог сервера." (Some admin) context
            | Ok (GuildAdminResult.Refused error) -> return! guildRefusal routes admin guild error context
            | Ok _ -> return! serviceFailure (Some admin) AdminServiceError.Unavailable context
            | Error error -> return! serviceFailure (Some admin) error context
        })

    // --- Registration ------------------------------------------------------

    let private registrationMode routes context = task {
        match! account routes context AccountAccessCommand.ReadRegistration with
        | Ok (AccountAccessResult.Registration mode) -> return Ok mode
        | Ok _ -> return Error AccountAccessError.Unavailable
        | Error error -> return Error error
    }

    let private showRegistration routes admin status (failure: string option) (created: CreatedPlayerModel option) : HttpHandler = fun context -> task {
        match! registrationMode routes context with
        | Ok mode ->
            let message = if failure.IsNone && created.IsNone then notice context else None
            let input = routes.Settings.Input
            let page = AdminViews.registration admin mode message failure created routes.Settings.SetupCodeHours input.Username input.DisplayName
            return! html status page context
        | Error error -> return! accountFailure (Some admin) error context
    }

    /// Stored by the account service, which reads it at every registration.
    let private setRegistration routes =
        mutation routes (fun admin form context -> task {
            let refuse message = showRegistration routes admin 400 (Some message) None context
            match RegistrationMode.ofKey (value form "mode") with
            | _ when not (confirmed form) -> return! refuse "Отметьте подтверждение смены режима."
            | None -> return! refuse "Неизвестный режим регистрации."
            | Some mode ->
                match! account routes context (AccountAccessCommand.SetRegistration(mode, ValueSome admin.Id)) with
                | Ok (AccountAccessResult.Registration stored) ->
                    let! audited = record routes context admin AdminAction.SetRegistrationMode AuditTarget.Server (RegistrationMode.key stored)
                    if audited then return! redirect "/registration?done=registration" context
                    else return! errorPage 503 "Режим сохранён, но строка аудита не записана; см. лог сервера." (Some admin) context
                | Ok _ -> return! accountFailure (Some admin) AccountAccessError.Unavailable context
                | Error error -> return! accountFailure (Some admin) error context
        })

    /// Works in every mode: the account has no password until the player redeems the code.
    let private createPlayer routes =
        mutation routes (fun admin form context -> task {
            let refuse status message = showRegistration routes admin status (Some message) None context
            let input = routes.Settings.Input
            let moderation = routes.Settings.Moderation
            match Username.create input.Username (value form "username"), DisplayName.create input.DisplayName (value form "displayName") with
            | _ when not (confirmed form) -> return! refuse 400 "Отметьте подтверждение создания игрока."
            | Error _, _ -> return! refuse 400 $"Имя пользователя: 1–{input.Username} символов, латиница, цифры, _ и точка."
            | _, Error _ -> return! refuse 400 $"Display name: 1–{input.DisplayName} символов без управляющих."
            | Ok username, _ when not (Moderation.allowsUsername moderation username) ->
                return! refuse 400 "Это имя пользователя зарезервировано или содержит недопустимые слова."
            | _, Ok displayName when not (Moderation.allows moderation (DisplayName.value displayName)) ->
                return! refuse 400 "Display name содержит недопустимые слова."
            | Ok username, Ok displayName ->
                match! account routes context (AccountAccessCommand.CreateAccount(username, displayName)) with
                | Ok (AccountAccessResult.AccountCreated(profile, code)) ->
                    let! audited = record routes context admin AdminAction.CreatedPlayer (AuditTarget.Player profile.PlayerId) (Username.value profile.Username)
                    let failure = if audited then None else Some "Игрок создан, но строка аудита не записана; см. лог сервера."
                    let created = { PlayerId = PlayerId.value profile.PlayerId; Username = Username.value profile.Username; SetupCode = code }
                    return! showRegistration routes admin 200 failure (Some created) context
                | Error AccountAccessError.UsernameTaken -> return! refuse 409 "Это имя пользователя уже занято."
                | Ok _ -> return! accountFailure (Some admin) AccountAccessError.Unavailable context
                | Error error -> return! accountFailure (Some admin) error context
        })

    // --- IP range bans ------------------------------------------------------

    let private addressBanList routes context = task {
        match! account routes context AccountAccessCommand.ListAddressBans with
        | Ok (AccountAccessResult.AddressBans bans) -> return Ok (bans |> List.map AdminModels.addressBan)
        | Ok _ -> return Error AccountAccessError.Unavailable
        | Error error -> return Error error
    }

    let private showAddressBans routes admin status (failure: string option) (range: string) (check: RangeCheckModel option) : HttpHandler = fun context -> task {
        match! addressBanList routes context with
        | Ok bans ->
            let message = if failure.IsNone && check.IsNone then notice context else None
            let page = AdminViews.addressBans admin bans message failure range check routes.Settings.AddressHistoryDays
            return! html status page context
        | Error error -> return! accountFailure (Some admin) error context
    }

    let private addressBansPage routes admin : HttpHandler = fun context ->
        let range = context.Request.Query["range"].ToString().Trim()
        showAddressBans routes admin 200 None (if range.Length > 64 then "" else range) None context

    // Range, term and reason by the rules of their domain types.
    let private banOrder (form: IFormCollection) =
        match AddressRange.parse (value form "range"), sanctionTerm form, SanctionReason.create (value form "reason") with
        | Error _, _, _ -> Error $"Диапазон: адрес или CIDR; IPv4 не шире /{AddressRange.MinIpv4Prefix}, IPv6 не шире /{AddressRange.MinIpv6Prefix}."
        | _, Error _, _ -> Error $"Срок: от 1 до {SanctionTerm.MaxMinutes} минут или бессрочно."
        | _, _, Error _ -> Error reasonHint
        | Ok range, Ok term, Ok reason -> Ok (range, term, reason)

    /// Before a ban: who it would hit, online now and in the sign-in history.
    let private checkRange routes =
        mutation routes (fun admin form context -> task {
            match banOrder form with
            | Error message -> return! showAddressBans routes admin 400 (Some message) (value form "range") None context
            | Ok (range, _, _) ->
                let! rows = sessions routes context
                let covered = rows |> Option.defaultValue [] |> List.filter (fun row -> AddressRange.contains range row.Address)
                let! online = describe routes covered
                match! account routes context (AccountAccessCommand.PlayersInRange range) with
                | Ok (AccountAccessResult.PlayersAt players) ->
                    let check = {
                        Range = AddressRange.key range; Reason = value form "reason"; Term = value form "term"; Minutes = value form "minutes"
                        Online = online; Players = players |> List.map AdminModels.addressMatch
                    }
                    return! showAddressBans routes admin 200 None (AddressRange.key range) (Some check) context
                | Ok _ -> return! accountFailure (Some admin) AccountAccessError.Unavailable context
                | Error error -> return! accountFailure (Some admin) error context
        })

    /// The account service stores the ban with its audit line and hands the
    /// bans in force to the runtime, which closes the connections they cover.
    let private banRange routes =
        mutation routes (fun admin form context -> task {
            let refuse message = showAddressBans routes admin 400 (Some message) (value form "range") None context
            match banOrder form with
            | _ when not (confirmed form) -> return! refuse "Проверьте диапазон и отметьте подтверждение бана."
            | Error message -> return! refuse message
            | Ok (range, term, reason) ->
                match! account routes context (AccountAccessCommand.BanAddresses(range, reason, term, admin.Id)) with
                | Ok (AccountAccessResult.AddressesBanned _) -> return! redirect "/address-bans?done=address-banned" context
                | Ok _ -> return! accountFailure (Some admin) AccountAccessError.Unavailable context
                | Error error -> return! accountFailure (Some admin) error context
        })

    let private routeBan (context: HttpContext) =
        match Int64.TryParse(string context.Request.RouteValues["id"]) with
        | true, id when id > 0L -> Some id
        | true, _ | false, _ -> None

    let private liftRange routes =
        mutation routes (fun admin form context -> task {
            match routeBan context with
            | None -> return! errorPage 404 "Бан не найден." (Some admin) context
            | Some _ when not (confirmed form) -> return! showAddressBans routes admin 400 (Some "Отметьте подтверждение снятия.") "" None context
            | Some id ->
                match! account routes context (AccountAccessCommand.LiftAddressBan(id, admin.Id)) with
                | Ok (AccountAccessResult.AddressBanLifted _) -> return! redirect "/address-bans?done=address-lifted" context
                | Error (AccountAccessError.SanctionRefused SanctionError.NotActive) ->
                    return! showAddressBans routes admin 409 (Some "Этот бан уже снят или истёк.") "" None context
                | Ok _ -> return! accountFailure (Some admin) AccountAccessError.Unavailable context
                | Error error -> return! accountFailure (Some admin) error context
        })

    // --- Announcements, audit, tokens, configuration -----------------------

    let private announcePage routes admin : HttpHandler = fun context ->
        html 200 (AdminViews.announce admin (notice context) None routes.Settings.Input.MessageText) context

    let private announce routes =
        mutation routes (fun admin form context -> task {
            let refuse status message = html status (AdminViews.announce admin None (Some message) routes.Settings.Input.MessageText)
            if not (confirmed form) then return! refuse 400 "Отметьте подтверждение отправки." context
            else
                match AdminAnnouncement.create routes.Settings.Input.MessageText (value form "text") (value form "kind") with
                | Error _ -> return! refuse 400 $"Текст: 1–{routes.Settings.Input.MessageText} символов без управляющих; вид — admin, announcement или event." context
                | Ok (text, kind) ->
                    // Same path as the console command: the runtime hands it to the system channel owner.
                    if not (routes.Ports.Announce { Text = text; Kind = kind }) then
                        return! refuse 503 "Рантайм занят; объявление не принято." context
                    else
                        let details = $"{AdminAnnouncement.kindKey kind}: {ChatMessageText.value text}"
                        let! audited = record routes context admin AdminAction.Announced AuditTarget.Server details
                        if audited then return! redirect "/announce?done=announced" context
                        else return! errorPage 503 "Объявление отправлено, но строка аудита не записана; см. лог сервера." (Some admin) context
        })

    let private auditPage routes admin : HttpHandler = fun context -> task {
        match! ask routes context (AdminCommand.RecentAudit AuditLimit) with
        | Ok (AdminReply.Audit entries) -> return! html 200 (AdminViews.audit admin (entries |> List.map AdminModels.audit)) context
        | Ok _ -> return! serviceFailure (Some admin) AdminServiceError.Unavailable context
        | Error error -> return! serviceFailure (Some admin) error context
    }

    let private tokenList routes context = task {
        match! ask routes context AdminCommand.ListApiTokens with
        | Ok (AdminReply.ApiTokens tokens) -> return Ok (tokens |> List.map AdminModels.token)
        | Ok _ -> return Error AdminServiceError.Unavailable
        | Error error -> return Error error
    }

    let private showTokens routes admin status (failure: string option) (created: string option) : HttpHandler = fun context -> task {
        match! tokenList routes context with
        | Ok tokens ->
            let message = if failure.IsNone && created = None then notice context else None
            return! html status (AdminViews.tokens admin tokens message failure created) context
        | Error error -> return! serviceFailure (Some admin) error context
    }

    let private createToken routes =
        mutation routes (fun admin form context -> task {
            match ApiTokenLabel.create (value form "label") with
            | Error _ -> return! showTokens routes admin 400 (Some "Метка: 1–64 символа без управляющих.") None context
            | Ok label ->
                match! ask routes context (AdminCommand.CreateApiToken(admin, label)) with
                | Ok (AdminReply.Secret secret) -> return! showTokens routes admin 200 None (Some secret) context
                | Ok _ -> return! serviceFailure (Some admin) AdminServiceError.Unavailable context
                | Error error -> return! serviceFailure (Some admin) error context
        })

    let private revokeToken routes =
        mutation routes (fun admin form context -> task {
            if not (confirmed form) then return! showTokens routes admin 400 (Some "Отметьте подтверждение отзыва.") None context
            else
                match! ask routes context (AdminCommand.RevokeApiToken(admin, value form "id")) with
                | Ok _ -> return! redirect "/tokens?done=token-revoked" context
                | Error error -> return! serviceFailure (Some admin) error context
        })

    let private configuration routes admin : HttpHandler =
        html 200 (AdminViews.configuration admin (routes.Ports.Configuration ()))

    // --- Static files from the assembly ------------------------------------

    let private resource name =
        lazy (
            use stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Dreamsleeve.Server.Web." + name)
            use copy = new MemoryStream()
            stream.CopyTo copy
            copy.ToArray())

    let private files =
        dict [
            "htmx.min.js", ("text/javascript; charset=utf-8", resource "htmx.min.js")
            "admin.css", ("text/css; charset=utf-8", resource "admin.css")
            "htmx.LICENSE", ("text/plain; charset=utf-8", resource "htmx.LICENSE")
        ]

    let private staticFile : HttpHandler = fun context ->
        match files.TryGetValue(string context.Request.RouteValues["name"]) with
        | true, (contentType, bytes) ->
            context.Response.Headers.CacheControl <- "public, max-age=3600"
            context.Response.ContentType <- contentType
            context.Response.Body.WriteAsync(bytes.Value, 0, bytes.Value.Length)
        | false, _ -> errorPage 404 "Нет такого файла." None context

    // --- REST: read only, same models as the pages --------------------------

    /// Cookie of a signed-in administrator or Authorization: Bearer <API token>.
    let private withApi routes (handler: AdminAccount -> HttpHandler) : HttpHandler = fun context -> task {
        let header = context.Request.Headers.Authorization.ToString()
        let command =
            if header.StartsWith("Bearer ", StringComparison.Ordinal) then Some (AdminCommand.AuthenticateApi(header.Substring(7).Trim()))
            else cookie context |> Option.map AdminCommand.Authenticate
        match command with
        | None -> return! apiError 401 "unauthorized" "Sign in to the panel or pass an API token." context
        | Some command ->
            match! ask routes context command with
            | Ok (AdminReply.Admin admin) -> return! handler admin context
            | Ok _ | Error AdminServiceError.InvalidCredentials -> return! apiError 401 "unauthorized" "Sign in to the panel or pass an API token." context
            | Error AdminServiceError.Busy -> return! apiError 503 "busy" "The panel is busy. Try again later." context
            | Error _ -> return! apiError 503 "unavailable" "The panel is temporarily unavailable." context
    }

    let private apiFailure error : HttpHandler =
        match error with
        | AdminServiceError.NotFound -> apiError 404 "not_found" "Not found."
        | AdminServiceError.Busy -> apiError 503 "busy" "The panel is busy. Try again later."
        | AdminServiceError.RateLimited -> apiError 429 "rate_limited" "Too many requests. Try again later."
        | AdminServiceError.InvalidCredentials -> apiError 401 "unauthorized" "Sign in to the panel or pass an API token."
        | AdminServiceError.AlreadyConfigured | AdminServiceError.Unavailable -> apiError 503 "unavailable" "The panel is temporarily unavailable."

    let private apiStatus routes (_: AdminAccount) : HttpHandler = fun context -> task {
        match! snapshot routes context with
        | Some status -> return! apiJson status context
        | None -> return! apiError 503 "unavailable" "The runtime did not answer." context
    }

    let private apiOnline routes (_: AdminAccount) : HttpHandler = fun context -> task {
        match! online routes context with
        | rows, true -> return! apiJson rows context
        | _, false -> return! apiError 503 "unavailable" "The runtime did not answer." context
    }

    let private apiPlayers routes (_: AdminAccount) : HttpHandler = fun context -> task {
        match! playerPage routes context with
        | Ok model -> return! apiJson model context
        | Error error -> return! apiFailure error context
    }

    let private apiPlayer routes (_: AdminAccount) : HttpHandler = fun context -> task {
        match routePlayer context with
        | None -> return! apiFailure AdminServiceError.NotFound context
        | Some playerId ->
            match! findPlayer routes context playerId with
            | Ok (Some record) ->
                let! model = card routes context record
                return! apiJson model context
            | Ok None -> return! apiFailure AdminServiceError.NotFound context
            | Error error -> return! apiFailure error context
    }

    let private apiGuilds routes (_: AdminAccount) : HttpHandler = fun context -> task {
        match! guildPage routes context with
        | Ok model -> return! apiJson model context
        | Error error -> return! apiFailure error context
    }

    let private apiGuild routes (_: AdminAccount) : HttpHandler = fun context -> task {
        match routeGuild context with
        | None -> return! apiFailure AdminServiceError.NotFound context
        | Some guild ->
            match! guildCard routes context guild with
            | Ok (ValueSome card) -> return! apiJson card context
            | Ok ValueNone -> return! apiFailure AdminServiceError.NotFound context
            | Error error -> return! apiFailure error context
    }

    let private apiSanctions routes (_: AdminAccount) : HttpHandler = fun context -> task {
        match! activeSanctions routes context with
        | Ok entries -> return! apiJson entries context
        | Error error -> return! apiFailure error context
    }

    let private apiAddressBans routes (_: AdminAccount) : HttpHandler = fun context -> task {
        match! addressBanList routes context with
        | Ok bans -> return! apiJson bans context
        | Error AccountAccessError.Busy -> return! apiError 503 "busy" "The panel is busy. Try again later." context
        | Error _ -> return! apiError 503 "unavailable" "The panel is temporarily unavailable." context
    }

    // --- Host --------------------------------------------------------------

    let private endpoints routes = [
        get "/static/{name}" staticFile
        get "/login" (loginPage routes)
        post "/login" (login routes)
        post "/logout" (logout routes)
        get "/setup" (setupPage routes)
        post "/setup" (setup routes)
        get "/reset" resetPage
        post "/reset" (reset routes)
        get "/" (withAdmin routes (overview routes))
        get "/partials/online" (withAdmin routes (onlinePartial routes))
        get "/players" (withAdmin routes (players routes))
        get "/players/{id}" (withAdmin routes (player routes))
        post "/players/{id}/role" (setRole routes)
        post "/players/{id}/rename" (rename routes)
        post "/players/{id}/reset-password" (administer routes true)
        post "/players/{id}/revoke" (administer routes false)
        post "/players/{id}/sanction" (sanction routes)
        post "/players/{id}/lift" (lift routes)
        post "/players/{id}/kick" (kick routes)
        get "/guilds" (withAdmin routes (guildsPage routes))
        get "/guilds/{id}" (withAdmin routes (guildPageOf routes))
        post "/guilds/{id}/appoint" (appoint routes)
        post "/guilds/{id}/dissolve" (dissolve routes)
        get "/sanctions" (withAdmin routes (sanctionsPage routes))
        get "/registration" (withAdmin routes (fun admin -> showRegistration routes admin 200 None None))
        post "/registration/mode" (setRegistration routes)
        post "/registration/players" (createPlayer routes)
        get "/address-bans" (withAdmin routes (addressBansPage routes))
        post "/address-bans/check" (checkRange routes)
        post "/address-bans" (banRange routes)
        post "/address-bans/{id}/lift" (liftRange routes)
        get "/announce" (withAdmin routes (announcePage routes))
        post "/announce" (announce routes)
        get "/audit" (withAdmin routes (auditPage routes))
        get "/tokens" (withAdmin routes (fun admin -> showTokens routes admin 200 None None))
        post "/tokens" (createToken routes)
        post "/tokens/revoke" (revokeToken routes)
        get "/config" (withAdmin routes (configuration routes))
        get "/api/v1/status" (withApi routes (apiStatus routes))
        get "/api/v1/online" (withApi routes (apiOnline routes))
        get "/api/v1/players" (withApi routes (apiPlayers routes))
        get "/api/v1/players/{id}" (withApi routes (apiPlayer routes))
        get "/api/v1/guilds" (withApi routes (apiGuilds routes))
        get "/api/v1/guilds/{id}" (withApi routes (apiGuild routes))
        get "/api/v1/sanctions" (withApi routes (apiSanctions routes))
        get "/api/v1/address-bans" (withApi routes (apiAddressBans routes))
    ]

    let private isApi (context: HttpContext) = context.Request.Path.StartsWithSegments(PathString "/api")

    /// Sign-in, setup and reset forms have their own, smaller per-address budget.
    let private rule (settings: AdminRouteSettings) (context: HttpContext) =
        let path = context.Request.Path.Value
        if HttpMethods.IsPost context.Request.Method && (path = "/login" || path = "/setup" || path = "/reset") then
            { Bucket = "admin-login"; PermitsPerMinute = settings.LoginAttemptsPerMinute }
        else { Bucket = "admin"; PermitsPerMinute = settings.RequestsPerMinute }

    let private rejected (context: HttpContext) : IResult =
        if isApi context then WebHost.error 429 "rate_limited" "Too many requests. Try again later."
        else Results.Content(renderHtml (AdminViews.errorPage 429 "Слишком много запросов. Подождите минуту." None), "text/html; charset=utf-8", System.Text.Encoding.UTF8, Nullable 429)

    let private notFound : HttpHandler = fun context ->
        if isApi context then apiError 404 "not_found" "Not found." context
        else errorPage 404 "Нет такой страницы." None context

    /// The caller starts and stops this host and owns every agent behind the ports.
    let build listener (settings: AdminRouteSettings) ports (logger: ILogger) =
        let routes = { Settings = settings; Ports = ports; Logger = logger }
        let limits = { MaxBodyBytes = MaxBodyBytes; MaxConnections = settings.MaxConnections; RequestTimeoutSeconds = settings.RequestTimeoutSeconds }
        let app = WebHost.create listener limits (rule settings) rejected logger
        app.UseFalco(endpoints routes).UseFalcoNotFound(notFound) |> ignore
        app
