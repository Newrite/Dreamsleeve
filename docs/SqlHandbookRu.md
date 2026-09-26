# SQL Handbook для MMO Chat

> Статус на 26.09.2026: справочник ранее предложенного SQLite-стека.
> Persistence не реализован; LiteDB обсуждалась без окончательного перехода.
> [Текущие решения](CurrentStateRu.md) и архив SQL-примеров в коммите `40059c5`.

## Отличия текущего домена от старых SQL-примеров

ID игроков/чатов/сообщений — ненулевые uint64, а не UUID/ULID. Диапазон хранения
и отображение в SQLite INTEGER ещё не определены. Уникален Username, а
DisplayName может совпадать; CharacterName — отдельное игровое поле.
Индексы уникального normalized_nickname ниже относятся к старому эскизу.
Схему identity необходимо пересмотреть до первой настоящей миграции.

Сообщения и игровая телеметрия остаются в памяти. Профили, ключи идентичности
и будущие социальные связи — кандидаты для persistence. Примеры партий/гильдий
не расширяют текущий MVP и не означают, что эти подсистемы уже созданы.

## 1. Назначение документа

Этот документ описывает практическую работу со слоем хранения данных в проекте MMO Chat на стеке:

- **SQLite** как основная БД
- **Microsoft.Data.Sqlite** как ADO.NET provider
- **SqlHydra** как генерация типов и typed query layer для F#
- **Fling** как слой для удобной загрузки и сохранения агрегатов из нескольких таблиц
- **Migrondi** как простой runner SQL-миграций

Документ **не** уходит глубоко в protobuf, сетевой транспорт и подробную доменную модель чата. Он объясняет:

- как этот стек должен быть организован;
- как он работает в проекте;
- как писать и применять миграции;
- как настраивать SQLite и правильно работать с подключениями;
- какие SQL-схемы я рекомендую для проекта;
- какие common mistakes и edge cases почти наверняка встретятся.

---

## 2. Роль каждого инструмента

### SQLite
БД исходного проекта хранения; рабочий адаптер пока отсутствует.

Подходит для проекта с умеренной нагрузкой и числом пользователей порядка тысячи-полутора, если:

- записи не превращаются в тяжелый конкурентный write storm;
- вы не пытаетесь хранить в БД каждое volatile state update;
- write-транзакции короткие;
- проект живет как один серверный процесс или небольшое число согласованных процессов.

### Microsoft.Data.Sqlite
Это провайдер доступа к SQLite для .NET.

Именно он дает:

- `SqliteConnection`
- `SqliteCommand`
- `SqliteTransaction`
- connection string builder
- pooling
- busy/locked retry behavior

SqlHydra и остальные библиотеки для работы с SQLite в .NET в итоге все равно опираются на этот слой.

### SqlHydra
SqlHydra — это **не ORM в стиле EF Core**, а более прямой typed SQL stack:

- CLI генерирует F#-типы из вашей БД;
- query layer дает typed computation expressions для SQL;
- вы сохраняете контроль над таблицами и SQL-мышлением.

SqlHydra хорошо подходит под проект, где:

- хочется typed-first доступ к данным;
- не хочется тяжелого ORM;
- структура БД важна и понятна;
- хочется, чтобы схема оставалась первичной.

### Fling
Fling — это не замена SqlHydra.

Он нужен для другого: **уменьшить бойлерплейт вокруг агрегатов**, которые растянуты на несколько таблиц.

Примеры агрегатов в вашем проекте:

- Guild + memberships + roles + invites
- Party + members + pending invites
- PlayerIdentity + social links + sessions

Fling особенно полезен там, где нужно:

- загрузить root entity и потом child entities;
- сохранять только реально изменившиеся дочерние строки;
- не писать вручную тонны orchestration-кода между таблицами.

### Migrondi
Migrondi — это file-based runner SQL-миграций.

Его идея очень простая:

- миграции — это обычные SQL-файлы;
- tool применяет их к БД;
- tool умеет создавать новые migration files и откатывать их.

Это хорошо совпадает с вашим стеком, потому что вы и так хотите жить ближе к SQL, а не к code-first ORM.

---

## 3. Рекомендуемая архитектура data layer

Я рекомендую такую модель:

### 3.1 Источник истины по схеме
Источник истины по схеме — **SQL-миграции**.

Не F# records.
Не auto-generated классы.
Не ORM metadata.

Порядок должен быть такой:

1. Вы меняете схему через новую SQL-миграцию.
2. Применяете миграцию к dev БД.
3. Перегенерируете SqlHydra types из обновленной схемы.
4. Обновляете F# код поверх новой схемы.

Это дает здоровую дисциплину:

- схема меняется явно;
- можно откатываться;
- generated code всегда соответствует реальной БД;
- меньше магии и рассинхрона.

### 3.2 Разделение ответственности

#### Migrondi
Отвечает только за:
- версионирование schema changes;
- применение/откат SQL-миграций.

#### SqlHydra
Отвечает за:
- generated DB types;
- typed queries;
- insert/update/select/delete слой.

#### Fling
Отвечает за:
- агрегаты поверх простых per-table CRUD/lookup функций.

#### Domain layer
Отвечает за:
- business rules;
- policy checks;
- orchestration;
- транзакционные сценарии.

Это важное разделение.

**Не надо превращать Fling в общий DAL.**
**Не надо превращать SqlHydra в domain model.**
**Не надо пытаться делать миграции через generated code.**

---

## 4. Рекомендуемые пакеты и инструменты

### Серверный F# проект

```bash
# ADO.NET provider для SQLite
 dotnet add package Microsoft.Data.Sqlite

# Typed query layer
 dotnet add package SqlHydra.Query

# Aggregate helper
 dotnet add package Fling
```

### Локальный tool для code generation

```bash
 dotnet new tool-manifest
 dotnet tool install --local SqlHydra.Cli
```

### Локальный или глобальный tool для миграций

```bash
 dotnet tool install Migrondi
```

Или как local tool, если хотите закрепить версию в репозитории.

---

## 5. Рекомендуемая структура каталогов

```text
/server
  /src
    /Server
    /Server.Admin
    /Server.Protocol.Generated
    /Server.Persistence.Generated
    /Server.Domain
    /Server.Infrastructure
    /Server.Tests

  /db
    /migrations
      000001_initial.up.sql
      000001_initial.down.sql
      000002_social.up.sql
      000002_social.down.sql
      000003_guilds.up.sql
      000003_guilds.down.sql

    /sqlhydra
      sqlhydra-sqlite.toml

    migrondi.json

  server.toml
```

### Что где лежит

- `/db/migrations` — реальные SQL migration files
- `/db/migrondi.json` — конфигурация Migrondi
- `/db/sqlhydra/...` — конфиг генерации SqlHydra, если хотите хранить его отдельно
- `/Server.Persistence.Generated` — generated F# code от SqlHydra
- `/Server.Infrastructure` — реализация доступа к БД, подключения, репозитории, транзакции
- `/Server.Domain` — доменная логика, policy layer, use cases

---

## 6. SQLite: как правильно настроить базу

### 6.1 Connection string

Базовый рекомендуемый connection string:

```text
Data Source=data/mmochat.db;Mode=ReadWriteCreate;Foreign Keys=True;Pooling=True
```

Рекомендация:

- `Foreign Keys=True` — обязательно
- `Pooling=True` — оставить включенным
- `Mode=ReadWriteCreate` — нормальный дефолт для серверного приложения

### 6.2 Что делать с WAL

Для серверного сценария я рекомендую **WAL mode**.

Базовая инициализация после открытия БД:

```sql
PRAGMA journal_mode = WAL;
PRAGMA synchronous = NORMAL;
PRAGMA busy_timeout = 5000;
```

Рекомендации:

- `journal_mode=WAL` — обычно лучший выбор для прикладного сервера с конкурентным доступом;
- `synchronous=NORMAL` — разумный компромисс между скоростью и надежностью для фан-проекта;
- `busy_timeout` — полезен как дополнительная защита от спорадических lock conflicts.

### 6.3 Что не делать

Не рекомендую смешивать:

- `WAL`
- и `Cache=Shared`

в обычном приложении.

Если вы используете WAL, живите в обычном pooled-per-connection режиме.

### 6.4 Пример инициализации подключения в F#

```fsharp
open Microsoft.Data.Sqlite

module Sqlite =
    let createConnectionString (path: string) =
        SqliteConnectionStringBuilder()
            .With(fun b ->
                b.DataSource <- path
                b.Mode <- SqliteOpenMode.ReadWriteCreate
                b.ForeignKeys <- true
                b.Pooling <- true)
            .ToString()

    let openConnection (connStr: string) = task {
        let conn = new SqliteConnection(connStr)
        do! conn.OpenAsync()

        let pragmaSql = """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            PRAGMA busy_timeout = 5000;
        """

        use cmd = conn.CreateCommand()
        cmd.CommandText <- pragmaSql
        do! cmd.ExecuteNonQueryAsync() |> Task.Ignore

        return conn
    }
```

Примечание: `.With(...)` здесь условный helper. Если такого helper нет, задавайте свойства у билдера обычным способом.

---

## 7. Как правильно работать с подключениями

### 7.1 Главный принцип

**Не делите один `SqliteConnection` между потоками.**

Правильная модель:

- на каждую операцию или unit of work берете новое подключение;
- открываете его как можно позже;
- закрываете как можно раньше;
- полагаетесь на pooling.

### 7.2 Что делать в конкурентном приложении

Для серверного приложения лучше так:

- один запрос / один use case / одна транзакция = одно подключение;
- для операций записи — короткие транзакции;
- не держать connection живым “на всякий случай”;
- не таскать connection через половину приложения.

### 7.3 Пул или один long-lived connection?

Мой совет:

**пул + короткоживущие подключения**, а не один long-lived global connection.

Почему:

- это лучше совпадает с тем, как устроен Microsoft.Data.Sqlite;
- меньше риск race conditions и accidental sharing;
- проще тестировать;
- проще оформлять транзакции по use case.

### 7.4 Нужен ли отдельный write mailbox?

Для вашего проекта это хорошая идея не потому, что SQLite “не справится”, а потому что у вас и так серверная логика на agent/mailbox-подходе.

То есть можно сделать так:

- чтения — обычные короткие pooled connections;
- важные конкурентные write-scenarios — через один сериализованный application-level writer actor.

Это особенно полезно для:

- guild mutations;
- party mutations;
- admin operations;
- invite accept/reject races.

Но это уже **доменная дисциплина**, а не обязательное требование драйвера.

---

## 8. SqlHydra: как это использовать

### 8.1 Общая идея

SqlHydra работает в два шага:

1. CLI читает вашу SQLite schema и генерирует F# код;
2. runtime library (`SqlHydra.Query`) позволяет писать typed queries к этим generated types.

### 8.2 Базовый workflow

```bash
# 1. Применить миграции к dev-базе
migrondi up

# 2. Сгенерировать типы из обновленной схемы
 dotnet sqlhydra sqlite

# 3. Подключить generated file/project в solution
```

### 8.3 Что генерируется

Вы получаете:

- record types по таблицам;
- metadata по колонкам;
- DB-specific `QueryContextFactory`.

### 8.4 Как создать query context

```fsharp
open SqlHydra.Query
open Server.Persistence.Generated

let db = ServerDb.QueryContextFactory.Create(connStr, printfn "SQL: %O")
```

Где `ServerDb` — условный namespace/модуль, который вы задали при генерации.

### 8.5 Базовый select

```fsharp
open SqlHydra.Query
open SqlHydra.Query.SqliteExtensions

let getPlayerById (db: ServerDb.QueryContextFactory) playerId =
    selectTask db {
        for p in Main.Players do
        where (p.PlayerId = playerId)
        select p
        tryHead
    }
```

### 8.6 Базовый insert

```fsharp
let insertPlayer (db: ServerDb.QueryContextFactory) player =
    insertTask db {
        into Main.Players
        entity player
    }
```

### 8.7 Когда SqlHydra подходит идеально

Используйте его для:

- простых select/insert/update/delete;
- фильтрации;
- list queries;
- lookup по id;
- административных списков;
- typed query building.

### 8.8 Когда SqlHydra не должен тащить все на себе

Не надо пытаться через один слой запросов решить:

- aggregate orchestration;
- сравнение old/new graph state;
- domain policy;
- сложную загрузку root + children + batch save.

Для этого лучше подключать Fling.

### 8.9 Практическая рекомендация

Сгенерированный код лучше держать:

- либо в отдельном проекте `Server.Persistence.Generated`;
- либо в отдельном generated `.fs` файле, который не редактируется руками.

**Не редактируйте generated code вручную.**

---

## 9. Fling: где он действительно нужен

### 9.1 Когда Fling стоит применять

Fling полезен там, где у вас есть:

- root entity;
- несколько child collections или child entities;
- и вы хотите минимизировать бойлерплейт при сохранении/загрузке.

Примеры:

- `Guild` + `GuildMemberships`
- `Party` + `PartyMembers` + `PartyInvites`
- `PlayerProfile` + `PlayerKeys` + `Sessions`

### 9.2 Когда Fling не нужен

Не надо тянуть Fling в:

- flat lookup tables;
- простые admin lists;
- “получить игрока по id”;
- “обновить mute_until”;
- “добавить одну строку в blacklist”.

Там проще и лучше использовать обычный SqlHydra.

### 9.3 Рекомендуемая модель использования

Слой можно разделить так:

- **SqlHydra repositories** — simple per-table read/write functions
- **Fling aggregate modules** — aggregate load/save orchestration
- **Domain services** — правила и сценарии

### 9.4 Пример направления

Например для Guild aggregate:

- `GuildRepository.getById`
- `GuildMembershipRepository.getByGuildIds`
- `GuildInviteRepository.getPendingByGuildIds`

И поверх этого:

- `GuildAggregate.load`
- `GuildAggregate.save`

Где Fling помогает:

- подгрузить memberships/invites;
- при save вставить/обновить/удалить только реально изменившиеся строки.

### 9.5 Важная рекомендация

Fling лучше вводить **точечно**, а не “на все подряд”.

Сначала:
- Players
- Social
- Moderation
- Sessions

можно держать просто на SqlHydra.

Потом:
- Party
- Guild

вынести в Fling aggregates.

---

## 10. Migrondi: как использовать миграции

### 10.1 Подход

Migrondi — это именно **SQL migration runner**.

Под ваш стек это хорошо, потому что:

- вы хотите SQL-first workflow;
- SQLite — маленькая и прозрачная схема;
- вам удобнее держать schema evolution явно в `.sql`.

### 10.2 Надо ли писать именно `.sql` миграции?

**Я рекомендую — да.**

Для этого стека SQL migration files — лучший вариант.

Почему:

- Migrondi именно про SQL migrations;
- SQLite очень удобно контролировать через явный SQL;
- не нужно смешивать schema evolution с кодом приложения;
- миграции легче читать, ревьюить и катить отдельно от кода.

### 10.3 Базовые команды

```bash
# Инициализировать конфиг и структуру
migrondi init

# Создать новую миграцию
migrondi create add_guild_invites

# Применить все pending миграции
migrondi up

# Откатить одну миграцию
migrondi down 1

# Посмотреть состояние
migrondi list
```

### 10.4 Какой стиль именования использовать

Рекомендую простой префикс с возрастающим номером:

```text
000001_initial.up.sql
000001_initial.down.sql
000002_social.up.sql
000002_social.down.sql
000003_party.up.sql
000003_party.down.sql
```

Это просто, стабильно и читаемо.

### 10.5 Как писать up/down

#### Пример `000001_initial.up.sql`

```sql
CREATE TABLE players (
    player_id            INTEGER PRIMARY KEY,
    current_nick         TEXT NOT NULL,
    created_at_utc       TEXT NOT NULL,
    updated_at_utc       TEXT NOT NULL
);
```

#### Пример `000001_initial.down.sql`

```sql
DROP TABLE IF EXISTS players;
```

### 10.6 Где должны лежать миграции

Мой совет:

- хранить миграции в репозитории в `/db/migrations`;
- на deploy либо:
  - копировать эту директорию рядом с сервером,
  - либо запускать `migrondi` из checkout/release bundle, где миграции уже лежат на диске.

### 10.7 Должны ли миграции билдиться вместе с проектом?

По умолчанию я **не рекомендую** делать миграции embedded resources.

Для этого стека проще и понятнее держать их как обычные файлы на диске.

То есть:

- **да**, их можно копировать в publish/output folder;
- **нет**, я бы не делал ставку на embed в сборку на первом этапе;
- **да**, их нужно иметь доступными тому процессу или окружению, которое запускает миграции.

### 10.8 Когда запускать миграции

Есть два здоровых пути:

#### Вариант A — миграции как deploy step
Лучший вариант для production-like режима.

Сценарий:
1. останавливаете сервер;
2. делаете backup DB;
3. запускаете `migrondi up`;
4. поднимаете новую версию сервера.

#### Вариант B — миграции на старте сервера
Удобно для dev/small self-hosted режима.

Сценарий:
1. сервер стартует;
2. проверяет миграции;
3. применяет pending;
4. дальше поднимает основной runtime.

Мой совет:
- **dev** — можно на старте;
- **prod** — лучше как отдельный deployment step.

### 10.9 Надо ли править старые миграции?

**Нет.**

После того как миграция уже применялась в истории проекта:
- старые миграции не переписываются;
- новая правка делается новой миграцией.

Исключение — только если проект еще вообще никуда не выкатывался и вы сознательно делаете reset локальной истории.

---

## 11. Рекомендуемая SQL-схема проекта

Ниже не “единственно верная схема”, а мой рекомендуемый стартовый каркас.

### 11.1 Что я бы хранил в БД

#### Обязательно
- игроки и identity
- client public keys / recovery metadata
- session metadata / resume tokens
- friends / blacklist
- guilds / memberships / invites
- parties / members / invites
- moderation state
- admin roles / audit-like state при необходимости

#### Не обязательно на старте
- chat history
- volatile player state
- transient online roster cache
- typing/presence telemetry

### 11.2 Игроки

```sql
CREATE TABLE players (
    player_id                INTEGER PRIMARY KEY,
    current_nick             TEXT NOT NULL,
    created_at_utc           TEXT NOT NULL,
    updated_at_utc           TEXT NOT NULL,
    is_deleted               INTEGER NOT NULL DEFAULT 0
);

CREATE UNIQUE INDEX ux_players_current_nick
    ON players(current_nick);
```

Комментарий:
- если захотите разрешить переиспользование ников после delete/rename, индекс и правила можно ослабить;
- если ник case-insensitive, нормализованный ник лучше хранить отдельно.

### 11.3 Ключи identity

```sql
CREATE TABLE player_keys (
    player_key_id            INTEGER PRIMARY KEY,
    player_id                INTEGER NOT NULL,
    key_version              INTEGER NOT NULL,
    public_key               BLOB NOT NULL,
    is_active                INTEGER NOT NULL DEFAULT 1,
    created_at_utc           TEXT NOT NULL,
    revoked_at_utc           TEXT NULL,
    FOREIGN KEY (player_id) REFERENCES players(player_id)
);

CREATE UNIQUE INDEX ux_player_keys_player_version
    ON player_keys(player_id, key_version);
```

Комментарий:
- позволяет делать rebind/recovery без потери истории ключей;
- активный ключ можно хранить либо флагом, либо вычислять по `revoked_at_utc IS NULL`.

### 11.4 Recovery

```sql
CREATE TABLE player_recovery (
    player_id                INTEGER PRIMARY KEY,
    recovery_secret_hash     BLOB NOT NULL,
    rotated_at_utc           TEXT NOT NULL,
    FOREIGN KEY (player_id) REFERENCES players(player_id)
);
```

Если recovery через админа полностью ручной и кода восстановления не будет, эту таблицу можно отложить.

### 11.5 Сессии

```sql
CREATE TABLE player_sessions (
    session_id               TEXT PRIMARY KEY,
    player_id                INTEGER NOT NULL,
    resume_token_hash        BLOB NOT NULL,
    created_at_utc           TEXT NOT NULL,
    expires_at_utc           TEXT NOT NULL,
    revoked_at_utc           TEXT NULL,
    last_seen_at_utc         TEXT NOT NULL,
    client_build             TEXT NULL,
    FOREIGN KEY (player_id) REFERENCES players(player_id)
);

CREATE INDEX ix_player_sessions_player_id
    ON player_sessions(player_id);
```

### 11.6 Дружба

```sql
CREATE TABLE friendships (
    friendship_id            INTEGER PRIMARY KEY,
    requester_player_id      INTEGER NOT NULL,
    addressee_player_id      INTEGER NOT NULL,
    state                    TEXT NOT NULL,
    created_at_utc           TEXT NOT NULL,
    updated_at_utc           TEXT NOT NULL,
    FOREIGN KEY (requester_player_id) REFERENCES players(player_id),
    FOREIGN KEY (addressee_player_id) REFERENCES players(player_id)
);

CREATE UNIQUE INDEX ux_friendships_pair
    ON friendships(requester_player_id, addressee_player_id);
```

Где `state` может быть:
- `pending`
- `accepted`
- `removed`
- `declined`

Можно сделать и две directed rows, но для старта одна row на directed pair тоже нормальна.

### 11.7 Блоклист

```sql
CREATE TABLE player_blocks (
    blocker_player_id        INTEGER NOT NULL,
    blocked_player_id        INTEGER NOT NULL,
    created_at_utc           TEXT NOT NULL,
    PRIMARY KEY (blocker_player_id, blocked_player_id),
    FOREIGN KEY (blocker_player_id) REFERENCES players(player_id),
    FOREIGN KEY (blocked_player_id) REFERENCES players(player_id)
);
```

### 11.8 Пати

```sql
CREATE TABLE parties (
    party_id                 INTEGER PRIMARY KEY,
    leader_player_id         INTEGER NOT NULL,
    created_at_utc           TEXT NOT NULL,
    disbanded_at_utc         TEXT NULL,
    FOREIGN KEY (leader_player_id) REFERENCES players(player_id)
);

CREATE TABLE party_members (
    party_id                 INTEGER NOT NULL,
    player_id                INTEGER NOT NULL,
    joined_at_utc            TEXT NOT NULL,
    left_at_utc              TEXT NULL,
    join_order               INTEGER NOT NULL,
    PRIMARY KEY (party_id, player_id),
    FOREIGN KEY (party_id) REFERENCES parties(party_id),
    FOREIGN KEY (player_id) REFERENCES players(player_id)
);

CREATE TABLE party_invites (
    party_invite_id          INTEGER PRIMARY KEY,
    party_id                 INTEGER NOT NULL,
    invited_player_id        INTEGER NOT NULL,
    invited_by_player_id     INTEGER NOT NULL,
    state                    TEXT NOT NULL,
    created_at_utc           TEXT NOT NULL,
    responded_at_utc         TEXT NULL,
    FOREIGN KEY (party_id) REFERENCES parties(party_id),
    FOREIGN KEY (invited_player_id) REFERENCES players(player_id),
    FOREIGN KEY (invited_by_player_id) REFERENCES players(player_id)
);
```

Примечания:
- `left_at_utc IS NULL` означает активное membership;
- `join_order` удобен для логики “старейший в группе получает лидерство”;
- logout/disconnect не должен менять `left_at_utc`.

### 11.9 Гильдии

```sql
CREATE TABLE guilds (
    guild_id                 INTEGER PRIMARY KEY,
    guild_name               TEXT NOT NULL,
    created_by_player_id     INTEGER NOT NULL,
    created_at_utc           TEXT NOT NULL,
    disbanded_at_utc         TEXT NULL,
    FOREIGN KEY (created_by_player_id) REFERENCES players(player_id)
);

CREATE UNIQUE INDEX ux_guilds_name
    ON guilds(guild_name);
```

### 11.10 Membership в гильдии

```sql
CREATE TABLE guild_memberships (
    guild_id                 INTEGER NOT NULL,
    player_id                INTEGER NOT NULL,
    role                     TEXT NOT NULL,
    guild_mute_until_utc     TEXT NULL,
    joined_at_utc            TEXT NOT NULL,
    left_at_utc              TEXT NULL,
    PRIMARY KEY (guild_id, player_id),
    FOREIGN KEY (guild_id) REFERENCES guilds(guild_id),
    FOREIGN KEY (player_id) REFERENCES players(player_id)
);
```

`role`:
- `guild_master`
- `officer`
- `member`

### 11.11 Инвайты в гильдию

```sql
CREATE TABLE guild_invites (
    guild_invite_id          INTEGER PRIMARY KEY,
    guild_id                 INTEGER NOT NULL,
    invited_player_id        INTEGER NOT NULL,
    invited_by_player_id     INTEGER NOT NULL,
    state                    TEXT NOT NULL,
    created_at_utc           TEXT NOT NULL,
    responded_at_utc         TEXT NULL,
    FOREIGN KEY (guild_id) REFERENCES guilds(guild_id),
    FOREIGN KEY (invited_player_id) REFERENCES players(player_id),
    FOREIGN KEY (invited_by_player_id) REFERENCES players(player_id)
);
```

### 11.12 Глобальная модерация

```sql
CREATE TABLE global_moderation (
    moderation_id            INTEGER PRIMARY KEY,
    target_player_id         INTEGER NOT NULL,
    action_type              TEXT NOT NULL,
    issued_by_player_id      INTEGER NOT NULL,
    reason                   TEXT NULL,
    created_at_utc           TEXT NOT NULL,
    expires_at_utc           TEXT NULL,
    revoked_at_utc           TEXT NULL,
    FOREIGN KEY (target_player_id) REFERENCES players(player_id),
    FOREIGN KEY (issued_by_player_id) REFERENCES players(player_id)
);
```

`action_type`:
- `global_mute`
- `global_ban`

### 11.13 Серверные роли / модераторы

```sql
CREATE TABLE server_roles (
    player_id                INTEGER NOT NULL,
    role                     TEXT NOT NULL,
    assigned_by_player_id    INTEGER NOT NULL,
    assigned_at_utc          TEXT NOT NULL,
    revoked_at_utc           TEXT NULL,
    PRIMARY KEY (player_id, role),
    FOREIGN KEY (player_id) REFERENCES players(player_id),
    FOREIGN KEY (assigned_by_player_id) REFERENCES players(player_id)
);
```

`role`:
- `owner`
- `moderator`
- `gamemaster`

### 11.14 Системные анонсы

Если хотите хранить persistent-конфигурацию periodic announcements в БД:

```sql
CREATE TABLE system_announcements (
    announcement_id          INTEGER PRIMARY KEY,
    is_enabled               INTEGER NOT NULL,
    message_text             TEXT NOT NULL,
    interval_seconds         INTEGER NOT NULL,
    created_at_utc           TEXT NOT NULL,
    updated_at_utc           TEXT NOT NULL
);
```

Но я бы на первом этапе такие вещи скорее держал в `server.toml`, а не в БД.

### 11.15 Что я бы не клал в БД на старте

#### Chat messages
У вас уже выбран cache-first подход:
- клиент хранит локально N;
- сервер держит N или M времени в кеше;
- БД не обязательна.

Я бы это решение сохранил.

#### Volatile player state
- hp
- location
- map state
- ephemeral telemetry

Это не нужно писать в SQLite как обычную постоянную модель.

---

## 12. Миграционная стратегия для SQLite

SQLite хорош, но миграции в нем нужно делать аккуратно.

### 12.1 Предпочитайте additive migrations

Наиболее здоровый путь:

- добавление таблицы
- добавление колонки
- добавление индекса
- добавление новой связи

Это самые безопасные миграции.

### 12.2 Осторожнее с destructive migrations

Изменения вроде:

- удалить колонку
- radically reorder table structure
- переписать constraint model

лучше делать отдельно и осознанно.

На SQLite такие вещи чаще всего требуют более внимательного сценария, чем в “больших” серверных СУБД.

### 12.3 Что рекомендую для down migrations

Для dev и early-stage проекта down migrations полезны.

Но не пытайтесь любой ценой сделать идеальный reversible down для каждой тяжелой data-migration.

Правильный приоритет:

1. up migration должна быть безопасной и ясной;
2. down migration — настолько хорошей, насколько это разумно;
3. для production rollout все равно нужен backup перед schema change.

---

## 13. Как должна выглядеть работа в коде

### 13.1 Простые read/write операции

Используйте SqlHydra.

Примеры:
- получить игрока по id
- получить guild memberships игрока
- вставить новую сессию
- обновить `last_seen_at_utc`
- получить pending invites

### 13.2 Сложные aggregate операции

Используйте Fling поверх простых функций доступа.

Примеры:
- сохранить `Guild` вместе с изменившимися memberships;
- сохранить `Party` вместе с member set;
- загрузить `Guild aggregate` из guild + memberships + invites.

### 13.3 Транзакционные use cases

Делайте на уровне domain/application service.

Например:
- принять invite в guild
- принять invite в party
- disband guild
- disband party
- rebind player key

Внутри такого use case:
1. открываете connection;
2. начинаете transaction;
3. читаете нужный state;
4. проверяете policy;
5. обновляете таблицы;
6. commit.

---

## 14. Пример базового инфраструктурного слоя

```fsharp
module Db =
    open Microsoft.Data.Sqlite

    type DbConfig = {
        ConnectionString: string
        DefaultTimeoutSeconds: int
    }

    let openConnection (cfg: DbConfig) = task {
        let conn = new SqliteConnection(cfg.ConnectionString)
        conn.DefaultTimeout <- cfg.DefaultTimeoutSeconds
        do! conn.OpenAsync()
        return conn
    }

    let withConnection cfg (f: SqliteConnection -> Task<'T>) = task {
        use! conn = openConnection cfg
        return! f conn
    }

    let withTransaction cfg (f: SqliteConnection -> SqliteTransaction -> Task<'T>) = task {
        use! conn = openConnection cfg
        use! tx = conn.BeginTransactionAsync()
        try
            let! result = f conn tx
            do! tx.CommitAsync()
            return result
        with ex ->
            do! tx.RollbackAsync()
            return raise ex
    }
```

Это лучше, чем таскать один глобальный `SqliteConnection` по всему серверу.

---

## 15. Common mistakes

### 15.1 Делить один connection между потоками

Плохая идея.

Даже если кажется, что “SQLite же легкий”, объекты `SqliteConnection/Command/DataReader` не должны использоваться конкурентно.

### 15.2 Делать очень длинные транзакции

Плохая идея.

Особенно в SQLite.

Если транзакция открыта слишком долго:
- растут lock conflicts;
- другие операции ждут;
- вы сами провоцируете `busy/locked` поведение.

### 15.3 Отключить foreign keys

Плохая идея.

Без foreign keys проект очень быстро начнет копить осиротевшие membership/invite/session записи.

### 15.4 Использовать `Cache=Shared` вместе с WAL без явной причины

Плохая идея.

Для нормального серверного сценария проще жить на WAL + pooled short-lived connections.

### 15.5 Генерировать SqlHydra из схемы, к которой не применены последние миграции

Очень частая ошибка.

Правильный порядок:
- применили миграции;
- потом регенерировали SqlHydra.

### 15.6 Использовать Fling везде подряд

Это приведет к переусложнению.

Fling нужен для агрегатов, а не для любого select/update.

### 15.7 Хранить volatile state в SQLite

Плохая идея.

Если вы начнете писать каждую позицию, HP и transient state в SQLite, то сами сделаете себе bottleneck.

### 15.8 Править старые миграции после того, как они уже были применены

Плохая идея.

Новая правка — новая миграция.

### 15.9 Не проверять миграции на копии реальной БД

Опасно.

Даже если схема маленькая, destructive migrations нужно прогонять на realistic data.

### 15.10 Забыть, что SQLite — это в первую очередь single-writer-friendly модель

Это не значит “писать нельзя”.
Это значит “не делайте write path хаотичным”.

---

## 16. Edge cases, которые стоит учитывать сразу

### 16.1 Party leader offline handoff

Вам важно, чтобы migration/history модель не ломала join order.

Поэтому для `party_members` поле `join_order` или эквивалент нужно хранить явно.
Иначе после перезаходов трудно стабильно выбирать “старейшего” участника.

### 16.2 Guild membership limit changed downward

Вы уже зафиксировали правило:
- если лимит был 50, стал 40, а в гильдии 45 — никого не удаляем.

Это значит, что в БД размер guild — не “инвариант схемы”, а предметная проверка на новых операциях.

То же касается:
- max guilds per player
- max parties size
- max created guilds per player

### 16.3 Recovery / rebind key

Не пытайтесь “восстанавливать” identity просто по нику.

Нужны таблицы и сценарии для:
- player
- active/inactive keys
- sessions
- revoke on rebind

### 16.4 Rename nick

Ник не должен быть primary identity.

Это влияет и на SQL модель, и на индексы, и на uniqueness rules.

### 16.5 Offline player remains in party

Значит `party_members` не должны удаляться по disconnect.

Disconnect — runtime state.
Membership — persistent domain state.

---

## 17. Мой рекомендуемый workflow команды

### Каждый schema change

1. Создать новую migration:
   ```bash
   migrondi create add_something
   ```
2. Написать `up.sql` и `down.sql`.
3. Применить миграции к локальной dev БД:
   ```bash
   migrondi up
   ```
4. Перегенерировать SqlHydra.
5. Обновить F# код и тесты.
6. Прогнать тесты.

### Перед релизом

1. Backup SQLite файла.
2. Прогнать `migrondi up` на staging copy.
3. Поднять сервер на этой копии.
4. Проверить startup и основные сценарии.
5. Только потом катить на production.

---

## 18. Итоговые рекомендации

### Что я считаю лучшей схемой для проекта

- **SQLite** как основная БД
- **Microsoft.Data.Sqlite** как провайдер
- **Migrondi** как SQL migration runner
- **SqlHydra** как generated typed query layer
- **Fling** только для сложных агрегатов

### Что я рекомендую закрепить как правила команды

1. Схема меняется только через SQL-миграции.
2. Generated SqlHydra code не редактируется вручную.
3. Простые операции делаются через SqlHydra.
4. Агрегаты делаются через Fling поверх простых репозиториев.
5. Подключения короткоживущие, pooling включен.
6. `Foreign Keys=True`, `WAL`, короткие транзакции.
7. `Cache=Shared` не использовать в обычном режиме.
8. Chat history и volatile state не писать в SQLite на старте.
9. Старые миграции не переписывать.
10. Перед релизной миграцией всегда делать backup БД.

### Мой практический вердикт

Для вашего проекта этот стек выглядит очень удачно:

- достаточно простой;
- SQL-first;
- без тяжелого ORM;
- с хорошей типизацией в F#;
- с понятной дорожкой роста.

Если через какое-то время проект упрется в ограничения SQLite, то этот стек все равно оставляет вам нормальный путь миграции на серверную СУБД, потому что:

- schema evolution уже живет в SQL;
- доменная логика не завязана на один ORM;
- SqlHydra поддерживает не только SQLite.

