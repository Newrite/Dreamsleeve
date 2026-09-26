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
| `Dreamsleeve.Client.Tests` | doctest, цель xmake | DreamNet.Address/Packet/Runtime/Network/Client; Client.Domain/State/Changes/StateUpdate/StateUpdateQueue/Exchange/Codec |
| `Dreamsleeve.Server.Tests` | Expecto + Faqt, F# executable | Dreamsleeve.Server.Domain (41), Dreamsleeve.Agent (28), Dreamsleeve.Server.Codec (12) |

C++: 144 сценария, включая 42 сохранённых из архивов, 6 проверок StateUpdate
и 7 проверок очереди: порядок, переполнение, восстановление чата и передача между
двумя потоками. Ещё 6 проверок ClientExchange покрывают FIFO/Full/Closed, отсутствие локального
добавления чата, доставку только новых сообщений, объединение показаний,
восстановление без потери отказов и двусторонний обмен с join владельца. F#: 81 сценарий.
Проверки codec: 9 native и 12 managed — корреляция, структура bootstrap,
доменные ошибки, повреждённые пакеты и выдача одного сообщения без истории.
Общие коды отказа проверены на известных значениях, недопустимом нуле, отказе
серверного encoder от неопределённых кодов и сохранении будущих кодов клиентом.
Генерация C++ enum проверена на временных копиях вывода protoc: новый именованный
код попадает в модуль и static_assert, неизвестный формат записи даёт ошибку.
Проверки конфигурации покрывают точную границу размера в обоих направлениях,
изменение лимитов bootstrap, отказ создания codec с ошибочными параметрами
и применение к реальному yENet host. C++ codec сохраняет копию настроек: последующая
правка исходного config не меняет лимит существующего экземпляра.
Отдельный loopback-сценарий отправляет результат codec.Encode как DreamNetPacket,
проверяет передачу владения и protobuf-содержимое на принимающей стороне.
Codec-тест проверяет обычные данные игроков/сообщений в SessionOpened и отсутствие
повторных проверок правил хранилищ в decoder. Правила PlayerStore/ChatCache проверяются
в их наборах. Client.Dev применяет начальные данные обычными операциями модели и
публикует их после завершения обработки; тестового контракта отката сессии нет.
Два дополнительных native-сценария проверяют бюджеты обоих host, передачу
фрагментированного пакета ровно на границе, отказ peer/broadcast при превышении
и сохранение владения пакетом при ошибке.
Две архивные проверки удалены вместе с неиспользуемым SnapshotMailbox.
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
xmake run Dreamsleeve.Client.Tests --test-suite=Client.Exchange
xmake run Dreamsleeve.Client.Tests --test-suite=DreamNet.Network

dotnet run --project tests/Dreamsleeve.Server.Tests -c Release -- --filter-test-list Dreamsleeve.Agent
dotnet run --project tests/Dreamsleeve.Server.Tests -c Release -- --filter-test-list Dreamsleeve.Server.Domain
```

Для всех F#-тестов: `dotnet run --project tests/Dreamsleeve.Server.Tests -c Release`.
Это executable с Expecto CLI; `dotnet test` не заменяет его запуск.
`Scripts/vxmakegen.py` и `Scripts/vxmakegen_modules.py` включают managed-проекты
из `src` и `tests` при генерации IDE solution. Старые generated solution нужно
перегенерировать после переноса, вручную их поддерживать не требуется.

Для ручной проверки двустороннего обмена есть `xmake build Dreamsleeve.Client.Dev`, затем
`xmake run Dreamsleeve.Client.Dev --state-demo` или интерактивный запуск без флага.
Это локальная демонстрация на синтетических серверных событиях; автоматические
проверки контракта находятся в Client.Exchange.

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
  Codec — `CodecTests.fs`. Новый F#-набор подключать в fsproj до Program.fs и в общий testList.
- Для unit-тестов использовать публичные операции и результат, не внутреннюю
  структуру реализации. При изменении контракта документировать изменённое ожидание.
- Проверки транспорта используют loopback и ограниченные ожидания; UI/игровая
  интеграция и межъязыковой echo не входят в нынешнюю проверенную сборку.

Проверено на Windows/MSVC/.NET 10: 144/144 native (1446 assertions) и 81/81 managed.
Шаг codec не повторяет ENet echo: проверяет новые прикладные контракты.

После обновления VS 18.10.2 / cl 19.51.36260 выполнена чистая сборка всех native-целей,
137/137 C++-тестов и Client.Dev --state-demo. Обход C1001 остаётся необходимым;
[минимальный repro и результаты](../docs/MsvcProtobufModulesRu.md).

Visitor ClientRequest проверен изолированной компиляцией с /O2 /DNDEBUG:
две типизированные перегрузки собираются, новый вариант без перегрузки даёт C2672.
Матчинги codec проверены отрицательными компиляциями изолированных копий:
новый DU-вариант и пропущенный именованный enum дают FS0025, новый C++ enum — C4061
даже при default. Неизвестные wire-payload возвращают ошибку; добавочные поля
при известном payload сохраняют совместимость. Собственное сообщение декодируется
в ChatAccepted с обязательным ID и обычным ChatMessagesReceived; рассылка — сразу
в ChatMessagesReceived. ServerRejection также требует ID; прежний тест «Connection
refused без ID» заменён на отказ конкретной команды, поскольку ошибки транспорта
не принадлежат этому контракту. Проверка сохранения отказов между сессиями осталась.
