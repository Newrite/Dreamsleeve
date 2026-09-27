# Dreamsleeve

Социальный слой для Skyrim: глобальный чат, таблица онлайна с состоянием персонажей
и отображение присутствия других игроков в виде светлячков. Сервер пересылает
показания клиентов, а не моделирует совместную авторитарную игру.

## Текущий этап

Реализованы C++ ClientRuntime/ClientExchange, серверный ENet runtime и общий
protobuf-контракт: вход, начальный снимок, онлайн, чат, отключение и повторный вход.
Client.Dev работает без Skyrim; `send` добавляет сообщение только после серверного
уведомления ChatPublished. Синтетическое демо состояния остаётся отдельным режимом.

На сервере PlayerSession владеет состоянием одного игрока, ChatRoomAgent — каналом
и историей, PresenceAgent — онлайном. ServerRuntime владеет транспортом и короткими
переходами таблицы сессий. Учётные записи и профили сохраняются в SQLite;
вход по паролю выдаёт одноразовый билет для ENet-сессии.
Protocol v4 передаёт полный PlayerInfo: персонажа, положение, actor values и
PlayerDetails (раса, уровень, занятие, описание места, время начала игры).
Метаданные периодически рассылаются всем; позиции — в том же WRLD/CELL и радиусе
видимости. Автор всегда получает свои показания; движение имеет компактный пакет.
[Контракт и границы телеметрии](docs/PlayerTelemetryPlanRu.md).
Конфигурация сервера загружается из JSON при запуске. Клиент хранит поток измерений
и вычисляет интерполированные положения на потоке потребителя; [демо и настройки](docs/MovementInterpolationRu.md).
SKSE/PrismaUI и интерфейс администрирования остаются следующими этапами.
Сервер пишет структурированные логи через Serilog в консоль и JSON-файлы.
[Авторизация, БД и зависимости](docs/AuthenticationRu.md).
Контракт: [Protocol/README.ru.md](Protocol/README.ru.md).

Ближайшее направление — работающий `Client.Dev` и сервер без запуска Skyrim.
Будущий интерфейс на HTML/CSS/JS планируется переиспользовать в PrismaUI через
адаптер; реализация интерфейса и игровая интеграция остаются следующими этапами.

| Каталог | Назначение |
|---|---|
| `src/Dreamsleeve.Client.Core` | C++23: DreamNet, независимый от Skyrim домен и состояние клиента |
| `src/Dreamsleeve.Client` | Заготовка игрового адаптера; пока static library, будущий SKSE-плагин |
| `src/Dreamsleeve.Client.Dev` | Двухпоточная консоль: сетевой вход через --connect и отдельное синтетическое демо |
| `src/Dreamsleeve.Server.Domain` | F#/.NET 10: проверяемые значения, игроки, ограниченная история чата |
| `src/Dreamsleeve.Agent` | Последовательные агенты на Channels/Task и примеры |
| `src/Dreamsleeve.Server.Core` | Runtime, сессии, владельцы чата/онлайна, конфигурация и codec |
| `src/Dreamsleeve.Server` | Запуск сервера, JSON-конфигурация, остановка по quit/Ctrl+C |
| `src/Dreamsleeve.Server.Infrastructure` | SQLite, AuthService и адаптер yENet; Interop обслуживает владение native-пакетами и бюджеты |
| `Protocol`, `src/Dreamsleeve.Protocol.*` | Рабочая схема protobuf и сгенерированные C++/C# типы |
| `tests` | Два тестовых проекта: native и managed; общий запуск |
| `docs` | Решения, спецификации, справочники и помеченные исторические примеры |

## Сборка и тесты

Нужны Windows x64, MSVC с поддержкой C++23 / `import std`, xmake,
Python 3.10+ и .NET SDK 10. Первый запуск восстанавливает зависимости.

```powershell
xmake build Dreamsleeve.Client.Dev
xmake run Dreamsleeve.Client.Dev --state-demo
xmake run Dreamsleeve.Client.Dev --connect 127.0.0.1 8778 player --register "Player Name"
dotnet run --project src/Dreamsleeve.Server -c Release
python Scripts/run_tests.py
```

Выбор набора и команды отдельных проектов: [tests/README.md](tests/README.md).
Без аргументов Client.Dev запускает синтетическую консоль с командами: `send <text>`, `accept`, `reject`,
`receive <text>`, `sample`, `read`, `snapshot`, `reset`, `quit`. Отправленный чат
появляется в модели только после `accept`.
Формат команд описан в [контракте состояния](src/Dreamsleeve.Client.Core/State/README.ru.md#обмен-с-одним-потребителем).
При первом запуске `--register` создаёт аккаунт; пароль вводится скрыто. При
последующих запусках уберите `--register`: вход восстановит профиль из БД.
Auth HTTP по умолчанию доступен только локально на 127.0.0.1:8779.

Сетевой режим `--connect` поддерживает чат, управление персонажем и отправку
положения, actor values и `details <json>`; список команд выводится при запуске.
Сервер запускается в отдельном терминале; по умолчанию адрес 127.0.0.1:8778. [Контракт runtime](src/Dreamsleeve.Client.Core/README.ru.md).
Генерация protobuf: `python Scripts/generate_protocol.py --help`.
Генерация IDE solution: `python Scripts/vxmakegen.py --help`.

## Документация

- [Принятые решения и состояние проекта](docs/CurrentStateRu.md).
- [Навигация по документации](docs/README.md).
- [Бенчмарки движения и интерполяции](docs/benchmarks/movement-2026-09-27.md).
- [Контракт состояния клиента](src/Dreamsleeve.Client.Core/State/README.ru.md).
- [Серверный домен](src/Dreamsleeve.Server.Domain/README.ru.md).
- [Агенты: RU](src/Dreamsleeve.Agent/README.ru.md) / [EN](src/Dreamsleeve.Agent/README.md).

Разработка идёт небольшими шагами, которые можно отдельно проверить и отревьюить.
Предложения из старых обсуждений не считаются реализованными или окончательно
принятыми только потому, что для них существует пример кода.

Серверная архитектура и запуск: [владельцы состояния и runtime](src/Dreamsleeve.Server.Core/README.ru.md).
Для файла настроек: `dotnet run --project src/Dreamsleeve.Server -c Release -- --write-config server.json`,
затем запуск с `--config server.json`. Частичные переопределения допустимы;
загрузка выполняется только при старте, автоматического reload нет.
