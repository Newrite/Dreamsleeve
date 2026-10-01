# Состояние клиента и уведомления

`PlayerStore`, `ChatCache` и `ClientModel` принадлежат одному последовательному
владельцу. Все методы, включая чтение, вызываются на нём. Перед передачей другому
потоку владелец формирует самостоятельные значения и передаёт их через
`ClientExchange`. Отдельный SnapshotMailbox удалён как неиспользуемый.

`ChangeBatch` — накопитель затронутых ID внутри владельца модели;
`ClientStateUpdate` — снимок или дельта с собственными данными для получателя;
`StateUpdateQueue` — внутренний буфер; синхронизацию между потоками обеспечивает ClientExchange.
Это один путь доставки, без параллельной публикации в mailbox.

## TakeChanges

```cpp
Dreamsleeve::Client::ChangeBatch changes; // Переиспользовать между итерациями.
model.TakeChanges(changes);
// Разрешить ID на владельце модели до её следующего изменения.
```

| Поле | Действие владельца |
|---|---|
| `requiresSnapshot` | Полностью заменить проекцию через `Snapshot()`; имеет приоритет над остальным |
| `playersReplaced` | Согласовать весь онлайн, включая ушедших, через `SnapshotPlayers()` без копии чатов |
| `selfPlayerChanged` | Прочитать `SelfPlayerId()` |
| `players` | Прочитать `FindPlayer(id)`; отсутствие означает удаление |
| `chats` | Прочитать `FindChatState(id)` без сообщений; отсутствие означает удаление канала |
| `resetChats` | Подмножество `chats`: если канал ещё существует, очистить прежние сообщения перед применением `chatContent` |
| `chatContent` | Передать и применить `Evicted/Added/Deleted` строго в сохранённом порядке |

Полные сообщения каналов есть только в `Snapshot()` для инициализации/восстановления.
Обычное сообщение не помечает `chats`. `ChatCacheState` включает count, но его
изменение при приёме сообщения не является отдельным уведомлением: получатель
обрабатывает content-delta. `chats` сообщает о регистрации канала.

## Дельты содержимого чата

```text
Новое сообщение:       Added [message]
Вытеснение из кэша:     Evicted [oldMessageId], затем Added [message]
Удаление модератором:   Deleted [messageId]
Идентичный дубликат:    без content-delta
```

`ChatMessagesEvicted` — только освобождение места в кэше Core: получатель ограничивает свою
историю сам и строки из-за него не убирает. `ChatMessagesDeleted` убирает сообщение отовсюду,
в том числе из более длинной истории получателя.

`ChatCache::Merge` возвращает точный видимый переход: новые сообщения, которые
сразу вытеснились из-за capacity, не попадают в `addedMessages`. Порядок кэша —
возрастание MessageId, а не порядок прихода или timestamp. Получатель дельт должен
поддерживать этот порядок. Соседние однотипные изменения одного канала могут
объединяться. Регистрация помечает канал в `resetChats`: получатель очищает прежние
сообщения этого ID. Клиентский лимит истории независим от серверного; постраничной
подгрузки истории нет — сервер присылает хвост каналов при входе.

## Готовые данные для получателя

Модуль `Dreamsleeve.Client.StateUpdate` предоставляет операцию, которая сразу
вычитывает ChangeBatch и разрешает его ID на владельце модели:

```cpp
import Dreamsleeve.Client.StateUpdate;

// scratch живёт у сетевого владельца и переиспользуется между итерациями.
Dreamsleeve::Client::ChangeBatch scratch;
auto update = Dreamsleeve::Client::TakeStateUpdate(model, scratch);
// update владеет данными: его можно переместить в StateUpdateQueue.
```

Результат — `optional<variant<ClientSnapshot, ClientStateDelta>>`:

- `nullopt`: изменений состояния нет; это возможно и после успешного дубликата
  сообщения либо ServerRejection. Отказы по-прежнему вычитываются отдельно.
- `ClientSnapshot`: `requiresSnapshot` имел приоритет, получатель полностью
  заменяет прежнее состояние.
- `ClientStateDelta`: собственные копии затронутых игроков, ID удалённых игроков,
  состояния каналов и перенесённые из накопителя дельты содержимого чата.

Для delta сначала при `playersReplaced` очищается онлайн, затем применяются
`players` как upsert и `removedPlayers` как удаления. `selfPlayerId` применяется
только при `selfPlayerChanged`: пустой optional тогда означает сброс своего ID.
Для каналов отсутствие `ChatStateChange.state` означает удаление, `resetContent`
при существующем канале — очистку его сообщений. После этого применяются
`chatContent` в порядке массива, с сохранением сортировки сообщений по MessageId.
Метаданные state описывают окончательный результат пачки; count не нужно
повторно увеличивать относительно уже переданного итогового count.

TakeStateUpdate сам вызывает TakeChanges; отдельно вызывать оба для одной пачки
нельзя. `scratch` — рабочий буфер, его содержимое после вызова не предназначено
для повторной доставки. Вызывающий не допускает других операций модели посреди
этого вызова. Результат не содержит ссылок на модель и переживает её изменения
и уничтожение. Верхний буфер `chatContent` передаётся результату перемещением;
его ёмкость, как и вложенные векторы, не остаётся в scratch для переиспользования.

Это подготовка payload; потокобезопасную доставку обеспечивает ClientExchange.
ClientExchange объединяет подготовку и выдачу одному потребителю; ClientRuntime использует его для входа/отключения и публикации модели.
Как и другие операции модели, сборка payload может бросить при аллокации:
продолжение этой же сессии после такого сбоя не гарантируется.

## Очередь между владельцем модели и потребителем

`Dreamsleeve.Client.StateUpdateQueue` хранит самостоятельные пакеты без mutex.
В рабочем клиенте все обращения сериализует ClientExchange своим единственным mutex.
При отдельном использовании очереди вызывающий обязан сериализовать доступ.
Один владелец модели публикует их по порядку; один потребитель забирает и применяет
пачки последовательно. Очередь не обращается к модели и не вызывает callbacks.

`Create(capacity)` доверяет лимиту: нулевой отвергает `ClientExchange::TryCreate`
(`InvalidConfig`, поле `stateCapacity`). Очередь создаётся через unique_ptr и принадлежит
одному Exchange. Новая очередь требует начального снимка.
При переполнении вся ожидающая цепочка удаляется; Publish возвращает
`SnapshotRequired`, а следующие дельты отклоняются до публикации полного снимка.
`batch.requiresSnapshot` показывает это состояние; чтение его не сбрасывает. При пустой пачке с этим флагом потребитель ожидает
восстановления владельцем, а не применяет неполную цепочку.

Свежий снимок заменяет все ожидающие пакеты, даже если очередь заполнена.
Уже вычитанные данные остаются собственностью потребителя и не изменяются.
TakeAll очищает предыдущий output и меняет векторы местами. Аллокации возможны;
лимит числа пакетов не ограничивает размер отдельного снимка или дельты.

Владелец отвечает за свежесть снимков и порядок публикации: очередь не проверяет
generation/revision. ClientExchange вычитывает модель один раз и передаёт
результат в единственную очередь состояния. Ошибки команд (`ServerRejection`)
передаются отдельно и не заменяются снимком. Измерения движения входят в
ClientStateDelta.movement; новый снимок сбрасывает их историю.

## Сессия и ошибки

- Отправка собственного сообщения не вызывает локальное добавление в ChatCache.
  Сервер возвращает принятое сообщение автору, и оно применяется тем же
  `ChatMessagesReceived`, что и чужое. При отказе история не меняется.
  SelfPlayerId не создаёт отдельного пути применения; подтверждаемые изменения
  поступают в модель после серверного события. Исходящие команды описаны в ClientExchange;
  SendChat кодируется `Dreamsleeve.Client.ProtocolCodec` и обслуживается ClientRuntime через Exchange.
- `Apply` отвергает другое поколение до изменения данных. Успешная операция
  увеличивает revision даже при no-op; ошибка `Domain::Result` сохраняет уже
  накопленные уведомления. Это не транзакционная гарантия при `bad_alloc`.
- Начальные данные применяются обычными операциями ResetSession, RegisterChannel,
  OnlinePlayersReplaced, SetSelfPlayer и ChatMessagesReceived. Порядок задаёт владелец;
  он публикует состояние после полного события. При ошибке входа незавершённая модель
  очищается; откат к прежней сессии не требуется. В модели нет отдельного объекта
  начального состояния или операции, дублирующей её хранилища для входа.
- `ResetSession` в начале каждой сессии удаляет игроков, каналы, сообщения и метки,
  меняет поколение и заменяет уведомления требованием полного снимка.
- Обновления модели — только те, что применяет runtime: онлайн целиком, игрок
  целиком (новый персонаж — новый `characterGeneration`), выход, место, движение,
  метаданные (показания и описание заменяются частями), сообщения, удаление сообщения
  модератором и метки.
- Модель не хранит итоги команд: отказ сервера, как и подтверждение, идёт в
  `ClientExchange` результатом команды (см. «Результаты команд»).
- `generation/revision` batch фиксируются при `TakeChanges`, в том числе для
  пустого batch. Revision не является последовательностью будущих UI-пакетов.

## Буферы

`TakeChanges` сначала очищает output, затем меняет его местами с внутренним
накопителем. Поэтому внутренний `requiresSnapshot` после обмена равен false.
Ёмкости верхних векторов переиспользуются. Вложенные в `chatContent` векторы
освобождаются при Clear; обещания полностью безаллокирующей доставки нет.
`MarkId` — поиск и push_back, без локального catch и без гарантии восстановления
после ошибки выделения памяти. Перехват на границе runtime ещё предстоит написать.

## Обмен с одним потребителем

Сетевой поток единолично владеет моделью, codec и ENet. Игровой поток один раз
за обновление вычитывает данные, затем использует их для UI и присутствия.
Реестр подписчиков, отдельные handles и очередь для каждого окна удалены.
Открытие UI не включает и не выключает вычитку. SKSE-адаптер (`Host::Session`) хранит своё
представление и при пересоздании окна запрашивает снимок владельца (`RequestSnapshot`).

`ClientExchange` создаётся до запуска владельца и живёт до его join.
Он не создаёт поток и не является готовым ClientRuntime. Его две стороны:

| Игровой/UI-поток | Сетевой владелец |
|---|---|
| Post(QueuedClientCommand) | TakeCommands(vector&, pendingReplies) без ожидания |
| Drain(ClientOutput&) | Publish(model, requestSnapshot) |
| CloseInput() перед остановкой | Обработать принятые команды, Publish, Finish |

Публичный API ClientApplication вызывается только главным потоком приложения,
который также обслуживает UI. Это обычный поток, не агент. PostLogin,
RequestDisconnect и RequestStop используют отдельный слот управления и флаги,
поэтому заполненная очередь игровых команд не блокирует отключение/остановку.
Сетевой владелец читает TakeControl, завершает вход через CompleteAuthentication и публикует
ошибки через PublishError. Drain выдаёт статус и состояние под одним mutex;
Status() читает тот же статус отдельно. В ClientApplication нет второго mutex,
дублирующего статуса или callbacks на сетевом потоке.

Post возвращает Queued, Replaced, Full или Closed. Queued не означает принятие
сервером. Ошибка конфигурации TryCreate возвращается через Domain::Result.
Игровые объекты и указатели на них не передаются: адаптер снимает значения и
преобразует их в доменные типы, кодирование/отправка выполняются сетевым владельцем.

Команды: SendChat, PostAnnouncement, PlaceGroundNote, ReportDeath, RemoveGroundMark, SetIdentityVisibility,
ChangeDisplayName, SanctionPlayer, LiftSanction, KickPlayer, ListSanctions, ListPlayerMarks,
ClearPlayerMarks, DeleteChatMessage, LocalMovement, LocalLocation, LocalActorValues, CharacterStarted,
CharacterRenamed, PlayerDetailsChanged, GameExited, RequestSnapshot.

PostAnnouncement (системный канал, текст, вид, заявленный источник, подпись) идёт по
пути SendChat: Chat-канал ENet, общий лимит ожидающих чат-запросов, `MessagePublished`
при публикации, `ServerRejection` при отказе. Канал команды должен быть известен и
подходящего вида: SendChat — `Global`, PostAnnouncement — `System`, иначе локальный
`InvalidRequest`. Разрешён ли источник и укладываются ли текст и подпись в лимиты, Core
сверяет с `AnnouncementPolicy` из приветствия; отказ — локальный `InvalidRequest`.

Канал знает свой вид (`RegisterChannel(id, capacity, kind)`, `ChatCacheState::kind`,
`ChatCacheSnapshot::kind`). `ChatCache::Merge` отвергает объявление в не системном
канале и сообщение без объявления в системном (`ChannelMismatch`). У сообщения
`author` — `std::optional`: его нет только у объявлений сервера.
NextRequestId общий для reliable-команд; движение не требует ID или результата.
QueuedClientCommand несёт generation текущей сессии. LocalMovement хранит последнюю
позу, LocalLocation — явный reliable-переход/clear. Объединяются только соседние
samples одного пространства и generation; переходы и остальные команды сохраняются.
ClientRuntime повторяет последнюю позу по playerSampleIntervalMs независимо от
поступления новых samples; новый контекст отправляет после принятия перехода.

ClientModel принимает realtime только для уже видимого игрока с совпадающим
viewRevision и большим movementSequence. Потери и перестановки не создают session
fault. Reliable baseline/clear меняет токен и историю; metadata сохраняет актуальную
позу. Повтор той же серверной sequence не создаёт новое наблюдение интерполяции.

Publish первый раз выдаёт снимок, затем вычитывает ChangeBatch и перемещает дельту
в очередь. **Обычное сообщение не копирует всю историю чата**: в пакет входят только
Added/Evicted/Deleted. Снимок используется при явном запросе, смене сессии и переполнении.
Запрошенный снимок сразу поглощает накопленные изменения, исключая их повторную выдачу.

Один Drain возвращает StateUpdateBatch, результаты команд и ClientStatus
(phase/authenticating/stopped/error, состояние входа, serverName, скрытое имя, `mute`, `role`,
`sessionEnd`).

### Результаты команд

Каждую команду с RequestId закрывает ровно один `CommandResult{generation, requestId, outcome}`
в `ClientOutput.results`, в порядке, в котором владелец их выдал. `outcome` —
`MessagePublished` (SendChat, PostAnnouncement), `MarkPlaced` / `MarkRemoved` (метки),
`IdentityChanged` (скрытое имя; псевдоним — в `ClientStatus`), `NameChanged` (отображаемое
имя), ответы модератору (`Sanctioned`, `Lifted`, `Kicked`, `SanctionsListed`, `MarksListed`,
`MarksCleared`, `MessageDeleted`), `ServerRejection{code, message, field}` или локальный
`CommandFailureCode`. `generation` —
поколение команды: результат переживает reset, потребитель сверяет его сам. Новая команда
добавляет один вариант исхода, а не поле `ClientOutput` и параметр `Publish`.

Общий бюджет commandCapacity покрывает невыданные результаты и pendingReplies — ожидаемые
владельцем ответы, включая OpenSession. TakeCommands резервирует место для каждого
выданного запроса, оставляя остальные в очереди до Drain; CanAcceptReplies проверяет место
перед запросом вне очереди команд. Поэтому результат, для которого место зарезервировано,
всегда помещается. Результат вместе с состоянием, которое он подтверждает, публикует
`Publish(model, …, result)`; результат без состояния (локальный отказ) — `PublishResult`.
Если результат не поместился, Publish возвращает false, а состояние и phase всё равно
публикуются, чтобы показать сбой; владелец завершает сессию с ошибкой. Это не блокирует
сетевой Poll.
Отказы сохраняются при замене состояния снимком и смене сессии; потребитель учитывает
их исходную generation. Лимиты задаются для числа команд, результатов и пакетов
состояния, не для байтов: размер отдельного текста или снимка этим не ограничен.
Владелец должен регулярно Publish, потребитель — регулярно Drain.

После CloseInput новые Post получают Closed, уже принятые команды остаются в FIFO.
TakeCommands возвращает false, когда закрытая входная очередь исчерпана. Владелец
завершает обработку взятой пачки, публикует результаты и вызывает Finish. После Finish
он больше не обращается к exchange. Если остановка произошла при заполненном бюджете
результатов, stopped отменяет команды, оставшиеся в очереди; отдельных отказов на них
не создаётся. Потребитель может забрать финальную выдачу;
перед уничтожением exchange вызывающий код обязан выполнить join потока владельца.

Параллельно вычитывать ту же модель через TakeChanges/TakeStateUpdate или второй
exchange нельзя. Измерения движения со временем приёма добавлены в ClientStateDelta.movement;
MovementView хранит их историю со сбросами на границах сессии/персонажа/
пространства. Схлопнутые изменения последнего состояния для этой истории не годятся.

## Скрытое имя

`ClientExchange::SetHideIdentity(Domain::HiddenIdentity)` (главный поток: `None`, `Everywhere`,
`ExceptGroundMarks`) задаёт выбор, который владелец читает при открытии следующей сессии
(`OpenSession.hidden_identity`); идущую сессию меняет только
`SetIdentityVisibility{requestId, hiding}` — по пути команд меток: Control-канал ENet, свой
набор ожидания, одна команда за раз (вторая — `CommandFailureCode::Busy`). Итог —
`IdentityChanged{hiding}`, `ServerRejection` или `CommandFailureCode`. `ClientStatus::pseudonym`
и `ClientStatus::hiding` — псевдоним текущей сессии, который видят другие, и где (из
`SessionOpened` и подтверждений; очищаются при завершении сессии). Модель не меняется: своя запись всегда приходит с настоящим профилем, а
`Domain::PlayerData::pseudonymous` отмечает чужие псевдонимные профили (username пуст).

`ChangeDisplayName{requestId, displayName}` — тот же путь: Control-канал, свой набор ожидания,
одна смена за раз (вторая — `CommandFailureCode::Busy`); локально отклоняется только
некорректный UTF-8 (`InvalidRequest`), пустое имя и словарь судит сервер. Итог — `NameChanged{displayName}`,
`ServerRejection` или `CommandFailureCode`. Модель подтверждение не меняет: своя запись с
новым именем приходит `PlayerUpserted`.

## Метки на земле

`PlaceGroundNote{requestId, text, placement, gameDate}`, `ReportDeath{requestId, label, placement, gameDate}`
и `RemoveGroundMark{requestId, markId}` идут по пути SendChat, но по Control-каналу ENet и с
собственным набором ожидающих запросов в ClientRuntime (бюджет `maxPendingChatRequests`).
Codec, как и для остальных команд, проверяет только ненулевой requestId, корректный UTF-8 и
размер пакета; текст, положение, дату, слова, квоты и частоту судит сервер. Результат —
`MarkPlaced{markId, evictedId}` или `MarkRemoved{markId}`, `ServerRejection` или `CommandFailureCode`.

Видимые метки живут в `GroundMarkStore` модели (`ClientSnapshot.groundMarks` с
`viewRevision`). Обновление `GroundMarksChanged{viewRevision, added, removedIds, clear}`
применяется только с растущим `viewRevision`, иначе `InvalidCursor` — дельты reliable и
упорядочены, повтор означает ошибку протокола. `ChangeBatch.groundMarks` и
`ClientStateDelta.groundMarks` несут упорядоченные переходы `GroundMarksCleared` /
`GroundMarksRemoved` / `GroundMarksAdded`; `clear` отбрасывает ещё не вычитанные
переходы, получатель начинает заново. `ResetSession` очищает хранилище. Собственная метка попадает в модель той же дельтой, что у других; метки не
попадают в `ChatCache`, `freshMessages` и облачка. Полный список своих меток, где бы они ни стояли,
сервер присылает отдельно (`OwnGroundMarksReplaced`): `ClientSnapshot.groundMarks.own` и
`ClientStateDelta.ownGroundMarks` (есть только при замене).

## Модерация

`SanctionPlayer`, `LiftSanction`, `KickPlayer`, `ListSanctions`, `ListPlayerMarks` и
`ClearPlayerMarks` идут по Control-каналу со своим набором ожидания (бюджет
`maxPendingChatRequests`); `DeleteChatMessage` — по Chat-каналу со своим набором ожидания, канал
должен быть известен. Роль и цель судит сервер. Итоги — `Sanctioned`, `Lifted`, `Kicked`,
`SanctionsListed`, `MarksListed`, `MarksCleared`, `MessageDeleted`, `ServerRejection` или
`CommandFailureCode`. Удалённое сообщение уходит у всех, включая модератора, одной дельтой
`ChatMessagesDeleted`. `ClientStatus::role`
(из `SessionOpened` и `RoleChanged`) и `mute` (из `SessionOpened` и `MuteChanged`) сбрасываются
с сессией; `sessionEnd` — как сервер закрыл сессию (`SessionEnded`) или бан при входе — хранится до
следующего входа, `sessionEndSequence` отличает повтор того же уведомления.

## Проверка в Client.Dev

```powershell
xmake build Dreamsleeve.Client.Dev
xmake run Dreamsleeve.Client.Dev
xmake run Dreamsleeve.Client.Dev --state-demo
```

Синтетическое state-demo целиком выполняется на главном потоке, без отдельного
владельца-потока и ожиданий задач. Реальный режим --connect использует ClientApplication
с приватным сетевым потоком. Post/Drain не ждут обработки команд, кроме захвата mutex.

Команды: `send <text>`, `accept`, `reject`, `receive <text>`, `sample`, `read`,
`snapshot`, `reset`, `quit`. send передаёт команду, accept имитирует серверное
подтверждение самой ранней отправки, reject — отказ. receive имитирует входящее
сообщение. sample передаёт отсутствие положения как проверку пути
отправки. read вычитывает единственную очередь и печатает дельты/снимки и отказы.

Демо показывает отсутствие локального добавления после send, подтверждение/отказ,
переполнение, вытеснение истории из кэша на 3 сообщения, явный снимок, новую сессию
и остановку. На остановке ожидающие синтетического ответа отправки получают
отказ. Это синтетический режим; реальный сервер доступен через --connect, игровой
адаптер — SKSE-плагин ([SkseClientRu.md](../../../docs/SkseClientRu.md)).

Проверки: [Tests.State.cpp](../../../tests/Dreamsleeve.Client.Tests/Tests.State.cpp),
[Tests.Changes.cpp](../../../tests/Dreamsleeve.Client.Tests/Tests.Changes.cpp)
[Tests.StateUpdate.cpp](../../../tests/Dreamsleeve.Client.Tests/Tests.StateUpdate.cpp)
[Tests.StateUpdateQueue.cpp](../../../tests/Dreamsleeve.Client.Tests/Tests.StateUpdateQueue.cpp)
и [Tests.Exchange.cpp](../../../tests/Dreamsleeve.Client.Tests/Tests.Exchange.cpp).
Запуск: [tests/README](../../../tests/README.md).

## История движения

ChangeBatch.movement сохраняет порядок успешных обновлений позиции, полных PlayerInfo,
удалений и границ персонажа. TakeStateUpdate переносит их в ClientStateDelta.movement.
Изменения actor values/Details/профиля не добавляют измерений. Лимит накопления
ClientModel(maxPendingMovementSamples) по умолчанию 4096; переполнение требует снимок.

MovementView принадлежит игровому потоку. После exchange.Drain(output) вызывается
movement.Apply(output.state), затем Sample(playerId, frameTime) каждый кадр.
Полный снимок/playersReplaced переустанавливает базу истории. Старые generation/revision
не возвращают исчезнувшие треки. [Подробности](../../../docs/MovementInterpolationRu.md).

`MessagePublished{messageId}` подтверждает собственное сообщение; UI-host связывает
pending-строки по RequestId, не по тексту; контент по-прежнему приходит
через обычные chatContent/snapshot. Статус содержит serverName из welcome;
при завершении сессии имя очищается вместе с моделью.
