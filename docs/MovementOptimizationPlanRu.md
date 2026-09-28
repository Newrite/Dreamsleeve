# План первой волны оптимизации движения

Анализ кода `7b53fb5`, 27 сентября 2026. **План принят к пошаговой реализации; состояние шагов — в журнале ниже.** Дополнительно выполнены отдельные Release-пробы аллокаций.
Цель — убрать лишнюю работу в текущих владельцах состояния, сохранив модель
агентов, проверку контрактов и ограниченные очереди. Шардирование и настройка GC
в эту волну не входят.

## Вывод

Начать с расписания публикаций, повторно создаваемых коллекций/снимков и механизма
доставки агентов. `ValueTask` полезен точечно; массовый перевод DU/records в struct
не нужен. Для движения хороший кандидат — **маленький struct-элемент в массиве**,
сохраняющий ссылку на общую immutable `PlayerLocation`. Саму `PlayerLocation`
пока оставить ссылочной.

Большие reliable-пачки и накопление устаревшего движения — отдельная транспортная
проблема. Устранение аллокаций не гарантирует приемлемого dense/1000. Не обещаем,
что все ограничения исчезнут после смены представления данных.

## Что подтверждено

Исходные данные: [диагностика ENet/GC](benchmarks/movement-diagnostics-2026-09-27.md),
[матрица v5](benchmarks/movement-v5-2026-09-27.md),
[новые allocation probes](benchmarks/allocation-probes-2026-09-27.json).

| Наблюдение | Следствие |
|---|---|
| Sparse/1000: 168 flush за 20 с, средний интервал 119 мс при настройке 100 мс; средний flush 7,83 мс | Есть дрейф расписания: новое ожидание ставится только на следующем Update после flush |
| Sparse: 3,57 GB выделено за ~20 с, GC pauses ~207 мс | Аллокации существенны, но секундные задержки не объясняются одними stop-the-world паузами |
| Dense: sampled pending payload до ~218 MiB с default buffers; с 8 MiB достигнут лимит 256 MiB | После передачи ENet позиции удерживаются до ACK/reset; увеличение памяти не исправляет семантику очереди |
| Пачка 25 игроков до 1381 байта, порог фрагментации установленного ENet — 1364 при MTU 1392 без checksum | Прикладной лимит пакета и желаемый размер пачки движения должны различаться |
| В allocation trace заметны tuple/list, HashSet entries, protobuf-графы и byte[] | Есть конкретные места в построении fanout, а не только общая «дороговизна .NET» |

Trace AllocationTick — выборочная оценка, не точный учёт каждого типа. SampleProfiler
stacks включают ожидания потоков, поэтому их проценты нельзя читать как CPU-профиль.
Один генератор использует один host/socket: его ACK-window и service loop также
ограничивают фактический вход. Не приписывать весь провал source Hz серверу.

### Дополнительные пробы

Разовый стенд удалён после завершения оптимизации; исходник доступен в Git на `2da9896`
(`tests/Dreamsleeve.AllocationProbes`). Результаты ниже сохранены как исторические измерения.
.NET 10.0.12, Release, x64, workstation GC; три повтора в одном процессе, прогрев, синхронные операции
на одном потоке. Это точные **байты, выделенные этим потоком в измеряемом участке**,
а не замер throughput сервера. Входные immutable объекты созданы заранее.
Рабочие буферы варианта reuse также предварительно прогреты; их удерживаемая память
не равна нулю. GC.Collect используется только между пробами в диагностическом EXE.

| Операция | Текущий/прямой вариант, B/op | Альтернатива, B/op |
|---|---:|---:|
| Построение и сортировка пачки 25: tuple + list | 3320 | struct + list: **4480** |
| Та же пачка 25 | 3320 | struct array + owned scratch: **848** |
| Пачка 1000 | 116808 | struct + list: 157512; struct array + scratch: **32048** |
| HashSet из 25 Guid | 2064 | Clear/reuse: **0** после прогрева |
| HashSet из 1000 Guid | 102200 | Clear/reuse: **0** после прогрева |
| Player.snapshot, 0 actor values | 248 | новая запись с уже готовой картой: **80** |
| Player.snapshot, 32 / 64 actor values | 10648 / 25712 | новая запись с уже готовой картой: **80 / 80** |
| Синхронный task { return () } | **0** | Перевод handler в ValueTask ради этой операции не нужен |
| task { return AgentPostResult.Posted } | 72 | кешированный Task либо прямой ValueTask: **0** |
| ValueTask, оборачивающий предыдущий task | **72** | Обёртка не устраняет исходную Task-аллокацию |
| Новый TaskCompletionSource<unit> + завершение без ожидающих | **96** | Не создавать notification, когда нет ожидающих writers |

Изолированная замена tuple на struct при сохранении Seq/list оказалась хуже по
аллокациям. Результат зависит от всей цепочки буферов, сортировки и коллекций.
Показанные альтернативы — диагностические модели, ещё не реализация в production.
Байты и checksums совпали также в повторном отдельном процессе. Сервер использует
Server GC; эти пробы не измеряют его паузы. Наносекунды сохранены в JSON для исследования, но не используются как обещание
ускорения: нет отдельных процессов BenchmarkDotNet, статистики шума и async-нагрузки.

## Конкретные пробелы в коде

1. [PresenceAgent.schedule/handle](../src/Dreamsleeve.Server.Core/PresenceAgent.fs):
   после Flush таймер не перевзводится до следующего Update. Получается период
   «flush + ожидание входа + interval», а не независимый ритм репликации.
2. [PresenceAgent.publishMovement](../src/Dreamsleeve.Server.Core/PresenceAgent.fs):
   для каждого наблюдателя новые HashSet, ResizeArray, reference tuples,
   сортировочные буферы и list. Одна и та же immutable позиция оборачивается заново
   для каждого получателя. Latest/Published spatial indexes уже существуют;
   повторное «добавление индекса» эту проблему не решит.
3. [Player.snapshot](../src/Dreamsleeve.Server.Domain/Players.fs) →
   [ActorValueStorage.snapshot](../src/Dreamsleeve.Server.Domain/ActorValues.fs):
   каждый Move пересобирает неизменившиеся actor values через Dictionary → tuples → Map.
   На пустых наборах в текущей нагрузке масштаб этого пробела почти не виден.
4. [Agent.releaseAdmission](../src/Dreamsleeve.Agent/Agent.fs): в BoundedWithControl
   каждый dequeue создаёт новую notification/TCS, даже когда ни один writer не ждёт.
   В PostAsync дополнительно создаются tuple/option для попытки admission и
   вложенный task. Это граница библиотеки; прикладным агентам не нужны новые локи.
5. [AgentDeliveryWindow](../src/Dreamsleeve.Agent/AgentOutbox.fs): готовое сообщение
   всегда проходит через SemaphoreSlim, finished TCS, Task.FromResult и
   StartDelivery → Task.Run. Свободный mailbox не получает синхронного TryPost пути.
   [Agent.startBackground](../src/Dreamsleeve.Agent/Agent.fs) также сканирует список
   фоновых операций при каждом запуске. Частота и стоимость сканирования пока не измерены.
6. [ServerRuntime.tick/schedule](../src/Dreamsleeve.Server.Core/ServerRuntime.fs):
   частый Task.Delay + Watch создаёт наблюдателя, linked CTS и фоновые задачи;
   каждый tick копирует все Connections.Values в новый массив и проверяет все routes.
   Watch для реальных lifecycle dependencies полезен; для периодического тика
   это чрезмерно общий механизм.
7. [ServerRuntime.send](../src/Dreamsleeve.Server.Core/ServerRuntime.fs): любой
   немувментный ответ, в том числе PlayerUpdateAccepted, сначала flush-ит движение.
   Это сохраняет текущий FIFO, но может дробить накопление. **Величина эффекта пока
   не измерена**, поэтому удалять барьеры без классификации нельзя.
8. [ProtocolCodec.encodeMovementPackets](../src/Dreamsleeve.Server.Core/Protocol/ProtocolCodec.fs)
   делит по MaxPacketBytes, а не по транспортному бюджету без фрагментации.
   [PlayerCodec.location/moved](../src/Dreamsleeve.Server.Core/Protocol/PlayerCodec.fs)
   заново строит одинаковые protobuf-графы для каждого наблюдателя.
   [OutgoingPackets](../src/Dreamsleeve.Server.Infrastructure.Interop/OutgoingPackets.cs)
   затем передаёт reliable packet на канал 0. Сохранённые ENet payloads нельзя
   заменить новым sample через нынешний runtime dictionary.

## Порядок реализации и проверки

Каждый пункт — отдельное изменение с review и контрольным замером. Не объединять
в один коммит изменение lifetime библиотеки, wire-семантики и представления домена.
Пункты 1–6 — первая волна. Пункт 7 выполнять по повторному профилю.

### 1. Исправить cadence без накопления тиков

- В Presence задавать следующий срок относительно предыдущего **планового** срока,
  а не времени окончания работы или прихода очередного Update. Использовать
  монотонные часы; при опоздании пропускать пропущенные периоды, не догонять их
  очередью из нескольких Flush. Первый dirty после настоящего idle может начать
  новый период; сброс LastFlush на idle должен исключать ложную метрику cadence.
  Раздельно измерять опоздание пробуждения timer и ожидание обработки в mailbox;
  обычный .NET timer не гарантирует real-time точность.
- Вынести небольшой owned timer helper в Agent library вместо создания нового
  Watch на каждый период. Один worker на активный timer, не более одного ожидающего
  сообщения тика. Tick продолжает обрабатываться владельцем состояния; worker
  не получает доступа к dictionaries. Lifetime должен прерываться и при Complete
  (DispatchStopped), и при Abort. Остановившийся адрес не должен удерживать worker.
- Повторно использовать helper для runtime Poll; интервалы Poll и репликации
  оставить разными. Синхронизация клиентского и серверного «кадра» не требуется:
  сервер публикует последнее доступное состояние на собственном расписании.

Проверки: медленный flush дольше периода, отсутствие Update, первый Update после
idle, Complete/Abort, отложенный тик после остановки. Регрессионные проверки через
управляемый clock/gates, а не чувствительные sleeps. Sparse/1000 с периодом 100 мс:
устойчивый интервал около 100 мс, без постоянного накопления опоздания. Частота
полученных изменений не обязана точно равняться числу тиков: публикуются изменения,
часть промежуточных sample закономерно объединяется.

### 2. Кешировать immutable snapshot actor values

В ActorValueStorage хранить последний Map и признак инвалидирования. Snapshot
строит карту только после set/setMany/remove/clear; следующие Move/Rename/Details
переиспользуют её. При clear можно сразу использовать Map.empty. Не делать глобальный
кеш и не передавать mutable Dictionary подписчикам. Если replacement уже поступил
как immutable Map, рассмотреть его сохранение как готовой проекции без обратной
пересборки, сохраняя текущие методы изменения storage.

Проверки: все мутации инвалидируют кеш; ранее выданный snapshot не меняется;
разные игроки не делят mutable storage; BeginCharacter/Leave очищают данные.
Пробы с 0/32/64 значениями и с действительными изменениями actor values. Цель:
стоимость Move со стабильными actor values не растёт пропорционально их количеству.

### 3. Убрать временные коллекции fanout и ввести MovementChange

- Добавить небольшой `[<Struct>] MovementChange` с PlayerId и
  `PlayerLocation voption`; передавать **массив** этих изменений через PresenceEvent,
  PlayerSession, ServerResponse и codec. Это внутренний контракт; protobuf не меняется.
- Presence владеет переиспользуемыми candidates HashSet и рабочим ResizeArray.
  Для отправки строится отдельный массив; после передачи его нельзя очищать или
  изменять. Сортировать массив по PlayerId на месте до публикации. Не сохранять
  промежуточные tuples/list в runtime flush и codec.
- Учитывать синхронный путь deliverDelta → удаление slow consumer → broadcast Left.
  Рабочие буферы movement не используются этим путём рекурсивно; после удаления
  проверять принадлежность оставшихся участников. Не брать live enumerator Members
  через операции, способные удалить member.
- Оставить snapshots members/changed на время flush, где они обеспечивают стабильную
  итерацию. Убрать сначала коллекции **на каждого получателя**. Их экономия важнее
  одного массива участников на flush; дальнейшее reuse проверять отдельно.

Проверки: AOI enter/leave, смена WRLD/CELL, движение наблюдателя, self, два обновления
до flush, slow consumer с каскадным Left, join между изменениями. Сохранить порядок
и отсутствие stale movement после Left. Сравнить allocation/flush при 25 и 1000
кандидатах; размер retained scratch ограничен конфигурацией состава сервера.

### 4. Сократить расходы доставки в Agent library

Выполнить отдельными небольшими изменениями:

- Ленивый admission notification: создать TCS только после неудачного TryWrite,
  когда writer действительно собирается ждать. Неудачная попытка и получение общего
  notification — под существующим admissionGate. При освобождении места/close
  снять и завершить notification; не оставить lost wakeup или спящего writer.
- В AgentOutbox для готового сообщения использовать destination.TryPost, когда
  нет более ранней незавершённой доставки. При Full сохранить bounded tracked
  fallback. При Closed и fault сохранить прежние onFailure/lifetime последствия.
  Успех означает admission, а не обработку получателем, как и раньше.
- Сохранить FIFO относительно ожидающего предыдущего send, лимит локальных
  reservations, Complete/drain, Abort/cancellation и AbortAfterDrain. Нельзя
  обойти предыдущую медленную доставку новым прямым TryPost. Ошибки mapper и
  synchronous exceptions также должны наблюдаться владельцем.
- Не менять смысл StartDelivery и createAsyncHandler глобальным удалением Task.Run:
  у async store-операции синхронная часть намеренно вынесена с владельца. Прямой
  путь нужен для готовых сообщений, а не произвольного пользовательского I/O.
- Убрать `.AsTask()` у Channel.WaitToReadAsync/WaitToWriteAsync там, где результат
  только однократно await-ится. Избежать промежуточного tuple/option в успешном
  admission пути. Оптимизацию background.RemoveAll делать только если после
  устранения лишних запусков сканирование всё ещё заметно.

Проверки: свободный и заполненный mailbox, гонка dequeue с регистрацией ожидания,
несколько writers, отмена одного из ожидающих, control reserve, закрытие адреса,
FIFO через переход direct → queued → direct, ошибки доставки, Complete и Abort.
Измерить настоящий путь агентов с 0%, редкими и частыми ожиданиями, включая
process-wide allocated bytes: thread-local счётчик не охватывает async continuation.

### 5. Уменьшить работу runtime на idle routes

Сначала переиспользовать owner-local массив/буфер для обхода Connections вместо
Seq.toArray на каждом tick; retain capacity допустим в пределах MaxSessions.
Сохранять безопасную итерацию при удалении маршрутов. После повторного замера,
если полный обход остаётся заметным, вести набор routes с pending movement и
отдельный набор Waiting/Opening/Closing для deadline-проверок. Это индексы одного
владельца, не новый агент или параллельный словарь. Не вводить timer wheel заранее.

Отдельно измерить число flush по причинам: runtime tick, lifecycle boundary,
metadata и ACK/chat reply. В первой версии сохранить FIFO барьеры. Если ACK
существенно дробит output, отдельно разрешить обход только для settlement-ответов,
не участвующих в применении состояния. Смена персонажа, пространства, очистка и
Left остаются барьерами. Проверить клиентский runtime и тесты порядка прежде,
чем менять эту политику. Не смешивать этот эксперимент с reuse коллекций.

### 6. Разделить допустимый пакет и транспортный бюджет движения

Добавить отдельный целевой размер movement packet. Эффективный предел:
`min(app MaxPacketBytes, configured movement target, peer nonfragmenting payload)`.
Оценку последнего даёт ENet adapter по согласованному MTU и включённым overheads;
Core не должен копировать ENet magic constants. Для стартового сравнения использовать
1200 байт, но это экспериментальное значение, не универсальная гарантия любого MTU.

Разбивать по границам protobuf entries, учитывать весь ServerPacket envelope.
Если одно допустимое entry больше цели из-за длинного имени пространства, явно
оставить разрешённую fallback-фрагментацию в пределах app limit и посчитать её;
не превращать tuning в новый неожиданный отказ валидному клиенту. Application limit
по-прежнему проверяется строго. Дополнительные пакеты увеличивают расходы leases,
ACK и очередей — разбиение нужно **измерить**, а не объявлять бесплатным улучшением.

Проверки: exact boundary, длинные plugin/location labels, меньший MTU, одиночный
oversize entry, сохранение порядка и атомарности ошибки кодирования. Сравнить
fragmented packets, bytes, packet count, pending payload, RTT и final-state latency.
Wire-version и Reliable policy на этом шаге сохраняются.

### 7. По повторному профилю: повторное использование wire-представления

Если protobuf-графы остаются крупной статьёй после предыдущих шагов, сделать
ограниченный эксперимент с кешем закодированного текущего движения в runtime/encoder.
Один источник часто имеет одинаковый sample для многих получателей; AOI меняет
набор получателей и очистку, но не сами координаты этого sample.

Первый вариант — повторно использовать готовый protobuf PlayerMoved/Location graph
для одного неизменившегося sample при сборке разных recipient packets. Единственный
владелец — runtime; построенный граф больше не мутируется, codec не выпускает его
наружу. Кеш ограничить актуальными источниками и очищать при глобальном уходе или
смене поколения. AOI clear отдельного наблюдателя не удаляет sample для остальных.
Старое пришедшее событие нельзя подменить новым: ключ обязан учитывать **точный
sample**, а не только PlayerId. Историю samples бесконечно не хранить.

Это уменьшит построение объектов, но не число исходящих координат, не удержание
payload ENet и не всю стоимость сериализации. Более сложный кеш byte fragments,
свой protobuf writer и cross-agent pooled buffers пока не вводить. Принять решение
по allocated bytes/publication, CPU/flush и удерживаемой памяти кеша.

## Какие типы делать значимыми

Размеры ниже измерены через Unsafe.SizeOf на текущем x64 runtime; размер ссылки
не равен размеру объекта в куче. Struct DU не является C union: нельзя рассчитывать,
что все payloads займут память только самого большого case.

| Тип / группа | Решение и причина |
|---|---|
| Position, Rotation | Уже struct по 12 байт; менять нечего |
| ActorValueState | Уже struct DU, **16 байт**; не заменять его на самодельный enum/payload |
| PlayerId, RequestId, числовые UMX measures | Уже числовые values; UMX не создаёт объект-обёртку для каждого ID |
| Guid, ValueOption, Result, KeyValuePair | Уже value types; не требуется повторная «оптимизация» |
| `(PlayerId * PlayerLocation voption)` | Кандидат первой волны: named MovementChange struct **24 байта** + array; сохранить shared Location reference |
| Временные attempt/notification tuples в Agent | Устранить в прямом пути; для оставшихся локальных пар возможен struct tuple без смены внешнего API |
| SpatialIndex.Cell | Уже struct; не добавлять boxing через object/interface-based коллекции |
| FormKey | Отложенный кандидат: компактный string-reference + uint32, но меняет layout Cell/keys и стоимость hash/equality. В trace protobuf FormKey — **другой тип**; его аллокации это не устранит |
| ActorValueInfo | Пока оставить ссылочным: values разделяются между immutable Maps; изменение размера map nodes и копирования нужно мерить на частых изменениях readings |
| Location | Оставить shared immutable record; чаще полезнее не пересоздавать неизменный space metadata, чем встраивать его во все sample |
| PlayerLocation | Пока reference: 8-байтная ссылка, voption — 16 байт. Struct-аналог payload — **40 байт**, MovementChange с inline optional payload — **56 вместо 24**; fanout размножает копирование |
| PlayerData, PlayerDetails, PlayerSnapshot | Оставить shared immutable references; большие графы и много получателей. Убрать пересборку частей снимка, а не копировать его целиком по очередям |
| Player, ActorValueStorage, Member, SessionTable.Entry | Владение mutable состоянием/identity; struct-копии затруднят корректность, не первая цель |
| AgentPostResult, AgentDeliveryResult, nullary phases | Cases без payload не создают новый объект на каждый результат. Главная цена здесь Task/notification, а не сам case |
| PlayerUpdate, PresenceCommand/Event, SessionHostCommand, PlayerSessionMessage, ServerResponse | Не переводить широкие DU массово. Пробный смешанный struct DU Move/Reply/Stop занимает **56 байт**; требуется замер каждой реальной формы и вложенных envelopes |
| ClientRequest, ServerTransportEvent | Возможные поздние локальные кандидаты, если останутся в профиле; встречаются на входе один раз, тогда как movement entries размножаются fanout |
| MailboxEnvelope, ReplyChannel, OutgoingPackets.Lease | Оставить reference identity: settlement/drop/callback/lifetime используют общий объект. Struct/boxing не уберёт необходимость общего состояния |
| Generated protobuf types | Это generated classes; не править вручную и не подменять их доменными structs |

Сохранять DU и исчерпывающие match. Экономия памяти не требует замены корректных
вариантов на enum с произвольными nullable payloads. Проверять boxing в Seq/object
и интерфейсах, размер элементов массивов/каналов, а не только количество new.

## Task, ValueTask и computation expressions

Рекомендация первой волны: сохранить Task-based публичную модель агентов, убрать
лишние доставки/notifications и ненужный AsTask. Синхронные Task<unit> handlers
в пробе не выделяют Task; в профиле Task<unit> также представлены реальными
асинхронными workers/TCS, это не противоречие.

Для частого PostAsync с небольшим конечным набором результатов сначала сравнить
прямой возврат кешированного Task на успешном admission с ValueTask<Admission>.
Оба дают 0 B для самого результата в пробе. Кеш Task сохраняет multi-await контракт,
поэтому это предпочтительный первый вариант без массового изменения сигнатур.
Нужен именно отдельный синхронный путь: await кешированного Task внутри нового
`task { return result }` не гарантирует устранения внешней Task-аллокации.

Если после этого остаётся измеримая цена async API, ValueTask допускается на
узкой операции с одним потребителем результата. Completion/Termination, shared
lifecycle watches и задачи, хранящиеся для join, оставить Task. Это соответствует
[рекомендациям .NET о ValueTask](https://devblogs.microsoft.com/dotnet/understanding-the-whys-whats-and-whens-of-valuetask/).

Стандартный F# `task` уже умеет `let!/do!` над ValueTask. `task { ... } |> ValueTask`
лишь оборачивает Task, что подтверждено пробой и следует из
[документированного построения](https://learn.microsoft.com/en-us/dotnet/fsharp/language-reference/task-expressions).
Для простой ветки `TryPost → immediate result`, с Task fallback только при ожидании,
отдельный CE вообще не нужен.

Если сложный hot path действительно потребует ValueTask CE, сначала сравнить
[IcedTasks valueTask](https://github.com/TheAngryByrd/IcedTasks) с текущей реализацией
и закрепить проверенную версию пакета. В эту волну библиотека не добавлена. Свой
CE или копия части builder ради нескольких методов создадут лишние обязательства
по cancellation, exception и continuation semantics. PoolingValueTaskSource требует
ещё более строгого single-consumer lifetime и сейчас не оправдан измерениями.

## Нужны ли пулы

Сначала **owner-local reuse**: HashSet/ResizeArray, runtime iteration buffer,
кеш immutable projection. Это не общий пул, locks в прикладных агентах не нужны.
Capacity останется у владельца, поэтому измерять steady-state retained memory
и поведение после большого transient состава тоже необходимо.

ArrayPool<byte> возможен позже только внутри понятной синхронной границы encoder →
ENet copy. Текущий Send принимает byte[] без отдельной длины, поэтому просто взять
массив из пула нельзя: его capacity может быть больше payload. Нужен length/span
контракт и доказательство, что транспорт уже скопировал данные до Return. Входной
payload живёт после освобождения native packet и пересекает владельцев — там нужен
явный lease, цена которого пока не обоснована. Пул protobuf objects также не нужен:
они mutable и могут ещё использоваться queued output.

## Контрольные замеры и критерии остановки

- Сначала каждая локальная проба и относящиеся к изменению тесты; перед сетевой
  матрицей весь managed suite. При изменении wire/delivery — native tests и smoke.
- Зафиксировать commit, runtime, config, target/source Hz и фактическое число
  принятых input/publications. Сравнивать минимум три одинаковых запуска по
  30–60 секунд после прогрева; отдельный trace запуск, а не смешивать overhead.
- Основные сценарии: sparse/1000 в группах 25 при 10 Гц и 30 Гц; spaces;
  boundaries; dense/500, затем ограниченный по времени dense/1000. Дополнительно
  0/32/64 actor values, idle, connect/disconnect churn и медленный получатель.
- Смотреть cadence/lateness, source throttling, pending requests, p50/p95/p99 age,
  flush/Poll time, packet size/count, pending ENet bytes, process allocation rate,
  retained memory и final-state convergence. Аллокации нормировать также на
  принятый input и доставленное movement entry, чтобы падение throughput не
  выглядело «экономией памяти».
- Для scratch/struct/cache принять изменение, только если снижается allocation
  целевого участка и не ухудшаются latency/CPU/memory при той же фактической работе.
  Exact B/op локальной пробы не является целевым значением всего сервера.
- Для Agent обязательно доказать прежние FIFO/lifetime/backpressure свойства;
  хорошие цифры свободного mailbox не компенсируют зависание заполненного.
- Не увеличивать очереди ради PASS. Не интерпретировать final-state PASS после
  долгого drain как соблюдение частоты во время нагрузки.

После первой волны заново определить ограничитель. Если dense упирается в reliable
backlog, следующий **отдельный** план — заменяемое движение до send, возможный
unreliable-sequenced канал, server generation/sequence и восстановление после потери
последнего sample. Нельзя просто сменить PacketFlag: lifecycle/clear и движение
между каналами могут переупорядочиться, а неподвижный игрок после потери последнего
пакета без refresh никогда не сойдётся. Чат/авторизация/переходы остаются надёжными.
Шардирование, ручной GC, server-wide pools и переписывание ENet не входят в текущий план.

## Журнал реализации

- База: `070b01a` — план и allocation probes.
- Шаг 1: owned AgentTicker на PeriodicTimer, один outstanding tick, отдельные
  метрики опоздания timer/mailbox. Presence больше не перевзводит интервал после
  следующего Update; runtime не создаёт Watch на каждый poll. При idle таймер
  остаётся активным, но не публикует неизменённое состояние; idle сбрасывает
  измерение межпубликационного интервала. Это небольшое уточнение реализации плана.
  Саморевью: worker не обращается к состоянию приложения; Ack только после handler;
  Complete/Abort прекращают ожидание. Три детерминированных ticker tests и весь
  managed suite (268) прошли. Сетевой контроль и итоговая матрица — после следующих шагов.

### Замечание о value-аналоге TCS

`ManualResetValueTaskSourceCore<T>` — стандартное struct-ядро для реализации
[IValueTaskSource](https://learn.microsoft.com/en-us/dotnet/api/system.threading.tasks.sources.manualresetvaluetasksourcecore-1).
Это не drop-in многопотребительский TCS. Его повторное использование требует
соблюдения version/token и lifetime каждого await. Общий admission notification
ожидают несколько writers, Completion также наблюдают несколько владельцев:
здесь остаётся Task/TCS. Однопотребительское ожидание PeriodicTimer возвращает
ValueTask; ticker уже ожидает его напрямую, без AsTask и собственного source pool.

- Шаг 2: ActorValueStorage кеширует immutable Map, инвалидирует её на всех мутациях
  и сохраняет готовую карту при replacement. Mutable Dictionary остаётся у владельца.
  Саморевью: published snapshots не меняются при set/setMany/remove/clear;
  BeginCharacter/Leave создают отдельный storage. Domain suite: 44/44.
  Повторные пробы: Player.snapshot с 0/32/64 actor values теперь выделяет 80 B/op
  во всех трёх случаях (раньше 248 / 10648 / 25712).

- Шаг 3: MovementChange — struct с shared immutable Location; PresenceEvent и
  ServerResponse передают detached arrays. Presence переиспользует candidates и
  рабочий ResizeArray; runtime/codec больше не создают list/tuple для движения.
  Саморевью: recipients не разделяют изменяемый scratch; сортировка завершается
  до отправки, каскадный Left не использует movement buffers. Wire не изменён.
  Release build без предупреждений; весь managed suite: 269/269, включая AOI,
  randomized visibility, slow consumers, split boundaries и lifecycle barriers.

- Шаг 4a: admission TCS создаётся только при ожидании; освобождение места завершает
  текущую общую notification и не создаёт следующую заранее. Убраны AsTask на
  Channel waits; синхронные PostAsync и reliable projection возвращают кешированные
  результатные Task. Саморевью: failed admission + capture notification атомарны
  под прежним gate; cancellation отдельного writer не отменяет общий сигнал.
  Добавлен сценарий с 64 ожидающими writers и отменой одного; managed suite 270/270.

- Шаг 4b: готовые сообщения обходятся без worker/TCS, когда admission завершается
  синхронно и нет предыдущей ожидающей отправки. Уточнение плана: вместо связки
  TryPost + PostAsync используется один PostAsync с проверкой IsCompletedSuccessfully;
  это сохраняет ровно один вызов mapper при заполнении очереди. Slow path остаётся
  tracked/bounded; I/O execute по-прежнему вынесен с handler. Саморевью дополнительно
  сохранило запрет dropping owner и проверку cancellation после получения слота,
  до изменения состояния. Проверки direct → pending → direct, mapper once,
  FIFO/Complete/Abort и ошибки: managed suite 272/272.

**Уточнение пользователя после шага 4:** финальные бенчмарки отложены, пока машина
занята другим тяжёлым приложением. До отдельного запуска выполняются сборки и
функциональные тесты. Шаг 7 (wire cache по повторному профилю), performance-решения
о route indexes/ACK barriers и количественная оценка ускорения также отложены.

- Шаг 5: runtime переиспользует массив обхода routes, очищает ссылки в finally и
  допускает удаления из Dictionary во время обхода. Проверки deadline выделены
  в функцию одной route. Саморевью: visitRoutes не вызывается рекурсивно из visitor;
  все вызовы принадлежат одному handler. 15/15 ServerRuntime tests прошли.
  Добавлены movement.flush.{tick,boundary,settlement,chat,metadata,lifecycle}.entries:
  count показывает число непустых flush, sum — число entries. ACK/lifecycle FIFO
  не изменён. Индексы dirty/deadline routes отложены до отдельных измерений.

- Шаг 6: MovementPacketTargetBytes (default 1200) ограничивает batch вместе с
  MaxPacketBytes и текущим unfragmented payload от adapter. Порог adapter соответствует
  enet_peer_send установленного xENet; Core не содержит ENet overhead constants.
  Неделимое entry выше цели остаётся отдельным пакетом, application cap строгий.
  Добавлены метрики encoded target-exceeded и accepted transport fragmentation;
  они не обозначают число реально отправленных UDP-фрагментов/ретраев.
  Саморевью: protobuf envelope входит в размер, нет partial send при ошибке,
  записи сохраняют порядок, unknown/reset/disposed peers не читают native pointers.
  Wire v5, reliable/channel и FIFO-барьеры остаются прежними.
  Итоговая проверка: Release build без предупреждений, 277/277 managed tests,
  19/19 функциональных ENet smoke-сценариев с двумя native Client.Dev, включая
  движение, AOI, metadata, чат и восстановление SQLite после рестарта. Оба benchmark
  проекта собраны для проверки совместимости, но не запускались. Шаги 1–6 завершены;
  шаг 7 и повторный performance-профиль остаются отложенными.


### Повторные бенчмарки после разрешения пользователя

На `00b3fe0` выполнена матрица 12 случаев и два контрольных прогона.
[Отчёт](benchmarks/movement-opt-wave1-2026-09-27.md) фиксирует улучшение cadence
и аллокаций в sparse/1000, но регрессии dense/500 и части тяжёлых сценариев.
Шаг 6 нельзя считать подтверждённой performance-оптимизацией: необходим A/B
с `b80915d` и выяснение причины раннего закрытия сессий. Увеличение packet count
(+62% в контрольном sparse/1000) и времени poll/tick измерено. Шаг 7 (wire cache)
не реализован; повторные сетевые метрики ещё не заменяют allocation stack profile.


### Исправление после расследования регрессии

[Контролируемые проверки](benchmarks/movement-regression-2026-09-27.md) отделили
стоимость MTU-разбиения от перегрузки общего сокета генератора. Шаг 6 скорректирован:
MovementPacketTargetBytes=0 по умолчанию, положительные значения — явный opt-in.
Нагрузчик получил несколько host/socket под одним владельцем, без шардирования
сервера. Добавлены логи транспортного отключения/неожиданного завершения сессии
и метрики retry timeout. 278 managed tests, 19 native smoke прошли; четыре финальных
нагрузочных случая сошлись, но плотная рассылка остаётся ограничением производительности.
Режимы и конфиги исходных диагностических экспериментов зафиксированы в отчёте.
