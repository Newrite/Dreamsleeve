# Технический handbook проекта MMO Chat / Social Server

> Статус на 26.09.2026: справочник и примеры, не перечень готовых подсистем.
> [Текущие решения](CurrentStateRu.md) определяют MVP и имеют приоритет над
> ранними примерами ниже. Тесты находятся в `tests`, общий запуск:
> `python Scripts/run_tests.py`.

## Текущая структура и стек

Сервер — F#/.NET 10+, yENet 1.2.1, домен с UMX и агенты Channels/Task.
Клиент — C++23/MSVC/xmake, native ENet 1.3.18; Core независим от Skyrim,
Client предназначен для SKSE/PrismaUI. Runtime/codec и прикладная сессия ещё
не реализованы. Server сейчас содержит заглушку запуска.

Protobuf генерируется `Scripts/generate_protocol.py` из `Protocol/*.proto`
в Native и Dotnet проекты; рабочая схема пока только DisconnectReason.
Grpc.Tools из ранних примеров не подключён к текущему csproj.

SQLite + SqlHydra/Fling/Migrondi — ранее выбранный проект хранения;
LiteDB обсуждалась, окончательная замена не принята. Infrastructure — заглушка.
Логирование/конфигурация F# и HTTP-админка ниже также описывают дальнейшие шаги.
Текущие зависимости надо сверять с fsproj/csproj и xmake.lua.

Тесты: `tests/Dreamsleeve.Client.Tests` (doctest) и
`tests/Dreamsleeve.Server.Tests` (Expecto + Faqt, наборы Agent/Domain).
См. [общий запуск](../tests/README.md) и [корневой README](../README.md).

## Статус документа
- Язык: русский
- Назначение: технический документ по выбранному стеку и базовой организации проекта
- Фокус: setup, практические примеры, common mistakes, edge cases
- Вне рамок этого документа:
  - детальная protobuf-схема и генерация сообщений
  - подробные SQL-модели предметной области
  - низкоуровневые сетевые оптимизации и performance tuning под финальный прод

---

## 1. Назначение и границы

Этот документ фиксирует техническую основу проекта игрового чат-сервера и связанной минималистичной админки.

Документ описывает:
- выбранный стек
- базовую организацию solution / репозиториев
- стартовую настройку ENet на C++ и .NET / F#
- базовую настройку логирования и конфигов на F#
- базовую настройку SQLite + SQLHydra + Migrondi
- подход к тестированию на .NET и C++
- типичные ошибки, ограничения и edge cases

Документ **не** пытается быть исчерпывающим reference manual по каждой библиотеке. Его задача — дать проекту устойчивую стартовую техническую основу и зафиксировать решения, чтобы команда не пересобирала стек заново по памяти.

---

## 2. Зафиксированный стек

### Сервер / .NET / F#
- ENet transport: `Molth/enet-csharp`, выбран слой `yENet`
- Message format: `Google.Protobuf`
- Protobuf codegen tooling: `Grpc.Tools`
- UMX units / typed wrappers: `FSharp.UMX`
- Error handling helpers: `FsToolkit.ErrorHandling`
- SQL / запросы: `SqlHydra`
- Удобная работа со связями и aggregate loading/saving: `Fling`
- Миграции: `Migrondi`
- База данных: `SQLite`
- Тесты: `Expecto` + `Faqt`
- Админка SSR: `Falco` + `Falco.Markup` + `Falco.Htmx`
- Логирование: `FSharp.Logf` + `Serilog`
- Конфигурация логирования: `Serilog.Settings.Configuration`
- Конфиги TOML через `IConfiguration`: `Tomlyn.Extensions.Configuration`

### Клиент / C++
- ENet transport: `lsalzman/enet`
- Protobuf: Google Protocol Buffers
- Конфиги: `glaze`
- Логирование: `spdlog`
- Тесты: `doctest`

---

## 3. Архитектурные принципы

### 3.1. Общий принцип
Проект должен быть разделен на два слоя:

1. **Transport / session layer**
   - ENet peer lifecycle
   - connect / disconnect / timeout
   - channel routing
   - raw packet send/receive
   - session resume hooks
   - low-level flood/rate safety hooks

2. **Application / domain layer**
   - auth
   - social / friends / blacklist
   - guild / party
   - chat
   - moderation
   - admin operations
   - persistence

Доменные сервисы не должны знать о деталях `ENet.Event` и буферах настолько, насколько это можно избежать.

### 3.2. Один источник истины для wire contract
`.proto`-файлы являются source of truth для транспортных сообщений.

### 3.3. Конфигурация и логирование — не хардкодить
- пути логов
- уровни логирования
- таймауты
- лимиты
- сетевые адреса
- строки подключения
- флаги фич

должны выноситься в конфиги.

### 3.4. SQLite — нормально для текущего масштаба
Пока масштаб проекта ограничен небольшим количеством пользователей, SQLite — допустимая и удобная база. Но код доступа к данным нужно строить так, чтобы переход на другой SQL backend не ломал всю архитектуру.

---

## 4. Исходный эскиз расширенной структуры (после MVP)

Примерный ориентир:

```text
/proto
  common.proto
  session.proto
  chat.proto
  guild.proto
  party.proto
  social.proto
  admin.proto

/server
  /src
    /Server.App
    /Server.Transport
    /Server.Protocol
    /Server.Domain
    /Server.Persistence
    /Server.Admin
    /Server.Migrations
  /tests
    /Server.Tests

/client-cpp
  /src
  /include
  /tests

/admin-web
  может быть отдельным F#-проектом или частью server solution
```

### Практическая разбивка F#-части
- `Server.App` — composition root, host, DI, wiring
- `Server.Transport` — ENet loop, packet routing, session registry
- `Server.Protocol` — адаптер между protobuf transport DTO и domain commands/events
- `Server.Domain` — предметная логика
- `Server.Persistence` — SQLHydra/Fling, repositories, migration bootstrap
- `Server.Admin` — Falco SSR admin
- `Server.Tests` — unit/integration tests

---

## 5. Базовая настройка ENet на C++

### 5.1. Базовая идея
Клиент на C++ использует оригинальный `lsalzman/enet` как эталонный transport.

### 5.2. Общий жизненный цикл
1. `enet_initialize()`
2. создать `ENetHost`
3. `enet_host_connect(...)`
4. крутить event loop через `enet_host_service(...)`
5. отправлять пакеты через `enet_peer_send(...)`
6. вызывать `enet_host_flush(...)`, если нужен немедленный flush
7. по завершении корректно уничтожить host и вызвать `enet_deinitialize()`

### 5.3. Базовый пример клиента

```cpp
#include <enet/enet.h>
#include <iostream>
#include <vector>

int main() {
    if (enet_initialize() != 0) {
        std::cerr << "Failed to initialize ENet\n";
        return 1;
    }

    ENetHost* client = enet_host_create(
        nullptr,
        1,
        2,
        0,
        0
    );

    if (!client) {
        std::cerr << "Failed to create ENet client host\n";
        enet_deinitialize();
        return 1;
    }

    ENetAddress address{};
    ENetEvent event{};
    ENetPeer* peer = nullptr;

    enet_address_set_host(&address, "127.0.0.1");
    address.port = 7777;

    peer = enet_host_connect(client, &address, 2, 0);
    if (!peer) {
        std::cerr << "No available peers for initiating connection\n";
        enet_host_destroy(client);
        enet_deinitialize();
        return 1;
    }

    bool connected = false;

    while (enet_host_service(client, &event, 100) >= 0) {
        switch (event.type) {
            case ENET_EVENT_TYPE_CONNECT:
                std::cout << "Connected to server\n";
                connected = true;
                break;

            case ENET_EVENT_TYPE_RECEIVE:
                std::cout << "Received packet of length: " << event.packet->dataLength << "\n";
                enet_packet_destroy(event.packet);
                break;

            case ENET_EVENT_TYPE_DISCONNECT:
                std::cout << "Disconnected from server\n";
                connected = false;
                goto shutdown;

            case ENET_EVENT_TYPE_NONE:
                break;
        }

        if (connected) {
            std::vector<unsigned char> payload{0x01, 0x02, 0x03};
            ENetPacket* packet = enet_packet_create(
                payload.data(),
                payload.size(),
                ENET_PACKET_FLAG_RELIABLE
            );
            enet_peer_send(peer, 0, packet);
            enet_host_flush(client);
            break;
        }
    }

shutdown:
    enet_peer_disconnect(peer, 0);
    enet_host_destroy(client);
    enet_deinitialize();
    return 0;
}
```

### 5.4. Практические правила
- Не отправлять application payload до успешного `ENET_EVENT_TYPE_CONNECT`.
- Не забывать `enet_packet_destroy(...)` для полученных пакетов.
- Не делать blocking-heavy работу прямо внутри event loop.
- Не держать event loop “иногда”; он должен крутиться регулярно.
- Не смешивать channel semantics хаотично. Каналы должны быть заранее задокументированы.

### 5.5. Рекомендуемое использование каналов
Пример стартовой схемы:
- `channel 0` — надежные control / session / auth / command packets
- `channel 1` — chat / social / stateful domain events

Если later понадобится, можно расширять, но стартовать лучше с минимального числа каналов.

### 5.6. Common mistakes на C++ стороне
- забыли инициализировать ENet глобально
- забыли уничтожить packet после receive
- сделали send до connect event
- засунули сериализацию, логирование и тяжелые операции прямо в сетевой loop
- не обрабатывают disconnect/timeout как полноценный сценарий
- используют один и тот же буфер после передачи, не понимая жизненный цикл данных
- считают, что reliable packet на одном канале равен “все в порядке везде”

### 5.7. Edge cases
- сервер умер без graceful disconnect
- клиент долго не крутил `enet_host_service` и накопил задержки
- reconnect приходит очень быстро после старого disconnect
- пришли неожиданные late packets после логического завершения сессии

Решение: session layer должен уметь валидировать актуальность session/token/sequence, а не слепо доверять transport событию.

---

## 6. Базовая настройка ENet на F# / .NET

### 6.1. Общая идея
На .NET используется `Molth/enet-csharp` как совместимый с оригинальным ENet порт. Сетевой слой должен жить в отдельном loop / агенте, а доменная логика — обрабатываться через очереди сообщений или dispatcher.

### 6.2. Рекомендуемый threading model
Под F# удобно использовать:
- отдельный transport loop
- mailbox processor / channel для входящих transport events
- mailbox processor / channel для исходящих domain-to-transport sends

Главная идея:
- один владелец сетевого host/state
- никакой хаотичной многопоточности вокруг ENet host

### 6.3. Базовый псевдо-скелет

```fsharp
open System
open ENet

module Transport =
    let startServer () =
        Library.Initialize()

        let address = Address()
        address.Port <- 7777us

        let host =
            new Host()

        host.Create(address, 1024, 2)

        let event = new Event()
        let mutable running = true

        while running do
            while host.CheckEvents(&event) > 0 || host.Service(15, &event) > 0 do
                match event.Type with
                | EventType.Connect ->
                    printfn "Peer connected"
                | EventType.Receive ->
                    let packet = event.Packet
                    try
                        let data = Array.zeroCreate<byte> packet.Length
                        packet.CopyTo(data)
                        // decode protobuf and dispatch further
                    finally
                        packet.Dispose()
                | EventType.Disconnect ->
                    printfn "Peer disconnected"
                | EventType.Timeout ->
                    printfn "Peer timed out"
                | _ -> ()

        host.Dispose()
        Library.Deinitialize()
```

### 6.4. Практические правила
- Инициализировать и деинициализировать библиотеку симметрично.
- Не размазывать владение `Host` и `Peer` по разным потокам без четкой модели.
- Сразу копировать payload из ENet packet в managed buffer, если дальше пакет пойдет в другой слой.
- Декодирование protobuf желательно делать после transport boundary, а не в глубине ENet-цикла.

### 6.5. Рекомендуемый паттерн
Хороший базовый flow:
1. ENet loop получает packet
2. копирует bytes
3. публикует `IncomingTransportPacket`
4. protocol layer декодирует protobuf
5. domain handler возвращает domain result / outgoing events
6. transport layer сериализует ответ и отправляет packet

### 6.6. Common mistakes на .NET стороне
- держать доменный state прямо внутри ENet loop
- выполнять БД-запросы из transport thread напрямую
- забывать dispose полученных packets
- не различать disconnect и timeout
- не вести session registry отдельно от peer object
- использовать peer ID как долгоживущий player identity

### 6.7. Edge cases
- peer disconnected, но доменная сессия еще висит
- reconnect пришел до полной cleanup предыдущей transport state
- старый peer успел прислать пакет уже после логического rebind/resume нового peer

Решение: session layer должен уметь инвалидацию stale peer bindings.

---

## 7. Каналы, transport semantics и практические оговорки

Этот документ не фиксирует окончательную transport-схему в деталях, но базово рекомендует:

- минимум каналов на старте
- надежные control packets по отдельному каналу
- не строить “свой REST по сокетам”
- четко отличать transport packet от domain command/event

### Практический минимум
- не отправлять огромные пачки без контроля размера
- не принимать безлимитные строки и коллекции
- не считать ENet reliable гарантией корректности доменной сессии
- любые incoming payload сначала валидировать по размеру и типу

---

## 8. Логирование и конфиги на F#

### 8.1. Рекомендуемый стек
- `FSharp.Logf`
- `Serilog`
- `Serilog.Extensions.Hosting`
- `Serilog.Sinks.Console`
- `Serilog.Sinks.File`
- `Serilog.Settings.Configuration`
- `Tomlyn.Extensions.Configuration`

### 8.2. Почему именно так
- приложение пишет логи через стандартный `ILogger`
- F# код получает удобный `printf`-style API через `FSharp.Logf`
- Serilog отвечает за sinks и формат вывода
- TOML используется как конфиг-источник через `IConfiguration`

### 8.3. Минимальный TOML-конфиг

```toml
[Serilog]
Using = ["Serilog.Sinks.Console", "Serilog.Sinks.File"]

[Serilog.MinimumLevel]
Default = "Information"

[[Serilog.WriteTo]]
Name = "Console"

[[Serilog.WriteTo]]
Name = "File"

[Serilog.WriteTo.Args]
path = "logs/server-.log"
rollingInterval = "Day"
shared = true
retainedFileCountLimit = 14
```

### 8.4. Минимальная загрузка конфигурации

```fsharp
open Microsoft.Extensions.Configuration

let configuration =
    ConfigurationBuilder()
        .AddTomlFile("appsettings.toml", optional = false, reloadOnChange = true)
        .Build()
```

### 8.5. Минимальная настройка Serilog

```fsharp
open Serilog

let configureSerilog (configuration: IConfiguration) =
    Log.Logger <-
        LoggerConfiguration()
            .ReadFrom.Configuration(configuration)
            .CreateLogger()
```

### 8.6. Встраивание в Host

```fsharp
open Microsoft.Extensions.Hosting
open Serilog

let builder = Host.CreateDefaultBuilder()

builder
    .UseSerilog(fun _ services loggerConfiguration ->
        loggerConfiguration
            .ReadFrom.Configuration(configuration)
        |> ignore)
|> ignore
```

### 8.7. Использование из F# через FSharp.Logf

```fsharp
open Microsoft.Extensions.Logging
open FSharp.Logf

let example (logger: ILogger) playerId =
    logfi logger "Player %O connected" playerId
```

### 8.8. Практические рекомендации
- Консоль + rolling file — нормальный базовый набор.
- Уровни логирования выносить в конфиг.
- Не логировать сырой бинарный packet body целиком по умолчанию.
- Для transport useful логировать:
  - connect
  - disconnect
  - timeout
  - session bind/rebind
  - auth success/failure
  - packet decode failure
  - rate-limit hit

### 8.9. Common mistakes
- логировать слишком много на `Information` и утонуть в шуме
- писать логи синхронно в горячем пути без меры
- складывать секреты / токены в логи
- считать, что file sink заменяет нормальную ротацию и retention policy по конфигу
- смешивать user-facing ошибки и internal diagnostic logs

### 8.10. Edge cases
- папка `logs/` не существует
- нет прав на запись
- лог-файл занят другим процессом
- конфиг поменяли на лету и уровни “прыгнули” неожиданно

Практика: делать явную bootstrap-проверку конфигурации логирования и директории логов при старте.

---

## 9. Конфиги проекта на F#

### 9.1. Рекомендация
Использовать TOML как человекочитаемый основной формат.

### 9.2. Что держать в конфиге
- network listen address / port
- transport timeouts
- server settings / caps
- db path
- logging config
- admin ui bind address
- feature flags

### 9.3. Что не держать в конфиге без нужды
- runtime mutable state
- long-lived session tables
- user data
- anything that should live in DB

### 9.4. Практический паттерн
- `appsettings.toml` — общие настройки
- `appsettings.local.toml` — локальные override’ы вне git
- `logging.toml` — опционально отдельно, если конфиг большой

### 9.5. Common mistakes
- пытаться сделать TOML “мини-БД”
- хранить там большие списки, которые лучше лежат в SQL
- смешивать серверный конфиг и постоянные domain settings, которые должны редактироваться из админки и храниться в БД

---

## 10. SQLite + SQLHydra + Migrondi

### 10.1. Роли инструментов
- `SQLite` — база
- `Migrondi` — миграции схемы
- `SqlHydra` — typed SQL access / query layer
- `Fling` — удобная загрузка и сохранение связанных aggregate’ов поверх базового DAL

### 10.2. Базовый принцип работы
1. поддерживаем схему через SQL-миграции
2. генерируем / используем typed database access через SQLHydra
3. сложные graph / aggregate операции решаем поверх этого слоя, при необходимости через Fling

### 10.3. Базовая настройка SQLite
Практически нужно:
- хранить файл БД в предсказуемом месте
- настроить bootstrap creation при пустом окружении
- применять миграции на старте или отдельной командой
- не писать в БД из хаотичных параллельных участков без понимания write contention

### 10.4. Миграции через Migrondi
Рекомендуемый подход:
- хранить SQL-файлы миграций в отдельной папке, например `/migrations`
- миграции нумеровать линейно и человекочитаемо
- migration runner вызывать при старте приложения или из отдельной admin/bootstrap команды

Пример naming:

```text
0001_init.sql
0002_add_sessions.sql
0003_add_guild_memberships.sql
0004_add_blacklist.sql
```

### 10.5. Пример миграции

```sql
CREATE TABLE players (
    player_id TEXT PRIMARY KEY,
    current_nick TEXT NOT NULL,
    public_key TEXT NOT NULL,
    created_at_utc TEXT NOT NULL
);
```

### 10.6. Практические правила для миграций
- миграции должны быть маленькими и атомарными
- одна миграция — одна понятная structural change
- не смешивать schema refactor и data cleanup без нужды
- не переписывать старые миграции после того, как они уже были применены в shared среде

### 10.7. SQLHydra
Стартовый смысл SQLHydra в проекте:
- typed DB access
- меньше ручного SQL-клея
- хорошая F#-эргономика для запросов

Практический подход:
- держать SQLHydra-слой как persistence boundary
- не таскать generated/db-specific детали глубоко в domain
- domain layer должен зависеть от repository/service abstractions, а не от всей низкоуровневой БД-механики

### 10.8. Fling
Fling полезен не как “ORM вместо всего”, а как helper для aggregate loading/saving.

Использовать его стоит там, где начинаются сценарии вроде:
- guild + memberships + roles
- player + friendships + blacklist + sessions
- party + members + invites

Не нужно тащить Fling в каждый простой CRUD.

### 10.9. Common mistakes с SQLite
- использовать SQLite как будто это полноценный high-write client/server DB server
- длинные транзакции
- тяжелые блокирующие операции из request path
- отсутствие нормального bootstrap и проверки существования БД
- смешивать schema migration, seed data и runtime config update в одну кучу

### 10.10. Edge cases
- миграция упала на середине старта
- БД-файл отсутствует или поврежден
- несколько процессов одновременно пытаются гонять миграции
- локально схема одна, а generated typed layer от старой схемы

Практика:
- запускать миграции централизованно
- валидировать совместимость схемы при старте
- держать простой способ пересоздания dev-окружения

---

## 11. Базовые варианты тестов на .NET: Expecto + Faqt

### 11.1. Роли инструментов
- `Expecto` — test runner и организация тестовых наборов
- `Faqt` — удобные assertions

### 11.2. Какие тесты нужны минимум
1. **Pure domain unit tests**
2. **Policy tests**
3. **Protocol mapping tests**
4. **Persistence tests**
5. **Transport integration smoke tests**

### 11.3. Пример unit test через Expecto + Faqt

```fsharp
open Expecto
open Faqt

[<Tests>]
let tests =
    testList "party rules" [
        testCase "disconnect does not remove player from party" <| fun _ ->
            let actual = true
            actual.Should().BeTrue()
    ]
```

### 11.4. Что тестировать в pure domain
- party leader reassignment
- guild mute semantics
- blacklist behavior
- invite permissions
- settings cap behavior
- session rebind rules

### 11.5. Что тестировать в persistence
- миграции применяются корректно
- repository roundtrip
- aggregate loading with relations
- idempotent read/write scenarios where applicable

### 11.6. Что тестировать в integration
- server starts
- DB opens
- migration runner executes
- ENet host starts
- connect / auth / disconnect smoke flow

### 11.7. Полезные паттерны
- тестировать domain без БД, где это возможно
- transport и persistence держать в integration tests
- для integration tests использовать отдельную test DB
- не переиспользовать production config paths

### 11.8. Common mistakes
- пытаться тестировать всю систему только end-to-end
- смешивать сетевые, доменные и БД-проверки в один огромный тест
- завязываться на wall-clock time и flaky timeouts
- тестировать логирование вместо поведения

### 11.9. Edge cases
- race при reconnect
- stale peer -> stale session
- duplicate invite acceptance
- repeated migration execution
- timeout path вместо graceful disconnect

---

## 12. Базовые варианты тестов для C++: doctest

### 12.1. Почему doctest
- минималистичный
- удобный старт
- хорошо подходит для unit / small integration tests

### 12.2. Базовый пример

```cpp
#define DOCTEST_CONFIG_IMPLEMENT_WITH_MAIN
#include <doctest/doctest.h>

TEST_CASE("simple truth") {
    CHECK(1 + 1 == 2);
}
```

### 12.3. Что тестировать на C++ стороне
- packet encoding/decoding boundary helpers
- config loading via glaze
- session/client state transitions
- reconnect handling logic
- small ENet client smoke tests

### 12.4. Пример state test

```cpp
#include <doctest/doctest.h>

enum class ClientState {
    Disconnected,
    Connecting,
    Connected
};

TEST_CASE("client starts disconnected") {
    ClientState state = ClientState::Disconnected;
    CHECK(state == ClientState::Disconnected);
}
```

### 12.5. Практические рекомендации
- unit tests держать без сети, где можно
- отдельные smoke integration tests делать поверх реального ENet connect
- не завязывать каждый тест на внешний сервер
- по возможности тестировать serialization boundary отдельно от ENet

### 12.6. Common mistakes
- все тесты делать только через live server
- засовывать sleeps вместо явных условий
- не отделять packet construction logic от actual network I/O
- не чистить ресурсы после failed connect

---

## 13. Минимальная админка: Falco + Falco.Markup + Falco.Htmx

### 13.1. Назначение админки
- просмотр состояния сервера
- просмотр игроков / guild / party / moderation state
- ограниченный набор управляющих действий
- без SPA и без полноценного frontend app

### 13.2. Подход
- SSR страницы
- htmx для partial updates, форм и action buttons
- минимум клиентского JavaScript

### 13.3. Практические правила
- не смешивать admin UI logic с transport/server runtime logic
- admin routes должны жить отдельно от игрового transport слоя
- все опасные действия должны быть explicit и логироваться
- не делать админку единственным способом восстановления данных: критичные действия должны иметь CLI / backup path

### 13.4. Common mistakes
- делать в админке слишком много runtime magic
- забывать auth / authorization даже для “внутренней” панели
- делать destructive actions без confirm / audit log

---

## 14. C++ конфиги: glaze

### 14.1. Практическая роль
На C++ стороне `glaze` используется для чтения и записи конфигов.

### 14.2. Рекомендации
- держать конфиг клиента простым
- не хранить в конфиге volatile runtime state
- логически отделять user preferences, identity storage и network settings

### 14.3. Common mistakes
- смешать конфиг, кэш и persistent player identity в один файл
- не валидировать обязательные поля после чтения
- молча принимать сломанный конфиг вместо явной ошибки и fallback

---

## 15. Общие common mistakes по проекту

### 15.1. Transport
- transport object =/= player identity
- peer handle =/= session
- reliable delivery =/= domain correctness
- отсутствие явной схемы каналов

### 15.2. Persistence
- домен слишком сильно зависит от SQL-деталей
- migration и runtime mutation смешаны
- нет отдельной test DB

### 15.3. Logging
- логируют слишком много, но не то, что реально помогает
- логируют секреты
- нет correlation / session identifiers в важных логах

### 15.4. Testing
- слишком мало pure domain tests
- слишком много “ручного” live integration без стабильных test fixtures
- flaky sleeps / time-based assertions

### 15.5. Admin
- админка превращается в источник бизнес-логики
- destructive actions без audit trail

---

## 16. Edge cases, которые надо учитывать с первого дня

### Session / transport
- reconnect раньше cleanup старой transport state
- duplicate packets после смены peer binding
- timeout вместо disconnect
- stale resume token

### Persistence
- миграции не применились, но сервер все равно пошел дальше
- частично заполненная БД после dev-экспериментов
- schema drift между кодом и БД

### Logging / config
- отсутствующий TOML-файл
- некорректный формат файла
- лог-путь недоступен
- runtime reload конфигурации ломает ожидаемое поведение

### Test environment
- integration tests делят одну БД
- network ports заняты
- leftover files from previous test run

---

## 17. Исходный эскиз этапов (текущее состояние описано выше)

1. Поднять минимальный ENet smoke test:
   - C++ client
   - F# server
   - connect / send / receive / disconnect

2. Поднять protobuf transport boundary:
   - одно тестовое сообщение
   - decode / encode roundtrip

3. Завести TOML конфиг и Serilog logging bootstrap

4. Поднять SQLite + Migrondi bootstrap

5. Завести SQLHydra persistence skeleton

6. Написать первые pure domain tests на Expecto + Faqt

7. Поднять минимальную SSR admin страницу через Falco

Такой порядок позволяет получить работающий позвоночник системы до того, как проект утонет в деталях доменной логики.

---

## 18. Что документировать отдельно после этого

Следующими отдельными документами желательно оформить:
- protobuf contract handbook
- protocol versioning policy
- domain model + SQL schema handbook
- session/auth/recovery spec
- admin operations handbook
- logging and diagnostics policy
- testing strategy handbook

---

## 19. Краткий итог

Зафиксированный стек хорошо подходит под проект, потому что:
- transport прагматичный и межъязыковой
- protobuf standard и предсказуемый
- F#-стек не перегружен лишними слоями
- SQLite + SQLHydra + Migrondi достаточно для текущего масштаба
- SSR админка минималистична и не требует полноценного frontend app
- тестовый стек покрывает как домен, так и транспортные smoke/integration сценарии

Главное условие успеха — не смешивать транспорт, домен, persistence и admin в один неразделенный слой. Этот документ фиксирует именно обратное: аккуратную модульную основу, на которую дальше уже можно безопасно наращивать специфику игры.
