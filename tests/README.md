# Тесты Dreamsleeve

Все тесты находятся в `tests/`, отдельно от production-кода. Для разных runtime
остаются два проекта с одной общей командой сборки и запуска:

```powershell
python Scripts/run_tests.py
python Scripts/run_tests.py --suite native
python Scripts/run_tests.py --suite managed
```

Скрипт работает из любой текущей папки, если передан путь к нему. Native-набор
использует активную конфигурацию xmake (по умолчанию Windows x64 releasedbg),
managed — Release / .NET 10. Сборка выполняется перед тестированием; после ошибки
сборки старый бинарник не запускается. При `all` второй набор проверяется даже
после сбоя первого. Ошибка/отсутствующий инструмент дают ненулевой exit code.

Нужны Python 3.10+, xmake, MSVC с C++23/`import std`, .NET SDK 10 и доступ к
пакетам при первоначальном восстановлении. Loopback-тестам нужен локальный UDP.
Skyrim, PrismaUI, работающий сервер и база данных не требуются.

## Проекты и наборы

| Проект | Фреймворк | Наборы |
|---|---|---|
| `Dreamsleeve.Client.Tests` | doctest, цель xmake | DreamNet.Address/Packet/Runtime/Network/Client; Client.Domain/State/Changes/StateUpdate |
| `Dreamsleeve.Server.Tests` | Expecto + Faqt, F# executable | Dreamsleeve.Server.Domain (41), Dreamsleeve.Agent (28) |

C++: 121 сценарий, включая 44 перенесённых из архивов и 6 проверок StateUpdate. F#: 69 сценариев.
Нативные файлы используют именованные doctest suites вместо лишнего второго
аргумента TEST_CASE с псевдотегом. F# Agent и Domain объединены одним entry point.
Agent сохраняет последовательное выполнение, gates и 15-секундный предел на
сценарий; короткие таймеры используются там, где проверяется сам timeout.
Это регрессионные проверки, а не доказательство всех возможных чередований потоков.

## Запуск отдельных наборов

Из корня репозитория:

```powershell
xmake build Dreamsleeve.Client.Tests
xmake run Dreamsleeve.Client.Tests --test-suite=Client.Changes
xmake run Dreamsleeve.Client.Tests --test-suite=DreamNet.Network

dotnet run --project tests/Dreamsleeve.Server.Tests -c Release -- --filter-test-list Dreamsleeve.Agent
dotnet run --project tests/Dreamsleeve.Server.Tests -c Release -- --filter-test-list Dreamsleeve.Server.Domain
```

Для всех F#-тестов: `dotnet run --project tests/Dreamsleeve.Server.Tests -c Release`.
Это executable с Expecto CLI; `dotnet test` не заменяет его запуск.
`Scripts/vxmakegen.py` и `Scripts/vxmakegen_modules.py` включают managed-проекты
из `src` и `tests` при генерации IDE solution. Старые generated solution нужно
перегенерировать после переноса, вручную их поддерживать не требуется.

## Происхождение и адаптация архивов

Архивы прочитаны из `F:\downloads`; тесты теперь самостоятельны и для запуска
этот каталог не нужен. Production-файлы из архивов не копировались поверх проекта.

| Источник | Что использовано |
|---|---|
| `Dreamsleeve.Client.Simplified.zip` | Tests.Domain.cpp, Tests.State.cpp — более поздний контракт, чем State.Mvp |
| `Dreamsleeve.Client.ChatDeltas.zip` | Tests.Changes.cpp — заменяет старый вариант ChangeBatch |
| `Dreamsleeve.Server.Review.zip` | Domain-тесты уже совпадали с репозиторием; перенесены без изменения проверок |
| `Dreamsleeve.Agent.NET10.review.zip` | Agent-тесты уже совпадали с репозиторием; заменён только runner на Expecto |
| Существующий `src/Dreamsleeve.Client.Tests` | Все транспортные тесты перенесены в native-проект |

Единственное изменение ожидаемого поведения архивных domain-тестов: убраны
ожидания отказа клиентских пространственных функций на NaN/Infinity. Это следует
из принятого удаления клиентских IsFinite-проверок в `36ea57e`, а не из попытки
скрыть сбой: конечность входа теперь предусловие. Проверки границы радиуса,
отрицательного радиуса, разных пространств и точности расчёта сохранены.
Серверные проверки нечисловых значений остались в DomainTests.

## Как добавлять тесты

- Клиентское поведение добавлять в соответствующий `Tests.*.cpp`; xmake подбирает
  файлы автоматически. Новый файл должен иметь именованный `TEST_SUITE`.
- Серверный домен — `DomainTests.fs`, поведение агентов — `AgentTests.fs`.
  Новый F#-набор подключать в fsproj до Program.fs и в общий testList.
- Для unit-тестов использовать публичные операции и результат, не внутреннюю
  структуру реализации. При изменении контракта документировать изменённое ожидание.
- Проверки транспорта используют loopback и ограниченные ожидания; UI/игровая
  интеграция и межъязыковой echo не входят в нынешнюю проверенную сборку.

Проверено на Windows/MSVC: 121/121 native. Последний прогон .NET 10 при объединении
проектов: 69/69 managed; шаг StateUpdate меняет только C++-код.
