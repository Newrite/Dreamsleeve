namespace Dreamsleeve.Server.Web.Admin

open System
open System.Net
open Falco.Markup
open Falco.Htmx
open Dreamsleeve.Server.Domain
open Dreamsleeve.Server.Infrastructure

/// Every page of the panel as Falco.Markup nodes. Falco.Markup encodes text
/// only through Text.enc and writes attribute values as given, so this module
/// builds attributes through attr, which encodes, and never uses Text.raw:
/// player and administrator text can never become markup.
[<RequireQualifiedAccess>]
module AdminViews =
    let private text (value: string) = Text.enc (if isNull value then "" else value)
    let private attr name (value: string) = Attr.create name (WebUtility.HtmlEncode(if isNull value then "" else value))
    let private flag name = Attr.createBool name
    let private css value = attr "class" value

    let private time (value: DateTimeOffset) = value.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'")

    let private query (pairs: (string * string) list) =
        pairs |> List.map (fun (key, value) -> key + "=" + Uri.EscapeDataString value) |> String.concat "&"

    // Script tags are never needed: the page only loads the vendored htmx,
    // configured without eval, script tags or inline indicator styles.
    [<Literal>]
    let private HtmxConfig =
        """{"allowEval":false,"allowScriptTags":false,"includeIndicatorStyles":false,"historyCacheSize":0,"selfRequestsOnly":true}"""

    type Navigation =
        | Overview
        | Players
        | Guilds
        | Registration
        | Sanctions
        | AddressBans
        | Announce
        | Audit
        | Tokens
        | Configuration
        | Outside

    let private navigation (current: Navigation) (admin: AdminAccount option) =
        match admin with
        | None -> Elem.header [] [ Elem.strong [] [ text "Dreamsleeve · админка" ] ]
        | Some admin ->
            let link target label page =
                Elem.a [ attr "href" target; (if current = page then css "current" else css "") ] [ text label ]
            Elem.header [] [
                Elem.nav [] [
                    Elem.strong [] [ text "Dreamsleeve" ]
                    link "/" "Обзор" Overview
                    link "/players" "Игроки" Players
                    link "/guilds" "Гильдии" Guilds
                    link "/registration" "Регистрация" Registration
                    link "/sanctions" "Наказания" Sanctions
                    link "/address-bans" "Баны IP" AddressBans
                    link "/announce" "Объявление" Announce
                    link "/audit" "Аудит" Audit
                    link "/tokens" "Токены API" Tokens
                    link "/config" "Конфигурация" Configuration
                ]
                Elem.form [ attr "method" "post"; attr "action" "/logout"; css "logout" ] [
                    Elem.span [] [ text (Username.value admin.Username) ]
                    Elem.button [ attr "type" "submit" ] [ text "Выйти" ]
                ]
            ]

    let page (title: string) current admin (notice: string option) (content: XmlNode list) =
        Elem.html [ attr "lang" "ru" ] [
            Elem.head [] [
                Elem.meta [ attr "charset" "utf-8" ]
                Elem.meta [ attr "name" "viewport"; attr "content" "width=device-width, initial-scale=1" ]
                Elem.meta [ attr "name" "htmx-config"; attr "content" HtmxConfig ]
                Elem.meta [ attr "name" "referrer"; attr "content" "no-referrer" ]
                Elem.title [] [ text $"{title} · Dreamsleeve" ]
                Elem.link [ attr "rel" "stylesheet"; attr "href" "/static/admin.css" ]
                Elem.script [ attr "src" "/static/htmx.min.js"; flag "defer" ] []
            ]
            Elem.body [] [
                navigation current admin
                Elem.main [] (
                    Elem.h1 [] [ text title ]
                    :: (match notice with
                        | Some message -> Elem.p [ css "notice"; attr "role" "status" ] [ text message ]
                        | None -> Text.empty)
                    :: content)
            ]
        ]

    let private field label name kind (value: string) (extra: XmlAttribute list) =
        Elem.label [] [
            Elem.span [] [ text label ]
            Elem.input ([ attr "type" kind; attr "name" name; attr "value" value ] @ extra)
        ]

    let private submit label = Elem.button [ attr "type" "submit" ] [ text label ]

    let private sanctionKind key =
        match SanctionKind.ofKey key with
        | Some SanctionKind.Mute -> "мут"
        | Some SanctionKind.Ban -> "бан"
        | None -> key

    let private until (sanction: SanctionModel) =
        if sanction.Expires.HasValue then time sanction.Expires.Value else "бессрочно"

    /// A dangerous action is sent only with the box ticked; the server checks it too.
    let private confirm label =
        Elem.label [ css "confirm" ] [
            Elem.input [ attr "type" "checkbox"; attr "name" "confirm"; attr "value" "yes"; flag "required" ]
            Elem.span [] [ text label ]
        ]

    let private error (message: string option) =
        match message with
        | Some message -> Elem.p [ css "error"; attr "role" "alert" ] [ text message ]
        | None -> Text.empty

    let errorPage status (message: string) admin =
        page $"Ошибка {status}" Outside admin None [ Elem.p [ css "error" ] [ text message ]; Elem.p [] [ Elem.a [ attr "href" "/" ] [ text "На главную" ] ] ]

    let login (failure: string option) (username: string) =
        page "Вход" Outside None None [
            error failure
            Elem.form [ attr "method" "post"; attr "action" "/login"; css "stack" ] [
                field "Имя администратора" "username" "text" username [ flag "required"; attr "autocomplete" "username" ]
                field "Пароль" "password" "password" "" [ flag "required"; attr "autocomplete" "current-password" ]
                submit "Войти"
            ]
            Elem.p [ css "hint" ] [
                text "Нет администратора — "
                Elem.a [ attr "href" "/setup" ] [ text "первичная настройка" ]
                text "; забыт пароль — "
                Elem.a [ attr "href" "/reset" ] [ text "сброс по коду из консоли" ]
                text "."
            ]
        ]

    let setup (configured: bool) (failure: string option) (username: string) =
        page "Первичная настройка" Outside None None [
            if configured then
                Elem.p [] [ text "Администратор уже создан. " ; Elem.a [ attr "href" "/login" ] [ text "Войти" ] ]
            else
                Elem.p [ css "hint" ] [ text "Код настройки печатается в консоли сервера при запуске и командой admin-setup. Он одноразовый и действует ограниченное время." ]
                error failure
                Elem.form [ attr "method" "post"; attr "action" "/setup"; css "stack" ] [
                    field "Код настройки" "code" "text" "" [ flag "required"; attr "autocomplete" "off" ]
                    field "Имя администратора" "username" "text" username [ flag "required"; attr "autocomplete" "username" ]
                    field "Пароль (12–128 байт UTF-8)" "password" "password" "" [ flag "required"; attr "autocomplete" "new-password" ]
                    field "Пароль ещё раз" "password2" "password" "" [ flag "required"; attr "autocomplete" "new-password" ]
                    submit "Создать администратора"
                ]
        ]

    let reset (failure: string option) =
        page "Новый пароль администратора" Outside None None [
            Elem.p [ css "hint" ] [ text "Код выдаёт команда консоли admin-reset <имя>. После смены пароля остальные сессии этого администратора закрываются." ]
            error failure
            Elem.form [ attr "method" "post"; attr "action" "/reset"; css "stack" ] [
                field "Код сброса" "code" "text" "" [ flag "required"; attr "autocomplete" "off" ]
                field "Новый пароль" "password" "password" "" [ flag "required"; attr "autocomplete" "new-password" ]
                field "Пароль ещё раз" "password2" "password" "" [ flag "required"; attr "autocomplete" "new-password" ]
                submit "Сменить пароль"
            ]
        ]

    let private identity (row: OnlineModel) =
        match row.Hidden, row.Pseudonym with
        | ("everywhere" | "except_ground_marks") as hidden, pseudonym when not (isNull pseudonym) ->
            let where = if hidden = "everywhere" then "везде" else "кроме меток"
            [ Elem.span [ css "pseudonym" ] [ text $"~{pseudonym}" ]; Elem.small [] [ text $" ({where})" ] ]
        | _ -> [ Elem.small [] [ text "показано" ] ]

    let private playerLink (playerId: Nullable<uint64>) =
        if playerId.HasValue then Elem.a [ attr "href" $"/players/{playerId.Value}" ] [ text (string playerId.Value) ]
        else text "—"

    /// The online table; htmx replaces the whole section every 5 seconds.
    /// Falco.Htmx writes the constant hx-* attributes; nothing dynamic goes through it.
    let online (result: Result<OnlineModel list, AdminServiceError>) =
        let rows, available = match result with Ok rows -> rows, true | Error _ -> [], false
        Elem.section [ attr "id" "online"; Hx.get "/partials/online"; Hx.trigger "every 5s"; Hx.swapOuterHtml ] [
            let guests = rows |> List.filter (fun row -> row.Phase = AdminModels.guestPhase) |> List.length
            Elem.h2 [] [ text (if not available then "Онлайн (недоступно)" elif guests = 0 then $"Онлайн ({rows.Length})" else $"Онлайн ({rows.Length}, из них гостей {guests})") ]
            if not available then Elem.p [ css "error" ] [ text "Рантайм не ответил; данные устарели." ]
            Elem.table [] [
                Elem.thead [] [
                    Elem.tr [] [
                        for heading in [ "PlayerId"; "IP"; "Username"; "Display name"; "Персонаж"; "Имя для других"; "Роль"; "Место"; "Фаза"; "Подключён" ] do
                            Elem.th [] [ text heading ]
                    ]
                ]
                Elem.tbody [] [
                    for row in rows do
                        Elem.tr [] [
                            Elem.td [] [ playerLink row.PlayerId ]
                            Elem.td [] [
                                Elem.code [] [ text row.Address ]
                                if not (isNull row.Proxy) then
                                    Elem.br []
                                    Elem.small [] [ text "через прокси "; Elem.code [] [ text row.Proxy ] ]
                            ]
                            if row.Described then
                                Elem.td [] [ text row.Username ]
                                Elem.td [] [ text row.DisplayName ]
                                Elem.td [] [
                                    text (if isNull row.CharacterName then "—" else row.CharacterName)
                                    if row.CharacterWithheld then Elem.small [] [ text " (скрыто словарём)" ]
                                ]
                                Elem.td [] (identity row)
                                Elem.td [] [ text row.Role ]
                                Elem.td [] [ text (if isNull row.Location then "—" else row.Location) ]
                            else
                                // A guest has not signed in: there is nothing to describe.
                                let note = if row.DescriptionStatus = "unavailable" then "недоступно" elif row.Phase = AdminModels.guestPhase then "гость" else "ещё не открыта"
                                Elem.td [ attr "colspan" "6"; css "muted" ] [ text note ]
                            Elem.td [] [ text row.Phase ]
                            Elem.td [] [ text (time row.ConnectedAt) ]
                        ]
                ]
            ]
        ]

    let overview admin (status: Result<StatusModel, AdminServiceError>) (rows: Result<OnlineModel list, AdminServiceError>) =
        page "Обзор" Overview (Some admin) None [
            Elem.section [] [
                Elem.h2 [] [ text "Сервер" ]
                match status with
                | Ok status ->
                    Elem.dl [] [
                        for label, value in [ "Соединения", string status.Connections; "Гости", string status.Guests; "Готовы", string status.Ready
                                              "Резервы PlayerId", string status.Reservations; "Закрываются", string status.Closing
                                              "Остановка", (if status.Stopping then "да" else "нет") ] do
                            Elem.dt [] [ text label ]
                            Elem.dd [] [ text value ]
                    ]
                | Error _ -> Elem.p [ css "error" ] [ text "Рантайм не ответил." ]
            ]
            online rows
        ]

    let players admin (model: PlayerPageModel) =
        let pages = max 1 ((model.Total + model.PageSize - 1) / model.PageSize)
        let link label number =
            Elem.a [ attr "href" ("/players?" + query [ "q", model.Query; "page", string number ]) ] [ text label ]
        page "Игроки" Players (Some admin) None [
            Elem.form [ attr "method" "get"; attr "action" "/players"; css "inline" ] [
                Elem.input [ attr "type" "search"; attr "name" "q"; attr "value" model.Query; attr "placeholder" "username, display name или PlayerId" ]
                submit "Найти"
            ]
            Elem.p [ css "muted" ] [ text $"Найдено: {model.Total}. Страница {model.Page} из {pages}." ]
            Elem.table [] [
                Elem.thead [] [ Elem.tr [] [ for heading in [ "PlayerId"; "Username"; "Display name"; "Роль"; "Онлайн" ] do Elem.th [] [ text heading ] ] ]
                Elem.tbody [] [
                    for player in model.Players do
                        Elem.tr [] [
                            Elem.td [] [ Elem.a [ attr "href" $"/players/{player.PlayerId}" ] [ text (string player.PlayerId) ] ]
                            Elem.td [] [ text player.Username ]
                            Elem.td [] [ text player.DisplayName ]
                            Elem.td [] [ text player.Role ]
                            Elem.td [] [ text (if player.Online then "да" else "") ]
                        ]
                ]
            ]
            Elem.p [ css "pager" ] [
                if model.Page > 1 then link "← назад" (model.Page - 1)
                if model.Page < pages then link "вперёд →" (model.Page + 1)
            ]
        ]

    let private guildRole key =
        match GuildRole.ofKey key with
        | Some GuildRole.Master -> "глава"
        | Some GuildRole.Officer -> "офицер"
        | Some GuildRole.Member -> "участник"
        | None -> key

    let private guildLink (guild: GuildModel) = Elem.a [ attr "href" $"/guilds/{guild.GuildId}" ] [ text guild.Name ]

    let private master (guild: GuildModel) =
        if guild.MasterId.HasValue then Elem.a [ attr "href" $"/players/{guild.MasterId.Value}" ] [ text guild.Master ]
        else Elem.span [ css "error" ] [ text "нет — назначьте" ]

    let guilds admin (model: GuildPageModel) (notice: string option) =
        let pages = max 1 ((model.Total + model.PageSize - 1) / model.PageSize)
        let link label number =
            Elem.a [ attr "href" ("/guilds?" + query [ "q", model.Query; "page", string number ]) ] [ text label ]
        page "Гильдии" Guilds (Some admin) notice [
            Elem.form [ attr "method" "get"; attr "action" "/guilds"; css "inline" ] [
                Elem.input [ attr "type" "search"; attr "name" "q"; attr "value" model.Query; attr "placeholder" "часть названия или ID гильдии" ]
                submit "Найти"
            ]
            Elem.p [ css "muted" ] [ text $"Найдено: {model.Total}. Страница {model.Page} из {pages}." ]
            Elem.table [] [
                Elem.thead [] [ Elem.tr [] [ for heading in [ "ID"; "Название"; "Глава"; "Участников"; "Создана" ] do Elem.th [] [ text heading ] ] ]
                Elem.tbody [] [
                    for guild in model.Guilds do
                        Elem.tr [] [
                            Elem.td [] [ text (string guild.GuildId) ]
                            Elem.td [] [ guildLink guild ]
                            Elem.td [] [ master guild ]
                            Elem.td [] [ text (string guild.Members) ]
                            Elem.td [] [ text (time guild.CreatedAt) ]
                        ]
                ]
            ]
            Elem.p [ css "pager" ] [
                if model.Page > 1 then link "← назад" (model.Page - 1)
                if model.Page < pages then link "вперёд →" (model.Page + 1)
            ]
        ]

    let guild admin (card: GuildCardModel) (notice: string option) (failure: string option) =
        let id = card.Guild.GuildId
        let action path = attr "action" $"/guilds/{id}/{path}"
        page $"Гильдия {card.Guild.Name}" Guilds (Some admin) notice [
            error failure
            Elem.dl [] [
                Elem.dt [] [ text "ID" ]
                Elem.dd [] [ text (string id) ]
                Elem.dt [] [ text "Глава" ]
                Elem.dd [] [ master card.Guild ]
                Elem.dt [] [ text "Участников" ]
                Elem.dd [] [ text (string card.Guild.Members) ]
                Elem.dt [] [ text "Создана" ]
                Elem.dd [] [ text (time card.Guild.CreatedAt) ]
            ]
            Elem.section [] [
                Elem.h2 [] [ text "Участники" ]
                Elem.p [ css "muted" ] [ text "Настоящие имена: в гильдии псевдонимы не действуют." ]
                Elem.table [] [
                    Elem.thead [] [
                        Elem.tr [] [ for heading in [ "PlayerId"; "Username"; "Display name"; "Роль"; "Онлайн"; "В гильдии с"; "Мут в гильдии" ] do Elem.th [] [ text heading ] ]
                    ]
                    Elem.tbody [] [
                        for entry in card.Members do
                            Elem.tr [] [
                                Elem.td [] [ Elem.a [ attr "href" $"/players/{entry.PlayerId}" ] [ text (string entry.PlayerId) ] ]
                                Elem.td [] [ text entry.Username ]
                                Elem.td [] [ text entry.DisplayName ]
                                Elem.td [] [ text (guildRole entry.Role) ]
                                Elem.td [] [ text (if entry.Online then "да" else "") ]
                                Elem.td [] [ text (time entry.JoinedAt) ]
                                Elem.td [] [
                                    if entry.Muted then
                                        let ends = if entry.MuteExpires.HasValue then time entry.MuteExpires.Value else "бессрочно"
                                        text $"до {ends}: {entry.MuteReason} ({entry.MutedBy})"
                                    else text ""
                                ]
                            ]
                    ]
                ]
            ]
            Elem.section [] [
                Elem.h2 [] [ text "Приглашения" ]
                if card.Invites.IsEmpty then Elem.p [ css "muted" ] [ text "Ожидающих приглашений нет." ]
                else
                    Elem.table [] [
                        Elem.thead [] [ Elem.tr [] [ for heading in [ "PlayerId"; "Username"; "Пригласил"; "Отправлено"; "Истекает" ] do Elem.th [] [ text heading ] ] ]
                        Elem.tbody [] [
                            for invite in card.Invites do
                                Elem.tr [] [
                                    Elem.td [] [ Elem.a [ attr "href" $"/players/{invite.PlayerId}" ] [ text (string invite.PlayerId) ] ]
                                    Elem.td [] [ text (if isNull invite.Username then "—" else invite.Username) ]
                                    Elem.td [] [ text invite.InvitedBy ]
                                    Elem.td [] [ text (time invite.CreatedAt) ]
                                    Elem.td [] [ text (time invite.Expires) ]
                                ]
                        ]
                    ]
            ]
            Elem.section [ css "actions" ] [
                Elem.h2 [] [ text "Действия" ]
                let candidates = card.Members |> List.filter (fun entry -> entry.Role <> GuildRole.key GuildRole.Master)
                if not candidates.IsEmpty then
                    Elem.form [ attr "method" "post"; action "appoint"; css "stack" ] [
                        Elem.p [ css "hint" ] [
                            text "Когда глава забанен или удалён: игроки сами называют, кого поставить. Прежний глава, если он в гильдии, становится офицером."
                        ]
                        Elem.label [] [
                            Elem.span [] [ text "Новый глава" ]
                            Elem.select [ attr "name" "player" ] [
                                for entry in candidates do
                                    Elem.option [ attr "value" (string entry.PlayerId) ] [ text $"{entry.DisplayName} ({entry.Username}, {guildRole entry.Role})" ]
                            ]
                        ]
                        confirm "Подтверждаю смену главы"
                        submit "Назначить главой"
                    ]
                Elem.form [ attr "method" "post"; action "dissolve"; css "stack" ] [
                    Elem.p [ css "hint" ] [ text "Например, за название против правил. Участники и приглашения удаляются, история чата гильдии пропадает, название освобождается." ]
                    confirm "Подтверждаю роспуск гильдии"
                    submit "Распустить"
                ]
            ]
        ]

    let private rangeLink (range: string) label =
        Elem.a [ attr "href" ("/address-bans?" + query [ "range", range ]) ] [ text label ]

    let player admin (card: PlayerCardModel) (notice: string option) (failure: string option) (resetCode: string option) (displayNameLimit: int) (historyDays: int) =
        let id = card.Player.PlayerId
        let action path = attr "action" $"/players/{id}/{path}"
        page $"Игрок {id}" Players (Some admin) notice [
            error failure
            match resetCode with
            | Some code ->
                Elem.section [ css "secret" ] [
                    Elem.h2 [] [ text "Одноразовый код сброса пароля" ]
                    Elem.p [] [ text "Показывается один раз. Передайте его игроку приватно; прежний вход отозван." ]
                    Elem.pre [] [ text code ]
                ]
            | None -> ()
            Elem.dl [] [
                for label, value in [ "PlayerId", string id; "Username", card.Player.Username; "Display name", card.Player.DisplayName
                                      "Роль", card.Player.Role; "Онлайн", (if card.Player.Online then "да" else "нет") ] do
                    Elem.dt [] [ text label ]
                    Elem.dd [] [ text value ]
            ]
            if not card.Sessions.IsEmpty then online (Ok card.Sessions)
            Elem.section [] [
                Elem.h2 [] [ text "Гильдии" ]
                if card.Guilds.IsEmpty then Elem.p [ css "muted" ] [ text "Не состоит в гильдиях." ]
                else
                    Elem.table [] [
                        Elem.thead [] [ Elem.tr [] [ for heading in [ "Гильдия"; "Роль"; "Глава"; "Участников" ] do Elem.th [] [ text heading ] ] ]
                        Elem.tbody [] [
                            for entry in card.Guilds do
                                Elem.tr [] [
                                    Elem.td [] [ guildLink entry.Guild ]
                                    Elem.td [] [ text (guildRole entry.Role) ]
                                    Elem.td [] [ master entry.Guild ]
                                    Elem.td [] [ text (string entry.Guild.Members) ]
                                ]
                        ]
                    ]
            ]
            Elem.section [] [
                Elem.h2 [] [ text "Наказания" ]
                if card.Sanctions.IsEmpty then Elem.p [ css "muted" ] [ text "Действующих наказаний нет." ]
                else
                    Elem.table [] [
                        Elem.thead [] [ Elem.tr [] [ for heading in [ "Вид"; "До"; "Причина"; "Выдал"; "" ] do Elem.th [] [ text heading ] ] ]
                        Elem.tbody [] [
                            for sanction in card.Sanctions do
                                Elem.tr [] [
                                    Elem.td [] [ text (sanctionKind sanction.Kind) ]
                                    Elem.td [] [ text (until sanction) ]
                                    Elem.td [] [ text sanction.Reason ]
                                    Elem.td [] [ text (if isNull sanction.IssuedBy then "—" else sanction.IssuedBy) ]
                                    Elem.td [] [
                                        Elem.form [ attr "method" "post"; action "lift"; css "inline" ] [
                                            Elem.input [ attr "type" "hidden"; attr "name" "kind"; attr "value" sanction.Kind ]
                                            confirm "Подтверждаю"
                                            submit "Снять"
                                        ]
                                    ]
                                ]
                        ]
                    ]
            ]
            Elem.section [] [
                Elem.h2 [] [ text $"Адреса входа (за {historyDays} дн.)" ]
                if card.Addresses.IsEmpty then Elem.p [ css "muted" ] [ text "Входов с записанным адресом нет." ]
                else
                    Elem.table [] [
                        Elem.thead [] [ Elem.tr [] [ for heading in [ "IP"; "Первый вход"; "Последний вход"; "Входов"; "" ] do Elem.th [] [ text heading ] ] ]
                        Elem.tbody [] [
                            for address in card.Addresses do
                                Elem.tr [] [
                                    Elem.td [] [ Elem.code [] [ text address.Address ] ]
                                    Elem.td [] [ text (time address.FirstSeen) ]
                                    Elem.td [] [ text (time address.LastSeen) ]
                                    Elem.td [] [ text (string address.SignIns) ]
                                    Elem.td [] [ rangeLink address.Range $"Бан {address.Range}…" ]
                                ]
                        ]
                    ]
            ]
            Elem.section [] [
                Elem.h2 [] [ text $"Устройства (за {historyDays} дн.)" ]
                if card.Devices.IsEmpty then Elem.p [ css "muted" ] [ text "Входов с известным устройством нет." ]
                else
                    Elem.table [] [
                        Elem.thead [] [ Elem.tr [] [ for heading in [ "Устройство"; "Первый вход"; "Последний вход"; "Входов" ] do Elem.th [] [ text heading ] ] ]
                        Elem.tbody [] [
                            for device in card.Devices do
                                Elem.tr [] [
                                    Elem.td [] [ Elem.code [] [ text device.Device ] ]
                                    Elem.td [] [ text (time device.FirstSeen) ]
                                    Elem.td [] [ text (time device.LastSeen) ]
                                    Elem.td [] [ text (string device.SignIns) ]
                                ]
                        ]
                    ]
            ]
            Elem.section [] [
                Elem.h2 [] [ text "История имён" ]
                if card.Names.IsEmpty then Elem.p [ css "muted" ] [ text "Display name не менялось." ]
                else
                    Elem.table [] [
                        Elem.thead [] [ Elem.tr [] [ for heading in [ "Когда"; "Было"; "Стало"; "Кто" ] do Elem.th [] [ text heading ] ] ]
                        Elem.tbody [] [
                            for change in card.Names do
                                Elem.tr [] [
                                    Elem.td [] [ text (time change.At) ]
                                    Elem.td [] [ text change.OldName ]
                                    Elem.td [] [ text change.NewName ]
                                    Elem.td [] [ text (if isNull change.ChangedBy then "игрок" else $"администратор {change.ChangedBy}") ]
                                ]
                        ]
                    ]
            ]
            Elem.section [ css "actions" ] [
                Elem.h2 [] [ text "Действия" ]
                Elem.form [ attr "method" "post"; action "role"; css "stack" ] [
                    Elem.label [] [
                        Elem.span [] [ text "Роль" ]
                        Elem.select [ attr "name" "role" ] [
                            for role in PlayerRole.all do
                                let key = PlayerRole.key role
                                Elem.option [ attr "value" key; (if key = card.Player.Role then flag "selected" else css "") ] [ text key ]
                        ]
                    ]
                    confirm "Подтверждаю смену роли"
                    submit "Сохранить роль"
                ]
                Elem.form [ attr "method" "post"; action "rename"; css "stack" ] [
                    field "Новое display name" "displayName" "text" card.Player.DisplayName [ flag "required"; attr "maxlength" (string displayNameLimit) ]
                    confirm "Подтверждаю переименование"
                    submit "Переименовать"
                ]
                Elem.form [ attr "method" "post"; action "reset-password"; css "stack" ] [
                    Elem.p [ css "hint" ] [ text "Новый одноразовый код; сохранённые входы и открытая сессия игрока отзываются." ]
                    confirm "Подтверждаю сброс пароля"
                    submit "Выдать код сброса"
                ]
                Elem.form [ attr "method" "post"; action "revoke"; css "stack" ] [
                    Elem.p [ css "hint" ] [ text "Отзывает сохранённые входы и закрывает сессию; пароль не меняется." ]
                    confirm "Подтверждаю отзыв доступа"
                    submit "Отозвать доступ"
                ]
                Elem.form [ attr "method" "post"; action "sanction"; css "stack" ] [
                    Elem.p [ css "hint" ] [
                        text "Мут запрещает писать в чат, оставлять надписи, публиковать объявления модов и менять имя; бан закрывает сессию и вход. "
                        text "Новое наказание того же вида заменяет действующее."
                    ]
                    Elem.label [] [
                        Elem.span [] [ text "Вид" ]
                        Elem.select [ attr "name" "kind" ] [
                            for kind in SanctionKind.all do
                                let key = SanctionKind.key kind
                                Elem.option [ attr "value" key ] [ text (sanctionKind key) ]
                        ]
                    ]
                    Elem.label [] [
                        Elem.span [] [ text "Срок" ]
                        Elem.select [ attr "name" "term" ] [
                            for label, minutes in AdminModels.sanctionTerms do
                                Elem.option [ attr "value" (match minutes with ValueSome minutes -> string minutes | ValueNone -> "") ] [ text label ]
                            Elem.option [ attr "value" "custom" ] [ text "Своё число минут" ]
                        ]
                    ]
                    field "Минут (для своего срока)" "minutes" "number" "" [ attr "min" "1"; attr "max" (string SanctionTerm.MaxMinutes) ]
                    field "Причина (видна игроку)" "reason" "text" "" [ flag "required"; attr "maxlength" (string SanctionReason.MaxLength) ]
                    Elem.label [ css "confirm" ] [
                        Elem.input [ attr "type" "checkbox"; attr "name" "devices"; attr "value" "yes" ]
                        Elem.span [] [ text "Бан закрывает и устройства игрока: с них не войти и не зарегистрироваться, пока бан действует" ]
                    ]
                    confirm "Подтверждаю наказание"
                    submit "Наказать"
                ]
                if card.Player.Online then
                    Elem.form [ attr "method" "post"; action "kick"; css "stack" ] [
                        Elem.p [ css "hint" ] [ text "Закрывает текущую сессию; войти снова можно сразу." ]
                        field "Причина (видна игроку)" "reason" "text" "" [ flag "required"; attr "maxlength" (string SanctionReason.MaxLength) ]
                        confirm "Подтверждаю кик"
                        submit "Кикнуть"
                    ]
            ]
        ]

    let private termLabel (term: string) (minutes: string) =
        match term with
        | "" -> "бессрочно"
        | "custom" -> $"{minutes} мин"
        | preset ->
            AdminModels.sanctionTerms
            |> List.tryPick (fun (label, value) -> match value with ValueSome value when string value = preset -> Some label | _ -> None)
            |> Option.defaultValue $"{preset} мин"

    let addressBans admin (bans: AddressBanModel list) (notice: string option) (failure: string option) (range: string)
                    (check: RangeCheckModel option) (historyDays: int) =
        page "Баны IP" AddressBans (Some admin) notice [
            error failure
            match check with
            | Some check ->
                Elem.section [ css "secret" ] [
                    Elem.h2 [] [ text $"Проверка диапазона {check.Range}" ]
                    Elem.p [] [
                        text $"Сейчас онлайн из диапазона: {check.Online.Length}. Входили из него за {historyDays} дн.: {check.Players.Length} игроков. "
                        text "Если среди них есть посторонние, это общий адрес (NAT оператора, общежитие): сузьте диапазон или баньте аккаунт."
                    ]
                    if not check.Online.IsEmpty then
                        Elem.table [] [
                            Elem.thead [] [ Elem.tr [] [ for heading in [ "PlayerId"; "IP"; "Username"; "Фаза" ] do Elem.th [] [ text heading ] ] ]
                            Elem.tbody [] [
                                for row in check.Online do
                                    Elem.tr [] [
                                        Elem.td [] [ playerLink row.PlayerId ]
                                        Elem.td [] [ Elem.code [] [ text row.Address ] ]
                                        Elem.td [] [ text (if isNull row.Username then "—" else row.Username) ]
                                        Elem.td [] [ text row.Phase ]
                                    ]
                            ]
                        ]
                    if not check.Players.IsEmpty then
                        Elem.table [] [
                            Elem.thead [] [ Elem.tr [] [ for heading in [ "PlayerId"; "Username"; "Display name"; "IP"; "Последний вход" ] do Elem.th [] [ text heading ] ] ]
                            Elem.tbody [] [
                                for player in check.Players do
                                    Elem.tr [] [
                                        Elem.td [] [ Elem.a [ attr "href" $"/players/{player.PlayerId}" ] [ text (string player.PlayerId) ] ]
                                        Elem.td [] [ text player.Username ]
                                        Elem.td [] [ text player.DisplayName ]
                                        Elem.td [] [ Elem.code [] [ text player.Address ] ]
                                        Elem.td [] [ text (time player.LastSeen) ]
                                    ]
                            ]
                        ]
                    Elem.form [ attr "method" "post"; attr "action" "/address-bans"; css "stack" ] [
                        Elem.input [ attr "type" "hidden"; attr "name" "range"; attr "value" check.Range ]
                        Elem.input [ attr "type" "hidden"; attr "name" "term"; attr "value" check.Term ]
                        Elem.input [ attr "type" "hidden"; attr "name" "minutes"; attr "value" check.Minutes ]
                        Elem.input [ attr "type" "hidden"; attr "name" "reason"; attr "value" check.Reason ]
                        Elem.p [] [ text $"Срок: {termLabel check.Term check.Minutes}. Причина: {check.Reason}" ]
                        confirm "Подтверждаю бан диапазона"
                        submit "Забанить диапазон"
                    ]
                ]
            | None -> ()
            Elem.section [] [
                Elem.h2 [] [ text "Новый бан" ]
                Elem.p [ css "hint" ] [
                    text $"Адрес или CIDR: IPv4 не шире /{AddressRange.MinIpv4Prefix}, IPv6 не шире /{AddressRange.MinIpv6Prefix}. "
                    text "Один IPv6-адрес банится обычно целым /64. Бан закрывает вход, регистрацию и подключение игры из диапазона, "
                    text "открытые сессии из него сразу завершаются. Сначала панель покажет, кого он заденет."
                ]
                Elem.form [ attr "method" "post"; attr "action" "/address-bans/check"; css "stack" ] [
                    field "Диапазон" "range" "text" range [ flag "required"; attr "placeholder" "203.0.113.0/24"; attr "autocomplete" "off" ]
                    Elem.label [] [
                        Elem.span [] [ text "Срок" ]
                        Elem.select [ attr "name" "term" ] [
                            for label, minutes in AdminModels.sanctionTerms do
                                Elem.option [ attr "value" (match minutes with ValueSome minutes -> string minutes | ValueNone -> "") ] [ text label ]
                            Elem.option [ attr "value" "custom" ] [ text "Своё число минут" ]
                        ]
                    ]
                    field "Минут (для своего срока)" "minutes" "number" "" [ attr "min" "1"; attr "max" (string SanctionTerm.MaxMinutes) ]
                    field "Причина (видна игроку при попытке входа)" "reason" "text" "" [ flag "required"; attr "maxlength" (string SanctionReason.MaxLength) ]
                    submit "Проверить диапазон"
                ]
            ]
            Elem.section [] [
                Elem.h2 [] [ text "Действующие баны" ]
                if bans.IsEmpty then Elem.p [ css "muted" ] [ text "Действующих банов диапазонов нет." ]
                else
                    Elem.table [] [
                        Elem.thead [] [ Elem.tr [] [ for heading in [ "Диапазон"; "До"; "Причина"; "Выдано"; "Выдал"; "" ] do Elem.th [] [ text heading ] ] ]
                        Elem.tbody [] [
                            for ban in bans do
                                Elem.tr [] [
                                    Elem.td [] [ Elem.code [] [ text ban.Range ] ]
                                    Elem.td [] [ text (if ban.Expires.HasValue then time ban.Expires.Value else "бессрочно") ]
                                    Elem.td [] [ text ban.Reason ]
                                    Elem.td [] [ text (time ban.IssuedAt) ]
                                    Elem.td [] [ text (if isNull ban.IssuedBy then "—" else ban.IssuedBy) ]
                                    Elem.td [] [
                                        Elem.form [ attr "method" "post"; attr "action" $"/address-bans/{ban.Id}/lift"; css "inline" ] [
                                            confirm "Подтверждаю"
                                            submit "Снять"
                                        ]
                                    ]
                                ]
                        ]
                    ]
            ]
        ]

    let sanctions admin (entries: SanctionEntryModel list) =
        page "Наказания" Sanctions (Some admin) None [
            Elem.p [ css "muted" ] [ text "Действующие муты и баны; снять наказание можно в карточке игрока." ]
            if entries.IsEmpty then Elem.p [ css "muted" ] [ text "Действующих наказаний нет." ]
            else
                Elem.table [] [
                    Elem.thead [] [ Elem.tr [] [ for heading in [ "PlayerId"; "Username"; "Display name"; "Вид"; "До"; "Причина"; "Выдано"; "Выдал" ] do Elem.th [] [ text heading ] ] ]
                    Elem.tbody [] [
                        for entry in entries do
                            Elem.tr [] [
                                Elem.td [] [ Elem.a [ attr "href" $"/players/{entry.PlayerId}" ] [ text (string entry.PlayerId) ] ]
                                Elem.td [] [ text entry.Username ]
                                Elem.td [] [ text entry.DisplayName ]
                                Elem.td [] [ text (sanctionKind entry.Sanction.Kind) ]
                                Elem.td [] [ text (until entry.Sanction) ]
                                Elem.td [] [ text entry.Sanction.Reason ]
                                Elem.td [] [ text (time entry.Sanction.IssuedAt) ]
                                Elem.td [] [ text (if isNull entry.Sanction.IssuedBy then "—" else entry.Sanction.IssuedBy) ]
                            ]
                    ]
                ]
        ]

    let private registrationLabel mode =
        match mode with
        | RegistrationMode.Open -> "Открыта — из игры паролем и через Steam"
        | RegistrationMode.Steam -> "Только Steam — новые аккаунты только входом через Steam"
        | RegistrationMode.Manual -> "Вручную — аккаунты создаёт администратор на этой странице"

    let registration admin (mode: RegistrationMode) (notice: string option) (failure: string option)
                     (created: CreatedPlayerModel option) (setupHours: int) (usernameLimit: int) (displayNameLimit: int) =
        page "Регистрация" Registration (Some admin) notice [
            error failure
            match created with
            | Some player ->
                Elem.section [ css "secret" ] [
                    Elem.h2 [] [ text $"Игрок {player.Username} создан" ]
                    Elem.p [] [ text $"Одноразовый код установки пароля, действует {setupHours} ч. Показывается один раз; передайте его игроку приватно." ]
                    Elem.pre [] [ text player.SetupCode ]
                    Elem.p [] [
                        text "В игре: окно чата → ☰ → «Аккаунт» → «Пароль по коду»: код и новый пароль, затем обычный вход с именем "
                        Elem.code [] [ text player.Username ]
                        text ". "
                        Elem.a [ attr "href" $"/players/{player.PlayerId}" ] [ text "Карточка игрока" ]
                    ]
                ]
            | None -> ()
            Elem.section [] [
                Elem.h2 [] [ text "Кто может регистрироваться" ]
                Elem.form [ attr "method" "post"; attr "action" "/registration/mode"; css "stack" ] [
                    Elem.label [] [
                        Elem.span [] [ text "Режим" ]
                        Elem.select [ attr "name" "mode" ] [
                            for candidate in RegistrationMode.all do
                                Elem.option [ attr "value" (RegistrationMode.key candidate); (if candidate = mode then flag "selected" else css "") ] [
                                    text (registrationLabel candidate)
                                ]
                        ]
                    ]
                    Elem.p [ css "hint" ] [
                        text "Действует сразу, без перезапуска. Существующие игроки входят как прежде; созданием игроков ниже режим не ограничивает. "
                        text "«Только Steam» имеет смысл при включённом входе через Steam."
                    ]
                    confirm "Подтверждаю смену режима"
                    submit "Сохранить режим"
                ]
            ]
            Elem.section [] [
                Elem.h2 [] [ text "Создать игрока" ]
                Elem.p [ css "hint" ] [ text "Аккаунт без пароля и одноразовый код, по которому игрок задаёт пароль сам. Имена проверяются словарём, как при регистрации." ]
                Elem.form [ attr "method" "post"; attr "action" "/registration/players"; css "stack" ] [
                    field "Имя пользователя (латиница, цифры, _ и точка)" "username" "text" "" [ flag "required"; attr "maxlength" (string usernameLimit); attr "autocomplete" "off" ]
                    field "Display name" "displayName" "text" "" [ flag "required"; attr "maxlength" (string displayNameLimit) ]
                    confirm "Подтверждаю создание игрока"
                    submit "Создать и выдать код"
                ]
            ]
        ]

    let announce admin (notice: string option) (failure: string option) (maxLength: int) =
        page "Объявление" Announce (Some admin) notice [
            error failure
            Elem.form [ attr "method" "post"; attr "action" "/announce"; css "stack" ] [
                Elem.label [] [
                    Elem.span [] [ text $"Текст (до {maxLength} символов)" ]
                    Elem.textarea [ attr "name" "text"; attr "rows" "4"; attr "maxlength" (string maxLength); flag "required" ] []
                ]
                Elem.label [] [
                    Elem.span [] [ text "Вид" ]
                    Elem.select [ attr "name" "kind" ] [
                        for kind in AdminAnnouncement.kinds do
                            let key = AdminAnnouncement.kindKey kind
                            Elem.option [ attr "value" key ] [ text key ]
                    ]
                ]
                confirm "Отправить всем в системный канал"
                submit "Опубликовать"
            ]
        ]

    let audit admin (entries: AuditModel list) =
        page "Аудит" Audit (Some admin) None [
            Elem.p [ css "muted" ] [ text "Последние 200 действий администраторов и модераторов." ]
            Elem.table [] [
                Elem.thead [] [ Elem.tr [] [ for heading in [ "Когда"; "Кто"; "Действие"; "Цель"; "Подробности" ] do Elem.th [] [ text heading ] ] ]
                Elem.tbody [] [
                    for entry in entries do
                        Elem.tr [] [
                            Elem.td [] [ text (time entry.At) ]
                            Elem.td [] [ text entry.Actor ]
                            Elem.td [] [ text entry.Action ]
                            Elem.td [] [ text entry.Target ]
                            Elem.td [] [ text entry.Details ]
                        ]
                ]
            ]
        ]

    let tokens admin (models: TokenModel list) (notice: string option) (failure: string option) (created: string option) =
        page "Токены API" Tokens (Some admin) notice [
            error failure
            match created with
            | Some secret ->
                Elem.section [ css "secret" ] [
                    Elem.h2 [] [ text "Новый токен" ]
                    Elem.p [] [ text "Показывается один раз; в базе хранится только хеш. Заголовок: Authorization: Bearer <токен>." ]
                    Elem.pre [] [ text secret ]
                ]
            | None -> ()
            Elem.form [ attr "method" "post"; attr "action" "/tokens"; css "inline" ] [
                Elem.input [ attr "type" "text"; attr "name" "label"; attr "placeholder" "Метка, 1–64 символа"; attr "maxlength" "64"; flag "required" ]
                submit "Создать токен"
            ]
            Elem.table [] [
                Elem.thead [] [ Elem.tr [] [ for heading in [ "Метка"; "Префикс хеша"; "Создал"; "Когда"; "" ] do Elem.th [] [ text heading ] ] ]
                Elem.tbody [] [
                    for token in models do
                        Elem.tr [] [
                            Elem.td [] [ text token.Label ]
                            Elem.td [] [ Elem.code [] [ text token.Prefix ] ]
                            Elem.td [] [ text token.Owner ]
                            Elem.td [] [ text (time token.CreatedAt) ]
                            Elem.td [] [
                                Elem.form [ attr "method" "post"; attr "action" "/tokens/revoke"; css "inline" ] [
                                    Elem.input [ attr "type" "hidden"; attr "name" "id"; attr "value" token.Id ]
                                    confirm "Подтверждаю"
                                    submit "Отозвать"
                                ]
                            ]
                        ]
                ]
            ]
        ]

    let configuration admin (sections: ConfigSection list) =
        page "Конфигурация" Configuration (Some admin) None [
            Elem.p [ css "hint" ] [ text "Файлы читаются при запуске; изменения вступают в силу после перезапуска сервера. Панель их не меняет." ]
            for section in sections do
                Elem.section [] [
                    Elem.h2 [] [ text section.Title ]
                    Elem.pre [] [ text section.Text ]
                ]
        ]
