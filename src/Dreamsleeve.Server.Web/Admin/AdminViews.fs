namespace Dreamsleeve.Server.Web.Admin

open System
open System.Net
open Falco.Markup
open Falco.Htmx
open Dreamsleeve.Server.Domain

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
    let online (rows: OnlineModel list) (available: bool) =
        Elem.section [ attr "id" "online"; Hx.get "/partials/online"; Hx.trigger "every 5s"; Hx.swapOuterHtml ] [
            Elem.h2 [] [ text $"Онлайн ({rows.Length})" ]
            if not available then Elem.p [ css "error" ] [ text "Рантайм не ответил; данные устарели." ]
            Elem.table [] [
                Elem.thead [] [
                    Elem.tr [] [
                        for heading in [ "PlayerId"; "Username"; "Display name"; "Персонаж"; "Имя для других"; "Роль"; "Место"; "Фаза"; "Подключён" ] do
                            Elem.th [] [ text heading ]
                    ]
                ]
                Elem.tbody [] [
                    for row in rows do
                        Elem.tr [] [
                            Elem.td [] [ playerLink row.PlayerId ]
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
                                Elem.td [ attr "colspan" "6"; css "muted" ] [ text "нет данных" ]
                            Elem.td [] [ text row.Phase ]
                            Elem.td [] [ text (time row.ConnectedAt) ]
                        ]
                ]
            ]
        ]

    let overview admin (status: StatusModel option) (rows: OnlineModel list) available =
        page "Обзор" Overview (Some admin) None [
            Elem.section [] [
                Elem.h2 [] [ text "Сервер" ]
                match status with
                | Some status ->
                    Elem.dl [] [
                        for label, value in [ "Соединения", string status.Connections; "Готовы", string status.Ready
                                              "Резервы PlayerId", string status.Reservations; "Закрываются", string status.Closing
                                              "Остановка", (if status.Stopping then "да" else "нет") ] do
                            Elem.dt [] [ text label ]
                            Elem.dd [] [ text value ]
                    ]
                | None -> Elem.p [ css "error" ] [ text "Рантайм не ответил." ]
            ]
            online rows available
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

    let player admin (card: PlayerCardModel) (notice: string option) (failure: string option) (resetCode: string option) (displayNameLimit: int) =
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
            if not card.Sessions.IsEmpty then online card.Sessions true
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
            Elem.p [ css "muted" ] [ text "Последние 200 действий." ]
            Elem.table [] [
                Elem.thead [] [ Elem.tr [] [ for heading in [ "Когда"; "Администратор"; "Действие"; "Цель"; "Подробности" ] do Elem.th [] [ text heading ] ] ]
                Elem.tbody [] [
                    for entry in entries do
                        Elem.tr [] [
                            Elem.td [] [ text (time entry.At) ]
                            Elem.td [] [ text entry.Admin ]
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
