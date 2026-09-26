# SQL-модели v1 для MMO Chat

> Исторический SQL-эскиз, не рабочие миграции Dreamsleeve.
> SQLite/LiteDB и окончательное отображение uint64 в persistence ещё требуют решения.
> [Актуальный домен и границы MVP](../CurrentStateRu.md).

## Что заменить до использования

ID UUID/ULID/TEXT не соответствуют текущим серверным uint64. Уникальный
normalized_nickname заменяется уникальным каноническим Username; DisplayName
неуникален. Не сужать uint64 до signed INTEGER без отдельного правила диапазона.
Identity, auth и сессии ещё не реализованы, а партии/гильдии/social/admin — позже.
Сами SQL-файлы ниже сохранены как исторические примеры и не исполняются тестами
или сервером. Первые реальные миграции должны исходить из нового контракта.

В этой папке лежит **исторический набор SQLite-моделей** под проект.

## Что входит

- `schema_v1.sql` — полный консолидированный DDL.
- `migrations/001_...005_...sql` — тот же набор, но разложенный по шагам для удобного переноса в Migrondi.
- Этот набор рассчитан на:
  - `SqlHydra` как typed query layer,
  - `Fling` как aggregate loader/saver поверх простых CRUD-функций,
  - `Migrondi` как runner SQL-миграций.

## Главные решения

### 1. Чатовые сообщения и PM-история **не сохраняются в БД**
Это сделано **осознанно**. По вашим доменным требованиям:
- клиент хранит локальный хвост сообщений,
- сервер держит короткий хвост в памяти/кеше,
- полноценное долговременное хранение сообщений не является обязательным v1-сценарием.

Поэтому в SQL-модели **нет** таблиц для:
- global / guild / party / direct chat history,
- системного realtime feed,
- volatile player state.

### 2. Серверные лимиты и transport-настройки **не хранятся в SQL**
Настройки вроде:
- max guild members,
- max party members,
- max guilds per player,
- timing для auto-promote лидера,
- ENet / logging / file paths

должны жить в TOML-конфигах, а не в relational state.

### 3. Identity отделена от display name
`players` отвечает за устойчивую identity игрока.  
`player_identities` хранит публичные ключи и позволяет делать:
- rebind ключа,
- key rotation,
- аудит восстановления доступа.

### 4. Multi-guild membership поддерживается прямо схемой
Один игрок может состоять в нескольких гильдиях, потому что `guild_memberships` — это обычная связь many-to-many.

### 5. Party membership — только одна активная пати на игрока
Это зафиксировано через `UNIQUE (player_id)` в `party_members`.

### 6. Нормализованные имена считаются приложением
Поля `normalized_nickname`, `normalized_name`, `normalized_term` должны заполняться **на стороне приложения**.
Не надо рассчитывать их через SQLite `lower()`:
- это слабее для Unicode,
- хуже контролируется,
- труднее унифицировать между F# и C++.

## Таблицы по смыслу

### Identity и сессии
- `players`
- `player_identities`
- `player_identity_rebinds`
- `player_server_roles`
- `player_sessions`

### Social
- `friend_requests`
- `friendships`
- `player_blocks`

### Party
- `parties`
- `party_members`
- `party_invites`

### Guild
- `guilds`
- `guild_memberships`
- `guild_invites`

### Moderation / admin content
- `global_sanctions`
- `text_filter_terms`
- `scheduled_announcements`
- `audit_events`

## Что не enforced на уровне БД, а проверяется приложением

Это важный пункт. Не все доменные инварианты надо пихать в SQL.

### Проверяется приложением
- текущий лидер пати действительно состоит в `party_members`;
- оффлайн-лидер через `N` времени передается старейшему участнику;
- офицер не может мутить/кикать офицера или guild master;
- guild master единственный и корректно назначается при создании гильдии;
- игрок не превышает лимит по числу гильдий;
- при снижении лимитов старые данные не режутся, а блокируются только новые действия;
- blacklist режет PM/invites, но не скрывает человека из online list;
- text filter прогоняется через общий сервис до доставки сообщения;
- системные сообщения и server-side chat retention живут вне SQL.

### Частично enforced SQL-слоем
- у игрока только одна активная identity;
- у гильдии только один guild master;
- у игрока только одна активная party membership;
- у пары нет второй активной pending friend request в том же направлении;
- в одной гильдии нет второй активной pending invite для того же игрока;
- в одной пати нет второй активной pending invite для того же игрока.

## Как использовать с SqlHydra

Рекомендуемый workflow:

1. Применить SQL-миграции к SQLite-файлу.
2. После этого запускать `SqlHydra.Cli`, чтобы:
   - прочитать текущую схему,
   - сгенерировать F#-типы и query context.
3. Поверх generated-слоя писать:
   - простые CRUD и list queries,
   - транзакционные application-services.
4. `Fling` подключать **не везде**, а только там, где реально есть aggregate:
   - Guild + memberships,
   - Player + identities + roles,
   - Party + members,
   - возможно admin aggregate.

## Как использовать с Migrondi

В этой папке `migrations/` файлы названы так, чтобы их было удобно:
- либо копировать в директорию миграций проекта,
- либо адаптировать под ваш naming convention.

Практически можно идти так:

- `001_core_identity.sql`
- `002_social.sql`
- `003_party_guild.sql`
- `004_admin_content.sql`
- `005_indexes.sql`

То есть это уже **готовая стартовая разбивка** для последовательного применения.

## Рекомендации по SQLite runtime

- Включать `Foreign Keys=True` в connection string.
- Использовать WAL.
- Не шарить одно `SqliteConnection` между потоками.
- Работать по схеме “новое подключение на операцию/транзакцию”.
- Полагаться на pooling провайдера, а не на ручной глобальный singleton connection.
- Держать транзакции короткими.

## Что я бы добавил позже, а не сейчас

Не включено в v1, но может понадобиться позже:

- таблицы для persistent chat history;
- unread counters / inbox-like state;
- server-side cached snapshots;
- explicit guild member action log вместо части `audit_events`;
- runtime-editable key/value server settings;
- отдельные query-optimized materialized views или summary tables.

## Быстрый совет по ID и времени

Для v1:
- IDs: `TEXT` UUID/ULID;
- время: `INTEGER` Unix milliseconds UTC.

Это не самое компактное решение, но:
- очень простое,
- прозрачное для SQLite,
- хорошо совпадает с F# и C++,
- легко логируется и отлаживается.

## Быстрый совет по soft delete

В `players` есть `is_deleted`, чтобы:
- не ломать исторические связи,
- при желании освобождать nickname только для неактивных/удаленных игроков.

Если soft delete вам пока не нужен, можно оставить его как резерв на будущее.
