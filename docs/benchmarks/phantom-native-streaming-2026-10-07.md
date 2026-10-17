# Native NIF: обновления и streaming, 07.10.2026

Продолжение после `a12bd7f`, protocol 23 / NIF container 2 / pose 3.
Ниже отделены изменения production, офлайн оценка и игровые ограничения.

## Внешность и точки обновления

7 записей, 2 107 кадров, 27 NIF. Последняя `1791319299286-idle-0` — по описанию
пользователя постоянная смена оружия и заклинаний, 15 с / 301 кадр / 3 NIF.
12→13 меняет только dynamic FaceGen (максимум0,02332 единицы);13→14 удаляет
Warhammer_Mesh. Сам факт equip/re-equip не означает новую геометрию.

Из 20 переходов между моделями 17 не меняли набор/packed geometry/skin partitions:
изменялись маленькие FaceGen positions. Относительно первого NIF каждой записи
максимум общей dynamic деформации0,06848 единицы. Сравнение соседних моделей
давало максимум0,08431. Оставшиеся3 перехода меняют GlowMeshLightSpell:1
(появление/исчезновение) или удаляют молот. Это данные сериализованных NIF;
они не доказывают неизменность всех live pointers между захватами.

Production теперь сравнивает sparse probe с принятой моделью, без округлённых
hash bins; порог деформации0,25, устойчивость1с с допуском0,0625. Состав ждёт250мс.
Это подавляет измеренную малую динамику и сохраняет накопленное отклонение
морфа. Порог не обещает распознать любые лицевые анимации. Отдельный исключающий
список по имени GlowMesh не добавлен: значимая новая геометрия остаётся причиной
asset update. Истинная мимика не передаётся отдельным vertex stream.

IDA подтвердила queued ветвь AIProcess::Update3DModel_Impl; SKSE NiNode callback
сам по себе не доказывает завершения. Hook ставится на вызов Clear3DFlags в
завершённой ветви SE/AE/VR, проверяет target и только запрашивает atomic audit.
Все адреса и ABI — в Hooks. Подробности в [runtime audit](../PhantomRuntimeRu.md).

## Переход моделей

Exchange владеет текущим и удерживаемым поколениями. Game сохраняет два native
Source; Worker — immutable PreparedAsset. Новый snapshot содержит optional
previous_sample того же времени/контекста, но со своим generation/sequence и
полной таблицей поз старого NIF. Смена индексов каналов между NIF безопасна,
поскольку чужая поза никогда не применяется к неподходящей сцене.

Пока идёт upload/download, старая сцена получает свою живую позу. При удалении
старой геометрии она временно следует сохранённой привязке к живому родителю;
на старой внешности возможны новое движение/каст со старым оружием. Заменённый
root/skeleton без сохранённых привязок так продолжать нельзя: source инвалидируется,
незавершённая публикация отменяется, начинается новый захват в правильном контексте.

Новый NIF появляется только после полной проверки bytes/hash/native Load и
успешного Apply+Attach. Получатель шлёт Displayed с view/generation; сервер
шлёт Settled источнику после подтверждений выбранного audience или 30 с после
commit. Новые изменения до этого объединяются, а не отменяют upload на каждый
equip. На таймауте bridge прекращается; медленный получатель может временно
перейти к обычному fallback. Временный трафик пары поз примерно вдвое выше.

При недостатке RAM replacement metadata ждёт без incoming asset allocation,
view уже обновлена и previous playback продолжает принимать позы. Planner
повторяет admission; current native scene и detached previous включены в бюджет.
Ошибка подготовки восстанавливает старую публикацию. Окончательный отказ upload
оставляет старые Source/PreparedAsset: клиент отправляет самостоятельную позу
старого NIF, сервер хранит оба sequence floors до смены ready asset. Следующее
изменение внешности может предложить новое поколение; rejected asset не становится
базой bridge. При отсутствии прежней модели остаётся обычный fallback.
Откат подготовки не переотправляет уже committed manifest; счётчик новых
поколений не откатывается вместе с native Source. Ошибка Load/Apply кандидата
не уничтожает пригодную текущую сцену. AOI removal/session/context остаются
существующими границами, нового пространственного индекса нет.

## Упаковка поз

Байты 23-байтовых каналов и 16-байтовых bounds переставлены в отдельные плоскости
перед Zstd1; заголовок 60 байт сохранён, version3. Все XYZ int32 с шагом1/16,
quaternion int16×4, scale uint16/1024 и visibility сохранены. Дополнительной
квантованной ошибки перестановка не вводит; зависимости от предыдущего UDP нет.

На 7 записях прежняя максимальная ошибка: позиция0,05413 единицы,
rotation0,003433°, scale0,00042969; не следует выдавать это за ошибку нулевой
квантованности — нулевая только дополнительная ошибка перестановки байтов.
Production C++ replay проверил все 7 архивов: 2 107 кадров и 27 моделей.
Исходные protocol 22 / pose 2 читаются только офлайн-адаптером; сетевой decoder
принимает только protocol 23 / pose 3. В каждой позе выполнены CheckSnapshot,
production WriteSnapshot/ReadSnapshot, сравнение position/scale/visibility/bounds;
quaternion после повторной нормализации проверен с суммарным допуском 0,00013.
Нативные игровые NiStream Load/Apply этим тестом не выполняются.

| Запись | Полный старый C2S, среднее B | Новый C2S, среднее / p95 B |
|---|---:|---:|
| `1791317521446-walk-turn-0` | 4733.18 | 3935.71 / 4006 |
| `1791317603796-sprint-0` | 4689.33 | 3964.77 / 4046 |
| `1791317657992-idle-0` | 4650.59 | 3895.67 / 3942 |
| `1791317778002-combat-0` | 4720.00 | 3963.97 / 4030 |
| `1791317824302-camera-0` | 4703.52 | 3940.44 / 4038 |
| `1791317877512-combat-0` | 4670.79 | 3918.59 / 3994 |
| `1791319299286-idle-0` | 4730.39 | 3954.29 / 4073 |

По всем кадрам: **4699.69 → 3939.06 B (−16.18%)**.
Новый payload в среднем 3 917,83 B; полный protobuf packet p95 4 026 B, max 4 100 B.
При новом default 10 Гц это 38,47 KiB/s C2S, при 20 Гц — 76,94 KiB/s без ENet/IP/UDP; временный bridge из двух поколений
приблизительно удваивает pose payload. Старый полный packet рассчитан из
архивного payload и точной protobuf формы; новый сериализован самим C++ Wire.

| Только codec, ms | Среднее | p95 | max |
|---|---:|---:|---:|
| WriteSnapshot (quantization + byte planes + Zstd) | 0,09877 | 0,1476 | 0,2935 |
| ReadSnapshot (Zstd + byte planes + validation) | 0,01523 | 0,0215 | 0,0948 |

Это wall-clock одного последовательного прохода, без параллельной сборки или
512-client load. Здесь нет Clone/Save, Load, native preparation или Apply;
затраты этих игровых этапов после изменения порога ещё не измерены. Старые
игровые capture-метрики оставлены в отчёте исходных записей, не выданы за новые.
Результаты: `build/native-nif/production-v23-replay-final/summary.json`, для
каждого архива — `poses.csv` и `replay.log`. Reader поддерживает точный выбор
архива/его каталога через тот же production load/decode path.


## Дельта NIF: измерение, не production

`Scripts/measure_native_nif_delta.py` использует существующие native NIF блоки
как границы копирования/XOR байтов. Сцену не сортирует и не создаёт geometry schema.
Результат проверен точным сравнением reconstructed NIF с оригиналом и Zstd roundtrip.
20 переходов: full Zstd3 NIF265284610B, experimental recipes1515378B (−99,43%).
Диапазон35 092–104 352B. Последний equip archive:12→13=77 078B,
13→14=35 092B вместо13,31/13,22МБ. Первоначальный NIF всё равно необходим.
Обычный Zstd dictionary=предыдущий NIF практически не уменьшил эти полные файлы.

Это нижняя оценка transport payload без нового protobuf/chunk overhead. Время
Python/NumPy проверки включает дополнительные копии и не является production
encode latency. Эффект зависит от наличия точной базы у получателя. Для внедрения
нужны immutable base hash, ограничение цепочек/удержания, восстановление при cache
miss, полный target hash и обычная NIF validation после восстановления. Сейчас
сеть передаёт полный NIF; патчить живую native scene и вводить второй renderer
не стали. Эта оптимизация оставлена отдельным следующим этапом, а не заявлена готовой.

## Одинаковый ENet-сценарий на 512 игроках

Два успешных последовательных прогона по 30 с: 512 publishers, 512 отдельных
ENet hosts, 8 generator workers, spatial sparse с группами максимум 25 игроков,
20 Гц, максимум 4 выбранных фантома на получателя, actor values 4 Гц. Обе стороны
используют текущий protocol 23. В первом прогоне сервер пересылает старую
interleaved упаковку как opaque payload, во втором — byte planes той же записи
movement (301 кадр, 327 каналов). Это изоляция размера пакета, не сравнение CPU
исторического protocol 22 с новым сервером. Native decoder/render в стенде нет.

Для этого A/B зафиксированы прежние лимиты: pose fanout 32 MiB/s суммарно, 2 MiB/s на peer;
model transfer 5 MiB/s суммарно на сервере и 5 MiB/s на peer. Модель 27 740 619 B
raw / 13 288 628 B compressed была предварительно положена в server cache;
получатели симулируют тёплый cache. В измеренном steady interval model payload
в обе стороны **0 B**. Отдельно setup отправил 19 464 192 / 23 396 352 B upload
payload attempts: стенд может начать окно после Transfer до обработки cached
Complete. Это не стоимость постоянного потока и не новый raw NIF каждого игрока.

| Метрика | Interleaved | Byte planes |
|---|---:|---:|
| Средний полный C2S pose packet, B | 4 699,77 | 3 950,81 |
| Средний полный S2C pose packet, B | 4 705,21 | 3 949,31 |
| Отправка источником, Гц | 19,91 | 19,73 |
| Получено на активную подписку, Гц | 3,455 | 4,323 |
| Pose C2S, MiB/s суммарно | 45,694 | 38,052 |
| Pose S2C, MiB/s суммарно | 31,326 | 32,928 |
| Server CPU, эквивалент ядра | 1,670 | 1,733 |
| Server private peak, MiB | 147,79 | 145,33 |
| Runtime tick p95 / p99, ms | 0,474 / 10,335 | 0,445 / 10,795 |
| Age p95, диапазон workers, ms | 210–239 | 372–411 |
| Receive gap p95, диапазон workers, ms | 893–1135 | 689–785 |
| Incoming queue age p95, ms | 24,63 | 18,99 |
| Outgoing queue age p95, ms | 46,50 | 53,60 |
| Pose admission drops у отправителей | 1 346 | 4 201 |
| Все generator budget backpressure attempts | 44 647 | 170 324 |

Полный пакет уменьшился на 15,94%; поступление на активную подписку выросло
на 25,14% при исчерпанном общем byte budget. Это **не** подтверждение 20 Гц
для 512 игроков: четыре получателя на каждого при полном потоке потребовали бы
около 184 / 154 MiB/s только pose protobuf. Размер уменьшился, CPU не снизился,
а age во втором прогоне хуже. Это один A/B на одной машине с loopback и всеми
512 клиентами; статистическую причинность различия CPU/latency он не доказывает.
Начальный burst credit также влияет на среднюю полосу короткого прогона.

Транспорт измерен отдельно по всем lanes (включая глобальный actor-values fanout,
movement, control, ACK/retransmit), а не назван overhead одних поз:

| Все lanes, MiB/s | Interleaved | Byte planes |
|---|---:|---:|
| ENet bytes клиент→сервер | 47,384 | 39,907 |
| ENet bytes сервер→клиенты | 73,673 | 75,045 |
| ENet counter минус application bytes, C2S | 1,091 | 1,259 |
| ENet counter минус application bytes, S2C | 4,186 | 4,385 |
| Расчёт IPv4+UDP headers по datagram count, C2S | 2,164 | 1,872 |
| Расчёт IPv4+UDP headers по datagram count, S2C | 2,025 | 1,963 |

Разность счётчиков включает границы окна/очередей, ACK и retransmit; IPv4/UDP
посчитаны как 28 B на датаграмму, без Ethernet/VPN. Нельзя складывать эти
all-lane числа с pose-only packet size и выдавать результат за стоимость одной позы.
Generator теперь coalesce-ит Displayed по source и считает BudgetExceeded как
backpressure с отказом текущей отправки, а не фатальную ошибку. Ранние прогоны,
остановленные старым harness, сохранены, но в таблицу не включены.

Артефакты: `build/benchmarks/native-v23-{interleaved,planar}-512-measured`,
`build/native-nif/benchmark-v23-summary.json`. Каждый содержит config, provenance,
client/server counters и сырые фазовые измерения.

## Профиль 10 Гц / 128 MiB/s

После запроса пользователя defaults переведены на 10 Гц для movement и поз.
Используются существующие настройки: client.playerSampleIntervalMs=100,
ui.chat.phantomSampleRate=10, Runtime.Presence.ReplicationIntervalMs=100,
Phantoms.PoseIntervalMs=100 и ReplicationIntervalMs=100. Общий pose fanout budget
поднят до 134217728 B/s; model budgets остаются 5 MiB/s. Соответствующие ключи
позволяют вернуть 20 Гц; таблица находится в [PhantomsRu](../PhantomsRu.md).

Интерполяция остаётся покадровой: movement delay 150 мс, native playback delay
100 мс, extrapolation максимум 100 мс. Общий AdvanceSample сохраняет сетку тиков
после опоздания и пропускает пропущенные слоты без повторных одинаковых снимков.

Третий последовательный прогон использует тот же 30-секундный сценарий:
512 источников, группы максимум 25, максимум 4 фантома у получателя, actor values
4 Гц, те же реальные pose fixtures. Изменены сразу частота и бюджет — это сравнение
профилей, а не изолированный эффект одного параметра. Сборки одновременно не шли.

| Метрика | Новый профиль |
|---|---:|
| Отправка источником, Гц | 9.999 |
| Получено на активную подписку, Гц | 8.242 |
| Полный C2S / S2C pose packet, среднее B | 3950.63 / 3956.46 |
| Pose C2S / S2C, MiB/s суммарно | 19.288 / 62.762 |
| Server CPU, эквивалент ядра | 1.434 |
| Server private peak, MiB | 127.19 |
| Runtime tick p95 / p99, ms | 0.138 / 12.291 |
| Age p95, диапазон workers, ms | 194–202 |
| Receive gap p95, диапазон workers, ms | 398–559 |
| Incoming / outgoing queue age p95, ms | 24,63 / 87,03 |
| Pose admission drops | 10 |
| Generator budget backpressure attempts, все lanes | 4451 |
| ENet C2S / S2C, MiB/s, все lanes | 20.295 / 100.599 |
| ENet counter минус application C2S / S2C, MiB/s | 0.570 / 3.485 |
| IPv4+UDP headers C2S / S2C, MiB/s | 1.169 / 2.370 |

Steady model payload — 0 B в обе стороны; setup upload attempts —
25 821 184 B, download — 0 B при прогретом cache. По сравнению с профилем
20 Гц / 32 MiB/s получено 8,24 вместо 4,32 Гц на активную подписку. Это улучшение,
но **не стабильные 10 Гц**: хвост интервалов 398–559 мс и outgoing queue p95 87 мс
остаются. Повышение bandwidth budget само по себе не устраняет задержки
общего транспорта/очередей при глобальном actor-values fanout. Этот прогон
не устанавливает единственную причину пропусков; её нужно профилировать отдельно.

Результаты: `build/benchmarks/native-v23-planar-512-10hz-128m` и третий элемент
`build/native-nif/benchmark-v23-summary.json`. Сервер не декодирует геометрию,
а стенд не проверяет визуальную плавность native playback на 10 Гц.

## Свидетельства и игровой статус

Автоматические проверки текущего профиля:

- Обычные C++ tests: 385/385, 9 454 assertions, 3 opt-in skipped.
- Diagnostic C++ tests: 398/398, 9 753 assertions, 4 opt-in skipped.
- Managed server: 609/609; UI: 94/94, production bundle собран.
- Обычный real UDP smoke: 1/1, 2 010 assertions; тот же сценарий,
  fragmented paired pose 3 444 B.
- Diagnostic real UDP smoke: 1/1, 2 020 assertions; cold 282 998 B,
  окно 4 chunks, fragmented paired pose 3 446 B, loss discarded,
  rollover unreliable, warm cache 0 chunks. Один предшествующий запуск
  во время тяжёлой компиляции истёк на ожидании пары поз; повтор без
  конкурирующей нагрузки прошёл. Тест ждёт один unreliable snapshot,
  поэтому этот успех не означает гарантированной доставки каждой позы.
- Production ReplayReader: 7/7 архивов, 2 107 кадров, 27 native assets;
  три описанных 512-player ENet прогона завершились успешно.

Логи проверок лежат в `build/native-nif/10hz-*`, UDP —
`build/native-nif/udp-smoke-v23-diagnostic-10hz-quiet`.

Исходные архивы не изменены. Derived files:
`build/native-nif/equipment-recording-2026-10-07`,
`build/native-nif/accepted-appearance-comparison.json`,
`build/native-nif/nif-delta-measurements.json`.
Предыдущие 6 записей пользователь проверил визуально на SE. Новые hook, пороги и
переход поколений ещё требуют игры. AE/VR проверены статически, не игровым прогоном.

## Сборка и установка

Полный release dist собран штатным `Scripts/package_dist.py --skip-build`
из свежих обычной DLL, UI и `dotnet publish` сервера:
`S:\Programming\Dreamsleeve\dist\Client` и `dist\Server`. Протокол 23 требует
одновременного обновления обеих сторон. Пакет включает ESP из Plugin, Papyrus API,
UI и примеры client/server/ui конфигурации. Проверены отсутствие диагностического
маркера в release DLL, игровых NIF, архивов записей, IDB и сторонних клиентских DLL.

Пять пользовательских файлов dist сохранены с проверкой SHA256. Только
согласованные rate/budget keys изменены перед упаковкой; прочие значения
проверены сравнением TOML. В `Plugin/client.toml` исходные пользовательские
правки побайтово сохранены: при обратном применении нашего rate/comment hunk
получается исходный SHA256 `10df191096b4a31d267f24c0fe73c17c19e414b04414cdaffe6ce67d2145e257`.

Диагностическая DLL и свежий UI установлены при закрытом Skyrim в
`F:\MO2 - Skyrim - VanillaLike\mods\Dreamsleeve`. В client.toml изменён только
playerSampleIntervalMs=100. Фактический UI-конфиг профиля находится в
`mods\Output_SKSE\SKSE\Plugins\Dreamsleeve\ui.toml`: изменён только
phantomSampleRate=10. Backup заменённых файлов и конфигов:
`build/native-nif/install-backup-20261007-053426`. Пользовательский theme.user.css
не заменялся. Локальный сервер для пользователя автоматически не запускался.

| DLL | Bytes | SHA256 |
|---|---:|---|
| Release / dist | 5 481 472 | `913f12e3cc6dd5dc03162b41e179c11eea98b8cbc190f8325133b2a999d496c0` |
| Diagnostic / установленная | 5 562 880 | `9777a97ee28045a98d48a5072aff6be97966ea4c7b24f6d557b2585b7e580a16` |

Манифесты: `build/native-nif/final-binaries-v23-10hz.json`,
`installed-diagnostic-10hz.json`, `dist-preserved-10hz.json`.
Лог упаковки: `build/native-nif/package-v23-10hz.log`.

Отдельные оставшиеся вопросы: общая очередь/глобальная рассылка actor values
в нагрузочном сценарии; gameplay QA новой DLL и AE/VR; completion сторонних
GPU-only/morph pipelines. Audience-driven capture, сектор камеры, delta NIF и
death clips описаны как следующие этапы в плане и не заявлены внедрёнными.
