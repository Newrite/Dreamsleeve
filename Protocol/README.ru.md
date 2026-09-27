# Прикладной протокол сессии, версия 6

Схемы разделены по назначению:

| Файл | Содержимое |
|---|---|
| [common.proto](common.proto) | PlayerProfile и FormKey |
| [chat.proto](chat.proto) | SendChat, ChatMessage, ChatPublished |
| [player.proto](player.proto) | Состояние персонажа, движение, actor values, Details и уведомления |
| [session.proto](session.proto) | OpenSession и начальный SessionOpened |
| [protocol.proto](protocol.proto) | ClientPacket/ServerPacket, подтверждение обновления и общие отказы |
| [network.proto](network.proto) | Причины отключения ENet и фиксированные DeliveryLane |

Граф импортов направлен от оболочек к сообщениям, от сообщений к общим типам;
циклов нет. Package `Dreamsleeve.Protocol.Chat` сохранён для существующих C++/C#
имён. Файловое разделение не меняет номера, типы, oneof, reserved или wire-формат;
Версия 6 отделяет движение от команд; версии 1–5 несовместимы с текущей. Native-код, работающий с оболочками, включает `protocol.pb.h`.
Генерация всех схем выполняется одной командой `python Scripts/generate_protocol.py`.

## Оболочки и сессия

Одно protobuf-сообщение занимает один ENet packet без внешнего length prefix.
Все оболочки содержат protocol_version = 6. Неизвестные дополнительные поля
допускаются; отсутствие ожидаемого payload или другая версия дают ошибку codec.

| Канал | DeliveryLane | Назначение |
|---|---|---|
| 0 | Control | ClientPacket/ServerPacket: сессия, lifecycle, UpdatePlayer и ответы, reliable |
| 1 | Chat | ClientPacket/ServerPacket: SendChat, ChatPublished и ответы чата, reliable |
| 2 | Realtime | ClientMovementPacket/ServerMovementPacket: абсолютные pose, unreliable sequenced (flags=0) |

Нужно минимум три согласованных канала. Номера фиксированы в network.proto,
надёжность задаётся флагом пакета. RequestRejected возвращается на канал исходной
команды; SessionOpened с историей всегда Control. Между каналами общего порядка нет.
Flags=0 — не Unsequenced/UnreliableFragment. При исчерпании unreliable sequence
ENet может внутренне перейти к reliable; прикладного ACK движения при этом нет.

Сессия привязана к одному ENet-соединению. После транспортного Connected клиент
посылает OpenSession. Только SessionOpened переводит прикладную сессию в Ready.
Повторное открытие на том же соединении и SendChat до Ready должен отклонять
серверный владелец. Codec не хранит состояние соединения и сам эти правила не применяет.

OpenSession передаёт только session_ticket: одноразовый билет из HTTP login,
32 случайных байта в base64url без padding (43 символа). Старые номера полей 1/2
и имена username/display_name зарезервированы; версии 1/2/3 несовместимы с версией 4.
Имя, отображаемое имя и PlayerId берутся из профиля, связанного с билетом.
Отсутствующий, просроченный, неизвестный или использованный билет не открывает сессию.
SessionTable резервирует PlayerId до завершения агента и очистки членства;
другой вход с этим ID получает SessionAlreadyOpen и закрывается.

Регистрация и проверка пароля идут отдельно от ENet: POST /auth/register принимает
username/displayName/password, POST /auth/login — username/password и возвращает
sessionTicket/expiresInSeconds/playerId/username/displayName. Повторный вход требует
нового login и билета. Core не выполняет HTTP и не получает пароль; в Client.Dev
эту границу обслуживает WinHTTP. Удалённый HTTP endpoint требует HTTPS;
plain HTTP допустим только для явно разрешённой локальной разработки.

После переподключения требуется новое начальное состояние. SessionOpened заменяет
прежнее состояние сессии, а не продолжает старую историю. В этой версии нет resume,
продолжения сессии по прежнему билету или history epoch; локальные generation/revision/round не передаются.

## Сообщения и корреляция

| Направление | Payload | Содержание |
|---|---|---|
| Клиент → сервер | OpenSession | Одноразовый SessionTicket |
| Клиент → сервер | SendChat | ChannelId и текст, без авторства/времени/MessageId |
| Сервер → клиент | SessionOpened | SelfPlayerId, GlobalChannelId, весь онлайн и хвост истории |
| Сервер → клиент | ChatPublished | Одно принятое сообщение |
| Сервер → клиент | RequestRejected | Общий RequestRejectionCode, объяснение, поле |
| Клиент → сервер | UpdatePlayer | BeginCharacter / RenameCharacter / SetLocation / SetActorValues / LeaveGame / SetDetails |
| Сервер → клиент | PlayerJoined / PlayerLeft | Полный PlayerInfo нового игрока / ID ушедшего |
| Сервер → клиент | PlayerUpdated / PlayerMetadataChanged | Идентичность и компоненты / изменённые actor values и Details |
| Сервер → клиент | PlayerVisibilityChanged | Reliable baseline/clear с view_revision и sequence |
| Клиент → сервер | ClientMovementPacket.sample | context_revision, sequence, pose без RequestId |
| Сервер → клиент | ServerMovementPacket.movements | PlayerMoved: player_id, view_revision, sequence, pose |
| Сервер → клиент | PlayerUpdateAccepted | ACK команды UpdatePlayer |

RequestId — ненулевой uint64, назначаемый клиентским API до отправки. Клиент должен
выдавать уникальные ID в течение жизни соединения; пропуски допустимы. Это не
ChatMessageId, не серверная последовательность и не обещание дедупликации запросов.
Повторная отправка после переподключения не является безопасным retry без отдельного
контракта. Счётчик общий для ClientRuntime и UI через ClientExchange.NextRequestId;
владелец сопоставляет OpenSession и ограниченное число ожидающих SendChat/UpdatePlayer.

В ClientPacket RequestId обязателен. В ServerPacket его наличие различается:

- SessionOpened, PlayerUpdateAccepted и RequestRejected обязательно возвращают ID исходного запроса.
- ChatPublished содержит RequestId только в копии инициатору. Остальные получают
  то же принятое сообщение без RequestId. ID других клиентов не завершает свои запросы.
- PlayerJoined/PlayerLeft/PlayerUpdated/PlayerVisibilityChanged/PlayerMetadataChanged не содержат RequestId. Явный ноль всегда ошибочен.

Realtime-оболочки вообще не имеют RequestId: samples не занимают pending,
не требуют PlayerUpdateAccepted, retry или коррелированного отказа.

Optional RequestId существует только в общей protobuf-оболочке и диагностике codec.
В прикладных ответах наличие ID закреплено вариантом типа:

- F# ServerResponse.SessionOpened, ChatAccepted и RequestRejected принимают обязательный uint64 ID.
- F# ChatPublished, PlayerJoined и PlayerLeft — уведомления без поля RequestId.
- C++ ServerResponse — variant; SessionOpened, ChatAccepted и ServerRejection содержат ID,
  ChatMessagesReceived, PlayerUpserted и PlayerRemoved — без него.

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

## Игровое состояние

PlayerInfo включает неизменяемую идентичность PlayerProfile, optional character_name,
optional PlayerLocation, actor_values, character_generation, PlayerDetails,
view_revision и movement_sequence. SessionOpened
содержит PlayerInfo в поле players=5; старое поле 3 зарезервировано. PlayerJoined тоже
несёт PlayerInfo. Это несовместимое изменение, закреплённое protocol_version=3.

PlayerLocation содержит Location(FormKey(plugin_name/local_form_id), location_name),
Position XYZ в world units и Rotation XYZ в радианах. ActorValueEntry имеет key,
display_name и oneof scalar/resource(current/maximum). Scalar 0 присутствует явно;
отсутствующий oneof — ошибка. Reliable SetLocation устанавливает пространство и
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
PlayerUpdated с view_revision=0 обновляет идентичность/компоненты без переустановки
позиции текущего персонажа. Новая character_generation сразу сбрасывает старую
позицию и контекст. Reliable PlayerVisibilityChanged устанавливает baseline/clear;
PlayerMetadataChanged никогда не меняет позицию.

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

SessionOpened содержит уникальные PlayerId, включая SelfPlayerId. RecentMessages
принадлежат GlobalChannelId и строго возрастают по MessageId; пустая история допустима.
Это ограниченный хвост для начала работы. Здесь нет курсора, hasMore или запроса старой
истории. Будущая пагинация должна отдельно определить историю и её epoch.

В C++ SessionOpened непосредственно содержит requestId, selfPlayerId,
globalChannelId, players и recentMessages. Players уже представлены обычными
Domain::Player, сообщения — Domain::ChatMessage; отдельного типа состояния сессии нет.

Начало сессии — последовательность действий владельца соединения. После проверки
ожидаемого запроса он сбрасывает прежнюю модель, регистрирует канал, применяет
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
транзакции для этого сценария. PlayerJoined может содержать отфильтрованный baseline.

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
и лимиты начального состояния. `config.network` передаётся в создание DreamNetHost,
сам config — в `Wire::ProtocolCodec::TryCreate(config)`. Полученный codec сохраняет копию
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
параметров: `ServerConfig.validate config`, затем `ServerConfig.applyPacketLimits
config host` после создания yENet host и **до первого Service/Connect**.
Из этого же config один раз создаётся `ProtocolCodec.create config`; затем используются
`ProtocolCodec.decodeClient codec bytes` / `ProtocolCodec.encodeServer codec response`.
EnetTransport применяет лимиты к реальному yENet host; все native операции
Host/Peer выполняет один владелец транспорта.

Конфигурация фиксируется на срок жизни сетевого владельца; менять только codec
после создания host нельзя. Создание кодека отклоняет некорректные лимиты через Result;
проверка всей конфигурации на каждом пакете не повторяется. C++ при создании также
ограничивает maxPacketBytes диапазоном int для protobuf ParseFromArray/SerializeToArray.
Сам размер каждого входного/выходного сообщения всё равно сравнивается с лимитом.
Серверный загрузчик JSON заполняет конфигурацию перед запуском владельца;
для C++ внешняя загрузка этих настроек остаётся следующим расширением.
ENet не согласует эти прикладные лимиты между
сторонами: пока развёртывание должно задавать совместимые настройки клиента и
сервера. Согласование по сети — отдельное расширение входа в сессию.

F# decodeClient проверяет форму SessionTicket и использует доменные фабрики для
ChatChannelId и ChatMessageText. Username и DisplayName проверяются на HTTP-границе. Лимиты строк задаёт приложение через ServerConfig.ChatInput; текущие
defaults — 32 / 64 / 2000 Unicode scalar values соответственно. Username нормализуется
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
В ProtocolCodec.fs и PlayerCodec.fs подавлен только FS0104 о неименованных enum-значениях; новый именованный
вариант по-прежнему требует обработки и вызывает FS0025.

В C++ реализациях кодека включены ошибки C4061/C4062 после generated headers.
PAYLOAD_NOT_SET обработан явно; default оставлен для неизвестных значений, но не
скрывает новые именованные enum-варианты. Visitor ClientRequest также явно отличает
OpenSession и SendChat через две перегрузки RequestWriter::operator(), без generic
fallback. Новая альтернатива требует перегрузки; проверено отдельной компиляцией
с /O2 /DNDEBUG: существующие типы собираются, добавленный ProbeAdded даёт C2672.
Сгенерированные файлы не редактируются ради этих проверок.

Проверено отдельными компиляциями копий в build: добавление DU-варианта ломает все
три F#-матчинга ответа, пропуск именованного protobuf-enum — входной match;
добавление C++ enum-варианта ломает switch даже с default. Неизвестный wire-payload
возвращает ошибку codec, неизвестное добавочное поле при известном payload допускается.

## Реализация и генерация

- C++: [ProtocolCodec.ixx](../src/Dreamsleeve.Client.Core/Protocol/ProtocolCodec.ixx),
  Codec::TryCreate(config), Encode(OpenSession | SendChat) → DreamNetPacket, Decode(bytes) → ServerResponse.
- F#: [ProtocolCodec.fs](../src/Dreamsleeve.Server.Core/Protocol/ProtocolCodec.fs),
  create config → codec, decodeClient codec → проверенная команда, encodeServer codec → bytes.
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
и не редактируются вручную. Этот же скрипт извлекает DisconnectReason и
RequestRejectionCode, ActivityKind и LockDifficulty из вывода protoc в Dreamsleeve.Protocol.Native.ixx и генерирует
ProtocolContract.cpp со static_assert для всех значений. Оба файла также generated;
ручного списка числовых кодов на стороне клиента нет. Неожиданный формат enum
в выводе protoc останавливает генерацию с ошибкой.
Кодеки используются C++ ClientRuntime/Client.Dev и F# ServerRuntime по настоящему ENet.
PlayerSession собирает SessionOpened из независимых снимков ChatRoomAgent и PresenceAgent,
буферизует дельты до активации и сохраняет исходящий порядок. Канал сам назначает
ID/время, принимает текст от зарегистрированного ConnectionId и возвращает одно
серверное сообщение каждому получателю. Автор получает ChatAccepted с RequestId,
остальные — ChatPublished без ID; на wire это один payload ChatPublished.
MessageId упорядочен внутри канала, идентичность сообщения — (ChannelId, MessageId).
Отказ Overloaded до принятия команды не изменяет историю; перегруженный получатель
рассылки закрывается отдельно. Чат и онлайн сохраняют порядок внутри своих источников,
но не обещают общего порядка между PlayerJoined/PlayerLeft и сообщениями чата.

Параметры сервера загружаются из JSON при старте. Настройки и запуск:
[Server.Core README](../src/Dreamsleeve.Server.Core/README.ru.md).

Игровые показания принимаются как сообщает клиент: уровень допускает весь uint32,
включая 0 (отсутствие поля означает неизвестный уровень). Сервер проверяет структуру,
конечность чисел и ресурсные лимиты, но не игровые диапазоны и правдоподобность показаний.

### Видимость позиций

В v6 правило видимости сохраняется: PlayerInfo остаётся записью онлайна, optional location в ней
означает положение, доступное конкретному получателю. Сервер передаёт чужие позиции
только при известной позиции получателя, совпадении WRLD/CELL FormKey и расстоянии
XYZ <= Runtime.Presence.VisibilityDistance. Себе игрок получает положение всегда.
На выходе reliable PlayerVisibilityChanged без location очищает положение, сохраняя
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

PlayerMetadataChanged включает изменившиеся actor_values/details: отсутствующий
блок не меняет компонент, присутствующий заменяет целиком. Пакет без обоих блоков
недопустим. PlayerUpdated применяется для идентичности/имени/поколения персонажа;
Location=None/ViewRevision=0 не очищает позицию того же персонажа. Lifecycle
координат устанавливает отдельная reliable-граница.

playerSampleIntervalMs задаёт период повторения последней локальной позы,
ReplicationIntervalMs — период серверной рассылки актуального состояния. По умолчанию
оба 100 мс; тики не синхронизированы и автоматически не согласуются. Чат/команды
обрабатываются независимо. Сохранять все промежуточные samples не требуется.

### Организация преобразований

Публичная точка входа — ProtocolCodec (F# type/module и C++ Wire::ProtocolCodec,
модуль Dreamsleeve.Client.ProtocolCodec). Она владеет одной проверенной конфигурацией,
парсингом/сериализацией оболочки, версией, лимитом пакета, корреляцией и диспетчеризацией.
Внутренние ChatCodec, PlayerCodec и SessionCodec выполняют преобразования своих
сообщений; SessionCodec использует преобразования игроков и истории чата. Отдельных
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
C++ runtime при повторе последней позы назначает новый sequence и текущее время.
Сервер сохраняет source sequence/time при пересылке, не выдавая повтор за новое
измерение. Та же sequence не добавляет наблюдение интерполяции.
[Клиентская история](../docs/MovementInterpolationRu.md).

ClientMovementPacket содержит один MovementSample; ServerMovementPacket — непустой
список PlayersMoved. Каждая запись самостоятельно применима. Список делится по
реальному сериализованному размеру с envelope/varint и negotiated MTU. Reliable
bootstrap может фрагментироваться, realtime — нет. Передача пачки не делает доставку
атомарной и не требует ожидания окончания всего прикладного тика.

Старые UpdatePlayer.sample_movement=6, ServerPacket.players_moved=19 и
PlayerMoved.location=2 зарезервированы. SetLocation использует tag8,
PlayerVisibilityChanged — tag20; pose/token/sequence движения — отдельные realtime
оболочки. Старые ветки и v5 одновременно не поддерживаются.
