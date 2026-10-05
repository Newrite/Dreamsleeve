# Документация Dreamsleeve

Начать с [CurrentStateRu.md](CurrentStateRu.md): что реализовано, принятые решения и открытые
вопросы. Владельцу сервера — сразу [DeploymentRu.md](DeploymentRu.md).

## Развёртывание и эксплуатация

| Документ | Назначение |
|---|---|
| [Развёртывание сервера](DeploymentRu.md) | Сборка, systemd, nginx и TLS, параметры, ёмкость, резервные копии, что раздать игрокам |
| [Авторизация](AuthenticationRu.md) | HTTP-контракт входа, сохранённый вход, сброс пароля, транспорт, логирование |
| [Веб-админка](AdminPanelRu.md) | Первичная настройка, роли, наказания, страницы и REST API, хостинг, безопасность |
| [База данных](../db/README.md) | Схема SQLite, миграции и генерация типов |
| Примеры конфигураций | [server](../src/Dreamsleeve.Server/server.example.toml), [client](../src/Dreamsleeve.Client.Core/client.example.toml), [ui](../src/Dreamsleeve.Client.UI/ui.example.toml), [moderation](../src/Dreamsleeve.Server/moderation.example.toml), [pseudonyms](../src/Dreamsleeve.Server/pseudonyms.example.toml) — каждый ключ с пояснением и допустимыми значениями |

## Функции

| Документ | Назначение |
|---|---|
| [Модерация, игнор и имена](ModerationAndNamesRu.md) | Словарь block/flag, антиспам, личный игнор, отображаемые имена, режим стримера, скрытое имя, наказания и инструменты модератора |
| [Метки на земле](GroundMarksRu.md) | Надписи и места смерти: домен, протокол, сервер, клиент, игровая дата |
| [Гильдии](GuildsRu.md) | Роли и права, имя и лимиты, приглашения, чат гильдии, интерфейс в игре, админка, настройки |
| [API для других модов](DreamsleeveModApiRu.md) | Объявления из C++ (`IVDreamsleeve1`) и Papyrus (`DreamsleeveClient`) |
| [Пространственная репликация](SpatialReplicationRu.md) | Каналы ENet, положение и область видимости, периодическая репликация |

## Сервер и протокол

| Документ | Назначение |
|---|---|
| [Прикладной протокол](../Protocol/README.ru.md) | Контракт protobuf, версии, C++/F# codec |
| [Server.Core](../src/Dreamsleeve.Server.Core/README.ru.md) | Runtime, сессии, владельцы состояния, конфигурация, логирование |
| [Server.Domain](../src/Dreamsleeve.Server.Domain/README.ru.md) | Проверяемые значения, текстовая политика, игроки, чат и объявления, метки, данные админки |
| [Infrastructure.Interop](../src/Dreamsleeve.Server.Infrastructure.Interop/README.ru.md) | Владение исходящими пакетами ENet и их бюджеты |
| [Агенты](../src/Dreamsleeve.Agent/README.ru.md) ([EN](../src/Dreamsleeve.Agent/README.md)) | Последовательные агенты на Channels/Task |

## Клиент

| Документ | Назначение |
|---|---|
| [SKSE-клиент](SkseClientRu.md) | Модули DLL, потоки, хуки, PrismaUI-host, телеметрия, светлячки, имена, облачка, метки |
| [Клиентский UI: требования](ClientUiPlanRu.md) | Требования к чату и панелям и состояние их реализации |
| [UI: запуск и bridge](../src/Dreamsleeve.Client.UI/README.ru.md) | React/TypeScript, события и команды host ↔ страница, тесты |
| [Client.Core](../src/Dreamsleeve.Client.Core/README.ru.md) | ClientRuntime, ClientApplication, вход, конфигурация Dev/SKSE |
| [Состояние клиента](../src/Dreamsleeve.Client.Core/State/README.ru.md) | ClientModel, ClientExchange, обмен с одним потребителем |
| [Интерполяция движения](MovementInterpolationRu.md) | MovementView, история и настройки |
| [Перехват ввода](InputCaptureHookRu.md) | Проверенные адреса и хук ввода |
| [Смерть и actor values](DeathAndActorValuesRu.md) | Исследованные события и хуки, выбранный sampling |
| [Локальный фантом](PhantomPrototypeRu.md) | Запись, replay и сравнение вариантов; экспериментальная ветка |
| [Измерения фантома](PhantomMeasurementsRu.md) | Размеры внешности/поз, сжатие и ошибки квантования |
| [Реверс фантома SE/AE/VR](PhantomRuntimeRu.md) | Проверенные адреса, ABI, размеры, VR bounds и границы проверки |
| [Будущая репликация фантомов](PhantomReplicationRu.md) | Reliable модели, unreliable позы с bounds, кеш и файловое хранилище, нагрузка |

## Спецификации и справочники

Ранние документы проекта. Там, где они расходятся с кодом, прав код и документы выше; в начале
каждого сказано, что из него реализовано.

| Документ | Назначение |
|---|---|
| [ProductSpecRu.MD](ProductSpecRu.MD) | Продуктовое видение: реализованное и будущие этапы |
| [DomainSpecRu.MD](DomainSpecRu.MD) | Предметная модель: имена, пространство, чат, роли, будущие социальные правила |
| [TechnicalHandbookRu.MD](TechnicalHandbookRu.MD) | Архитектура и справочные примеры стека |
| [ProtobufHandbookRu.MD](ProtobufHandbookRu.MD) | Работа с protobuf |
| [SqlHandbookRu.md](SqlHandbookRu.md) | SQLite: реальная схема и эскизы будущих таблиц |
| [MSVC и protobuf в модулях](MsvcProtobufModulesRu.md) | Проверка C1001 и обход через отдельный модуль |

## Тесты и измерения

- [Тесты](../tests/README.md): состав, запуск, smoke-сценарии.
- [512 клиентов, 1 октября 2026](benchmarks/load-512-2026-10-01.md): движение при разной
  плотности, actor values и одновременное появление (protocol 15, loopback).
- Нагрузочные прогоны 27–28 сентября 2026 (протокол того времени, loopback), без обновления
  под текущий код: [чат через ENet](benchmarks/enet-2026-09-27.md),
  [движение v6 и владелец ENet](benchmarks/movement-v6-owner-2026-09-27.md),
  [1000 клиентов при 20 Гц](benchmarks/movement-workers-2026-09-27.md),
  [разделение SessionRegistry](benchmarks/session-routing-2026-09-27.md).

Исторические SQL/protobuf-примеры удалены из рабочего дерева; архивный коммит `40059c5`:
`git show 40059c5:docs/ProtoExamples/README.md`, `git show 40059c5:docs/SqlExamples/README_ru.md`.
Завершённые планы и промежуточные отчёты о производительности удалены 1 октября 2026 года;
они остаются в истории git.
