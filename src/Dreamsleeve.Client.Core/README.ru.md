# Клиентский сетевой владелец

`Dreamsleeve.Client.Runtime` соединяет DreamNetClient, codec, ClientModel и
ClientExchange. После создания им владеет один сетевой поток; сам класс не создаёт потоков. DreamNetRuntime и ClientExchange должны пережить ClientRuntime.
Игровой/UI-поток получает изменения через Exchange и статус через ClientApplication,
без доступа к модели и transport.

## Общий запуск и конфигурационный файл

`import Dreamsleeve.Client.Application;` экспортирует `ClientSettings`,
`LoadClientSettings(path)`, `ClientApplication` и существующий Exchange/Runtime.
[client.example.json](client.example.json) показывает все настройки файла версии 1.
Путь типа `std::filesystem::path` выбирает конечный клиент или SKSE-плагин:
библиотека не ищет файл в cwd/Data/AppData и не создаёт его автоматически.
Относительный путь имеет обычную семантику файловой системы вызывающей программы.

Отсутствие/ошибка чтения файла, неизвестные поля, неверные типы и недопустимые
значения возвращают `std::unexpected`. Размер файла ограничен 64 KiB.
Частичный объект (включая `{}`) дополняется defaults: 20 Гц, три канала,
один peer, прежние лимиты и настройки интерполяции. `serverIp` — IPv4,
`serverPort` — порт ENet; `authUrl` — HTTP(S) origin. Прямое создание
`ClientSettings` проходит ту же проверку при запуске. Hot reload отсутствует.
Пароля и одноразового билета в схеме файла нет.

```cpp
// finalConfigPath передан конечным приложением; ошибки обрабатывает оно же.
auto settings = Dreamsleeve::Client::LoadClientSettings(finalConfigPath);
if (!settings) return HandleError(settings.error());
auto app = Dreamsleeve::Client::ClientApplication::TryCreate(std::move(*settings));
if (!app) return HandleError(app.error());
auto accepted = (*app)->Connect({username, password});
// Для регистрации с последующим входом: Connect({username, password}, displayName).
```

TryCreate проверяет настройки, владеет ENet runtime, Exchange и сетевым потоком.
Connect принимает операцию без HTTP на вызывающем потоке. Одновременно допускается
одна операция входа; новая отклоняется, пока идёт auth, disconnect или активна
сессия. Каждый повторный Connect требует учётных данных и получает свежий билет;
общий объект не сохраняет пароль для автоматических повторов.

`Status()` возвращает текущие phase/authenticating/stopped и последнюю ошибку
запуска сессии/HTTP/transport; следующий принятый Connect очищает эту ошибку.
`Exchange().Drain(output)` выдаёт прежние снимки, изменения и отказы команд,
`Exchange().Post(...)` принимает игровые/UI-команды. Потребитель по-прежнему один:
игровой поток вычитывает изменения для себя и UI. MovementView также принадлежит
потребителю, не сетевому потоку.

`Disconnect()` отменяет ожидающий вход и закрывает текущую сессию; после завершения
можно вызвать Connect снова. `Stop()` — окончательная идемпотентная остановка с join,
также вызывается деструктором. Новый запуск после Stop требует нового объекта.
Синхронный WinHTTP выполняется только на worker до открытия ENet-сессии;
Disconnect/Stop не прерывают WinHTTP внутри системного вызова, ожидая его timeout.
Отменённый результат не используется для последующего открытия сессии.

Опциональный StatusHandler предназначен для диагностики и вызывается на worker
без удержания mutex. Он не должен блокировать, бросать исключения или вызывать
Stop/уничтожать объект. Для UI нужно читать Status на игровом потоке.
Connect/Disconnect/Stop вызывает один владелец приложения; Status/Exchange
предназначены для обмена с сетевым потоком. В Core нет вывода в консоль,
чтения пароля из окружения или выбора файлов логирования.

Client.Dev использует тот же объект. Чтение пароля осталось в консольном адаптере:

```powershell
xmake run Dreamsleeve.Client.Dev --config "path/to/client.json" player
xmake run Dreamsleeve.Client.Dev --config "path/to/client.json" player --register "Player Name"
```

Прежний `--connect <IPv4> <port> <username>` сохранён. Его `--config <path>`
загружает остальные настройки; позиционные IPv4/port и явный `--auth-url` затем
переопределяют файл независимо от порядка опций. Без файла используются defaults.

## Вход и завершение

`ClientRuntime::TryCreate(config, exchange)` проверяет настройки codec, таймауты
и ёмкость чата. `Connect(sessionTicket)` создаёт новый транспортный host
из той же конфигурации и начинает подключение. При каждом новом входе вызывающий
код передаёт новый одноразовый билет, полученный из HTTP login; ClientRuntime не знает пароль
и не выполняет HTTP; общий ClientApplication получает билет через AuthHttp. `Poll(waitMs)` обслуживает ENet;
его нужно вызывать регулярно. Общий Exchange.NextRequestId() выдаёт ненулевые ID для OpenSession, SendChat и UpdatePlayer.
Счётчик не сбрасывается при переподключении; исчерпание возвращает пустой результат.

Фазы: Disconnected → Connecting → Opening → Ready. ENet Connected запускает
отправку OpenSession непосредственно из DreamNetPacket. Ready означает, что
коррелированный SessionOpened успешно применён обычными операциями модели.
Публикация начального снимка и фазы Ready выполняется под одним lock Exchange.
Промежуточные онлайн/self/история не публикуются. При некорректном ответе модель
очищается, фаза становится Faulted, Poll возвращает исходную ошибку transport,
codec или модели. Новый Connect создаёт исправный host и новый RequestId.

RequestRejected ожидаемого входа сохраняется в Exchange с RequestId и серверным
кодом, затем соединение локально завершается через Abort с best-effort уведомлением
peer. Терминальный ответ уже получен; встречный graceful handshake не запускается,
поскольку сервер также закрывает отказанный вход. Отказ ожидаемого SendChat завершает только
этот запрос и оставляет Ready и принятую историю без изменений.
Disconnect начинает штатное закрытие с ограниченным временем ожидания; во время
Connecting он отменяет попытку. Поздние данные при Disconnecting игнорируются.
Закрытие сервера очищает модель. Протокольный сбой и уничтожение runtime используют
немедленное завершение с best-effort уведомлением peer через Abort(reason).

`ClientOutput.phase` содержит актуальную фазу; snapshots/deltas и rejections
забираются прежним Drain. Ошибки из Result обрабатывает владелец. После остановки
цикла владелец вызывает Exchange.Finish, вызывающая сторона делает join.
Необработанные ошибки выделения памяти не преобразуются в ошибки протокола.

## Конфигурация и границы этого шага

Configuration задаёт адрес сервера (default 127.0.0.1:8778), один peer, таймауты
connect/disconnect/session (5000/2000/5000 ms), ёмкость чата (512) и существующие
лимиты codec/ENet. Одна конфигурация используется для host и codec, в течение
сессии не меняется. LoadClientSettings читает JSON по переданному вызывающей стороной пути.

Poll обслуживает SendChat и RequestSnapshot из Exchange. Для отправки producer
берёт ID через NextRequestId(), затем Post({generation, SendChat{ID, channel, text}}).
Generation берётся из последнего снимка/дельты; один producer публикует команды
в порядке выделенных ID. Пропущенные и отклонённые ID не используются повторно.

До ответа сервера модель не меняется. Автор получает wire ChatPublished с RequestId;
внутренний ChatAccepted сверяет ожидаемые ID/канал/автора и применяет тот же
ChatMessagesReceived, что и публикации остальных игроков. Неизвестная корреляция
считается протокольным сбоем. Disconnect очищает pending, новый ID не совпадёт со старым.
Лимит ожидаемых SendChat задаёт config.maxPendingChatRequests (default 32).
Автоматических повторов и отдельного таймера чата нет: запрос завершается ответом
либо сбросом сессии.

Локальные отказы находятся отдельно в ClientOutput.commandFailures:
StaleGeneration, SessionNotReady, Busy, InvalidRequest, EncodingFailed.
Они сохраняют исходные generation/requestId и не выдаются за ServerRejection.
Общий бюджет commandCapacity охватывает накопленные локальные и серверные отказы,
а также места для возможных отказов ожидаемых SendChat, UpdatePlayer и OpenSession. TakeCommands
берёт команды с учётом этих резервов; Connect требует свободное место до начала входа.
Если UI не вызывает Drain, команды остаются в ограниченной очереди, а Poll продолжает
обслуживать сеть. Успешный ChatAccepted освобождает резерв: сообщение восстанавливается
из снимка, а отказы сохраняются отдельно до Drain. Переполнение Publish при нарушении
этого контракта возвращает явную ошибку, сохраняя отказы и публикуя терминальное состояние.
RequestSnapshot работает и после смены generation.

## Состояние персонажа и репликация

`CharacterStarted`, `CharacterRenamed`, `LocalLocation`, `LocalActorValues`,
`PlayerDetailsChanged` и `GameExited` — reliable-команды с RequestId.
`LocalMovement` обновляет последнюю позу сетевого владельца без запроса/ACK.
Первое положение или новое пространство автоматически создаёт reliable SetLocation;
`LocalLocation` явно задаёт границу, в том числе телепорт внутри того же пространства.
Отсутствие location очищает позицию. Пока переход не подтверждён, samples не идут;
отказ очищает локальный поток, следующая локальная позиция может создать новый переход.
LocalActorValues отдельно заменяет всю карту, включая очистку пустой картой;
лимит config.maxActorValues по умолчанию 64. Scalar 0 — допустимое значение.

PlayerDetails содержит расу/FormKey, уровень, вид занятия и его контекст,
описание места/ближайшего маркера и время начала игры в Unix ms. Это отдельная полная
замена metadata; частые samples её не стирают. Enum ActivityKind/LockDifficulty
генерируются из общего proto. BeginCharacter и LeaveGame на сервере очищают
игровые поля/details и увеличивают characterGeneration, даже для того же имени.
Rename сохраняет generation и остальное состояние. Клиент только принимает
серверную generation в PlayerInfo.

UpdatePlayer не меняет модель. PlayerUpdateAccepted завершает команду. Репликация
приходит также автору: metadata не откатывает позицию, а reliable PlayerVisibilityChanged
устанавливает/очищает её контекст. Bootstrap/PlayerJoined включают исходную позу,
viewRevision и movementSequence. Realtime PlayersMoved содержит только pose с токеном
видимости и sequence; неизвестные контексты и старые номера молча отбрасываются.

Чужая позиция видима только в соответствующем WRLD/CELL и серверном радиусе AOI.
Периодический повтор всех текущих видимых позиций восстанавливает потерю пакета;
отсутствие записи в пачке ничего не удаляет. Очистка и повторный вход используют
reliable-границу с новым токеном, который сбрасывает историю интерполяции.
Профиль, имя, actor values и details остаются глобальными.

Клиентская Configuration содержит `visibilityDistance` (8192 Skyrim units) и
`showFireflies` (true) для будущего игрового адаптера. Расстояние должно быть конечным
и неотрицательным; 0 соответствует точному совпадению координат. Эти настройки пока
не меняют сетевой трафик: выключение визуальных огоньков не отключает отправку samples
и получение серверной репликации. Сервер самостоятельно задаёт и применяет свой AOI.

`maxPendingPlayerUpdates` (32) ограничивает ожидания ACK; они также занимают общий
бюджет результатов Exchange. Отказ завершает один запрос и оставляет Ready.
Disconnect очищает pending, IDs не используются повторно. ACK другого/старого ID
считается ошибкой протокола. Для движения `playerSampleIntervalMs` (50 ms, 20 Гц) задаёт период
повторения последней позы, включая остановившегося игрока. Повтор получает новый
sequence и время измерения. Clear/Leave/Begin прекращают старый поток.
Exchange объединяет лишь соседние samples того же пространства и поколения;
reliable-переходы не объединяются. Чат не ждёт окна отправки movement.

Каналы: Control=0 reliable, Chat=1 reliable, Realtime=2 unreliable sequenced.
Пакет движения не превышает согласованный MTU; запроса и pending-записи у него нет.
ChatPublished, обогнавший SessionOpened, сохраняется в bounded bootstrap-буфере.

Тесты Client.Runtime используют настоящий ENet host и protobuf-пакеты на серверной
стороне: отсутствие локального эха, корреляция, перегрузка, отказы без отключения,
повторный вход и защита от устаревших команд/ответов.

## Client.Dev

```powershell
xmake run Dreamsleeve.Client.Dev --connect 127.0.0.1 8778 player --register "Player Name"
# Последующие запуски: без --register
xmake run Dreamsleeve.Client.Dev --connect 127.0.0.1 8778 player --auth-url http://127.0.0.1:8779
```

Пароль вводится скрыто в консоли либо берётся из DREAMSLEEVE_PASSWORD для автотестов.
Допустимы 12–128 байт UTF-8 без trim/нормализации. Пароль и билет не попадают в argv
или вывод. --register выполняется один раз, затем каждый connect получает свежий
билет через POST /auth/login. Auth URL по умолчанию http://127.0.0.1:8779;
HTTP допускается только на loopback, удалённый endpoint требует HTTPS с обычной
проверкой сертификата. HTTP redirects не выполняются. Ответы ограничены 16 KiB,
отдельные сетевые операции имеют таймауты 5 секунд; чтение тела дополнительно
прерывается по общему сроку запроса, чтобы медленная передача не удерживала поток.

После login начинает вход; команды: `send <text>` (также `chat <text>`), `read`,
`disconnect`, `connect`, `quit`. `read` печатает фазу, онлайн, принятые сообщения
и отказы. Перед send консоль вычитывает актуальные generation/канал, затем Post. Сеть обслуживается отдельным потоком
даже пока консоль ждёт ввода. EOF/quit закрывает соединение и завершает поток.
Игровые команды: `begin <name>`, `rename <name>`, `leave`, `move <json>`, `values <json>`,
`details <json>`, `clear-location`. Move, values и details — полные замены своих частей;
clear-location очищает только положение; actor values и details не затрагиваются.
Принятый PlayerInfo печатается через read строкой `player <json>`.
Позиция измеряется игровыми world units, вращение XYZ — радианами.

```text
begin Nerevar
move {"location": {"location": {"locationId": {"pluginName": "Skyrim.esm", "localFormId": 291}, "locationName": "Whiterun"}, "position": {"X": 1, "Y": 2, "Z": 3}, "rotation": {"X": 0, "Y": 0, "Z": 1.5}}}
values {"skyrim:health": {"displayName": "Health", "state": {"current": 150, "maximum": 100}}, "skyrim:speed": {"displayName": "Speed", "state": {"value": 0}}}
details {"race":{"form":{"pluginName":"Skyrim.esm","localFormId":79686},"name":"Nord"},"level":25,"activity":{"kind":2,"targetName":"Dragon"},"place":{"worldspaceName":"Tamriel","locationName":"Whiterun","nearbyMarkerName":"Dragonsreach","markerKind":"castle","isInterior":false},"gameStartedAtUnixMs":1700000000000}
clear-location
rename Nerevar Renamed
leave
```

В JSON enum задаются номерами из player.proto: например activity.kind=2 — Combat,
16 — Menu (menuKey="main" для главного меню), 18 — Loading. Внутри API это enum.
Для подключения нужен Protocol/protocol.proto на IPv4, три ENet-канала,
протокол версии 6 без checksum/compression. Старый `--state-demo` и консоль без аргументов
остаются явно синтетическими проверками очередей и чата.

## Проверка с реальным сервером

```powershell
dotnet build src/Dreamsleeve.Server -c Release
xmake build Dreamsleeve.Client.Dev
python Scripts/smoke_chat.py
```

Smoke запускает два отдельных Client.Dev и F#-сервер на свободном локальном
UDP-порту, проверяет оба направления чата, единственную публикацию автору,
присутствие, reconnect со старым PlayerId/историей и штатную остановку.
Дополнительно проверяет авторскую репликацию персонажа/details, полный late-join,
движение/rotation, rename, сброс при same-name BeginCharacter и LeaveGame.
Проверки видимости включают bootstrap без позиции наблюдателя, выход из радиуса,
смену пространства, отсутствие утечки координат через полное PlayerInfo и возврат
неподвижной цели после перемещения только наблюдателя.
Аккаунты регистрируются через Client.Dev, SQLite/config создаются во временной папке.
После рестарта сервера проверяются тот же PlayerId и новый успешный login;
история чата пока сохраняется только в памяти работающего сервера.
Ожидания определяются наблюдаемыми состояниями и ограничены таймаутами;
все созданные процессы завершаются также при ошибке. Структурированные логи сервера
проверяются как JSON и не должны содержать пароль или известный тесту билет.
Лог: build/smoke-chat.log.

Публичный ProtocolCodec обрабатывает оболочку и вызывает внутренние преобразования
ChatCodec/PlayerCodec/SessionCodec. Файлы находятся в каталоге Protocol соответствующего
Core-проекта; отдельные экземпляры для частей протокола не создаются.

## Поток движения и интерполяция

Модуль Dreamsleeve.Client.MovementView вычисляет положение на игровом потоке:
Apply принимает StateUpdateBatch из ClientExchange, Sample вычисляет XYZ/rotation
на заданное время кадра. История ограничена, без экстраполяции, со сбросами при
телепорте и смене пространства/персонажа/видимости. Настройки — Configuration.movement
и maxPendingMovementSamples. [Полный контракт и ограничения](../../docs/MovementInterpolationRu.md).

Client.Dev: --movement-demo выводит детерминированную трассу с джиттером; в --connect
команды pose <id> и watch <id> <ms> показывают вычисленные координаты реального игрока.
