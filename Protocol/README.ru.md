# Прикладной протокол сессии, версия 15

Схемы разделены по назначению:

| Файл | Содержимое |
|---|---|
| [common.proto](common.proto) | PlayerProfile (публичная личность, в том числе псевдонимная) и FormKey |
| [chat.proto](chat.proto) | SendChat, ChatMessage, ChatPublished, ChatChannel и ChatChannelKind; объявления: Announcement, PostAnnouncement, AnnouncementPolicy и их enum |
| [player.proto](player.proto) | Состояние персонажа, движение, actor values, Details и уведомления |
| [session.proto](session.proto) | OpenSession, JoinAsGuest и начальный SessionOpened; скрытое имя: SetIdentityVisibility и IdentityVisibilityChanged; смена отображаемого имени: ChangeDisplayName и DisplayNameChanged; мут и конец сессии: MuteState, MuteChanged, SessionEndReason, SessionEnded |
| [ground.proto](ground.proto) | Метки на земле: GroundMark, GroundMarkKind, PlaceGroundNote, ReportDeath, RemoveGroundMark, GroundMarksChanged, GroundMarkPlaced, GroundMarkRemoved, OwnGroundMarks |
| [moderation.proto](moderation.proto) | Роль и инструменты модератора: PlayerRole, SanctionKind, RoleChanged, SanctionEntry, запросы наказаний, списков и удаления контента с их ответами |
| [protocol.proto](protocol.proto) | ClientPacket/ServerPacket, подтверждение обновления и общие отказы |
| [network.proto](network.proto) | Причины отключения ENet и фиксированные DeliveryLane |

Граф импортов направлен от оболочек к сообщениям, от сообщений к общим типам;
циклов нет. Package `Dreamsleeve.Protocol.Chat` сохранён для существующих C++/C#
имён. Файловое разделение не меняет номера, типы, oneof, reserved или wire-формат;
Версия 15 даёт модератору инструменты в игре; версия 14 добавляет муты, баны и кик; версия 13 оставляет клиента подключённым гостем до входа; версия 12 датирует метки игровым календарём; версия 11 позволяет игроку сменить своё отображаемое имя; версия 10 позволяет скрыть свои имена за серверным псевдонимом; версия 9 присылает игроку полный список его меток; версия 8 добавляет метки на земле; версия 7 открывает сессию списком каналов (общий и системный); версия 6 отделила движение от команд. Версии 1–14 несовместимы с текущей. Native-код, работающий с оболочками, включает `protocol.pb.h`.
Генерация всех схем выполняется одной командой `python Scripts/generate_protocol.py`.

## Оболочки и сессия

Одно protobuf-сообщение занимает один ENet packet без внешнего length prefix.
Все оболочки содержат protocol_version = 15. Неизвестные дополнительные поля
допускаются; отсутствие ожидаемого payload или другая версия дают ошибку codec.

| Канал | DeliveryLane | Назначение |
|---|---|---|
| 0 | Control | ClientPacket/ServerPacket: сессия, lifecycle, UpdatePlayer, метки, скрытое имя, смена имени, модерация (кроме удаления сообщений) и ответы, reliable |
| 1 | Chat | ClientPacket/ServerPacket: SendChat, PostAnnouncement, DeleteChatMessage, ChatPublished, ChatMessageRemoved и ответы чата, reliable |
| 2 | Realtime | ClientMovementPacket/ServerMovementPacket: абсолютные pose, unreliable sequenced (flags=0) |

Нужно минимум три согласованных канала. Номера фиксированы в network.proto,
надёжность задаётся флагом пакета. RequestRejected возвращается на канал исходной
команды; SessionOpened с историей всегда Control. Между каналами общего порядка нет.
Flags=0 — не Unsequenced/UnreliableFragment. При исчерпании unreliable sequence
ENet может внутренне перейти к reliable; прикладного ACK движения при этом нет.

Сессия привязана к одному ENet-соединению. После транспортного Connected клиент
посылает OpenSession или, пока не вошёл, JoinAsGuest. Гость остаётся подключённым без
дедлайна (мёртвое соединение отсекает таймаут ENet), сервер считает его в онлайне и
принимает OpenSession на том же соединении. Ответа на JoinAsGuest нет; на соединении,
которое уже гость, открывает или открыло сессию, он получает InvalidRequest, прочие команды
гостя — SessionNotReady. Только SessionOpened переводит прикладную сессию в Ready.
Повторное открытие на том же соединении и SendChat до Ready должен отклонять
серверный владелец. Codec не хранит состояние соединения и сам эти правила не применяет.

OpenSession передаёт session_ticket и выбор hidden_identity: одноразовый билет из HTTP login,
32 случайных байта в base64url без padding (43 символа). Старые номера полей 1/2
и имена username/display_name зарезервированы; версии 1/2/3 несовместимы с версией 4.
Имя, отображаемое имя и PlayerId берутся из профиля, связанного с билетом.
Отсутствующий, просроченный, неизвестный или использованный билет не открывает сессию.
SessionTable резервирует PlayerId до завершения агента и очистки членства;
другой вход с этим ID получает SessionAlreadyOpen и закрывается.

Регистрация и проверка пароля идут отдельно от ENet: POST /auth/register принимает
username/displayName/password, POST /auth/login — username/password и возвращает
sessionTicket/expiresInSeconds/playerId/username/displayName (полный контракт, включая
сохранённый вход, — [AuthenticationRu.md](../docs/AuthenticationRu.md)). Повторный вход требует
нового билета. `ClientRuntime` не выполняет HTTP и не получает пароль; эту границу
обслуживает `AuthHttp` (WinHTTP), который вызывает `ClientApplication`. Удалённый HTTP endpoint требует HTTPS;
plain HTTP допустим только для явно разрешённой локальной разработки.

После переподключения требуется новое начальное состояние. SessionOpened заменяет
прежнее состояние сессии, а не продолжает старую историю. В этой версии нет продолжения
ENet-сессии по прежнему билету или history epoch (HTTP `/auth/resume` лишь выдаёт новый билет
для нового OpenSession); локальные generation/revision/round не передаются.

## Сообщения и корреляция

| Направление | Payload | Содержание |
|---|---|---|
| Клиент → сервер | OpenSession | Одноразовый SessionTicket и выбор hidden_identity |
| Клиент → сервер | JoinAsGuest | Пустой: соединение остаётся гостем до OpenSession; ответа нет |
| Клиент → сервер | SendChat | ChannelId и текст, без авторства/времени/MessageId |
| Клиент → сервер | PostAnnouncement | ChannelId системного канала, текст, вид (Announcement/Event), заявленный источник (TrustedClient/ThirdParty) и подпись; Chat-канал ENet |
| Сервер → клиент | SessionOpened | Имя сервера, SelfPlayerId, весь онлайн, каналы с видом и хвостом истории, политика объявлений, own_pseudonym и hidden_identity, действующий мут и роль |
| Сервер → клиент | ChatPublished | Одно принятое сообщение |
| Сервер → клиент | RequestRejected | Общий RequestRejectionCode, объяснение, поле |
| Клиент → сервер | UpdatePlayer | BeginCharacter / RenameCharacter / SetLocation / SetActorValues / LeaveGame / SetDetails |
| Сервер → клиент | PresenceChanged | Всё изменение онлайна для получателя: новые виды actor values, вошедшие и обновлённые игроки (PlayerInfo), патчи actor values и деталей, свои baseline/clear видимости и ушедшие; один пакет на получателя за такт репликации |
| Клиент → сервер | ClientMovementPacket.sample | context_revision, sequence, pose без RequestId |
| Сервер → клиент | ServerMovementPacket.movements | PlayerMoved: player_id, view_revision, sequence, pose |
| Сервер → клиент | PlayerUpdateAccepted | ACK команды UpdatePlayer |
| Клиент → сервер | PlaceGroundNote / ReportDeath / RemoveGroundMark | Надпись или место смерти с положением (FormKey, позиция, курс) либо id своей метки; Control-канал |
| Сервер → клиент | GroundMarkPlaced / GroundMarkRemoved | Подтверждение с RequestId: метка и id вытесненной / id удалённой |
| Сервер → клиент | GroundMarksChanged | Reliable-дельта видимых меток: view_revision, added, removed_ids, clear |
| Сервер → клиент | OwnGroundMarks | Полный список меток получателя, где бы они ни стояли: после открытия сессии и при каждом изменении набора; без RequestId |
| Клиент → сервер | SetIdentityVisibility | Где скрыть свои имена от других игроков (HiddenIdentity); Control-канал |
| Сервер → клиент | IdentityVisibilityChanged | Подтверждение с RequestId: применённый вариант и псевдоним, который теперь видят другие, или его отсутствие |
| Клиент → сервер | ChangeDisplayName | Новое собственное отображаемое имя; Control-канал |
| Сервер → клиент | DisplayNameChanged | Подтверждение с RequestId: имя, как сервер его сохранил (Trim + NFC) |
| Сервер → клиент | MuteChanged / SessionEnded / RoleChanged | Свой мут, причина конца сессии, своя роль; без RequestId (см. «Модерация») |
| Клиент → сервер | SanctionPlayer … DeleteChatMessage | Запросы модератора; ответы и коды — в разделе «Модерация» |

RequestId — ненулевой uint64, назначаемый клиентским API до отправки. Клиент должен
выдавать уникальные ID в течение жизни соединения; пропуски допустимы. Это не
ChatMessageId, не серверная последовательность и не обещание дедупликации запросов.
Повторная отправка после переподключения не является безопасным retry без отдельного
контракта. Счётчик общий для ClientRuntime и UI через ClientExchange.NextRequestId;
владелец сопоставляет OpenSession и ограниченное число ожидающих SendChat/UpdatePlayer.

В ClientPacket RequestId обязателен. В ServerPacket его наличие различается:

- SessionOpened, PlayerUpdateAccepted, GroundMarkPlaced, GroundMarkRemoved, IdentityVisibilityChanged, DisplayNameChanged, ответы модератору (SanctionIssued … PlayerMarksCleared) и RequestRejected обязательно возвращают ID исходного запроса.
- ChatPublished и ChatMessageRemoved содержат RequestId только в копии инициатору. Остальные получают
  то же сообщение без RequestId. ID других клиентов не завершает свои запросы.
- PresenceChanged/GroundMarksChanged/OwnGroundMarks/MuteChanged/SessionEnded/RoleChanged не содержат RequestId. Явный ноль всегда ошибочен.

Realtime-оболочки вообще не имеют RequestId: samples не занимают pending,
не требуют PlayerUpdateAccepted, retry или коррелированного отказа.

Optional RequestId существует только в общей protobuf-оболочке и диагностике codec.
В прикладных ответах наличие ID закреплено вариантом типа:

- F# ServerResponse: SessionOpened, ChatAccepted, ChatRejected, RequestRejected и остальные ответы
  на команды принимают обязательный uint64 ID; ChatMessageRemoved — `voption` (только у копии инициатору).
- F# ChatPublished, PresenceChanged и прочие уведомления — без поля RequestId.
- C++ ServerResponse — variant; SessionOpened, ChatAccepted, RequestRejected и остальные ответы
  содержат requestId, ChatMessageRemoved — optional, ChatMessagesReceived, PresenceChanged
  и прочие уведомления — без него.

ChatAccepted и ChatPublished на F# кодируются одним wire-payload ChatPublished,
различаясь корреляцией для получателя. На C++ ChatAccepted содержит обычный
ChatMessagesReceived в поле changes. Владелец завершает ожидающий запрос и передаёт
changes в ту же модель, куда поступают сообщения других игроков. Само подтверждение
не добавляет сообщение вторым путём.

Ноль остаётся недопустимым ID, его проверяет codec. ProtocolCodecError.RequestId остаётся
option: пустой или повреждённый пакет может не содержать достоверной корреляции.
Транспортная ошибка соединения не является ServerRejection без ID.

Собственное сообщение применяется тем же ChatMessagesReceived, что и чужое.
Корреляция относится к результату команды и не создаёт второй путь изменения чата.
При обычной публикации сервер передаёт одно сообщение, а локальный UI получает
Added/Removed из модели. Полная история не копируется при каждом сообщении.

## Каналы и объявления

Модель — [DomainSpecRu.MD §4.8, §5](../docs/DomainSpecRu.MD).

- `ChatChannelKind`: `GLOBAL` (1) — игроки пишут `SendChat`; `SYSTEM` (5) — объявления,
  канал только для чтения. Номера 2–4 оставлены партии, гильдии и личным сообщениям
  ([ProtobufHandbook](../docs/ProtobufHandbookRu.MD)). Вкладка «Все» клиента — агрегат,
  а не канал. `SessionOpened.channels` (`ChatChannel{channel_id, kind, recent_messages}`)
  перечисляет каналы сессии: в версии 7 это один `GLOBAL` и один `SYSTEM`; ID общих
  каналов сервер выводит из вида (`ChatChannelKind.channelId`: 1 и 2).
- `ChatMessage.announcement = 8` (`Announcement{source, kind, signature}`) присутствует
  ровно у сообщений `SYSTEM`-канала; клиент отвергает объявление в общем канале и
  чат в системном. `author` отсутствует только у объявлений `SERVER`: фиктивного
  игрока нет; у клиентских объявлений автор — игрок, чей клиент их отправил.
- `AnnouncementSource`: `SERVER` (1) назначает только сервер; `TRUSTED_CLIENT` (2),
  `THIRD_PARTY` (3). `AnnouncementKind`: `ANNOUNCEMENT` (1), `EVENT` (2), `ADMIN` (3),
  `PERIODIC` (4); два последних клиент запросить не может.
- `ClientPacket.post_announcement = 13` (`PostAnnouncement{channel_id, ...}`) — запрос
  клиента в системный канал. Его enum `ClientAnnouncementSource` не содержит значения
  сервера. Ответ как у SendChat: автор получает `ChatPublished` со своим `request_id`,
  при отказе — `RequestRejected` на Chat-канале ENet (`MUTED`, `ANNOUNCEMENT_NOT_ALLOWED`,
  `TEXT_NOT_ALLOWED`, `RATE_LIMITED`, `INVALID_REQUEST`: не системный канал — с полем
  `channel_id`, длина — с полем `text`/`source`, серверный вид — без поля). `SendChat` в
  системный канал тоже получает `INVALID_REQUEST`.
- `SessionOpened.announcements = 7` (`AnnouncementPolicy`: разрешённые клиентские
  источники, лимиты текста и подписи в скалярах Unicode) обязателен; клиент по нему
  проверяет длины до отправки.
- Неизвестные значения source/kind клиент принимает без ошибки codec; host показывает
  их с наименьшим доверием.

## Метки на земле

Модель — [DomainSpecRu.MD §4.9](../docs/DomainSpecRu.MD), реализация —
[GroundMarksRu.md](../docs/GroundMarksRu.md). Всё в [ground.proto](ground.proto) и
Control-канале ENet.

- `GroundMark{mark_id, author, kind, text, flagged, placement, created_at_unix_ms,
  character_name, game_date}`: `author` — снимок `PlayerProfile` (имя и у офлайн-автора),
  `character_name` — optional снимок имени персонажа при размещении, `kind` —
  `GROUND_MARK_KIND_NOTE` (1) / `DEATH` (2), `text` пуст только у смерти, `flagged` — как у
  сообщения, `placement` — `FormKey` пространства, `Position` и `heading` (угол Z, радианы).
- `GameDate{era, year, month, day, day_of_week, hour, minute}` — игровая дата у автора в
  момент размещения (версия 12). Обязательна в `PlaceGroundNote.game_date = 3` и
  `ReportDeath.game_date = 3`; в `GroundMark.game_date = 9` отсутствует только у меток,
  сохранённых до версии 12. Диапазоны: эра 1–99 (4 — Четвёртая), год 1–99999, месяц 1–12
  (1 — Утренней звезды), день 1–длина месяца (без високосных), день недели 0–6 (0 — Сандас),
  час 0–23, минута 0–59. Сервер проверяет только диапазоны: это оформление, не метка времени.
- `ClientPacket.place_ground_note = 14`, `report_death = 15`, `remove_ground_mark = 16` —
  команды с RequestId. Ответ — `ServerPacket.ground_mark_placed = 22`
  (`GroundMark` и `evicted_id`, 0 — ничего не вытеснено), `ground_mark_removed = 23`
  (`mark_id`) либо `RequestRejected`. Сама метка в модель автора попадает только через
  дельту, как у остальных.
- `ServerPacket.ground_marks_changed = 21` — `GroundMarksChanged{view_revision, added,
  removed_ids, clear}` без RequestId. `view_revision` растёт с каждым сообщением одной
  сессии; повтор или откат — ошибка протокола на клиенте (дельты reliable и
  упорядочены). `clear = true` начинает новый baseline (смена пространства, поколения
  персонажа или потеря позиции); дельта без `clear` и без изменений недопустима.
- `ServerPacket.own_ground_marks = 24` — `OwnGroundMarks{marks}` без RequestId: все метки
  получателя обоих видов, где бы они ни стояли, по возрастанию id. Приходит сразу после
  подписки на метки (после `SessionOpened`) и заново при размещении, вытеснении, удалении
  или истечении любой из них. Замена целиком, не дельта; не зависит от видимого набора.
  Клиент отвергает список с чужим автором или повтором id.
- Коды: `GROUND_MARK_AREA_FULL` (12) — ячейка индекса полна; `GROUND_MARK_NOT_FOUND`
  (13) — нет такой своей метки (у модератора — никакой); `MUTED` — надпись в муте (метку смерти
  мут не останавливает); `RATE_LIMITED` — частота надписей или смертей;
  `TEXT_NOT_ALLOWED` — словарь (поле `text`); `INVALID_REQUEST` — длина (`text`) или
  положение (`placement`). Codec отклоняет нулевой id, неконечные координаты,
  многострочную подпись смерти и слишком длинный текст (`GroundNoteText.create`,
  `DeathMarkText.create`, лимиты `ChatInput.GroundNoteText` = 200,
  `ChatInput.DeathMarkText` = 64).

## Скрытое имя

Модель — публичная личность ([DomainSpecRu.MD §3.1](../docs/DomainSpecRu.MD)), реализация и
ограничения — [ModerationAndNamesRu.md](../docs/ModerationAndNamesRu.md#скрытое-имя).

- `PlayerProfile.pseudonymous = 4`: профиль игрока, скрывшего имена. `username` пуст,
  `display_name` — серверный псевдоним, `player_id` — тот же открытый ID; имени персонажа
  (`PlayerInfo.character_name`, `ChatMessage.character_name`, `GroundMark.character_name`)
  при нём нет. `character_name_withheld` сохраняет прежний смысл (имя не прошло словарь) и
  для псевдонима не используется. Клиент отвергает псевдонимный профиль с username или
  пустым `display_name`. Так приходят все проекции скрытого игрока другим: bootstrap
  `SessionOpened.players`, `PresenceChanged.joined`/`updated`, автор `ChatMessage` (чат и
  объявления клиента) и автор `GroundMark` (надписи, места смерти, `OwnGroundMarks`).
- Свою запись игрок всегда получает с настоящим профилем (кодек сервера отвергает
  приветствие, где запись получателя псевдонимна); свой псевдоним — в
  `SessionOpened.own_pseudonym = 9` (optional, есть только при скрытом имени) и в ответе на
  переключение.
- `HiddenIdentity` (session.proto): `NONE = 0` — имена показаны; `EVERYWHERE = 1` —
  присутствие (онлайн, надпись над светлячком), чат (сообщения и объявления клиента) и метки на
  земле; `EXCEPT_GROUND_MARKS = 2` — присутствие и чат, новые метки несут настоящий профиль и
  имя персонажа. Неизвестное значение — `INVALID_REQUEST` (сервер) или ошибка codec (клиент).
- `OpenSession.hidden_identity = 4`: сессия открывается уже со скрытым именем, первый
  `PresenceChanged.joined` не несёт настоящего профиля. Сервер, запрещающий режим, отвечает на такое
  открытие `RequestRejected` с `HIDDEN_IDENTITY_NOT_ALLOWED` до погашения билета.
  `SessionOpened.hidden_identity = 10` — применённый вариант; `own_pseudonym` есть ровно тогда,
  когда он не `NONE`.
- `ClientPacket.set_identity_visibility = 17` (`SetIdentityVisibility{hidden}`, Control, RequestId):
  ответ — `ServerPacket.identity_visibility_changed = 25` (`IdentityVisibilityChanged{optional
  pseudonym, hidden}`) или `RequestRejected`: `RATE_LIMITED` (чаще `Identity.ToggleIntervalMs`,
  состояние не меняется), `HIDDEN_IDENTITY_NOT_ALLOWED`, `OVERLOADED` (другое переключение ещё
  ждёт ответа). Запрос текущего варианта подтверждается сразу и переключением не считается.
  Новый псевдоним выдаётся только при переходе из `NONE`; смена `EVERYWHERE` ↔
  `EXCEPT_GROUND_MARKS` сохраняет прежний.
- Снимок автора в сообщении и метке фиксируется при создании: созданное при скрытом имени
  навсегда остаётся с псевдонимом, в том числе в истории нового подключения и в метках после
  перезапуска сервера.

## Смена отображаемого имени

С версии 11 игрок меняет своё отображаемое имя в сессии; username и PlayerId не меняются.
Реализация и настройки — [ModerationAndNamesRu.md](../docs/ModerationAndNamesRu.md#смена-отображаемого-имени).

- `ClientPacket.change_display_name = 18` (`ChangeDisplayName{display_name}`, Control, RequestId):
  сервер проверяет имя правилами `DisplayName` (Trim + NFC, одна строка, лимит
  `[Server.ChatInput] DisplayName`) ещё в кодеке — нарушение даёт `INVALID_REQUEST` с полем
  `display_name`; затем мут (`MUTED`), словарь (`TEXT_NOT_ALLOWED`), разрешение сервера
  (`DISPLAY_NAME_CHANGE_NOT_ALLOWED = 15`), одну смену за раз (`OVERLOADED`) и интервал между
  собственными сменами (`RATE_LIMITED`, message «The display name can be changed again in N min.»).
  Запрос текущего имени подтверждается сразу и сменой не считается.
- Ответ — `ServerPacket.display_name_changed = 26` (`DisplayNameChanged{display_name}`) с сохранённым
  именем. Сам профиль приходит обычным `PresenceChanged.updated` — и автору, и остальным; у
  игрока со скрытым именем другие по-прежнему видят псевдоним, обновление им не приходит.
- Клиентский кодек отвергает пустое имя в запросе и в ответе и ответ без RequestId.

## Модерация

С версии 14 сервер сообщает игроку его мут и конец сессии; с версии 15 модератор действует
из игры. Правила, хранение и аудит — [ModerationAndNamesRu.md](../docs/ModerationAndNamesRu.md#муты-баны-и-кик).

- v14: `SessionOpened.mute = 11` (`MuteState{reason, optional until_unix_ms}`),
  `ServerPacket.mute_changed = 27` (`MuteChanged{mute}`; без `mute` — снят),
  `ServerPacket.session_ended = 28` (`SessionEnded{reason, text, optional until_unix_ms}`: `ACCESS_REVOKED`,
  `BANNED`, `KICKED`; последний пакет перед закрытием), код `MUTED = 16`.
- v15: `SessionOpened.role = 12` (`PlayerRole`: `PLAYER = 0`, `MODERATOR = 1`) и уведомление
  `ServerPacket.role_changed = 29` (Control, без RequestId), когда панель меняет роль в живой сессии.
- Запросы модератора (Control, RequestId): `sanction_player = 20`
  (`SanctionPlayer{player_id, kind, optional minutes, reason}`; без `minutes` — бессрочно),
  `lift_sanction = 21`, `kick_player = 22`, `list_sanctions = 23`, `list_player_marks = 24`,
  `clear_player_marks = 25` (`notes`, `deaths`, хотя бы одно). Ответы: `sanction_issued = 30`
  (`SanctionEntry`), `sanction_lifted = 31`, `player_kicked = 32`, `sanction_list = 33` (все
  действующие, новые сверху; игроки только по PlayerId), `player_marks = 34` (все метки одного
  автора, новые сверху), `player_marks_cleared = 35` (`removed`).
- Удаление сообщения идёт по Chat-полосе: `ClientPacket.delete_chat_message = 26`
  (`DeleteChatMessage{channel_id, message_id}`); `ServerPacket.chat_message_removed = 36`
  (`ChatMessageRemoved{channel_id, message_id}`) получают все участники канала, копия модератора —
  с его RequestId. Отказ удаления — `RequestRejected` на Chat-канале, как у сообщения.
- Удаление одной метки — прежний `RemoveGroundMark`: модератору сервер удаляет метку любого автора.
- Коды: `NOT_PERMITTED = 17` (нет роли модератора, или цель — модератор/сам модератор),
  `TARGET_NOT_FOUND = 18` (нет игрока, действующего наказания или сообщения). Причина и срок
  проверяются в кодеке доменом: `INVALID_REQUEST` с полем `reason` или `minutes`.

## Игровое состояние

PlayerInfo включает неизменяемую идентичность PlayerProfile, optional character_name,
optional PlayerLocation, actor_values, character_generation, PlayerDetails,
view_revision, movement_sequence и character_name_withheld (персонаж есть, но его имя
не прошло серверный словарь и не публикуется). ChatMessage несёт optional
character_name — снимок опубликованного имени персонажа на момент отправки; у старой
истории его нет. `ChatMessage.flagged` — диапазоны (байты UTF-8), которые словарь
пометил, не отклонив сообщение. Коды отказа TEXT_NOT_ALLOWED (9) и RATE_LIMITED (10) добавлены
совместимо в рамках v6; см. [модерация и имена](../docs/ModerationAndNamesRu.md). SessionOpened
содержит PlayerInfo в поле players=5; старое поле 3 зарезервировано. PresenceChanged.joined и
updated тоже несут PlayerInfo.

PlayerLocation содержит Location(FormKey(plugin_name/local_form_id), location_name),
Position XYZ в world units и Rotation XYZ в радианах. Клиент шлёт свои показания как
ActorValueEntry: key, display_name и oneof scalar (float) / resource (current/maximum, sint32 —
целые очки, отрицательные допустимы: игра не обрезает удар, превысивший здоровье). Scalar 0
присутствует явно; отсутствующий oneof — ошибка. Сервер публикует их как ActorValue с номером
вида вместо строк (раздел «Пачка присутствия»). Reliable SetLocation устанавливает пространство и
начальную позу; отсутствие location очищает положение. SetActorValues независимо заменяет карту показаний; пустая
карта очищает её. Зарезервирован старый номер 3 объединённого sample_player_state.

PlayerDetails отдельно заменяет расу (NamedForm), optional level, PlayerActivity,
PlaceDescription и optional game_started_at_unix_ms. Занятие — общий ActivityKind с
optional target_name/menu_key и LockDifficulty; место хранит worldspace/location,
ближайший маркер, его вид и is_interior. Это данные для разных UI, а не готовая
Discord-строка. Движение и actor values не стирают details. BeginCharacter/LeaveGame очищают игровой
контекст и увеличивают character_generation, даже при повторе имени; Rename сохраняет
остальные поля. Поля профиля клиент изменить этой командой не может.

PlayerUpdateAccepted завершает reliable-команду, не создавая локальное эхо.
Автор применяет серверное состояние тем же путём, что остальные наблюдатели.
Обновлённый игрок (PresenceChanged.updated) с view_revision=0 обновляет идентичность и
компоненты без переустановки позиции текущего персонажа. Новая character_generation сразу
сбрасывает старую позицию и контекст. Видимость (PresenceChanged.visibility) устанавливает
baseline/clear; патч метаданных никогда не меняет позицию.

### Пачка присутствия (v16)

`ServerPacket.presence_changed = 37` (Control, без RequestId) — единственное уведомление онлайна.
Сервер шлёт получателю не больше одной пачки за такт `ReplicationIntervalMs` со всеми
изменениями этого такта, плюс по пачке на каждого вошедшего или ушедшего игрока. Части
применяются в порядке полей: `actor_value_kinds`, `joined`, `updated`, `metadata`, `visibility`,
`left`; пустая пачка недопустима. Прежние `player_joined`/`player_left`/`player_updated`/
`player_metadata_changed`/`player_visibility_changed` (13–15, 18, 20) зарезервированы.

**Виды actor values.** `ActorValueKind{id, key, display_name}` — серверный номер (uint64, не 0)
пары «ключ + подпись». Подпись приходит от клиента, у игроков с разной локализацией один ключ даёт
разные виды. Сервер определяет вид получателю до первого сообщения, которое его использует:
в `SessionOpened.actor_value_kinds` (все виды игроков снимка) или в той пачке, где вид впервые
нужен (номера, которых получатель ещё не знает, по возрастанию). Номер живёт, пока какой-нибудь
онлайн-игрок публикует эту пару, и никогда не переходит к другой: пара, опубликованная заново
после забвения, получает новый номер. Поэтому клиент может забыть вид, которого нет ни у одного
игрока его модели, — сервер такой номер больше не пришлёт. Неизвестный или повторно определённый
номер — ошибка codec.

**Патч метаданных.** `PlayerMetadataPatch{player_id, removed_actor_values, actor_values, details,
cleared_details}` — что изменилось у игрока с прошлого такта; хотя бы одна часть есть. Сначала
удаляются виды из `removed_actor_values`, затем ставятся новые и изменившиеся `actor_values`
(смена подписи — удаление старого вида и новый). В `details` присутствующие компоненты (раса,
уровень, занятие, место, начало игры) заменяют прежние, отсутствующие не меняются;
`cleared_details` (`PlayerDetailsField`) перечисляет необязательные компоненты, ставшие
неизвестными. Один компонент не может быть одновременно заменён и очищен.

**Видимость.** `PlayerVisibility{player_id, view_revision, sequence, pose}`: поза — baseline,
её отсутствие — clear. Видимый игрок всегда в пространстве получателя, поэтому место одно на пачку:
`PresenceChanged.space` (собственная Location получателя на сервере); оно есть ровно тогда, когда
хотя бы у одной записи есть поза.

### Контекст движения и порядок v6

Клиент назначает возрастающий context_revision каждому SetLocation в соединении,
включая clear и телепорт в том же WRLD/CELL. Сервер принимает номер выше предыдущего
принятого перехода. BeginCharacter/LeaveGame очищают активный контекст, сохраняя его
high-water mark. Клиент прекращает старый поток и начинает новый после принятия
перехода. Отказ отключает локальный поток; следующий локальный переход может
заново установить положение.

Baseline имеет sequence=0, последующие клиентские измерения — возрастающий
sequence>=1. Сервер принимает только совпадающий context_revision и больший sequence.
Старый, повторный или обогнавший переход sample отбрасывается без ответа. Realtime
не меняет пространство, не создаёт персонажа и не восстанавливает очищенную позицию.

Presence назначает view_revision из счётчика конкретного наблюдателя. Новый вход
в AOI, контекст/персонаж источника или наблюдателя, новое соединение источника
получают новый токен; после clear/reentry токен не переиспользуется. PlayerMoved
применяется только к уже видимой позиции с совпадающим токеном и большим sequence.
Повтор вниз сохраняет source sequence/time; sequence=0 допустим как повтор baseline.
Локальные ClientModel generation/revision — отдельные курсоры публикации.

Поздний sample не оживляет clear/Leave и не переносит координаты в другую локацию.
Новый sample, обогнавший baseline, пропускается: следующий период повторит позицию.
Не нужно хранить будущие samples, tombstones всех игроков или собирать целый тик.

## Значения и начальное состояние

PlayerId, ChatChannelId и ChatMessageId — ненулевые uint64 полного диапазона.
Сервер назначает ID сообщения, его авторство и время. Автор содержит профиль на
момент отправки, может уже быть офлайн и не обязан присутствовать в списке онлайна.
Время — знаковые Unix milliseconds; допустим диапазон DateTimeOffset
[-62135596800000, 253402300799999]. Порядок истории задаёт MessageId.

SessionOpened содержит уникальные PlayerId, включая SelfPlayerId, и уникальные каналы.
Хвост каждого канала принадлежит ему и строго возрастает по MessageId; пустая история допустима.
Это ограниченный хвост для начала работы. Здесь нет курсора, hasMore или запроса старой
истории. Будущая пагинация должна отдельно определить историю и её epoch.

В C++ SessionOpened непосредственно содержит requestId, selfPlayerId, players,
channels (`ChannelOpened{channelId, kind, recentMessages}`), serverName, announcements, ownPseudonym,
hiding, mute и role. Players уже представлены обычными
Domain::Player, сообщения — Domain::ChatMessage; отдельного типа состояния сессии нет.

Начало сессии — последовательность действий владельца соединения. После проверки
ожидаемого запроса он сбрасывает прежнюю модель, регистрирует каналы с их видом, применяет
OnlinePlayersReplaced, назначает self и передаёт историю через ChatMessagesReceived.
Модель получает те же операции, что и при дальнейшей работе. PlayerStore проверяет
дубли игроков, ChatCache — канал и конфликты сообщений. Проверки не дублируются
в decoder. Сервер формирует историю в порядке MessageId; ChatCache хранит сообщения
в этом порядке независимо от порядка поступления.

Публикация выполняется после обработки всего события, когда можно переходить в Ready.
При ошибке входа владелец очищает незавершённое состояние и завершает вход с ошибкой;
сохранение старой модели как откат новой сессии не является контрактом. Один владелец
и отдельная публикация не позволяют потребителю увидеть промежуточные изменения.
C++ ClientRuntime реализует этот обработчик, сверяет фазу Opening и RequestId,
публикует снимок вместе с Ready. Client.Dev --connect использует настоящий транспорт. Модель не содержит специальной
транзакции для этого сценария. PresenceChanged.joined может содержать отфильтрованный baseline.

ChatPublished способен обогнать SessionOpened по своему каналу: runtime хранит такие
публикации в bounded bootstrap-буфере и применяет после истории через ChatCache.
Публикация содержит профиль автора; его наличие в онлайне не требуется. Ранний
realtime отбрасывается и восстанавливается следующим периодом.

## Границы проверки

Лимиты задаются конфигурацией, а не константами codec. Значения по умолчанию:

| Настройка | Default | Где применяется |
|---|---|---|
| MaxPacketBytes / network.maxPacketBytes | 1 MiB | Вход/выход codec и ENet maximumPacketSize |
| Server.MovementPacketTargetBytes | 0 (автоматически) | Всегда min(MaxPacketBytes, negotiated MTU budget); положительное значение уменьшает цель. Неделимая запись сверх лимита даёт ошибку, не фрагментируется |
| MaxWaitingData / network.maxWaitingData | 32 MiB | ENet maximumWaitingData, бюджет ожидающих данных на peer |
| MaxInitialPlayers / maxInitialPlayers | 4096 | Число игроков в начальном состоянии |
| MaxRecentMessages / maxRecentMessages | 512 | Число сообщений начальной истории; 0 отключает её |
| ChatInput.Username / DisplayName / MessageText | 32 / 64 / 2000 | Серверная проверка строк в Unicode scalar values |

Сервер обязан сформировать снимок, помещающийся одновременно в лимиты количества
и байтов; разбиения bootstrap на пакеты пока нет. MaximumPacketSize ограничивает
целое ENet-сообщение, а не MTU датаграммы. Максимальный пакет должен помещаться
в MaxWaitingData; этот бюджет не заменяет отдельные лимиты очередей приложения.

На C++ [Configuration](../src/Dreamsleeve.Client.Core/Config.ixx) содержит network
и лимиты начального состояния; её один раз проверяет `ValidateClientSettings`
(`ClientApplication::TryCreate`). `config.network` передаётся в создание DreamNetHost,
сам config — в конструктор `Wire::ProtocolCodec{config}`. Codec хранит копию
проверенных настроек; дальше вызываются `codec.Encode(request)` / `codec.Decode(bytes, channel)`.
Для движения — `codec.Encode(sample, negotiatedPayloadBytes)` с flags=0.
Encode возвращает владеющий DreamNetPacket: TryAllocateWith выделяет буфер ENet,
protobuf пишет прямо в него. Пакет передаётся через `client.Send(std::move(packet))`
или `peer.PushPacket(std::move(packet), channel)`, без промежуточного vector и повторного
копирования через span. Reliable — для команд, flags=0 — для движения; владение переходит ENet
только при успешной отправке. Ошибка выделения/записи возвращает PacketCreationFailed.
Host устанавливает maximumPacketSize/maximumWaitingData до работы с соединениями.
Отправка span проверяет лимит до копирования; broadcast сообщает ошибку превышения.
DreamNetPacket проверяет представимость длины в ENet, а не фиксирует default ENet
в 32 MiB как потолок всех конфигураций.

На F# [ServerConfig](../src/Dreamsleeve.Server.Core/Config.fs) — общий источник
параметров: `GameSettings.create` один раз проверяет его (`ServerConfig.validate`) и создаёт
`ProtocolCodec.create config`; `ServerConfig.applyPacketLimits config host` применяется после
создания yENet host и **до первого Service/Connect**. Затем используются
`ProtocolCodec.decodeClient codec bytes` / `ProtocolCodec.encode codec payloadBudget response`
(пакеты одного ответа: движение делится по бюджету peer, остальное — один пакет).
`ProtocolCodec.delivery response` — единственная таблица свойств ответа: канал, RequestId
для ответа на команду (нет у уведомления) и можно ли слать его до SessionOpened.
EnetTransport применяет лимиты к реальному yENet host; все native операции
Host/Peer выполняет один владелец транспорта.

Конфигурация фиксируется на срок жизни сетевого владельца; менять только codec
после создания host нельзя. Некорректные лимиты отклоняет единственная проверка настроек
(`GameSettings.create` на сервере, `ValidateClientSettings` на клиенте); кодек её не повторяет,
проверки всей конфигурации на каждом пакете нет. C++ при этой проверке также
ограничивает maxPacketBytes диапазоном int для protobuf ParseFromArray/SerializeToArray.
Сам размер каждого входного/выходного сообщения всё равно сравнивается с лимитом.
Сервер загружает конфигурацию из `server.toml`, клиент — из своего TOML (образец —
`client.example.toml`, путь выбирает конечное приложение), перед запуском владельца.
ENet не согласует эти прикладные лимиты между
сторонами: пока развёртывание должно задавать совместимые настройки клиента и
сервера. Согласование по сети — отдельное расширение входа в сессию.

F# decodeClient проверяет форму SessionTicket и использует доменные фабрики для
ChatChannelId, ChatMessageText и остальных полей команд (DisplayName в ChangeDisplayName,
тексты меток и объявлений, причина и срок наказания). Username и DisplayName при регистрации
проверяются на HTTP-границе. Лимиты строк задаёт приложение через ServerConfig.ChatInput; текущие
defaults для Username / DisplayName / MessageText — 32 / 64 / 2000 Unicode scalar values. Username нормализуется
в нижний ASCII, DisplayName — Trim/NFC, текст чата сохраняется как был принят фабрикой.
C++ не повторяет эти бизнес-проверки и не нормализует серверные строки.

Проверки C++ на входе относятся к форме пакета: версия, payload, наличие вложенных
сообщений, ненулевые ID, корреляция, диапазон времени и лимиты размера bootstrap.
Слишком большой, повреждённый или неполный пакет возвращает Result с ошибкой.
F# перехватывает InvalidProtocolBufferException на границе парсинга, наружу тоже
возвращает Result. Доменная ошибка сохраняет RequestId для коррелированного отказа.
Повреждённые reliable-команды без надёжной корреляции закрывают соединение через
ProtocolError. Realtime не порождает коррелированные отказы на каждую позицию.

### Ответственность проверок

| Место | Что проверяет |
|---|---|
| ENet / DreamNet | Размер транспортного сообщения, канал, состояние peer и передача владения пакетом |
| Protobuf | Корректность бинарного формата; отсутствие поля может дать default, успешный parse не означает корректную команду |
| Создание codec / host | Неизменные настройки и бюджеты; используется существующая проверка Configuration / ServerConfig |
| Клиентский codec | Версия, известный payload, обязательные значения, корреляция, размер пакета и списков; правила хранилищ в decoder не дублируются |
| Серверные доменные фабрики | ID и пользовательские строки; encoder не перепроверяет ID уже созданных доменных значений |
| Модель / обработчик сессии | Изменение состояния, конфликты сообщений, членство, уникальность сессии по PlayerId и допустимость команды в текущей сессии |

Проверка байтов в codec выполняется до разбора/выделения выходного буфера. ENet
проверяет свой host, а Decode принимает произвольный span/array, поэтому обе границы
сохраняют свой лимит. Отдельного сканера protobuf или проверки доставки в codec нет.
В C++ отсутствующие message/author/player уже отклоняются проверками ненулевых ID:
сгенерированные getters возвращают default instance. Отдельные has_* для этих трёх
вложенных сообщений не нужны; has_request_id остаётся необходимым для корреляции.

Владелец соединения отвечает за последовательность применения начальных данных
и момент публикации, модель — за свои обычные операции. Успешный Decode сам по себе
не означает готовность сессии. Отправитель на сервере проверяет взаимосвязи списков,
которые сами доменные типы не гарантируют, до отправки состояния клиенту.

## Коды отказа

`RequestRejected.code` имеет тип `RequestRejectionCode` из protocol.proto. Это общий
контракт клиента и сервера; логика различает причины по enum, а не по тексту message.

| Значение | Имя в C++ / F# | Смысл |
|---|---|---|
| 0 | Unspecified | Недопустим в ответе; позволяет обнаружить незаполненное поле |
| 1 | InvalidRequest | Некорректное поле или аргументы команды |
| 2 | SessionNotReady | Прикладная сессия ещё не открыта |
| 3 | SessionAlreadyOpen | Соединение или PlayerId уже имеет открывающуюся/активную/закрывающуюся сессию |
| 4 | UsernameTaken | Запрошенное имя уже занято |
| 5 | ChannelNotFound | Канал не существует |
| 6 | NotChannelMember | Отправитель не состоит в канале |
| 7 | Overloaded | Запрос не допущен из-за лимита; сессия остаётся рабочей |
| 8 | AuthenticationFailed | Билет отсутствует, недействителен, просрочен или уже использован |
| 9 | TextNotAllowed | Текст или подпись не прошли серверный словарь |
| 10 | RateLimited | Слишком часто или повтор; лимиты чата и объявлений раздельные |
| 11 | AnnouncementNotAllowed | Сервер не принимает объявления заявленного источника |
| 12 | GroundMarkAreaFull | Ячейка пространственного индекса уже содержит предельное число меток |
| 13 | GroundMarkNotFound | Нет такой метки этого автора (у модератора — нет такой метки вообще) |
| 14 | HiddenIdentityNotAllowed | Сервер не разрешает скрывать имя (открытие со скрытым именем или переключение) |
| 15 | DisplayNameChangeNotAllowed | Сервер не разрешает игрокам менять отображаемое имя |
| 16 | Muted | Игрок в муте: писать нельзя, пока мут не истёк или не снят |
| 17 | NotPermitted | Запрос модератора без роли, или цель — модератор или сам модератор |
| 18 | TargetNotFound | Нет такого игрока, действующего наказания или сообщения в канале |

F# использует тип, сгенерированный protoc для .NET. Серверный encoder принимает
только определённые ненулевые коды. C++ использует автоматически сгенерированное
представление того же enum без protobuf-заголовков в интерфейсе модуля.

Клиент сохраняет неизвестный ненулевой код и message: более новый сервер может
добавить причину, для которой у старого UI ещё нет специальной обработки. Число
сохраняется как int32, включая отрицательные неизвестные значения. Ноль отклоняется.
`message` остаётся пояснением для пользователя/диагностики, `field` указывает поле
при ошибке ввода. Локальные ошибки codec и причины отключения ENet — отдельные типы.

Новые причины добавляются в .proto с новыми номерами. Номера и имена удалённых
значений нужно объявлять reserved, повторно использовать их нельзя. Выбор кода
по состоянию сессии/канала принадлежит серверному обработчику.

## Полнота обработки вариантов

Матчинги F# ServerResponse явно перечисляют все DU-варианты; проверки значений стоят
внутри веток, без общего `_ -> None`. В Server.Core FS0025 включён как ошибка сборки.
Входной protobuf PayloadOneofCase перечисляется явно, включая None. Неименованные
числовые значения обрабатываются веткой `unknown when not (Enum.IsDefined unknown)`.
В ProtocolCodec.fs, PlayerCodec.fs, ChatCodec.fs, SessionCodec.fs и ModerationCodec.fs подавлен только FS0104
о неименованных enum-значениях; новый именованный вариант по-прежнему требует обработки и вызывает FS0025.

В C++ реализациях кодека включены ошибки C4061/C4062 после generated headers.
PAYLOAD_NOT_SET обработан явно; default оставлен для неизвестных значений, но не
скрывает новые именованные enum-варианты. Visitor ClientRequest также явно отличает
каждую альтернативу (OpenSession, SendChat … DeleteChatMessage) своей перегрузкой
RequestWriter::operator(), без generic fallback. Новая альтернатива требует перегрузки; проверено отдельной компиляцией
с /O2 /DNDEBUG: существующие типы собираются, добавленный ProbeAdded даёт C2672.
Сгенерированные файлы не редактируются ради этих проверок.

Проверено отдельными компиляциями копий в build: добавление DU-варианта ломает
F#-матчинги ответа (`ProtocolCodec.delivery` и `encode`), пропуск именованного protobuf-enum — входной match;
добавление C++ enum-варианта ломает switch даже с default. Неизвестный wire-payload
возвращает ошибку codec, неизвестное добавочное поле при известном payload допускается.

## Реализация и генерация

- C++: [ProtocolCodec.ixx](../src/Dreamsleeve.Client.Core/Protocol/ProtocolCodec.ixx),
  ProtocolCodec{config}, Encode(ClientRequest | MovementSample) → DreamNetPacket, Decode(bytes, channel) → ServerResponse.
- F#: [ProtocolCodec.fs](../src/Dreamsleeve.Server.Core/Protocol/ProtocolCodec.fs),
  create config → codec, decodeClient codec → проверенная команда, encode codec budget → пакеты.
- C++ protobuf headers подключаются только в .cpp реализации. Они не попадают
  в интерфейс модуля: [C1001 воспроизведён на MSVC 19.51.36260](../docs/MsvcProtobufModulesRu.md)
  после обновления Visual Studio 18.10.2. Переименование интерфейса в .cpp
  с /interface также вызывает C1001; работает отдельная единица реализации
  (`module Dreamsleeve.Client.ProtocolCodec;`) при интерфейсе без protobuf.

```powershell
python Scripts/generate_protocol.py
python Scripts/run_tests.py
```

Использован установленный protoc 33.2; runtime — protobuf-cpp 33.2 и Google.Protobuf
3.33.2. Сгенерированные .pb.h/.pb.cc/.g.cs хранятся в Protocol.Native/Protocol.Dotnet
и не редактируются вручную. Этот же скрипт извлекает выбранные enum (DisconnectReason,
RequestRejectionCode, ActivityKind, LockDifficulty, ChatChannelKind, AnnouncementSource, AnnouncementKind,
ClientAnnouncementSource, GroundMarkKind, HiddenIdentity, SessionEndReason, PlayerRole, SanctionKind)
из вывода protoc в Dreamsleeve.Protocol.Native.ixx и генерирует ProtocolContract.cpp со static_assert
для всех значений. Оба файла также generated;
ручного списка числовых кодов на стороне клиента нет. Неожиданный формат enum
в выводе protoc останавливает генерацию с ошибкой.
Кодеки используются C++ ClientRuntime/Client.Dev и F# ServerRuntime по настоящему ENet.
PlayerSession собирает SessionOpened из независимых снимков обоих ChatRoomAgent и PresenceAgent,
буферизует дельты до активации и сохраняет исходящий порядок. Канал сам назначает
ID/время, принимает текст от зарегистрированного ConnectionId и возвращает одно
серверное сообщение каждому получателю. Автор получает ChatAccepted с RequestId,
остальные — ChatPublished без ID; на wire это один payload ChatPublished.
MessageId упорядочен внутри канала, идентичность сообщения — (ChannelId, MessageId).
Отказ Overloaded до принятия команды не изменяет историю; перегруженный получатель
рассылки закрывается отдельно. Чат и онлайн сохраняют порядок внутри своих источников,
но не обещают общего порядка между пачками присутствия и сообщениями чата.

Параметры сервера загружаются из TOML при старте. Настройки и запуск:
[Server.Core README](../src/Dreamsleeve.Server.Core/README.ru.md).

Игровые показания принимаются как сообщает клиент: уровень допускает весь uint32,
включая 0 (отсутствие поля означает неизвестный уровень). Сервер проверяет структуру,
конечность чисел и ресурсные лимиты, но не игровые диапазоны и правдоподобность показаний.

### Видимость позиций

В v6 правило видимости сохраняется: PlayerInfo остаётся записью онлайна, optional location в ней
означает положение, доступное конкретному получателю. Сервер передаёт чужие позиции
только при известной позиции получателя, совпадении WRLD/CELL FormKey и расстоянии
XYZ <= Runtime.Presence.VisibilityDistance. Себе игрок получает положение всегда.
На выходе reliable запись видимости без позы очищает положение, сохраняя
метаданные. Вход устанавливает baseline с новым токеном, в том числе когда двигался
только наблюдатель. Joined и начальный снимок содержат доступные позиции;
metadata не обходит AOI.

Описательные Place-поля остаются глобальными данными таблицы онлайна.

Радиус сервера по умолчанию 8192 Skyrim units, граница включена; 0 допустим.
Клиентские настройки отображения не отправляются серверу: выключение светлячков
не является отпиской от пакетов, а больший клиентский радиус не расширяет доставку.

### Репликация компонентов v6

Каждый период Presence рассылает все текущие видимые позиции, включая неподвижных
игроков и самого автора. Latest/Published и Dirty подавляют повторы метаданных,
а не движения. Потерянная часть пачки восстанавливается следующим повтором;
отсутствие записи не означает clear. Срок восстановления при произвольных потерях
не гарантируется.

Патч метаданных несёт только изменившиеся показания и компоненты деталей (раздел «Пачка
присутствия»). Обновлённый игрок применяется для идентичности/имени/поколения персонажа;
Location=None/ViewRevision=0 не очищает позицию того же персонажа. Lifecycle
координат устанавливает отдельная reliable-граница.

playerSampleIntervalMs задаёт период повторения последней локальной позы,
ReplicationIntervalMs — период серверной рассылки актуального состояния. По умолчанию
оба 50 мс (20 Гц); тики не синхронизированы и автоматически не согласуются. Чат/команды
обрабатываются независимо. Сохранять все промежуточные samples не требуется.

### Организация преобразований

Публичная точка входа — ProtocolCodec (F# type/module и C++ Wire::ProtocolCodec,
модуль Dreamsleeve.Client.ProtocolCodec). Она владеет одной проверенной конфигурацией,
парсингом/сериализацией оболочки, версией, лимитом пакета, корреляцией и диспетчеризацией.
Внутренние ChatCodec, PlayerCodec, SessionCodec, кодек меток (F# GroundMarkCodec, C++ GroundCodec)
и ModerationCodec выполняют преобразования своих сообщений; SessionCodec использует
преобразования игроков и истории чата. Отдельных
экземпляров, DI или конфигураций на каждую часть нет. Серверные общие типы находятся
в Protocol/ProtocolTypes.fs: ClientCommand, ClientRequest, ServerResponse, SessionWelcome
и RequestRejection; ошибки — ProtocolCodecError/ProtocolCodecFailure.

На C++ все protobuf-типы остаются в global module fragment обычных .cpp. CodecParts.h
содержит только внутренние объявления функций и включается после module declaration;
в публичный .ixx protobuf не попадает.

### Время измерения и пакетирование v6

PlayerLocation.sampled_at_us в baseline и MovementPose.sampled_at_us в realtime —
монотонные микросекунды источника, не UTC. Часы игроков не сравниваются. Ноль допустим
и допускает интерполяцию по приёму; порядок задаёт sequence, не timestamp.
C++ runtime при повторе последней позы назначает новый sequence, а метку оставляет
временем замера из ClientExchange: повтор закрывает потерю пакета и не выдаёт себя за
новое измерение (время отправки сдвигало бы позу до одного интервала при движении).
Сервер сохраняет source sequence/time при пересылке, не выдавая повтор за новое
измерение. Та же sequence не добавляет наблюдение интерполяции.
[Клиентская история](../docs/MovementInterpolationRu.md).

ClientMovementPacket содержит один MovementSample; ServerMovementPacket — непустой
список PlayersMoved. Каждая запись самостоятельно применима. Список делится по
реальному сериализованному размеру с envelope/varint и negotiated MTU. Reliable
bootstrap может фрагментироваться, realtime — нет. Передача пачки не делает доставку
атомарной и не требует ожидания окончания всего прикладного тика.

Старые UpdatePlayer.sample_movement=6, ServerPacket.players_moved=19 и
PlayerMoved.location=2 зарезервированы. SetLocation использует tag8, видимость — часть
PresenceChanged; pose/token/sequence движения — отдельные realtime оболочки. Старые ветки
одновременно не поддерживаются.
