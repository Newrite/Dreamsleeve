# Тесты Dreamsleeve

Тесты находятся отдельно от production-кода. Одна команда собирает и запускает
native (`tests/Dreamsleeve.Client.Tests`, doctest) и managed (`tests/Dreamsleeve.Server.Tests`,
Expecto) проекты; тесты веб-UI, smoke-сценарии с настоящими процессами и нагрузочные
измерения запускаются отдельно.

```powershell
python Scripts/run_tests.py
python Scripts/run_tests.py --suite native
python Scripts/run_tests.py --suite managed
```

Скрипт работает из любой текущей папки, если передан путь к нему. Native использует
активную конфигурацию xmake (Windows x64 releasedbg по умолчанию), managed — Release
и .NET 10. После ошибки сборки старый бинарник не запускается; при `all` второй набор
проверяется и после сбоя первого. Ошибка или отсутствие инструмента дают ненулевой exit code.
Нужны Python 3.11+, xmake, MSVC с C++23/`import std`, .NET SDK 10 и пакеты при первом
восстановлении. Loopback-проверкам нужен локальный UDP. Skyrim, PrismaUI и отдельный сервер БД не нужны: persistence-тесты создают временные SQLite-файлы.

Для smoke-скриптов и бенчмарков установите зависимости из корня репозитория:
`python -m pip install -r Scripts/requirements.txt`. Они записывают настройки в TOML;
отчёты измерений остаются JSON.

## Что проверяется

| Набор | Наблюдаемое поведение |
|---|---|
| Client Domain/State/Changes/Exchange | Владение моделью, дельты вместо копирования истории, bounded FIFO команд, снимок/восстановление UI, отсутствие локального эха |
| Client.Movement | История замеров, джиттер, rotation wrap, остановки, переходы, лимиты и восстановление снимком |
| Client.Host, Host.* | Host-модули SKSE-адаптера без Skyrim: JSON bridge (строковые uint64, безопасный текст, команды UI, таблицы и примеры событий для веб-UI), команды UI, ui.toml (правила настроек, round trip, нормализация, атомарная запись), Session (снимок только при Ready, корреляция requestId, отказы, проекция онлайна, сброс view, reconnect), цвета надписей, объявления, фильтр ввода при захвате клавиатуры |
| DreamNet/Client.Runtime | ENet ownership, лимиты host/peer, коррелированный вход и чат, таймаут/повторный вход, ошибочные и запоздалые ответы |
| Server Domain/Codec | Правила value objects/хранилищ, bootstrap, доменные ошибки, общий enum отказов, обязательная корреляция, повреждённые пакеты и конфигурация |
| Agent/Background/Outbox/AsyncDispatcher/Lifetimes/AgentTicker/Supervisor | Последовательный handler, bounded доставка, фоновые запросы, отмена, наблюдение Completion, owned children и независимый Watch, тикер без догоняющих периодов, супервизор (рестарт после сбоя и неудачного запуска, удвоение задержки, отказ сверх лимита в окне и забывание старых отказов, Stop во время задержки и во время запуска) |
| Admission | Reliable TryPost Posted/Full/Closed; обычная квота и служебный резерв в одной FIFO; возврат допуска при чтении/остановке; mapped refs |
| SQLite/Auth/HTTP | Миграции, rollback регистрации, restart профиля, пароль, one-use/expiry билета, роль в билете, переименование, лимиты и HTTP boundaries (`AuthRoutes` на общей обвязке `Server.Web`, прежние ожидания) |
| Profiles (memory fixture) | Атомарный GetOrCreate, уникальные ID, сохранение офлайн-профиля, асинхронные ответы, полный/закрытый получатель |
| ChatRoomAgent | Авторские ID/время, один ответ автору, прямые рассылки, снимок перед дельтами, история/курсор, независимые каналы, изоляция медленного подписчика |
| PresenceAgent | Снимок собственного онлайна, Joined/Left, конфликт личности, старый Detach и закрытие только переполненного подписчика |
| GroundMarks | Домен (текст, правила, TTL, видимость, положение, квота с вытеснением), агент (дельты по движению и смене пространства, удаление, вытеснение, плотность, частота, загрузка и истечение), кодек, конфигурация, SQLite-хранилище и писатель; native: хранилище, модель, обмен, кодек и сценарий ClientRuntime |
| Скрытое имя | Домен (псевдоним, словарь, книга: не из имени, номера при совпадении), сессия (подмена во всех проекциях, себе — нет, переключение, лимит, запрет), runtime (байтовая проверка пакетов другому игроку, новый псевдоним при повторном открытии), кодеки, SQLite (миграция 4), конфигурация; native: `NameFor`, проекция в UI, `ui.toml`, корреляция и отказы, кодек, ClientRuntime; UI: vitest и Playwright; `smoke_chat.py` |
| Админка | Домен (роли, ключи аудита, коды и сессии панели, `AdminPlayerView`), SQLite схемы 5 (миграция поверх 4 и DOWN, уникальность, сроки сессий, токены, роль только для профиля и её чтение при входе, поиск с `%`/`_`, пагинация, аудит, переименование), `AdminService` (одноразовые коды, лимит входа, сброс пароля закрывает сессии, `Busy`, токены), runtime (`ListSessions`, `Describe`, роль вживую и при открытии, переименование), HTTP с фейковыми портами (setup, cookie, Origin, REST, объявление и аудит, CSP и кодирование, лимит входа, `X-Forwarded-*`), конфигурация `[Admin]` |
| Смена отображаемого имени | Кодек (Trim/NFC, лимит, корреляция), сессия (словарь, текущее имя, одна смена, запрет, интервал), runtime (новое имя у других, у скрытого — побайтно нет), `AuthService`/SQLite (интервал, история); native: кодек, `ClientRuntime`, host; UI: vitest, Playwright; `smoke_chat.py` |
| Словарь, антиспам, объявления | Нормализация, уровни `[block]`/`[flag]`, корпус ложных срабатываний, лимиты частоты; объявления сервера и клиентов (подпись, каналы, политика, расписание) |
| Наказания и инструменты модератора | Домен (сроки, причина, ранг), SQLite (замена, истечение, аудит под выдавшим, миграция 9), `AuthService` (мут, бан, кик, список), удаление сообщений и меток, сессия и кодеки; native: `ClientRuntime`; UI: vitest и Playwright; `smoke_moderation.py`. Подробно — [ModerationAndNamesRu](../docs/ModerationAndNamesRu.md#проверки) |
| Гильдии | Домен (имя, роли и ранги, лимиты и их снижение, приглашения и срок, мут, удаление сообщений, передача и назначение главы), владелец (рассылка, чат, отказы, истечение, панель), SQLite-хранилище и писатель (миграция 13), кодек и линии доставки, сессия, конфигурация, админка (страницы, аудит, REST); native: книга, модель, кодек, `ClientRuntime`, host и bridge; UI: vitest и Playwright; `smoke_guilds.py`. Подробно — [GuildsRu](../docs/GuildsRu.md#проверка) |
| PlayerSession | Оба порядка bootstrap, ограниченный буфер, персональная квота RequestId, отказ/подтверждение, независимые показания и очистка подписок |
| ServerRuntime | Реальный обмен агентов через управляемый транспорт: вход, профильный резерв, адресованные пакеты, disconnect/Completion, старые ответы, дедлайны и Stop |
| EnetTransport | Настоящий yENet loopback: correlation ID/peer lifetime, reliable channel, размеры и исходящие бюджеты, отключение и очистка; `PumpHealth`: отказ отправки одному адресату проходит со сводкой в логе, неизвестная ошибка или минута сплошных сбоев ломают транспорт |

Codec преобразует проверенные доменные значения и wire-структуру. Проверки
PlayerStore/ChatCache/Domain.Chat находятся у их владельцев. C++ codec сохраняет
свою копию настроек и возвращает DreamNetPacket; loopback проверяет передачу владения.
Сохранённые проверки enum/DU не заменяются wildcard: неизвестный payload отклоняется,
добавочные поля известного payload остаются совместимыми. `ChatAccepted` содержит
обязательный RequestId; рассылка создаёт обычное входящее сообщение без корреляции.

Функциональные проверки используют gates и конечные ожидания для обнаружения
зависаний; короткие таймеры — в проверках самих deadline. Они не доказывают все
возможные чередования потоков или пропускную способность. Количество тестов выводит
runner; фиксированный счётчик в документации не используется как критерий готовности.

## Запуск отдельных наборов

```powershell
xmake build Dreamsleeve.Client.Tests
xmake run Dreamsleeve.Client.Tests --test-suite=Client.Runtime
xmake run Dreamsleeve.Client.Tests --test-suite=Client.Exchange
xmake run Dreamsleeve.Client.Tests --test-suite=Client.Host
xmake run Dreamsleeve.Client.Tests --test-suite=DreamNet.Network

dotnet run --project tests/Dreamsleeve.Server.Tests -c Release -- --filter-test-list Admission
dotnet run --project tests/Dreamsleeve.Server.Tests -c Release -- --filter-test-list ChatRoomAgent
dotnet run --project tests/Dreamsleeve.Server.Tests -c Release -- --filter-test-list PlayerSession
dotnet run --project tests/Dreamsleeve.Server.Tests -c Release -- --filter-test-list ServerRuntime
dotnet run --project tests/Dreamsleeve.Server.Tests -c Release -- --filter-test-list "ENet transport"
```

`--filter-test-list` выбирает списки по подстроке имени; имена — первый аргумент
`testList` в файлах тестов.

Все managed-проверки: `dotnet run --project tests/Dreamsleeve.Server.Tests -c Release`.
Это Expecto executable; `dotnet test` не заменяет его запуск. `--no-build` допустим
только после успешной сборки текущих исходников. `vxmakegen.py` и
`vxmakegen_modules.py` включают managed-проекты из `src` и `tests` в IDE solution.

## Настоящий сервер и клиенты (smoke)

```powershell
dotnet build src/Dreamsleeve.Server -c Release
xmake build Dreamsleeve.Client.Dev
python Scripts/smoke_chat.py
python Scripts/smoke_moderation.py
python Scripts/smoke_guilds.py
python Scripts/smoke_saved_auth.py
```

Сборку нужно выполнить заранее (сервер Release, Client.Dev в releasedbg); каждый скрипт
создаёт временную SQLite-базу и конфигурацию, пишет лог в `build/` и завершает дочерние
процессы на любом пути выхода.

- `smoke_chat.py` — F# ENet сервер и два C++ Client.Dev: авторитетная публикация и доставка,
  телеметрия автора/наблюдателя и поздний вход, видимость по радиусу и пространству, сброс
  персонажа, словарь и антиспам, объявления, метки на земле, скрытое имя, смена отображаемого
  имени, отключение/повторный вход и перезапуск сервера с той же базой (PlayerId, метки).
  Пароль передаётся через `DREAMSLEEVE_PASSWORD`, после перезапуска login выдаёт свежий билет.
  Параметры: `--help`.
- `smoke_moderation.py` — инструменты модератора: отказ без роли, роль из БД, удаление
  сообщения, мут и снятие, список, метки игрока, кик, бан при входе, строки аудита.
- `smoke_guilds.py` — гильдии с тремя Client.Dev: создание и правила имени, приглашение и
  вступление, настоящее имя в чате гильдии при скрытом в общем, гильдейский мут, офицер удаляет
  сообщение и исключает, глава не выходит и передаёт роль, строки в SQLite, роспуск и свободное имя.
- `smoke_saved_auth.py` — сохранённый вход через native Core, Credential Manager, HTTP,
  SQLite и ENet: перезапуск, отзыв живой сессии администратором, сброс пароля.

Ручной запуск:

```powershell
dotnet run --project src/Dreamsleeve.Server -c Release
xmake run Dreamsleeve.Client.Dev --connect 127.0.0.1 8778 alice --register "Alice"
xmake run Dreamsleeve.Client.Dev --connect 127.0.0.1 8778 bob --register "Bob"
```

Пароль Client.Dev спрашивает в консоли или берёт из `DREAMSLEEVE_PASSWORD`; список команд
(`send`, `read`, `note`, `hide`, `name`, `mod …` и другие) печатает при старте.
`--state-demo` остаётся локальной демонстрацией синтетических серверных событий.
Параметры сервера и TOML: [Core README](../src/Dreamsleeve.Server.Core/README.ru.md).

## Измерения

```powershell
./Scripts/benchmark_server.ps1 -Requests 32768 -Repetitions 3
```

Отдельный проект `Dreamsleeve.Server.Benchmarks` сравнивает старый `f4eef57` и текущий
путь в одной среде. Скрипт извлекает baseline в `build/benchmark-baseline`; старые
production-файлы не возвращаются в текущую сборку. Это отдельная нагрузочная работа,
не часть `run_tests.py`. Исходные измерения сохраняются в `build/benchmarks`;
производительность следует оценивать по сценарию и окружению, а не по числу агентов.

Отдельный прогон через настоящий ENet: `python Scripts/benchmark_enet.py`.
Требует Release-сборки сервера и `Dreamsleeve.Server.NetworkBenchmarks`.
[Методика, конфигурация и ограничения](Dreamsleeve.Server.NetworkBenchmarks/README.md).

## Нечисловые координаты

Клиентские пространственные функции принимают конечные координаты как предусловие
(проверки IsFinite удалены в `36ea57e`), поэтому ожиданий отказа на NaN/Infinity у них
нет; проверяются граница и отрицательный радиус, разные пространства и точность расчёта.
Серверные проверки нечисловых значений находятся в DomainTests.

## Как добавлять тесты

- Клиент: соответствующий `Tests.*.cpp`; xmake подбирает файлы автоматически.
  Новый файл должен иметь именованный doctest `TEST_SUITE`.
- Сервер: набор владельца поведения; библиотеку агентов проверять отдельно от Core.
  Новый F#-файл подключать в fsproj до Program.fs и в общий testList.
- Проверять публичный результат и владение ресурсами, не внутреннее устройство записи.
  При изменении контракта сохранять значимое поведение и описывать заменённое ожидание.
- Для async-гонок использовать управляемую доставку и gates. Все процессы/агенты должны
  завершаться даже после сбоя assertion; нагрузочные измерения не смешивать с unit-тестами.

MSVC/protobuf workaround описан в [MsvcProtobufModulesRu.md](../docs/MsvcProtobufModulesRu.md).

Нагрузочные сравнения запускаются отдельно: [методика и скрипт](Dreamsleeve.Server.Benchmarks/README.md), [измеренные результаты](../docs/benchmarks/session-routing-2026-09-27.md).

Телеметрия игрока проверяется на границах Domain/Codec, PlayerSession/PresenceAgent и
ClientRuntime: полная замена actor values, scalar zero, ресурсы, details, bounded pending,
отсутствие локального эха, компактное движение без потери метаданных,
сброс поколения и поздняя подписка между обновлением и возвратом к прежнему состоянию.

Область видимости проверяется на границе радиуса, при смене WRLD/CELL, отсутствии
позиции наблюдателя, одновременном движении и позднем входе между двумя изменениями.
Сетевой smoke проверяет очистку при удалении и восстановление неподвижного источника
при возвращении наблюдателя, а также отсутствие координат в скрытом PlayerInfo.

Движение ([контракт](../docs/SpatialReplicationRu.md)): reliable SetLocation/SetActorValues/SetDetails
и отдельные repeated MovementSample. Managed/native тесты проверяют view/context revisions, старые samples,
повтор остановившегося игрока, clear/reentry и metadata без отката позиции.
Транспортные тесты проверяют три канала, MTU, перегрузку и выделенного владельца.

Для воспроизводимой трассы движения без сервера: `xmake run Dreamsleeve.Client.Dev --movement-demo`.
Реальный ENet smoke также проверяет source timestamp и потребителя MovementView через watch.
[Настройки и сценарии](../docs/MovementInterpolationRu.md).

## Нагрузка движения

Сетевые сценарии dense/spaces/sparse/boundaries и отдельный C++ benchmark потребителя:
[методика](Dreamsleeve.Server.NetworkBenchmarks/README.md#movement-benchmark).
В отличие от чата, --rate задаёт частоту замеров **каждого** клиента, а успех требует
сходимости последнего состояния/AOI, а не доставки каждого промежуточного пакета.
Регистрация и полная рассылка начальных персонажей исключены из интервала нагрузки.


`Dreamsleeve.TraceReport` — офлайн-разборщик EventPipe `.nettrace` на TraceEvent.
Он выводит JSON с паузами GC, потерянными событиями и выборочными аллокациями;
сервер и генератор на этот проект не ссылаются. Команды профилирования приведены
в [NetworkBenchmarks](Dreamsleeve.Server.NetworkBenchmarks/README.md).

Многопроцессное сравнение движения запускает `Scripts/benchmark_enet_workers.py`:
один сервер, одинаковые суммарные клиенты/сокеты, синхронное измерение и проверка
межпроцессной доставки. [Методика](Dreamsleeve.Server.NetworkBenchmarks/README.md#multiple-load-processes);
замеры 1000 клиентов при 20 Гц: [до v6](../docs/benchmarks/movement-workers-2026-09-27.md),
[v6 и отдельный владелец ENet](../docs/benchmarks/movement-v6-owner-2026-09-27.md).

## Проверки владельца ENet

Регрессии production `TransportOwner` входят в `Dreamsleeve.Server.Tests`:
единственный владелец, непрерывное обслуживание, bounded очереди, перегрузка,
ошибки, уведомления и остановка. Отдельный экспериментальный проект удалён.

Разовые стенды сравнения библиотек, патчи DLL/исходников и allocation probes
удалены после завершения расследования. Сохранённые результаты — исторические
измерения; актуальные нагрузочные инструменты — Server.Benchmarks,
Server.NetworkBenchmarks, TraceReport и скрипты benchmark_server/enet/enet_workers.

Клиентский общий запуск проверяется в Tests.ClientApplication.cpp: путь к файлу,
частичные overrides/defaults, строгая схема/лимиты, ошибка auth и повторный вход,
остановка без соединения и при принятом входе. ENet smoke запускает Alice через --config с путём,
содержащим пробел, и Bob через прежний --connect; оба используют ClientApplication. Отдельный задержанный
HTTP-ответ проверяет, что Disconnect во время регистрации не запускает затем login/ENet.

Обмен главного и сетевого потоков дополнительно проверяется в Tests.Exchange.cpp:
управление входом/отключением/остановкой при заполненных очередях, подавление ошибки
отменённого HTTP-входа, единый статус в Status/Drain и согласованная выдача состояния
при одновременных Publish/Drain. StateUpdateQueue сама не синхронизирует доступ;
её отдельные тесты сериализуют обращения снаружи.

Сохранённый вход: `Tests.CredentialStore.cpp` использует уникальную запись Windows
и удаляет её при завершении; AuthServiceTests проверяет restart, TTL, лимит устройств,
отзыв билетов и одноразовый reset. SqliteAccountStoreTests проверяет миграцию базы
версии 1 и отказ от более новой схемы. Полный путь — `smoke_saved_auth.py` (выше).

## Клиентский web UI

`src/Dreamsleeve.Client.UI` имеет отдельные TypeScript/Vitest/Playwright проверки.
Из его каталога:

```powershell
npm ci
npx vitest run                  # то же, что npm test
npm run build                   # tsc, vite build и проверка игровой сборки
$env:UI_BROWSER_CHANNEL='msedge'; npx playwright test
```

`npm run test:browser` сам выполняет сборку и Playwright. Для Playwright нужен Chromium
(`npx playwright install chromium`) либо установленный Edge (`UI_BROWSER_CHANNEL=msedge`);
конфигурация поднимает dev-сервер (5178) и preview игровой сборки (5179). Vitest
(`tests/*.test.ts`) проверяет чат, имена, объявления, модератора, разбор bridge и контракт
команд/событий (`tests/contract/*.json`: `events.json` пишет native-тест host,
`commands.json` — vitest, каждая сторона проверяет файл другой); Playwright
(`tests/browser`) — публикации и отказы, fade, scroll/unread, геометрию и настройки,
режим стримера, игнор, фильтр слов, метки, скрытое имя, смену имени и инструменты модератора.
Game build автоматически проверяется на отсутствие dev fixtures и localStorage.
Это не тест Skyrim/PrismaUI; [граница интеграции](../src/Dreamsleeve.Client.UI/README.ru.md).
