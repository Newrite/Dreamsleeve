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
и историей, PresenceAgent — онлайном. ServerRuntime обрабатывает короткие
переходы таблицы сессий; выделенный TransportOwner обслуживает ENet. Учётные записи и профили сохраняются в SQLite;
вход по паролю выдаёт одноразовый билет для ENet-сессии.
Protocol v6 передаёт полный PlayerInfo: персонажа, положение, actor values и
PlayerDetails (раса, уровень, занятие, описание места, время начала игры).
Метаданные рассылаются reliable по изменениям; позиции — периодически через
unreliable sequenced канал в том же WRLD/CELL и радиусе видимости. Остановившиеся
позиции тоже повторяются. Базовая частота клиента и серверной репликации — 20 Гц
(50 мс), ориентир для расчётов нагрузки. Reliable baseline/clear задаёт контекст видимости;
автор получает своё состояние тем же путём. Чат использует отдельный reliable-канал.
[Контракт и границы телеметрии](docs/PlayerTelemetryPlanRu.md).
Конфигурация сервера загружается из TOML при запуске. Клиент хранит поток измерений
и вычисляет интерполированные положения на потоке потребителя; [демо и настройки](docs/MovementInterpolationRu.md).
SKSE-клиент реализован: DLL на CommonLibSSE-NG с хуком `Main::Update`, PrismaUI-host
production UI, страницей SKSE Menu Framework, сбором телеметрии и светлячками;
[описание адаптера](docs/SkseClientRu.md). Интерфейс администрирования остаётся следующим этапом.
Сервер пишет структурированные логи через Serilog в консоль и JSON-файлы.
[Авторизация, БД и зависимости](docs/AuthenticationRu.md).
Контракт: [Protocol/README.ru.md](Protocol/README.ru.md).

Ближайшее направление — работающий `Client.Dev` и сервер без запуска Skyrim.
Для общего интерфейса выбран TypeScript/React/Vite/Zustand с отдельными CSS-темами
и адаптером PrismaUI. [Требования и этапы UI](docs/ClientUiPlanRu.md) включают
чат, системные объявления, fade, настройки и перемещение/изменение размера окна.
Браузерный UI уже реализован: [запуск и bridge](src/Dreamsleeve.Client.UI/README.ru.md).
C++ host подключён в SKSE DLL; проверки внутри Skyrim перечислены в [SkseClientRu.md](docs/SkseClientRu.md).

| Каталог | Назначение |
|---|---|
| `src/Dreamsleeve.Client.UI` | React/TypeScript: чат, панели и темы, отдельные browser/game сборки |
| `src/Dreamsleeve.Client.Core` | C++23: DreamNet, независимый от Skyrim домен и состояние клиента |
| `src/Dreamsleeve.Client` | SKSE DLL: адаптер игры к Core, PrismaUI-host, SKSE Menu, телеметрия, светлячки |
| `src/Dreamsleeve.Client.Dev` | Двухпоточная консоль: сетевой вход через --connect и отдельное синтетическое демо |
| `src/Dreamsleeve.Server.Domain` | F#/.NET 10: проверяемые значения, игроки, ограниченная история чата |
| `src/Dreamsleeve.Agent` | Последовательные агенты на Channels/Task и примеры |
| `src/Dreamsleeve.Server.Core` | Runtime, сессии, владельцы чата/онлайна, конфигурация и codec |
| `src/Dreamsleeve.Server` | Запуск сервера, TOML-конфигурация, остановка по quit/Ctrl+C |
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

SKSE-плагин собирается командой `xmake build Dreamsleeve.Client` при настроенном
`CommonLibSSE-NG`; `python Scripts/package_dist.py` собирает DLL, UI и публикацию сервера
в `dist/Client` и `dist/Server`. В xmake для CommonLib и Core явно согласованы настройки spdlog:
compiled library, wchar и std::format. Header-only вариант подключает Windows-макросы
в PCH CommonLib и конфликтует с её `REX::W32` именами; порядок зависимостей сам по
себе не заменяет согласованную конфигурацию.

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
`py .\Scripts\vxmakegen.py` также добавляет `UI / Dreamsleeve.Client.UI` с исходниками,
ресурсами, тестами и конфигами. Сборка UI остаётся через npm/Vite; `node_modules`,
`dist`, `dist-demo` и результаты тестов в IDE-проект не включаются.

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
Для файла настроек: `dotnet run --project src/Dreamsleeve.Server -c Release -- --write-config server.toml`,
затем запуск с `--config server.toml`. Частичные переопределения допустимы;
загрузка выполняется только при старте, автоматического reload нет.
Оба загрузчика читают UTF-8 TOML размером до 64 KiB. Имена ключей чувствительны
к регистру; неизвестные/повторные ключи и неверные типы отклоняются. Пустой файл
сохраняет defaults. Старые JSON-конфиги нужно перенести в TOML.

Клиентский TOML можно передать конечному приложению явно:
`xmake run Dreamsleeve.Client.Dev --config "path/to/client.toml" player`.
[Полный пример](src/Dreamsleeve.Client.Core/client.example.toml) и
[общий запуск для Dev/SKSE](src/Dreamsleeve.Client.Core/README.ru.md#общий-запуск-и-конфигурационный-файл).
Путь выбирает вызывающая сторона; пароли в файле не сохраняются.
