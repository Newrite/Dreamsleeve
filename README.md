# Dreamsleeve

Социальный слой для Skyrim: глобальный чат, таблица онлайна с состоянием персонажей
и отображение присутствия других игроков в виде светлячков. Сервер пересылает
показания клиентов, а не моделирует совместную авторитарную игру.

## Текущий этап

Реализованы обёртки ENet на C++, доменные типы и операции F#, библиотека агентов,
клиентские хранилища игроков и чата, `ClientModel`, снимки и `ChangeBatch`
с дельтами содержимого чата, подготовка самостоятельного `ClientStateUpdate`
для получателя и ClientExchange: команды к сетевому владельцу, дельты/снимки
и отказы обратно в игровой/UI-поток. В Client.Dev есть двухпоточная консоль
с синтетическим сервером для проверки этого обмена.
Добавлены `Protocol/chat.proto` и C++/F# codec для сессии, онлайна и чата.
C++ ClientRuntime выполняет настоящий вход по ENet, обрабатывает ответ/отказ,
отключение и переподключение. Серверные обработчики пока не реализованы;
SKSE/PrismaUI и интерполяция остаются следующими этапами.
Лимиты codec и ENet задаются конфигурацией клиента/сервера; загрузчик внешнего
конфига ещё не подключён. Контракт: [Protocol/README.ru.md](Protocol/README.ru.md).

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
| `src/Dreamsleeve.Server.Core` | Конфигурация транспорта, yENet и прикладной codec |
| `src/Dreamsleeve.Server` | Точка запуска сервера; пока заглушка |
| `src/Dreamsleeve.Server.Infrastructure` | Заготовка хранения данных |
| `Protocol`, `src/Dreamsleeve.Protocol.*` | Рабочая схема protobuf и сгенерированные C++/C# типы |
| `tests` | Два тестовых проекта: native и managed; общий запуск |
| `docs` | Решения, спецификации, справочники и помеченные исторические примеры |

## Сборка и тесты

Нужны Windows x64, MSVC с поддержкой C++23 / `import std`, xmake,
Python 3.10+ и .NET SDK 10. Первый запуск восстанавливает зависимости.

```powershell
xmake build Dreamsleeve.Client.Dev
xmake run Dreamsleeve.Client.Dev --state-demo
xmake run Dreamsleeve.Client.Dev --connect 127.0.0.1 8778 player "Player Name"
dotnet build src/Dreamsleeve.Server/Dreamsleeve.Server.fsproj -c Release
python Scripts/run_tests.py
```

Выбор набора и команды отдельных проектов: [tests/README.md](tests/README.md).
Без аргументов Client.Dev запускает синтетическую консоль с командами: `send <text>`, `accept`, `reject`,
`receive <text>`, `sample`, `read`, `snapshot`, `reset`, `quit`. Отправленный чат
появляется в модели только после `accept`.
Формат команд описан в [контракте состояния](src/Dreamsleeve.Client.Core/State/README.ru.md#обмен-с-одним-потребителем).
Сетевой режим `--connect` принимает `read`, `disconnect`, `connect`, `quit`;
нужен сервер, реализующий chat.proto. [Контракт runtime](src/Dreamsleeve.Client.Core/README.ru.md).
Генерация protobuf: `python Scripts/generate_protocol.py --help`.
Генерация IDE solution: `python Scripts/vxmakegen.py --help`.

## Документация

- [Принятые решения и состояние проекта](docs/CurrentStateRu.md).
- [Навигация по документации](docs/README.md).
- [Контракт состояния клиента](src/Dreamsleeve.Client.Core/State/README.ru.md).
- [Серверный домен](src/Dreamsleeve.Server.Domain/README.ru.md).
- [Агенты: RU](src/Dreamsleeve.Agent/README.ru.md) / [EN](src/Dreamsleeve.Agent/README.md).

Разработка идёт небольшими шагами, которые можно отдельно проверить и отревьюить.
Предложения из старых обсуждений не считаются реализованными или окончательно
принятыми только потому, что для них существует пример кода.
