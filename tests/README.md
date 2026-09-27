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
Нужны Python 3.10+, xmake, MSVC с C++23/`import std`, .NET SDK 10 и пакеты при первом
восстановлении. Loopback-проверкам нужен локальный UDP. Skyrim, PrismaUI и БД не нужны.

## Что проверяется

| Набор | Наблюдаемое поведение |
|---|---|
| Client Domain/State/Changes/Exchange | Владение моделью, дельты вместо копирования истории, bounded FIFO команд, снимок/восстановление UI, отсутствие локального эха |
| DreamNet/Client.Runtime | ENet ownership, лимиты host/peer, коррелированный вход и чат, таймаут/повторный вход, ошибочные и запоздалые ответы |
| Server Domain/Codec | Правила value objects/хранилищ, bootstrap, доменные ошибки, общий enum отказов, обязательная корреляция, повреждённые пакеты и конфигурация |
| Agent/Background/Outbox/Lifetimes | Последовательный handler, bounded доставка, отмена, наблюдение Completion, owned children и независимый Watch |
| Admission | Reliable TryPost Posted/Full/Closed; обычная квота и служебный резерв в одной FIFO; возврат допуска при чтении/остановке; mapped refs |
| Profiles | Атомарный GetOrCreate, уникальные ID, сохранение офлайн-профиля, асинхронные ответы, полный/закрытый получатель |
| ChatRoomAgent | Авторские ID/время, один ответ автору, прямые рассылки, снимок перед дельтами, история/курсор, независимые каналы, изоляция медленного подписчика |
| PresenceAgent | Снимок собственного онлайна, Joined/Left, конфликт личности, старый Detach и закрытие только переполненного подписчика |
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
доставку второй стороне, отключение/повторный вход и завершает дочерние процессы.
Сборку нужно выполнить заранее. Параметры: `python Scripts/smoke_chat.py --help`.

Ручной запуск:

```powershell
dotnet run --project src/Dreamsleeve.Server -c Release
xmake run Dreamsleeve.Client.Dev --connect 127.0.0.1 8778 alice "Alice"
xmake run Dreamsleeve.Client.Dev --connect 127.0.0.1 8778 bob "Bob"
```

В Client.Dev: `send <text>`, `read`, `disconnect`, `connect`, `quit`.
`--state-demo` остаётся локальной демонстрацией синтетических серверных событий.
Параметры сервера и JSON: [Core README](../src/Dreamsleeve.Server.Core/README.ru.md).

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
