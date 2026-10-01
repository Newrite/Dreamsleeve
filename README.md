# Dreamsleeve

Социальный слой для Skyrim: глобальный чат, системные объявления, онлайн с состоянием
персонажей, другие игроки в виде светлячков с именами и облачками чата, метки на земле.
Сервер пересылает показания клиентов, а не моделирует совместную авторитарную игру.

## Что реализовано

- **Сервер** (F#/.NET 10): ENet runtime с выделенным владельцем транспорта, сессия на игрока,
  владельцы чата, системного канала, онлайна и меток; SQLite (аккаунты, профили, роли,
  наказания, метки, админка); вход по паролю и сохранённый вход через HTTP(S) с одноразовым
  билетом для ENet; веб-админка с REST API; TOML-конфигурация; Serilog.
- **Протокол** v18 (protobuf): три канала ENet — Control, Chat (reliable) и Realtime (позы,
  unreliable sequenced, 20 Гц, в пределах пространства и радиуса видимости).
- **Модерация:** словарь с уровнями block/flag, антиспам, личный игнор, отображаемые имена,
  режим стримера и скрытое имя; роли, муты, баны и кик; инструменты модератора в игре.
- **Клиент:** C++23 Core, независимый от Skyrim; SKSE-плагин на CommonLibSSE-NG (хук
  `Main::Update`, PrismaUI-host, страница SKSE Menu Framework, телеметрия, светлячки, имена,
  облачка, метки, API объявлений для других модов); React-интерфейс; консоль `Client.Dev` для
  работы без Skyrim.

Подробно — [состояние проекта и принятые решения](docs/CurrentStateRu.md) и
[навигация по документации](docs/README.md). Развернуть свой сервер:
[docs/DeploymentRu.md](docs/DeploymentRu.md).

| Каталог | Назначение |
|---|---|
| `src/Dreamsleeve.Client.UI` | React/TypeScript: чат, панели и темы, отдельные browser/game сборки |
| `src/Dreamsleeve.Client.Core` | C++23: DreamNet, независимый от Skyrim домен и состояние клиента |
| `src/Dreamsleeve.Client` | SKSE DLL: адаптер игры к Core, PrismaUI-host, SKSE Menu, телеметрия, светлячки, метки |
| `src/Dreamsleeve.Client.Dev` | Консоль: сетевой клиент (`--connect`/`--config`) и синтетические демо |
| `src/Dreamsleeve.Server.Domain` | F#: проверяемые значения, игроки, чат, метки, модерация |
| `src/Dreamsleeve.Agent` | Последовательные агенты на Channels/Task |
| `src/Dreamsleeve.Server.Core` | Runtime, сессии, владельцы чата/онлайна/меток, конфигурация и codec |
| `src/Dreamsleeve.Server.Infrastructure` | SQLite, AuthService, AdminService, адаптер yENet; Interop — владение пакетами ENet |
| `src/Dreamsleeve.Server.Web` | HTTP-хосты: вход (`/auth/*`) и веб-админка (Falco, htmx) |
| `src/Dreamsleeve.Server` | Запуск сервера, TOML-конфигурация, консольные команды |
| `Protocol`, `src/Dreamsleeve.Protocol.*` | Схема protobuf и сгенерированные C++/C# типы |
| `db` | Миграции SQLite |
| `tests` | Native (doctest), managed (Expecto), нагрузочные проекты |
| `docs` | Документация: развёртывание, функции, спецификации |

## Сборка и тесты

Нужны Windows x64, MSVC с поддержкой C++23 / `import std`, xmake, Python 3.10+, .NET SDK 10 и
Node.js (для UI). Первый запуск восстанавливает зависимости.

```powershell
xmake build Dreamsleeve.Client.Dev
xmake run Dreamsleeve.Client.Dev --state-demo
dotnet run --project src/Dreamsleeve.Server -c Release
xmake run Dreamsleeve.Client.Dev --connect 127.0.0.1 8778 player --register "Player Name"
python Scripts/run_tests.py
```

SKSE-плагин собирается командой `xmake build Dreamsleeve.Client` при настроенном
`CommonLibSSE-NG`; `python Scripts/package_dist.py` собирает DLL, UI и публикацию сервера
в `dist/Client` и `dist/Server`. В xmake для CommonLib и Core явно согласованы настройки spdlog:
compiled library, wchar и std::format. Header-only вариант подключает Windows-макросы
в PCH CommonLib и конфликтует с её `REX::W32` именами; порядок зависимостей сам по
себе не заменяет согласованную конфигурацию.

Наборы тестов, UI-тесты (vitest, Playwright) и smoke-сценарии: [tests/README.md](tests/README.md).
Без аргументов Client.Dev запускает синтетическую консоль состояния: `send <text>`, `accept`,
`reject`, `receive <text>`, `sample`, `read`, `snapshot`, `reset`, `quit`. Отправленный чат
появляется в модели только после `accept`. Формат команд описан в
[контракте состояния](src/Dreamsleeve.Client.Core/State/README.ru.md#обмен-с-одним-потребителем).
При первом запуске `--register` создаёт аккаунт; пароль вводится скрыто. При
последующих запусках уберите `--register`: вход восстановит профиль из БД.
Сетевой режим поддерживает чат, управление персонажем и отправку положения, actor values и
`details <json>`; список команд выводится при запуске. [Контракт runtime](src/Dreamsleeve.Client.Core/README.ru.md).
Генерация protobuf: `python Scripts/generate_protocol.py --help`.
Генерация IDE solution: `python Scripts/vxmakegen.py --help`; она также добавляет
`UI / Dreamsleeve.Client.UI` с исходниками, ресурсами, тестами и конфигами. Сборка UI остаётся
через npm/Vite; `node_modules`, `dist`, `dist-demo` и результаты тестов в IDE-проект не включаются.

## Конфигурация

Сервер: `dotnet run --project src/Dreamsleeve.Server -c Release -- --config server.toml`.
[server.example.toml](src/Dreamsleeve.Server/server.example.toml) описывает каждый ключ,
значение по умолчанию и допустимые значения; `--write-config server.toml` пишет те же значения
без комментариев. Частичные переопределения допустимы; загрузка только при старте.
Клиент: [client.example.toml](src/Dreamsleeve.Client.Core/client.example.toml) — первый
`client.toml` плагина и `Client.Dev --config`; внешний вид — `ui.toml`
([пример](src/Dreamsleeve.Client.UI/ui.example.toml)).
Оба загрузчика читают UTF-8 TOML размером до 64 KiB; имена ключей чувствительны к регистру,
неизвестные и повторные ключи и неверные типы отклоняются. Пароли в файлах не хранятся:
сохранённый вход клиента — в Windows Credential Manager.
По умолчанию сервер слушает только `127.0.0.1`: ENet на 8778, вход на 8779, админка на 8780.

Разработка идёт небольшими шагами, которые можно отдельно проверить и отревьюить.
Предложения из старых обсуждений не считаются реализованными или окончательно
принятыми только потому, что для них существует пример кода.

## Лицензия

[GPL-3.0-or-later](LICENSE) WITH [Modding Exception AND GPL-3.0 Linking Exception (with
Corresponding Source)](EXCEPTIONS.md) — те же условия, что у
[CommonLibSSE-NG](https://github.com/alandtse/CommonLibSSE-NG), с которой собран SKSE-плагин, и
у [skyrim-rich-presence](https://github.com/doodlum/skyrim-rich-presence), на который клиент
опирается как на образец. Modded Code в смысле исключения — Skyrim (и его варианты: SE, AE, VR).

Исключение — заголовок C++ API для других модов
[`DreamsleeveAPI.h`](src/Dreamsleeve.Client/API/DreamsleeveAPI.h): он под MIT (текст в самом
файле), чтобы его мог подключить плагин под любой лицензией.

Каждый, кто распространяет сборки Dreamsleeve или основанные на нём, обязан предоставить
исходный код на тех же условиях. Архивы релизов содержат `LICENSE` и `EXCEPTIONS.md`, их
исходный код — тег релиза в этом репозитории. Лицензии сторонних компонентов —
`THIRD_PARTY_NOTICES.md` в архивах.
