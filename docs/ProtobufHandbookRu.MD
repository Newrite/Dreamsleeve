# Protocol Buffers Handbook для MMO Chat проекта

> Статус на 26.09.2026: справочник и примеры, не перечень готовых подсистем.
> [Текущие решения](CurrentStateRu.md) определяют MVP и имеют приоритет над
> ранними примерами ниже. Тесты находятся в `tests`, общий запуск:
> `python Scripts/run_tests.py`.

## Реальный контракт проекта

Source of truth — `Protocol/*.proto`: `network.proto` с DisconnectReason и
`chat.proto` для первого среза сессии/чата. Генерацию C++ и C# выполняет `Scripts/generate_protocol.py`.
`Dreamsleeve.Protocol.Dotnet` содержит generated C# и Google.Protobuf, а
Dreamsleeve.Protocol.Native — generated C++ и модульную границу. Рецепт
Grpc.Tools ниже описывает альтернативную настройку, не текущую сборку.

Вход, начальные профили/история, глобальный чат, онлайн и отказы описаны в
[актуальном контракте](../Protocol/README.ru.md); реализованы C++/F# codec.
Игровые показания и пагинация ещё не спроектированы. Будущее расширение должно
отражать CharacterName, FormKey пространства и scalar/resource Actor Values.
Локальные generation/revision/round модели не являются wire sequence.

Примеры из `40059c5:docs/ProtoExamples` — исторический проект широкого
социального протокола: не копировать его как готовую схему MVP. Там нет пути
публикации клиентом своего состояния, а представления профиля/пространства/статов
не соответствуют принятому домену.

## 1. Назначение документа

Этот документ объясняет, как использовать **Protocol Buffers (protobuf)** в проекте MMO Chat со стеком:

- **F# / .NET** на сервере
- **C++** на клиенте
- **Google.Protobuf** как .NET runtime
- **Grpc.Tools** как инструмент генерации C#-кода из `.proto`
- **оригинальный Google protobuf toolchain** как источник истины по схеме и генерации

Документ **не** уходит глубоко в доменные SQL-модели, сетевую транспортную реализацию ENet и не описывает генерацию gRPC-сервисов. Здесь фокус только на protobuf-схеме, ее организации, генерации кода и практических правилах работы.

---

## 2. Что такое Protocol Buffers

Protocol Buffers — это схема-ориентированный бинарный формат сериализации. Идея простая:

1. Вы описываете сообщения в `.proto` файлах.
2. Генератор `protoc` создает типы для нужных языков.
3. Код на сервере и клиенте работает уже с generated classes/struct-like API.
4. На проводе ходят компактные бинарные сообщения.

Для вашего проекта protobuf нужен как:

- единый язык описания transport-сообщений;
- база для межъязыковой совместимости F# ↔ C++;
- способ эволюции схемы без ручной синхронизации бинарного формата.

---

## 3. Из чего состоит protobuf-стек в вашем проекте

### На .NET / F# стороне

#### `Google.Protobuf`
Runtime-библиотека для работы с generated C#-типами:

- сериализация / десериализация;
- `IMessage<T>` API;
- `RepeatedField<T>`, `MapField<TKey, TValue>`;
- parser / merge / size calculation.

#### `Grpc.Tools`
Инструментальная часть для build-time генерации C#-кода из `.proto` внутри `.csproj`.

Практически это значит:

- `.proto` лежат у вас в репозитории;
- `Grpc.Tools` вызывает `protoc` при сборке;
- generated `.cs` появляются автоматически;
- F#-проект просто ссылается на отдельную C# protocol assembly.

### На C++ стороне

Нужны:

- `protoc`
- protobuf runtime / headers для C++
- generation через `--cpp_out`

То есть на C++ стороне protobuf — это классический workflow:

- `.proto` → `*.pb.h` + `*.pb.cc`
- линкуемся с `libprotobuf`

---

## 4. Почему для F# лучше отдельный C# protocol project

Официальный protobuf workflow отлично поддерживает **C#** и **C++**. Для F# наиболее спокойный путь — **не пытаться генерировать F#-типы напрямую**, а держать отдельный маленький C#-проект, который:

- содержит только generated protobuf types;
- почти не содержит ручной бизнес-логики;
- referenced из F#-сервера.

Это хороший компромисс:

- protobuf остается официальным и стандартным;
- F# работает с обычными .NET-типами;
- не появляется самодельный F# serializer / parser / bindgen.

Рекомендуемая структура:

```text
/src
  /Protocol
    Game.Protocol.csproj
    /Proto
      common/v1/common.proto
      transport/v1/envelope.proto
      session/v1/session.proto
      chat/v1/chat.proto
      party/v1/party.proto
      guild/v1/guild.proto
      social/v1/social.proto
      moderation/v1/moderation.proto
      admin/v1/admin.proto
      state/v1/state.proto

  /Server
    Game.Server.fsproj

  /Admin
    Game.Admin.fsproj
```

---

## 5. Что выбрать: `proto3` или editions

У protobuf сейчас есть и **editions**, и привычный **proto3**.

Для этого проекта рекомендуется **`syntax = "proto3";`** по причинам практики:

- проще стартовать;
- больше готовых примеров;
- меньше cognitive overhead для первой интеграции;
- легче обсуждать схему с командой.

То есть здесь ставка не на самую новую возможность protobuf, а на **простоту и предсказуемость**.

---

## 6. Базовый синтаксис `.proto`

Простейший пример:

```proto
syntax = "proto3";

package mmochat.chat.v1;

option csharp_namespace = "Game.Protocol.Chat.V1";

message ChatMessage {
  uint64 message_id = 1;
  uint64 sender_player_id = 2;
  string sender_name = 3;
  string text = 4;
  int64 created_at_unix_ms = 5;
}
```

Здесь:

- `syntax = "proto3";` — версия языка схемы;
- `package` — protobuf namespace / package name;
- `option csharp_namespace` — явный namespace для generated C#-кода;
- `message` — описание структуры сообщения;
- у каждого поля есть **тип**, **имя** и **номер поля**.

Номер поля — очень важен. Именно он участвует в wire-формате.

---

## 7. Основные элементы синтаксиса

### 7.1. Скалярные типы

Чаще всего вам понадобятся:

- `bool`
- `int32`, `int64`
- `uint32`, `uint64`
- `string`
- `bytes`
- `float`, `double`

Для проекта MMO Chat базовые рекомендации такие:

- **ID сущностей**: `uint64`
- **короткие enum-like статусы**: `enum`
- **текст**: `string`
- **opaque token / сырой бинарный payload**: `bytes`
- **время**: либо `int64 unix_ms`, либо `google.protobuf.Timestamp`

### 7.2. `enum`

```proto
enum ChatChannelKind {
  CHAT_CHANNEL_KIND_UNSPECIFIED = 0;
  CHAT_CHANNEL_KIND_GLOBAL = 1;
  CHAT_CHANNEL_KIND_PARTY = 2;
  CHAT_CHANNEL_KIND_GUILD = 3;
  CHAT_CHANNEL_KIND_DIRECT = 4;
  CHAT_CHANNEL_KIND_SYSTEM = 5;
}
```

Правило: значение `0` оставляйте как `UNSPECIFIED` / `UNKNOWN`.

### 7.3. `repeated`

```proto
message PartySnapshot {
  repeated uint64 member_player_ids = 1;
}
```

Это список.

### 7.4. `map`

```proto
message ServerSettings {
  map<string, string> values = 1;
}
```

Для проекта я бы использовал `map` умеренно. Для большинства transport-сообщений лучше явные поля.

### 7.5. `oneof`

Это очень важный механизм для вашего проекта.

```proto
message ClientPacket {
  uint64 client_seq = 1;

  oneof payload {
    SendChatMessage send_chat_message = 10;
    InviteToParty invite_to_party = 11;
    ResumeSession resume_session = 12;
  }
}
```

`oneof` означает: **в сообщении активен только один из вариантов**.

Для transport envelope это почти идеальный инструмент.

### 7.6. `optional`

В `proto3` можно использовать `optional`, если вам нужна **presence semantics**, а не просто default value.

```proto
message UpdatePlayerProfile {
  optional string nickname = 1;
}
```

Это полезно в командах `patch/update`, где важно различать:

- поле не прислали вообще;
- поле прислали пустым значением.

---

## 8. Как protobuf работает на практике

### 8.1. Source of truth

Source of truth — это `.proto` файлы.

Не generated `.cs`, не generated `.pb.h`, а именно:

- `chat.proto`
- `guild.proto`
- `state.proto`
- и т.д.

### 8.2. Code generation

Из этих файлов строятся:

- C# классы для .NET/F#
- C++ классы для клиента

### 8.3. Runtime

Сгенерированные классы умеют:

- сериализоваться в бинарный protobuf;
- десериализоваться из бинарного protobuf;
- копироваться / merge'иться;
- работать с repeated / map / oneof.

---

## 9. Базовая настройка `.NET` проекта с `Google.Protobuf + Grpc.Tools`

### 9.1. Пакеты

Для protocol-проекта:

```bash
dotnet add package Google.Protobuf
dotnet add package Grpc.Tools
```

### 9.2. Рекомендуемый `Game.Protocol.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Google.Protobuf" Version="3.*" />
    <PackageReference Include="Grpc.Tools" Version="2.*" PrivateAssets="All" />
  </ItemGroup>

  <ItemGroup>
    <Protobuf Include="Proto\**\*.proto"
              ProtoRoot="Proto"
              GrpcServices="None" />
  </ItemGroup>
</Project>
```

### Пояснения

- `Google.Protobuf` нужен в runtime;
- `Grpc.Tools` нужен только на build-time;
- `PrivateAssets="All"` не протаскивает tooling дальше по dependency graph;
- `GrpcServices="None"` важно, потому что вы сейчас не генерируете gRPC stubs, а только protobuf messages;
- `ProtoRoot="Proto"` помогает нормальной работе с импортами.

### 9.3. Пример импорта `.proto`

Если в `chat.proto` импортируется `common.proto`, структура может быть такой:

```proto
import "common/v1/common.proto";
```

И это будет стабильно работать, если `ProtoRoot` указывает на корень каталога `Proto`.

---

## 10. Как использовать generated protobuf types из F#

Предположим, что generated C# namespace:

```proto
option csharp_namespace = "Game.Protocol.Chat.V1";
```

Тогда в F#:

```fsharp
open Game.Protocol.Chat.V1

let msg = ChatMessage()
msg.MessageId <- 42UL
msg.SenderPlayerId <- 1001UL
msg.SenderName <- "Alice"
msg.Text <- "hello"
msg.CreatedAtUnixMs <- 1710000000000L

let bytes = msg.ToByteArray()
let parsed = ChatMessage.Parser.ParseFrom(bytes)
```

Практический совет:

- generated protobuf types держите на границе transport layer;
- внутри домена маппите их в свои F# record/DU модели;
- не тяните protobuf-классы глубоко в бизнес-логику.

То есть transport-model и domain-model — разные слои.

---

## 11. Как сгенерировать биндинги для C++

### 11.1. Базовая команда

Если у вас есть `Proto/chat/v1/chat.proto`, то базовая генерация выглядит так:

```bash
protoc \
  --proto_path=Proto \
  --cpp_out=generated/cpp \
  Proto/chat/v1/chat.proto
```

Если файлов много:

```bash
protoc \
  --proto_path=Proto \
  --cpp_out=generated/cpp \
  Proto/common/v1/common.proto \
  Proto/transport/v1/envelope.proto \
  Proto/session/v1/session.proto \
  Proto/chat/v1/chat.proto \
  Proto/party/v1/party.proto \
  Proto/guild/v1/guild.proto \
  Proto/social/v1/social.proto \
  Proto/moderation/v1/moderation.proto \
  Proto/admin/v1/admin.proto \
  Proto/state/v1/state.proto
```

Результат:

- `generated/cpp/.../*.pb.h`
- `generated/cpp/.../*.pb.cc`

### 11.2. Что потом подключать

В C++ коде:

```cpp
#include "chat/v1/chat.pb.h"
```

И проект должен линковаться с protobuf runtime.

### 11.3. Базовый пример использования в C++

```cpp
mmochat::chat::v1::ChatMessage msg;
msg.set_message_id(42);
msg.set_sender_player_id(1001);
msg.set_sender_name("Alice");
msg.set_text("hello");
msg.set_created_at_unix_ms(1710000000000LL);

std::string buffer;
msg.SerializeToString(&buffer);

mmochat::chat::v1::ChatMessage parsed;
parsed.ParseFromString(buffer);
```

---

## 12. Базовая идея transport envelope для проекта

Для MMO Chat я рекомендую **не гонять на проводе десятки разрозненных top-level сообщений без общего контейнера**, а использовать envelope pattern.

### 12.1. Клиент → сервер

```proto
message ClientPacket {
  uint64 client_seq = 1;

  oneof payload {
    session.v1.AuthHello auth_hello = 10;
    session.v1.ResumeSession resume_session = 11;

    chat.v1.SendChatMessage send_chat_message = 20;

    party.v1.CreateParty create_party = 30;
    party.v1.InviteToParty invite_to_party = 31;
    party.v1.LeaveParty leave_party = 32;

    guild.v1.CreateGuild create_guild = 40;
    guild.v1.InviteToGuild invite_to_guild = 41;

    social.v1.AddFriend add_friend = 50;
    social.v1.AddBlacklist add_blacklist = 51;
  }
}
```

### 12.2. Сервер → клиент

```proto
message ServerPacket {
  uint64 server_seq = 1;

  oneof payload {
    session.v1.AuthChallenge auth_challenge = 10;
    session.v1.AuthOk auth_ok = 11;
    session.v1.ResumeOk resume_ok = 12;
    session.v1.ProtocolError protocol_error = 13;

    chat.v1.ChatMessageEvent chat_message_event = 20;
    chat.v1.ChatHistoryBatch chat_history_batch = 21;

    party.v1.PartyEvent party_event = 30;
    guild.v1.GuildEvent guild_event = 40;
    social.v1.SocialEvent social_event = 50;
    moderation.v1.ModerationEvent moderation_event = 60;
    state.v1.PlayerStateBatch player_state_batch = 70;
  }
}
```

### Почему это хорошо

- один входной decoder;
- один выходной encoder;
- удобно логировать packet kind;
- удобно делать versioned expansion;
- удобно писать routing/dispatch layer.

---

## 13. Рекомендуемая структура `.proto` файлов

### 13.1. `common/v1/common.proto`
Общие enum'ы и типы:

- `PlayerRef`
- `GuildRef`
- `PartyRef`
- `DirectDialogRef`
- `ChatChannelKind`
- `ErrorCode`
- `VisibilityFlags`

### 13.2. `transport/v1/envelope.proto`
Только transport envelope:

- `ClientPacket`
- `ServerPacket`

### 13.3. `session/v1/session.proto`
Все, что связано с identity/session/auth:

- `AuthHello`
- `AuthChallenge`
- `AuthProof`
- `AuthOk`
- `ResumeSession`
- `ResumeOk`
- `SessionInvalidated`
- `ProtocolError`

### 13.4. `chat/v1/chat.proto`

- `SendChatMessage`
- `ChatMessageEvent`
- `FetchChatHistory`
- `ChatHistoryBatch`
- `SystemAnnouncementEvent`
- `ChatCursor`

### 13.5. `party/v1/party.proto`

- `CreateParty`
- `InviteToParty`
- `RespondPartyInvite`
- `LeaveParty`
- `DisbandParty`
- `TransferPartyLeadership`
- `PartySnapshot`
- `PartyEvent`

### 13.6. `guild/v1/guild.proto`

- `CreateGuild`
- `InviteToGuild`
- `RespondGuildInvite`
- `LeaveGuild`
- `DisbandGuild`
- `GuildMuteMember`
- `GuildKickMember`
- `GuildChangeRole`
- `GuildSnapshot`
- `GuildEvent`

### 13.7. `social/v1/social.proto`

- `AddFriend`
- `RemoveFriend`
- `AddBlacklist`
- `RemoveBlacklist`
- `DirectMessageSend`
- `DirectMessageEvent`
- `SocialSnapshot`
- `SocialEvent`

### 13.8. `moderation/v1/moderation.proto`

- `GlobalMutePlayer`
- `GlobalBanPlayer`
- `RevokeGlobalMute`
- `RevokeGlobalBan`
- `ModerationEvent`

### 13.9. `admin/v1/admin.proto`
Для админки и административных команд:

- `AdminListPlayers`
- `AdminPlayerSummary`
- `AdminRebindPlayerKey`
- `AdminSetServerConfig`
- `AdminAnnouncement`

### 13.10. `state/v1/state.proto`
Для fast-changing state feed:

- `PlayerStateSnapshot`
- `PlayerStateBatch`
- `LocationRef`
- `CombatFlags`

---

## 14. Конкретные предложения по схеме для проекта

### 14.1. Версионирование через package path

Используйте versioning в package path:

```proto
package mmochat.chat.v1;
```

А не просто:

```proto
package mmochat.chat;
```

Это упростит большие миграции схемы, если когда-нибудь придется делать несовместимый `v2`.

### 14.2. Явный `csharp_namespace`

Всегда задавайте:

```proto
option csharp_namespace = "Game.Protocol.Chat.V1";
```

Иначе namespace может быть неудобным или нестабильным для .NET.

### 14.3. Отдельные команды, события и снапшоты

Не смешивайте всё в сообщения вида `DoThing` / `ThingResponse` без ясной семантики.

Лучше мыслить так:

- **Command** — клиент просит действие
- **Event** — сервер сообщает, что произошло
- **Snapshot** — сервер отдает текущее состояние
- **Batch** — сервер отдает пачку элементов

### 14.4. Курсоры истории отдельным типом

Для чата и reconnect лучше не таскать просто `uint64 last_seen_seq` в случайных местах. Лучше сделать отдельный тип:

```proto
message ChatCursor {
  uint64 last_seen_server_seq = 1;
}
```

И аналогично для других лент, если понадобится.

### 14.5. Серверные настройки как явная схема

Не храните transport settings snapshot как `map<string, string>` по умолчанию.

Лучше:

```proto
message ServerLimits {
  uint32 max_party_members = 1;
  uint32 max_guild_members = 2;
  uint32 max_guilds_per_player = 3;
  uint32 max_guilds_created_per_player = 4;
  bool streamer_filter_enabled = 5;
}
```

Такой протокол надежнее и проще развивать.

### 14.6. Быстро меняющееся состояние — отдельные сообщения

Для `HP`, `location`, `zone`, `volatile flags` не смешивайте это с guild/party/chat payload'ами. Держите их в `state.v1`.

---

## 15. Пример минимального набора `.proto`

### `common/v1/common.proto`

```proto
syntax = "proto3";

package mmochat.common.v1;
option csharp_namespace = "Game.Protocol.Common.V1";

enum ChatChannelKind {
  CHAT_CHANNEL_KIND_UNSPECIFIED = 0;
  CHAT_CHANNEL_KIND_GLOBAL = 1;
  CHAT_CHANNEL_KIND_PARTY = 2;
  CHAT_CHANNEL_KIND_GUILD = 3;
  CHAT_CHANNEL_KIND_DIRECT = 4;
  CHAT_CHANNEL_KIND_SYSTEM = 5;
}

enum ErrorCode {
  ERROR_CODE_UNSPECIFIED = 0;
  ERROR_CODE_UNAUTHORIZED = 1;
  ERROR_CODE_RATE_LIMITED = 2;
  ERROR_CODE_NOT_FOUND = 3;
  ERROR_CODE_INVALID_STATE = 4;
  ERROR_CODE_FORBIDDEN = 5;
}

message PlayerRef {
  uint64 player_id = 1;
  string nickname = 2;
}
```

### `chat/v1/chat.proto`

```proto
syntax = "proto3";

package mmochat.chat.v1;
option csharp_namespace = "Game.Protocol.Chat.V1";

import "common/v1/common.proto";

message ChatCursor {
  uint64 last_seen_server_seq = 1;
}

message SendChatMessage {
  mmochat.common.v1.ChatChannelKind channel_kind = 1;
  optional uint64 target_id = 2;
  string text = 3;
}

message ChatMessageEvent {
  uint64 server_seq = 1;
  uint64 message_id = 2;
  mmochat.common.v1.ChatChannelKind channel_kind = 3;
  optional uint64 target_id = 4;
  mmochat.common.v1.PlayerRef sender = 5;
  string text = 6;
  int64 created_at_unix_ms = 7;
}

message FetchChatHistory {
  mmochat.common.v1.ChatChannelKind channel_kind = 1;
  optional uint64 target_id = 2;
  ChatCursor cursor = 3;
  uint32 limit = 4;
}

message ChatHistoryBatch {
  repeated ChatMessageEvent messages = 1;
}
```

---

## 16. Как думать о нумерации полей

Самое важное правило protobuf:

### НИКОГДА
- не меняйте номер существующего поля;
- не переиспользуйте старый номер для нового смысла;
- не переиспользуйте старое имя без явного решения.

### Если поле удаляется
Используйте `reserved`:

```proto
message GuildMember {
  reserved 5;
  reserved "legacy_status";

  uint64 player_id = 1;
  string nickname = 2;
}
```

Это спасает от случайного повторного использования.

### Практический совет по нумерации

Оставляйте зазоры:

- `1..9` — базовые поля
- `10..19` — дополнительные поля
- `20+` — future expansion

Это не обязательно, но удобно.

---

## 17. Как думать о совместимости схемы

### Безопасные изменения

Обычно безопасны:

- добавить новое поле с новым номером;
- добавить новое значение enum;
- добавить новый вариант в `oneof`;
- перестать использовать поле, но оставить номер reserved;
- добавить новый message type.

### Опасные изменения

Опасны:

- менять номер поля;
- менять тип поля без продуманной миграции;
- менять смысл существующего enum значения;
- переводить поле в совсем другой semantic meaning;
- выкидывать поле и потом использовать тот же номер заново.

---

## 18. Что делать с временем

Есть два рабочих варианта:

### Вариант A — `int64 unix_ms`
Плюсы:

- простота;
- очевидность;
- легко использовать на F# и C++;
- не требует дополнительных well-known types в схеме.

### Вариант B — `google.protobuf.Timestamp`
Плюсы:

- стандартный тип protobuf;
- понятная семантика времени.

Для этого проекта я бы на старте выбрал **`int64 unix_ms`**, если у вас нет сильной потребности в строго стандартном временном типе.

---

## 19. Что делать с ID

Для доменных сущностей проекта я рекомендую:

- `player_id: uint64`
- `guild_id: uint64`
- `party_id: uint64`
- `message_id: uint64`
- `dialog_id: uint64`

А для opaque identity/session/recovery токенов:

- `string` — если это человеко-переносимый или текстовый токен;
- `bytes` — если это чисто бинарный токен.

Один стиль на весь проект лучше смеси `int64/string/guid` без правил.

---

## 20. Common mistakes

### 20.1. Один гигантский `game.proto`
Плохая идея.

Минусы:

- тяжело навигировать;
- больше merge conflicts;
- неудобно ревьюить;
- растет связанность схем.

### 20.2. Generated code как hand-edited code
Generated файлы не редактируются вручную.

Меняется только `.proto`.

### 20.3. Протаскивание protobuf-классов в домен
Это быстро делает доменную модель зависимой от transport concerns.

Правильнее:

- transport layer работает с protobuf;
- domain layer работает со своими record/DU/entity моделями.

### 20.4. Использование `string` там, где нужен enum
Например `channel_kind = "global"` — хуже, чем enum.

### 20.5. Использование `bool` там, где вероятно появятся новые состояния
Например вместо:

```proto
bool is_muted = 1;
```

часто лучше:

```proto
enum GuildChatPermission {
  GUILD_CHAT_PERMISSION_UNSPECIFIED = 0;
  GUILD_CHAT_PERMISSION_READ_WRITE = 1;
  GUILD_CHAT_PERMISSION_READ_ONLY = 2;
}
```

### 20.6. Слишком ранняя “универсальность”
Не надо делать гигантские generic envelopes вроде:

- `entity_type`
- `operation_kind`
- `map<string, bytes> fields`

Для вашего проекта лучше **явные typed messages**.

### 20.7. Отсутствие `reserved` при удалении полей
Это классический способ словить несовместимость через полгода.

---

## 21. Edge cases, которые стоит учитывать

### 21.1. `oneof` и эволюция схемы
`oneof` очень удобен, но не надо бездумно смешивать туда все подряд.

Хороший `oneof`:

- envelope payload;
- mutually exclusive operation result.

Плохой `oneof`:

- попытка сделать весь мир через один “mega oneof”, который знает о каждой сущности проекта.

### 21.2. `optional` и patch semantics
Если хотите сделать `UpdateGuildSettings`, где поля могут быть:

- не присланы;
- присланы пустыми;
- присланы со значением;

нужен именно `optional`, а не просто поле со значением по умолчанию.

### 21.3. Большие repeated batches
`repeated` удобно, но не превращайте один пакет в гигантский dump на тысячи объектов.

Лучше:

- лимиты на batch size;
- pagination/cursors;
- history chunks.

### 21.4. Enum expansion
Новые enum values могут появиться позже. Не завязывайтесь на то, что список статусов “навсегда закрыт”.

---

## 22. Минимальный процесс работы с protobuf в команде

### Шаг 1
Изменяем `.proto`

### Шаг 2
Ревьюим именно schema diff

### Шаг 3
Пересобираем C# protocol project

### Шаг 4
Перегенерируем C++ биндинги

### Шаг 5
Обновляем transport mapping layer на сервере и клиенте

### Шаг 6
Добавляем/обновляем тесты совместимости

---

## 23. Что тестировать вокруг protobuf

### На .NET стороне

- roundtrip serialize → parse;
- mapping protobuf → domain;
- mapping domain → protobuf;
- backward-compatible parsing старых payload'ов, если сохраняете бинарные fixtures;
- envelope dispatch по `oneof`.

### На C++ стороне

- serialize/parse roundtrip;
- interop с payload'ами от .NET;
- parsing malformed/empty buffers;
- корректность repeated / optional / oneof.

---

## 24. Практический итог для вашего проекта

### Что рекомендовано

- `syntax = "proto3";`
- отдельный C# `Game.Protocol` проект
- `.proto` как source of truth
- generated C# code используется из F#
- generated C++ code используется на клиенте
- структура схем по доменам: `common/session/chat/party/guild/social/moderation/admin/state`
- transport envelopes через `ClientPacket` / `ServerPacket` + `oneof payload`
- явный `csharp_namespace`
- `uint64` для доменных ID
- `reserved` при удалении полей
- protobuf types не тащить глубоко в доменную логику

### Что не рекомендовано

- один гигантский `.proto`
- ручное редактирование generated code
- самодельный bindgen вместо стандартного `protoc`
- использование protobuf runtime без codegen
- переиспользование field numbers
- хранение всей семантики протокола в stringly-typed map'ах

---

## 25. Рекомендуемый следующий документ

После этого handbook логичный следующий документ — **конкретная protobuf-схема проекта**, уже без вводной теории:

- полный каталог `.proto` файлов;
- точные message definitions;
- envelope contracts;
- правила versioning;
- mapping transport ↔ domain;
- guidelines по тестовым binary fixtures.

