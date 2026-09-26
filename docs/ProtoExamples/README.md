# MMO Chat Proto v1

> Исторический эскиз широкого социального протокола, не рабочая схема MVP.
> Актуальные `.proto` находятся в [Protocol](../../Protocol/network.proto),
> решения — в [CurrentStateRu.md](../CurrentStateRu.md).

## Что устарело

- `nickname` заменён моделью Username / DisplayName / CharacterName.
- Адресация ChatAddress по channel_type/scope_id не отражает принятый ChatChannelId.
- Пространство задаётся FormKey WRLD/CELL, а Actor Values — словарём scalar/resource.
- Нужна публикация собственного состояния клиентом; её нет в старом ClientPacket.
- Auth/resume/epoch ещё требуют решения на уровне прикладной сессии.
- Партии, гильдии, social и admin выходят за текущий MVP.

Файлы сохранены для справки и не включены в генерацию проекта.

Исходный набор примеров `.proto` для будущих социальных функций.

## Состав

- `common.proto` — общие enum/message types
- `session.proto` — identity, auth, resume, initial state
- `chat.proto` — чат, PM, история
- `party.proto` — party-команды и события
- `guild.proto` — guild-команды и события
- `social.proto` — friends, blocks, directory, dialogs
- `moderation.proto` — global mute / ban
- `admin.proto` — настройки сервера, анонсы, admin actions
- `state.proto` — volatile state feed
- `transport.proto` — корневые `ClientPacket` / `ServerPacket`

## Принципы набора

- `syntax = "proto3"`
- единый `package mmochat.v1`
- единый `csharp_namespace = "MmoChat.Protocol.V1"`
- `All chat` не является серверным каналом и не описан в protobuf как отдельный stream
- volatile state выделен в `state.proto`, но для v1 все еще доступен через `ServerPacket`

## Общие принципы (для новой рабочей схемы)

На старте держите source of truth только в `.proto`.
Не редактируйте руками generated C# / C++ файлы.

## Генерация для C++

Пример:

```bash
protoc -I=./mmo_chat_proto_v1 \
  --cpp_out=./generated/cpp \
  ./mmo_chat_proto_v1/common.proto \
  ./mmo_chat_proto_v1/session.proto \
  ./mmo_chat_proto_v1/chat.proto \
  ./mmo_chat_proto_v1/party.proto \
  ./mmo_chat_proto_v1/guild.proto \
  ./mmo_chat_proto_v1/social.proto \
  ./mmo_chat_proto_v1/moderation.proto \
  ./mmo_chat_proto_v1/admin.proto \
  ./mmo_chat_proto_v1/state.proto \
  ./mmo_chat_proto_v1/transport.proto
```

## Генерация для .NET / F#

Подключите `.proto` в отдельный protocol-проект через `Grpc.Tools`.
F#-сервер использует generated C#-типы из этого проекта.
