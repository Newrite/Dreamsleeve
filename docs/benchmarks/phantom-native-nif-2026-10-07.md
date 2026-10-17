# Native NIF: сервер и транспорт, 07.10.2026

Эти замеры проверяют передачу настоящих production payload через настоящий ENet
и сервер. NiStream Load и Skyrim renderer здесь не вызываются. Нельзя выводить
визуальную корректность или производительность рендера из приведённых чисел.

## Данные и граница измерения

Источник — подготовленная копия настоящей movement-записи прототипа, проверенная
production codec roundtrip на 301 кадре. Исходная пользовательская запись не менялась.
Локальные fixtures: `build/native-nif/movement-poses` (не входят в Git/dist).
Сетевой asset `model.zst`: **13 288 628 compressed bytes**, **27 740 619 raw bytes**,
327 bindings. SHA256 compressed container:
`579cbf15e84f6fff984bef84a08ff56487b25d2de0583fb573b76bc85b4dc198`.

301 pose payload: 4618–4773 bytes, среднее 4677.897. Полный исходный клиентский
protobuf packet по `measurements.csv`: 4638–4792 bytes, среднее 4697.402.
Нагрузчик циклически передаёт эти payload, обновляя **внешние** protobuf sequence/time.
Внутренние идентификаторы payload остаются как в записи: сервер их не интерпретирует.
Это измерение opaque server fanout, не проверка client envelope/payload consistency.
Хэши каждого payload и параметры сохраняются в `*-probe.provenance.json`.

Все клиенты намеренно используют один hash. Измеряется повторное использование
контента, а не объём кеша 512 уникальных персонажей. Сервер по-прежнему не знает
геометрию, материалы, skinning или число вершин.

## Сценарий 512, группы максимум 25

512 независимых UDP sockets, 8 worker-процессов, один server process. Штатный
`sparse` workload: двадцать групп по 25 и одна из 12, расстояние между группами
20000 Skyrim units. Движение 20 Hz, actor values 4 Hz, Presence/phantom replication
50 ms; позиции прогреты. Каждый из 512 источников публикует позы 20 Hz.
`Maximum=4` даёт **2048 подписок**, а не 24 получателя у каждого источника.
Все 24 соседа в полной группе означали бы 12132 подписки на всём сценарии.

Steady предварительно заполняет server/client cache, публикует модели и ждёт общего
барьера готовности. Измеряемое окно — 30 секунд, без model chunks. Reliable команды
имеют ограниченный FIFO 16 на peer; при исчерпании ENet budget поза отбрасывается
без очереди и учитывается отдельно. Multiworker workload не добавляет чат; доставка
чата одновременно с моделями/позами проверяется managed UDP E2E и прежним
однопроцессным режимом нагрузчика. Все процессы конкурируют на одной машине;
CPU affinity/JIT/GC изоляции нет. Это диагностические замеры, не SLA мощности сервера.

Предыдущие запуски могли пересекаться с обычными и диагностическими C++ сборками
на том же host. Post-fairness запуск `native-nif-v22-steady-512-fair` точно пересёкся
с `package_dist`: это contended diagnostic, а не чистое измерение. Он не дошёл
до измеряемого окна — часть workers вышла во время movement setup. Его результат
сохранён [отдельно](../../build/benchmarks/native-nif-v22-steady-512-fair/results.json).
Эти числа нельзя использовать для выводов об ускорении или регрессии сервера.
Финальный 30-секундный запуск выполняется только после завершения native сборок.

Модель: общий upload+download budget сервера **5 MiB/s**, также **5 MiB/s на peer**.
Позы: ingress 2 MiB/s на source, egress 2 MiB/s на receiver, общий egress **32 MiB/s**.
При текущем полном packet на 2048 подписок ×20 Hz требуется примерно **183.5 MiB/s**
прикладных данных, до ENet/IP/UDP overhead. Поэтому default общий budget не может
дать 20 Hz всем 2048 подпискам; это нельзя маскировать флагом успешного benchmark.

## Зафиксированные результаты

Первый контроль phantom-off: 512/group25/20Hz + actor values4Hz, полный PASS.
Server CPU 1.015 core equivalent, private peak96.92 MiB. Этот запуск был до добавления
точного per-lane traffic recorder и не используется для вычитания transport overhead.
[Данные](../../build/benchmarks/native-nif-v22-off-512/results.json).

Текущая реализация до исправления fairness, warmed:

| Показатель | Результат |
|---|---:|
| Проверка / длительность | PASS /30s |
| Исходящая частота источников | 19.998–20.000 Hz |
| Отправлено /получено полных поз | 307200 /212409 |
| Получено на active subscription second | 3.474 Hz |
| Local pose admission drops | 0 |
| P95 receive gap, диапазон workers | 152.68–3009.97 ms |
| P95 delivery age, диапазон workers | 88.80–192.20 ms |
| Server CPU | 1.551 core equivalent |
| Server private peak | 152.84 MiB |
| Средний полный C2S /S2C pose packet | 4699.71 /4704.04 bytes |
| Model upload/download payload в измеряемом окне | 0 /0 bytes |

[Результаты до fairness fix](../../build/benchmarks/native-nif-v22-steady-512-warmed/results.json).
В warmup были отправлены27262976 model payload bytes: после Transfer нагрузчик
мог успеть поставить chunks до получения cached Complete. Они вынесены из steady
окна и не скрыты из отчёта. Сервер уже имел hash, и повторные chunks не создавали
новую модель. Offers/Remove могут продолжаться в steady при изменении выбранных
ближайших источников; это metadata lane3, а не model chunks.

## Fairness общего pose budget

При исчерпании общего byte credit прежний tick всё равно обходил оставшиеся sources.
Для512 источников cursor сдвигался на512 и следующий обход опять начинался с того же
места. Начало списка получало следующий refill, хвост мог голодать.

Детерминированный regression: 3 источника/3 получателя, credit на одну600-byte позу
за тик, три пополнения. До изменения получили доставку **1/3** источников, после —
**3/3**, без превышения byte credit. Изменение только в PhantomAgent: при исчерпании
общего credit закончить текущий fanout turn, продолжая следующий с нового участка.
[RED](../../build/native-nif-fairness-red.log),
[GREEN](../../build/native-nif-fairness-green.log).
Все managed tests после изменения: **605/605 PASS**, включая real UDP E2E;
Release build без предупреждений и ошибок.
[Build](../../build/native-nif-managed-build.log),
[Tests](../../build/native-nif-managed-tests.log).

Финальный запуск после завершения всех native сборок, тем же сценарием и fixtures:

| Показатель | Clean post-fairness |
|---|---:|
| Проверка / длительность | PASS /30s |
| Исходящая частота источников | 19.9985–19.9997 Hz |
| Отправлено /получено полных поз | 307200 /211723 |
| Missed capture intervals /local admission drops | 0 /0 |
| Active subscription seconds /подписки на момент stop | 60929.21 /2034 |
| Получено на active subscription second | 3.475 Hz |
| P95 receive gap, диапазон workers | 655.96–986.90 ms |
| P95 delivery age, диапазон workers | 142.34–158.92 ms |
| Server CPU | 1.660 core equivalent |
| Server private peak | 139.15 MiB |
| Model upload/download payload в измеряемом окне | 0 /0 bytes |
| Оставшиеся reliable model commands | 0 |

[Clean512 результаты](../../build/benchmarks/native-nif-v22-steady-512-fair-clean/results.json),
[лог](../../build/native-nif-benchmark-steady-512-fair-clean.log).
Warmup upload27200KiB учитывается отдельно. Число выбранных subscriptions меняется
при движении, поэтому частота нормирована на интеграл active subscription seconds.
`receiveGapMs` сохраняет last-seen пары между Remove/Offer: большой gap может включать
период отсутствия подписки (максимум28.71s), а не только задержку активного потока.
Это ограничение метрики не скрывается; regression fairness проверяет доставку при
неизменных subscriptions. Разницу CPU между прежним contended и этим чистым запуском
нельзя приписывать изменению алгоритма. Оба передают те же payload и имеют один budget.

## Cold модель настоящей записи

После clean512 выполнен отдельный **PASS**45s: 8 peers, 2 publishers, 2 workers,
одна sparse группа, server/client cache пустые. Model limits и bandwidth оставлены
штатными: общий5MiB/s, на peer5MiB/s. Публикации начали передачу одновременно;
одинаковый hash загружен двумя издателями и скачан всеми восемью получателями.

| Показатель | Cold8 |
|---|---:|
| Принятые полные публикации | 2/2 |
| Upload accepted, время от старта | 12.769–12.770s |
| Полные скачивания с SHA256 verification | 8/8 |
| Verified download, среднее /максимум от старта | 27.651 /32.142s |
| Model upload payload | 26577256 bytes |
| Model download payload | 106309024 bytes |
| Незаконченные downloads /queued model commands /refusals | 0 /0 /0 |
| Missed pose intervals /local admission drops | 0 /0 |
| Server CPU /private peak | 0.0327core /95.22MiB |

[Cold результаты](../../build/benchmarks/native-nif-v22-cold-8-clean/results.json),
[лог](../../build/native-nif-benchmark-cold-8-clean.log).
Время включает admission, ACK windows, scheduling, disk и ожидание готовой публикации;
это не latency отдельного IO вызова.8×13288628 bytes проверены побайтно и по SHA256.
NiStream и сцена Skyrim в этом тесте не загружаются. Как и в steady, используется
один общий hash; этот тест не характеризует512 уникальных моделей или полный кеш.

## Исторический сервер: границы A/B

Для сравнения создан git archive `codex/phantom-replication`,
`3b80b8eb386d3a6ce061da1d0a1ce1517bc52ee2`, в
`build/native-nif/server-baseline-source`. Основной checkout не переключался.
Минимальный compatibility overlay заменяет protocol21/neutral manifest на v22/format2
и одинаковые limits/codec; исторические PhantomAgent, Presence, storage и ENet
оставлены прежними. Точный список файлов и SHA256:
[server-baseline-overlay.json](../../build/native-nif/server-baseline-overlay.json).
Это адаптированный baseline серверной инфраструктуры, не неизменённый protocol21 binary.

**В каждой сопоставляемой A/B паре worker generator одинаковый**, из текущего checkout.
`--server-benchmark` меняет только server host DLL. В ранних запусках Worker DLL SHA256:
`bb55c35947da47efcfa74844a1c2139b41fbfb110460d8c3f9740a478091ad2d`.
Он совпадает в `binarySha256` текущего warmed run и обоих baseline runs.

Оба ранних baseline прогона не прошли полные30s: первый закончился примерно на16.5s,
повтор — на9.1s до первой ошибки. Во втором первичная ошибка нагрузчика:
`Client61 packet admission failed: BudgetExceeded` в унаследованном общем
movement/control sender. Затем остальные workers увидели уход участников;
server supervisor записал `Source Presence terminated: A task was canceled`.
Отмена Presence после ухода клиентов сама по себе не доказывает первичный server crash.
Причина первого ухода в первом прогоне не установлена.

[Первый baseline](../../build/benchmarks/native-nif-baseline-steady-512/results.json),
[повтор](../../build/benchmarks/native-nif-baseline-steady-512-repeat/results.json).
Эти неполные результаты **не используются для заявления об ускорении** или процентной
разницы CPU/памяти. BudgetExceeded генератора не приписывается серверу.
Дальнейший ремонт общего movement/control нагрузчика — отдельная задача.

После окончания native сборок выполнен последний bounded baseline: **PASS30s**.
Он использует точно тот же generator, что clean current (добавлен только recorder
download latency), SHA256 worker DLL:
`d3572b0a163642b55ae46a7cb8190ac3ef39c6dd3e219c825e85d5f7ac2ad767`.
Совпадает вся карта `binarySha256` worker dependencies. Отличается только isolated
server runtime с описанным compatibility overlay. Ни компиляции, ни других benchmark
в обоих clean измеряемых окнах не было.

| Одинаковый steady512/group25/Maximum4/20Hz/30s | Clean baseline | Clean current |
|---|---:|---:|
| Отправлено поз | 307200 | 307200 |
| Частота источников, Hz | 19.9983–19.9995 | 19.9985–19.9997 |
| Missed intervals /admission drops | 0 /0 | 0 /0 |
| Получено полных поз | 212471 | 211723 |
| Active subscription seconds | 61180.54 | 60929.21 |
| Получено на active subscription second, Hz | 3.4729 | 3.4749 |
| P95 receive gap по workers, ms | 124.95–2752.05 | 655.96–986.90 |
| P95 delivery age по workers, ms | 89.69–152.68 | 142.34–158.92 |
| Server CPU, core equivalent | 1.625 | 1.660 |
| Server private peak, MiB | 142.65 | 139.15 |
| Model upload/download payload в steady | 0 /0 | 0 /0 |

[Clean baseline](../../build/benchmarks/native-nif-baseline-steady-512-clean/results.json),
[лог](../../build/native-nif-benchmark-baseline-512-clean.log).
Это по одному запуску каждой версии на одном host, а не статистическая оценка
ускорения. Throughput ограничен одним32MiB/s budget и практически одинаков;
распределение gaps изменилось после ротации исчерпанного budget, но они включают
неактивные AOI интервалы. Проверяемое устранение голодания доказывает regression
с фиксированными subscriptions. Неполные ранние runs остаются в истории результатов,
а успешный clean baseline не даёт оснований объявлять их первичной ошибкой сервера.

Два ранних текущих прогона также сохранены: нагрузчик ошибочно считал временный
BudgetExceeded на model/pose lane фатальным. После исправления bounded model FIFO,
pose-drop учёта и steady readiness barrier прошёл полный warmed run.
[Первый](../../build/benchmarks/native-nif-v22-steady-512/results.json),
[второй](../../build/benchmarks/native-nif-v22-steady-512-corrected/results.json).

## Traffic overhead и воспроизведение

Каждый client report содержит `transportDuringLoad`: ENet byte/datagram counter
в точном окне, application bytes по lanes0..4. ENet bytes включают собственные
заголовки, fragment commands, ACK и retransmits, но не IP/UDP headers.
Оценка IPv4+UDP overhead —28 bytes на datagram. Вычитание application из ENet
включает boundary backlog/drop и эффекты reliable доставки; это не чистый размер header.

Для warmed run до fairness, сумма всех 512 клиентов за окно (включая движение):

| Направление с точки зрения клиентов | Application bytes | ENet bytes | UDP datagrams | Оценка IPv4+UDP bytes |
|---|---:|---:|---:|---:|
| Отправлено | 1461654752 | 1488807502 | 2215459 | 62032852 |
| Получено | 2211997129 | 2297191678 | 2117687 | 59295236 |

В финальных clean запусках теми же counters:

| Сценарий /направление | Application bytes | ENet bytes | UDP datagrams | Оценка IPv4+UDP bytes |
|---|---:|---:|---:|---:|
| Steady512 отправлено | 1461622034 | 1486371440 | 2259626 | 63269528 |
| Steady512 получено | 2189490252 | 2293485383 | 2144202 | 60037656 |
| Cold8 отправлено | 33157423 | 34798699 | 52217 | 1462076 |
| Cold8 получено | 142591664 | 146098138 | 128479 | 3597412 |

Отдельно lane4 поз: C2S1443750400 bytes, S2C999492926 bytes. ENet counters не
раскладываются по lane, поэтому точный transport overhead только поз из этого
запуска не выводится. Chunk payload в измеряемом steady окне равен нулю; lane3 включает
offers/removals и другие metadata. Cold передача этого13MiB asset измерена отдельным
8-peer сценарием выше;512 cold не запускался.

Команда текущего warmed512 и final clean fairness run отличается только output path:

```powershell
$poses = 1..301 | ForEach-Object { "build/native-nif/movement-poses/pose-$_.zst" }
python -u Scripts/benchmark_phantom.py --mode steady --clients 512 --publishers 512 --workers 8 --scenario sparse --rate 20 --seconds 30 --actor-values-hz 4 --maximum 4 --timeout 1800 --model-file build/native-nif/movement-poses/model.zst --raw-model-bytes 27740619 --channels 327 --pose-file $poses --output build/benchmarks/native-nif-v22-steady-512-fair-clean
python -u Scripts/benchmark_phantom.py --mode cold --clients 8 --publishers 2 --workers 2 --scenario sparse --rate 20 --seconds 45 --actor-values-hz 4 --maximum 4 --timeout 1800 --model-file build/native-nif/movement-poses/model.zst --raw-model-bytes 27740619 --channels 327 --pose-file $poses --output build/benchmarks/native-nif-v22-cold-8-clean
```

Для baseline добавлялся `--server-benchmark
build/native-nif/server-baseline-source/tests/Dreamsleeve.Server.NetworkBenchmarks/bin/Release/net10.0/Dreamsleeve.Server.NetworkBenchmarks.dll`.
Схема overlay и hashes сохранены рядом с git archive. Для сборки обоих использован
.NET10 Release; baseline restore выполнен только из локального NuGet cache.
Все generated TOML, fixtures provenance, stdout, process samples и результаты
остаются под ignored `build/`. Игровые assets и диагностические архивы не входят в dist.
