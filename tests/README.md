# Тесты Dreamsleeve

Тесты находятся отдельно от production-кода. Одна команда собирает и запускает
native и managed проекты; нагрузочные измерения и проверка двух настоящих клиентов
запускаются отдельно.

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
| Client.Host | Host-модули SKSE-адаптера без Skyrim: JSON bridge (строковые uint64, безопасный текст, команды UI), ui.toml (round trip, нормализация, атомарная запись), Session (снимок только при Ready, корреляция requestId, отказы, проекция онлайна, сброс view, reconnect) |
| DreamNet/Client.Runtime | ENet ownership, лимиты host/peer, коррелированный вход и чат, таймаут/повторный вход, ошибочные и запоздалые ответы |
| Server Domain/Codec | Правила value objects/хранилищ, bootstrap, доменные ошибки, общий enum отказов, обязательная корреляция, повреждённые пакеты и конфигурация |
| Agent/Background/Outbox/Lifetimes | Последовательный handler, bounded доставка, отмена, наблюдение Completion, owned children и независимый Watch |
| Admission | Reliable TryPost Posted/Full/Closed; обычная квота и служебный резерв в одной FIFO; возврат допуска при чтении/остановке; mapped refs |
| SQLite/Auth/HTTP | Миграции, rollback регистрации, restart профиля, пароль, one-use/expiry билета, роль в билете, переименование, лимиты и HTTP boundaries (`AuthRoutes` на общей обвязке `Server.Web`, прежние ожидания) |
| Profiles (memory fixture) | Атомарный GetOrCreate, уникальные ID, сохранение офлайн-профиля, асинхронные ответы, полный/закрытый получатель |
| ChatRoomAgent | Авторские ID/время, один ответ автору, прямые рассылки, снимок перед дельтами, история/курсор, независимые каналы, изоляция медленного подписчика |
| PresenceAgent | Снимок собственного онлайна, Joined/Left, конфликт личности, старый Detach и закрытие только переполненного подписчика |
| GroundMarks | Домен (текст, правила, TTL, видимость, положение, квота с вытеснением), агент (дельты по движению и смене пространства, удаление, вытеснение, плотность, частота, загрузка и истечение), кодек, конфигурация, SQLite-хранилище и писатель; native: хранилище, модель, обмен, кодек и сценарий ClientRuntime |
| Скрытое имя | Домен (псевдоним, словарь, книга: не из имени, номера при совпадении), сессия (подмена во всех проекциях, себе — нет, переключение, лимит, запрет), runtime (байтовая проверка пакетов другому игроку, новый псевдоним при повторном открытии), кодеки, SQLite (миграция 4), конфигурация; native: `NameFor`, проекция в UI, `ui.toml`, корреляция и отказы, кодек, ClientRuntime; UI: vitest и Playwright; `smoke_chat.py` |
| Админка | Домен (роли, ключи аудита, коды и сессии панели, `AdminPlayerView`), SQLite схемы 5 (миграция поверх 4 и DOWN, уникальность, сроки сессий, токены, роль только для профиля и её чтение при входе, поиск с `%`/`_`, пагинация, аудит, переименование), `AdminService` (одноразовые коды, лимит входа, сброс пароля закрывает сессии, `Busy`, токены), runtime (`ListSessions`, `Describe`, роль вживую и при открытии, переименование), HTTP с фейковыми портами (setup, cookie, Origin, REST, объявление и аудит, CSP и кодирование, лимит входа, `X-Forwarded-*`), конфигурация `[Admin]` |
| Смена отображаемого имени | Кодек (Trim/NFC, лимит, корреляция), сессия (словарь, текущее имя, одна смена, запрет, интервал), runtime (новое имя у других, у скрытого — побайтно нет), `AuthService`/SQLite (интервал, история); native: кодек, `ClientRuntime`, host; UI: vitest, Playwright; `smoke_chat.py` |
| PlayerSession | Оба порядка bootstrap, ограниченный буфер, персональная квота RequestId, отказ/подтверждение, независимые показания и очистка подписок |
| ServerRuntime | Реальный обмен агентов через управляемый транспорт: вход, профильный резерв, адресованные пакеты, disconnect/Completion, старые ответы, дедлайны и Stop |
| EnetTransport | Настоящий yENet loopback: correlation ID/peer lifetime, reliable channel, размеры и исходящие бюджеты, отключение и очистка |

Codec преобразует проверенные доменные значения и wire-структуру. Проверки
PlayerStore/ChatCache/Domain.Chat находятся у их владельцев. C++ codec сохраняет
свою копию настроек и возвращает DreamNetPacket; loopback проверяет передачу владения.
Сохранённые проверки enum/DU не заменяются wildcard: неизвестный payload отклоняется,
добавочные поля известного payload остаются совместимыми. `ChatAccepted` содержит
обязательный RequestId; рассылка создаёт обычное входящее сообщение без корреляции.

Старые SessionRegistry/ChatFlow/PlayerAgent/ChatAgent сценарии перенесены к владельцам
нового пути. Их исходники доступны в `f4eef57`, но второй серверный путь ради тестов
не собирается. Проверки сравнения ответа с заранее созданным реестром ChatMessage
убраны: теперь сообщение создаёт сам канал. Сохранены история/курсор/отдельные снимки,
отказ без изменения истории, профильные ошибки и жизненный цикл показаний игрока.

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
dotnet run --project tests/Dreamsleeve.Server.Tests -c Release -- --filter-test-list EnetTransport
```

Все managed-проверки: `dotnet run --project tests/Dreamsleeve.Server.Tests -c Release`.
Это Expecto executable; `dotnet test` не заменяет его запуск. `--no-build` допустим
только после успешной сборки текущих исходников. `vxmakegen.py` и
`vxmakegen_modules.py` включают managed-проекты из `src` и `tests` в IDE solution.

## Два настоящих клиента и сервер

```powershell
dotnet build src/Dreamsleeve.Server -c Release
xmake build Dreamsleeve.Client.Dev
python Scripts/smoke_chat.py
```

Скрипт запускает F# ENet сервер и два C++ Client.Dev, проверяет авторитетную публикацию,
доставку второй стороне, телеметрию автора/наблюдателя и поздний вход,
сброс персонажа, отключение/повторный вход и завершает дочерние процессы.
Сборку нужно выполнить заранее. Параметры: `python Scripts/smoke_chat.py --help`.

Ручной запуск:

```powershell
dotnet run --project src/Dreamsleeve.Server -c Release
xmake run Dreamsleeve.Client.Dev --connect 127.0.0.1 8778 alice --register "Alice"
xmake run Dreamsleeve.Client.Dev --connect 127.0.0.1 8778 bob --register "Bob"
```

В Client.Dev: `send <text>`, `read`, `disconnect`, `connect`, `quit`.
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

## Происхождение и адаптация архивов

Архивы прочитаны из `F:\downloads`; тесты теперь самостоятельны и для запуска
этот каталог не нужен. Production-файлы из архивов не копировались поверх проекта.

| Источник | Что использовано |
|---|---|
| `Dreamsleeve.Client.Simplified.zip` | Tests.Domain.cpp, Tests.State.cpp — более поздний контракт, чем State.Mvp |
| `Dreamsleeve.Client.ChatDeltas.zip` | Tests.Changes.cpp — заменяет старый вариант ChangeBatch |
| `Dreamsleeve.Server.Review.zip` | Domain-тесты уже совпадали с репозиторием; перенесены без изменения проверок |
| `Dreamsleeve.Agent.NET10.review.zip` | Agent-тесты уже совпадали с репозиторием; заменён только runner на Expecto |
| Существующий `src/Dreamsleeve.Client.Tests` | Все транспортные тесты перенесены в native-проект |

Единственное изменение ожидаемого поведения архивных domain-тестов: убраны
ожидания отказа клиентских пространственных функций на NaN/Infinity. Это следует
из принятого удаления клиентских IsFinite-проверок в `36ea57e`, а не из попытки
скрыть сбой: конечность входа теперь предусловие. Проверки границы радиуса,
отрицательного радиуса, разных пространств и точности расчёта сохранены.
Серверные проверки нечисловых значений остались в DomainTests.

## Как добавлять тесты

- Клиент: соответствующий `Tests.*.cpp`; xmake подбирает файлы автоматически.
  Новый файл должен иметь именованный doctest `TEST_SUITE`.
- Сервер: набор владельца поведения; библиотеку агентов проверять отдельно от Core.
  Новый F#-файл подключать в fsproj до Program.fs и в общий testList.
- Проверять публичный результат и владение ресурсами, не внутреннее устройство записи.
  При изменении контракта сохранять значимое поведение и описывать заменённое ожидание.
- Для async-гонок использовать управляемую доставку и gates. Все процессы/агенты должны
  завершаться даже после сбоя assertion; нагрузочные измерения не смешивать с unit-тестами.

MSVC/protobuf workaround и проверенные отрицательные компиляции enum/visitor описаны
в [MsvcProtobufModulesRu.md](../docs/MsvcProtobufModulesRu.md).

Нагрузочные сравнения запускаются отдельно: [методика и скрипт](Dreamsleeve.Server.Benchmarks/README.md), [измеренные результаты](../docs/benchmarks/session-routing-2026-09-27.md).

Auth smoke перезапускает настоящий сервер с той же временной SQLite-базой и проверяет
сохранение PlayerId. Пароль передаётся через окружение, login выдаёт свежий билет.

Телеметрия v3 проверяется на границах Domain/Codec, PlayerSession/PresenceAgent и
ClientRuntime: полная замена Sample, scalar zero, ресурсы, rich details, bounded pending,
отсутствие локального эха, частота Sample, компактное движение без потери метаданных,
сброс поколения и поздняя подписка между обновлением и возвратом к прежнему состоянию.

Область видимости проверяется на границе радиуса, при смене WRLD/CELL, отсутствии
позиции наблюдателя, одновременном движении и позднем входе между двумя изменениями.
Сетевой smoke проверяет очистку при удалении и восстановление неподвижного источника
при возвращении наблюдателя, а также отсутствие координат в скрытом PlayerInfo.

Protocol v6: reliable SetLocation/SetActorValues/SetDetails и отдельные repeated
MovementSample. Managed/native тесты проверяют view/context revisions, старые samples,
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
межпроцессной доставки. [Методика](Dreamsleeve.Server.NetworkBenchmarks/README.md#multiple-load-processes),
[результаты 1000 клиентов при 20 Гц](../docs/benchmarks/movement-workers-2026-09-27.md).

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
отзыв билетов и одноразовый reset. SqliteAccountStoreTests проверяет миграцию v1 → v2.
`python Scripts/smoke_saved_auth.py` проходит полный путь через native Core, Credential
Manager, HTTP, SQLite и ENet, включая отзыв живой сессии администратором.

## Клиентский web UI

`src/Dreamsleeve.Client.UI` имеет отдельные TypeScript/Vitest/Playwright проверки.
Из его каталога: `npm ci`, `npm test`, `npm run build`, `npm run test:browser`.
Для Playwright нужен Chromium (`npx playwright install chromium`) либо установленный
Edge (`$env:UI_BROWSER_CHANNEL='msedge'`). Проверяются публикации/отказы, readonly
объявления, fade, scroll/unread, геометрия, сохранение dev-настроек и native JS bridge.
Game build автоматически проверяется на отсутствие dev fixtures и localStorage.
Это не тест Skyrim/PrismaUI; [граница интеграции](../src/Dreamsleeve.Client.UI/README.ru.md).
