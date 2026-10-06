# Локальная диагностика фантомов

Диагностика нужна для измерений текущего production codec перед оптимизацией
трафика: исходные float-позы нельзя восстановить из уже квантованных пакетов.
Сетевой контракт она не меняет. Флаг сборки `DREAMSLEEVE_DIAGNOSTICS` независим
от `DEBUG`; xmake включает его через отдельную опцию, по умолчанию выключенную.

```powershell
xmake f --diagnostics=y
xmake build -y Dreamsleeve.Client
xmake build -y Dreamsleeve.Client.Tests
xmake run Dreamsleeve.Client.Tests --test-case="Diagnostic*"
```

Оптимизированная DLL находится в
`build/diagnostics/windows/x64/releasedbg/Dreamsleeve.Client.dll`. Обычная DLL
остаётся в `build/windows/x64/releasedbg`. Для возврата к обычной сборке:

```powershell
xmake f --diagnostics=n
xmake build -y Dreamsleeve.Client
```

При выключенном флаге модуль recorder, отдельный поток, меню записи и точки
наблюдения не компилируются. Обычные счётчики фантомов остаются. Диагностическая
DLL экспортирует `DreamsleeveDiagnosticsBuild`; `package_dist.py` отказывает
при обнаружении её маркера, в том числе с `--skip-build`; обычная полная
упаковка сначала конфигурирует `--diagnostics=n` и собирает свежую обычную DLL.
Не копировать диагностическую DLL в release вручную. Файлы `.phdiag` и каталог
записи исключены из dist/git.

## Как записать

В SKSE Menu Framework открыть **Dreamsleeve → Запись фантомов**, выбрать
сценарий и нажать **Начать запись**. Закрыть меню и выполнять движение. Частота
берётся из существующей настройки фантомов; для первого сравнения достаточно
20 Гц. Продолжительность — 15 или 30 секунд от первого захваченного кадра.
Остановка доступна в том же меню. Настройки сценария не являются пользовательской
конфигурацией и не переписывают `ui.toml`.

Сервер не требуется: локальный capture использует те же источник, канонизацию,
проверку модели и production codec. При подключённой публикации дополнительно
записываются реальные результаты encoder и пакеты, принятые ENet к отправке,
а также movement с context/sequence/source timestamp. Это не подтверждение
доставки получателю. При выключенном сервере этих событий в записи нет.

Нужные сценарии: покой; ходьба/повороты; спринт; бой/оружие; переключение камеры;
смена снаряжения и SMP. Отдельно проверить лицо, волосы, руки/ножны и смену модели.
Новая внешность записывается как новая generation, а не обрывает весь архив.
Загрузка сохранения, переход в другой cell/world или выход завершает запись.

Архив находится рядом с логом SKSE:
`DreamsleevePhantomDiagnostics/<время>-<сценарий>-<номер>/capture.phdiag`.
`summary.json` содержит частоту, счётчики, причину завершения, p50/p95 затрат
capture и production encoder. Сам диагностический worker дополнительно
кодирует локальные кадры; это имеет цену CPU/IO, поэтому его показатели не
считаются измерением чистой недиагностической сборки. `captureMs` первого кадра
включает подготовку модели. Нулевая численность `encoded` означает отсутствие
измерений production encoder, а не нулевую стоимость кодирования.

## Ограничения и владение

`Diagnostics/PhantomRecorder` владеет ограниченной очередью detached immutable
значений и своим writer thread. Игровой поток отдаёт shared model/snapshot;
сетевой поток отдаёт только разрешённые позы/movement, без auth/chat/config.
Файловый IO, повторное кодирование и инвентаризация диска выполняются writer.
RE/GFx/D3D/engine pointers в очереди и формате отсутствуют.

Очередь ограничена 64 заданиями и консервативно учтёнными 128 МиБ, включая
выполняемое задание. Дополнительные codec scratch buffers ограничены обычными
лимитами модели/позы. Общий каталог записей ограничен 1 ГиБ, архив — 4096
records и 32 generations. Старые файлы автоматически не удаляются. Лимит
очереди/диска завершает запись с явной причиной и счётчиком пропусков.
Ожидание первого кадра ограничено 60 секундами. `.partial` означает незавершённый
или повреждённый архив; успешное завершение переименовывает его в `.phdiag`.
Shutdown прекращает admission и дожидается сохранения оставшихся заданий.

## Инспекция и формат

```powershell
python Scripts/inspect_phantom_diagnostics.py "<папка записи>" --output build/phantom-report.json
python Scripts/inspect_phantom_diagnostics.py "<папка записи>" --extract build/phantom-codec-inputs
```

Инспектор использует только стандартную библиотеку Python. Он проверяет длины,
SHA256 моделей, соответствие заголовков oracle/production, собирает размеры
каналов/bounds/deformations, реальную частоту, интервалы и отличие корня 3D от
actor movement. В `--extract` доступны модель `.zst`, исходные позы
`.original.bin`, production pre-Zstd `.quantized.bin`, `.zst` поз и реальные
protobuf envelopes. Распаковка Zstd в Python для статистики не требуется;
модель можно читать текущим `ReadAsset`, позу — `ReadSnapshot` в native tool.

Little-endian, без struct padding. Header: `DLPDIAG1` (8 bytes), archive version,
текущий protocol version, asset version (по uint32). Далее records:
`kind:uint32, length:uint32, payload[length]`.

| Kind | Содержание |
|---|---|
| 1 Model | generation:uint64, raw/compressed length:uint32, SHA256:32 bytes, полный production Zstd asset |
| 2 Sample | captureMs:float64, firstPerson:uint8, actor Movement, три uint32 длины, original Snapshot, pre-Zstd production bytes, independent production Zstd pose |
| 3 Encoded | generation/sequence/context/sampleTime:uint64, encodeMs:float64, compressed length:uint64 |
| 4 PosePacket | acceptedAtUs:uint64, полный ClientPosePacket protobuf |
| 5 MovementPacket | acceptedAtUs:uint64, Movement, envelope length:uint64, точные байты movement envelope |
| 6 CaptureFailure | observedAtUs:uint64, существующий Phantom::Failure:uint32 |

Movement: context/sequence/sampleTime:uint64 и position/Euler XYZ по float32.
У actor sample, снятого вместе с capture, context/sequence равны 0; его timestamp
равен времени исходной позы. Euler — radians, root rotation — quaternion.
Original Snapshot: generation/sequence/context/sampleTime:uint64, origin XYZ
float32, counts channels/bounds/deformations:uint32. Channel: position XYZ,
quaternion XYZW, scale float32, hidden:uint8. Bound: center XYZ/radius float32.
Deformation: geometry/count:uint32, все positions XYZ, затем normals XYZ float32.
Исходный локальный snapshot имеет capture context 1; production event содержит
фактический сетевой context. Сопоставлять по generation/sequence/time, а не
подменять одно другим. Частоты capture/encoding/ENet отправки измеряются отдельно.

Сборка и native-тесты проверяют формат, сохранение до квантования, совпадение
с production bytes, смену generation, opt-in, лимиты, restart и shutdown.
Проверка меню и реального захвата в игре выполняется отдельно.

## Исправление игрового потока, 06.10.2026

Первая игровая запись `sprint` содержала 81 movement, 66 ошибок capture и
ни одной позы. Лог показал `capture.main-thread`: Graphics запоминал поток
`kDataLoaded`, который не является владельцем игрового обновления.
Теперь Graphics инициализируется при первом входе в проверенный хук
`Main::Update`, а capture выполняется из `Phantoms::Tick` после исходного
обновления игры, в том же потоке, что playback, Clear и Shutdown.
Отдельный хук `PlayerCharacter::Update` удалён из live feature; проверки
потока в Capture/Scene/Graphics сохранены. Новая строка лога
`Phantom graphics bound to Main::Update thread ...` фиксирует привязку.
Исправление применяется к обычной и диагностической DLL, без изменения
протокола или формата архива. Захват реальной модели после этого исправления
нуждается в повторной проверке в игре.

Проверка 06.10.2026: диагностическая сборка — 371/371 native-тестов,
обычная — 364/364; в обеих один тест реального UDP запускается отдельно и
по умолчанию пропущен. Семь диагностических тестов включают завершение по
таймеру без нового кадра и освобождение очереди при лимите records. Инспектор
прочитал тестовый архив, извлёк входы кодека и отклонил усечённые архивы и
повреждённый SHA256 модели. В игре эта версия рекордера ещё не проверена.
