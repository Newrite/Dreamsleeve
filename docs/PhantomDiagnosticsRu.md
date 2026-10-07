Актуальное дополнение07.10.2026 после двухклиентского теста: диагностическая DLL
раз в5 секунд пишет `[Phantom network]` по каждому подписанному источнику:
ожидаемое/показанное поколение, asset state (1=Loading,2=Ready,3=Unavailable),
число samples, последний sequence, максимальные source/arrival gaps среди
последних8 снимков, возраст последнего принятого снимка и запас буфера ahead.
Отрицательный ahead означает выход за последний mapped sample. Это локальная
история уже декодированных поз, **не** независимый wire-loss counter и не p95
за всю сессию. Лог `[Phantom] native scene displayed` фиксирует реальное
успешное Apply/Attach, а не только скачивание. Ошибка материала содержит RTTI/skin;
конверсия BSEffect в native ghost lighting пишет имя поверхности.

Разбор входных логов и результаты: [двухклиентский тест](benchmarks/phantom-two-player-2026-10-07.md).

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
10 Гц (текущий default). Для сравнения со старыми записями можно выбрать 20 Гц.
Продолжительность — 15 или 30 секунд от первого захваченного кадра.
Остановка доступна в том же меню. Настройки сценария не являются пользовательской
конфигурацией и не переписывают `ui.toml`.

Сервер не требуется: локальный capture использует те же источник, канонизацию,
проверку модели и codec. Пока активна запись, новая публикация своего фантома
приостановлена даже при подключении к серверу. После записи она возобновляется.
Локальный буфер позы и codec используют тот же `Limits`, что сеть:
raw256 КиБ/compressed128 КиБ. Dynamic vertex stream отсутствует.
Формат новых архивов DLPDIAG2, version2, protocol23, asset2, pose3.
Офлайн ReplayReader читает также protocol22/pose2 тем же валидатором.
Это поддержка исторических записей; сетевой decoder принимает только pose3. Предыдущие пользовательские
DLPDIAG1 записи остаются на диске неизменными; они относятся к neutral pipeline
и не подаются нативному renderer.
События movement продолжают записываться; `encoded`/`sent` относятся к
production-пути и в локальной записи обычно равны нулю. Полные байты codec
сохраняются внутри каждого Sample. Нулевые счётчики отправки не означают
отсутствия записанных поз — смотреть `samples` и длительность.

Нужные сценарии: покой; ходьба/повороты; спринт; бой/оружие; переключение камеры;
смена снаряжения и SMP. Отдельно проверить лицо, волосы, руки/ножны и смену модели.
Новая внешность записывается как новая generation, а не обрывает весь архив.
Загрузка сохранения, переход в другой интерьер/WRLD или выход завершает запись.
Обычная ходьба между наружными CELL одного WRLD продолжает запись; временное
отсутствие готового cell/3D пропускает кадр, сохраняя источник и запись.

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
Файловый IO, повторное кодирование и проверка свободного места выполняются writer.
RE/GFx/D3D/engine pointers в очереди и формате отсутствуют.

Очередь ограничена 64 заданиями и консервативно учтёнными 128 МиБ, включая
выполняемое задание. Дополнительные codec scratch buffers ограничены лимитами модели и
отдельным диагностическим лимитом позы. Общей квоты каталога и квоты размера архива нет: предыдущие записи не блокируют
новые. Перед записью очередного record проверяется фактическое доступное место
на целевом томе; 64 КиБ оставляются для завершения архива и summary. Только
нехватка этого места даёт `disk-space-low`. Архив ограничен 4096 records и
32 generations. Старые файлы автоматически не удаляются. Временное
заполнение очереди пропускает текущий кадр со счётчиком dropped и продолжает
запись после освобождения места. Задание, которое само больше всей очереди,
и исчерпание диска завершают запись с явной причиной.
Ожидание первого кадра ограничено 60 секундами. `.partial` означает незавершённый
или повреждённый архив; успешное завершение переименовывает его в `.phdiag`.
Shutdown прекращает admission и дожидается сохранения оставшихся заданий.

## Инспекция и формат

Сравнение новых native записей, поколений NIF и обратимых вариантов упаковки:
`Scripts/analyze_native_phantom_recordings.py` (offline NumPy и локальный
Zstd bridge из `Scripts/phantom_zstd_probe.c`). Он использует новый output
вне оригиналов, проверяет production compressed bytes и не изменяет DLL/сеть.
[Измерения и воспроизведение эксперимента](benchmarks/phantom-native-recordings-2026-10-07.md).

```powershell
python Scripts/inspect_phantom_diagnostics.py "<папка записи>" --output build/phantom-report.json
python Scripts/inspect_phantom_diagnostics.py "<папка записи>" --extract build/phantom-codec-inputs
```

Инспектор использует только стандартную библиотеку Python. Он проверяет длины,
SHA256 моделей, соответствие заголовков oracle/production, собирает размеры
каналов/bounds, реальную частоту, интервалы и отличие корня 3D от
actor movement. В `--extract` доступны модель `.zst`, исходные позы
`.original.bin`, production pre-Zstd `.quantized.bin`, `.zst` поз и реальные
protobuf envelopes. Распаковка Zstd в Python для статистики не требуется;
модель можно читать текущим `ReadAsset`, позу — `ReadSnapshot` с
`Diagnostics::CaptureLimits()` в native tool.

Little-endian, без struct padding. Header: `DLPDIAG2` (8 bytes), archive version,
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
float32, counts channels/bounds:uint32. Channel: position XYZ,
quaternion XYZW, scale float32, hidden:uint8. Bound: center XYZ/radius float32.
Исходный локальный snapshot имеет capture context 1; production event содержит
фактический сетевой context. Сопоставлять по generation/sequence/time, а не
подменять одно другим. Частоты capture/encoding/ENet отправки измеряются отдельно.

Сборка и native-тесты проверяют формат, сохранение до квантования, совпадение
с production bytes, смену generation, opt-in, лимиты, restart и shutdown.
Проверка меню и реального захвата в игре выполняется отдельно.

## Использование существующего прототипа

`Scripts/native_nif_fixture.py SOURCE OUTPUT` создаёт отдельную копию
NIF без excluded auxiliary leaves/controllers/texture paths и переносит
записанные позы в `native-poses.bin`. SOURCE не меняется, OUTPUT должен быть
новым каталогом вне SOURCE. `DREAMSLEEVE_NATIVE_NIF=OUTPUT/appearance.nif`
включает проверку настоящей записи в Client.Tests: production Prepare/ReadAsset,
WriteSnapshot/ReadSnapshot, ошибка квантования, полные protobuf bytes и Recorder.
Результаты — `production.txt`, `measurements.csv`, `model.zst`, `pose-N.zst`
и `replay/*/capture.phdiag`. Полученный phdiag использует общий replay в игре.
Это офлайн конверсия исторической записи, не альтернативный production capture.
Игровые assets и архивы остаются в игнорируемом build и не входят в dist.

# Исторические исследования neutral pipeline

Разделы от 06–07.10 ниже сохраняют наблюдения отвергнутой реализации.
Её GPU readback, маски, деформации и renderer удалены; это не текущая архитектура.
Актуальный native путь описан в PhantomsRu.md.

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

## Отказ по материалу после исправления потока, 06.10.2026

Повторные записи idle/walk-turn/combat подтвердили привязку к Main::Update:
`capture.main-thread` исчез. Однако ни одной позы ещё не было: новый лог
содержал `capture.effect/decal/projected-material`. Combat-запись
`1791283486555-combat-0` содержит 251 movement и 14 capture failures.
`context-changed` — причина завершения записи при уходе из игрового контекста,
а первоначальный отказ происходил раньше, при сборке neutral asset.

Open/Sample раньше отвергали всю модель при любом effect/decal/projected
mesh, хотя Rebind уже исключал такие meshes. Теперь нескиненные effect meshes
пропускаются согласованно во всех трёх путях. Lighting mesh с projected/decal
флагом сохраняется; RGB-проекции не входят в neutral asset. Нужные mesh/skin
каналы сохраняются, ненужные каналы удаляет существующая Canonicalize.
Скиненные effect materials не пропускаются молча: их неподдерживаемый материал
по-прежнему даёт явный отказ с именем меша и shader flags.

Строка `Phantom materials: ...` показывает число исключённых вспомогательных
эффектов и сохранённых projected/decal lighting meshes, не чаще раза за пять
секунд. Если останется ошибка конкретного layout/material, её mesh name,
shader type и flags теперь видны в логе без отдельной пересборки диагностики.
Это исправление требует игровой проверки: native-тесты не исполняют сцену
Skyrim и не подтверждают захват конкретного модпака.

Проверка material fix: diagnostic DLL собрана, 371/371 native-тестов,
9252 assertions, один явный skip реального UDP. Форматирование и diff check
прошли. SHA256 установленной diagnostic DLL:
`cd1fe86ceac07108eeda5ea4fccfcf282f2ffaa051f44ab67f6196f66b8330f5`.
DLL/PDB установлены в MO2; client/ui/aliases/ESP сохранены по SHA256,
backup `build/phantom-material-install-backup-20261006-175945`.
Логи: `build/phantom-material-diag-build.log`,
`build/phantom-material-test-build.log`, `build/phantom-material-tests.log`.
Самопроверка: Canonicalize удаляет ненужные channels исключённых effects;
Sample не считает новый auxiliary effect экипировкой; переход опубликованного
mesh в auxiliary effect инвалидирует appearance; Rebind использует ту же
классификацию. Скиненные неподдерживаемые materials явно отклоняются.
Релизный dist не пересобирался в этой диагностической итерации. Успех захвата
модели в игре и её визуальная полнота требуют отдельной проверки.

## Отбор скрытой геометрии и blood decals, 06.10.2026

Запись `1791289940334-idle-0` после material fix снова содержала 0 моделей и
0 поз: 445 movement, 25 capture errors. Лог теперь показал конкретный отказ:
`mesh.vertex [mesh=EdgeBlood12, shader=BSLightingShaderProperty, flags=80208E400309]`.
Флаги содержат Decal и DynamicDecal, но не Skinned. Это отделило проблему
от потока и предыдущего запрета projected materials. Политика не использует
имя EdgeBlood12 как исключение.

При сравнении с локальным прототипом выявлено различие путей: прототип
клонировал/сохранял сцену через NiStream, сетевой adapter напрямую декодирует
буферы в neutral asset. Open раньше читал также скрытые meshes, а hidden
применял только к уже построенной позе. Невалидный неактивный blood mesh
поэтому мог остановить весь новый путь, не мешая прототипу.

Теперь pure PhantomCaptureRules определяет auxiliary surfaces и camera-aware
visibility; native adapter только извлекает факты из CommonLib. Нескиненные
lighting decals также исключаются до декодирования, как dedicated skinned
engine decals и WeaponBlood. Скиненные базовые lighting surfaces с decal flags
сохраняются. Open/Sample/Rebind разделяют CaptureCandidate. Изначально скрытый
mesh не декодируется; ранее сохранённый hidden mesh использует валидный кеш
без повторного GPU readback/deformation/audit. При возврате видимости старый
stamp проверяется. При draw/sheath видимый эквивалент имеет приоритет перед
скрытым экземпляром; нового asset только из-за такого двойника не требуется.

Строка `Phantom selection: ...` отдельно показывает auxiliary и hidden skips.
Пути и файлы текстур не сериализуются; alpha masks для силуэта остаются.
Четыре новых native-теста проверяют реальную используемую политику на blood
surfaces, теле/одежде, hidden/partition visibility и смене камеры. Это ещё
не игровой тест чтения GPU/skin данных конкретного персонажа.

Проверка visibility fix: diagnostic DLL собрана из окончательного исходника;
375/375 native-тестов, 9034 assertions, один явный skip реального UDP.
Форматирование изменённых C++ файлов и diff check прошли. Логи:
`build/phantom-visibility-diag-build.log`, `build/phantom-visibility-test-build.log`,
`build/phantom-visibility-tests.log`.
SHA256 установленной diagnostic DLL:
`de6c572b4819169cb1864f92921a5283d12e756dbbc26b15941d9e8fe6a0a393`.
DLL/PDB установлены в `F:\MO2 - Skyrim - VanillaLike\mods\Dreamsleeve`;
client.toml, aliases.toml, theme.user.css и ESP сохранены по SHA256.
Backup и install manifest:
`build/phantom-visibility-install-backup-20261006-195551`.
Самопроверка охватила отбор перед чтением буферов, скрытые legacy meshes,
кеш скрытых опубликованных meshes и выбор видимого оружейного двойника.
Релизный dist не пересобирался: его normal DLL остаётся от thread fix.
Успех записи и полнота внешности в игре ещё не подтверждены. Для следующей
проверки достаточно одной локальной записи 15 секунд при 20 Гц без сервера:
должны увеличиваться samples и появиться model, вместо одних movement.

## FP32 позиции без VF_FULLPREC, 06.10.2026

Запись `1791293387229-idle-0`: 0 моделей/поз, 1090 movement, 60 capture
failures, 136380 байт, завершение `no-samples`. Свежий лог подтверждает
исключение EdgeBlood05/EdgeBlood12; новый отказ — `mesh.vertex` на
`Warhammer_Mesh`, shader flags `840182400300`/`850182400300`.

Локальный `PGOutput/meshes/armor/aokili/battlenun/warhammer.nif` содержит этот
mesh: SSE stream 100, descriptor `0x1B00000650407`, stride 28, 3223 вершины,
UV offset 16, normal 20, tangent 24. VF_FULLPREC не выставлен, но позиции
записаны как FP32. Неверное чтение как FP16 даёт 206 нечисловых позиций;
чтение FP32 — ни одной, UV также валидны. Это воспроизводимый отказ декодера,
не повод исключать оружие. NIF и его геометрия не добавлены в репозиторий/dist.

Независимое подтверждение: nifly `BSTriShape::Sync` читает FP32 при stream 100
независимо от FULLPREC. В IDA SE `BSTriShape::LoadBinary` (`C66F80`) вызывает
BSShaderResourceManager slot 2; renderer loader `D6B8D0` копирует descriptor
без изменения и загружает vertexCount*stride исходных байт в CPU shadow +20
и GPU buffer. В функции оставлен комментарий с descriptor evidence.
Новых engine addresses/hooks не добавлено.

Production decoder использует чистый `Game/PhantomVertexStream`: формат
определяется по footprint позиции до следующего атрибута, не по численной
правдоподобности данных. 16-byte FP32 и 8-byte FP16 остаются разными layouts;
противоречивые offsets/stride и NaN/Inf отклоняются. Dynamic positions по-прежнему
перекрывают packed stream. Ошибки позиции/UV теперь включают descriptor,
stride и индекс проблемной вершины, чтобы не повторять диагностику вслепую.

Четыре постоянных регрессионных теста проверяют FP32 без FULLPREC, FP16,
противоречивые layouts и отказ без попытки прочитать невалидный FP32 как FP16.
Пятый opt-in тест читает локальный fixture через
`DREAMSLEEVE_PHANTOM_VERTEX_FIXTURE`: header DLPVTX01, descriptor u64, stride/count
u32, packed bytes, затем независимый FP32 position oracle. В этой проверке
все 3223 позиции реального Warhammer_Mesh точно совпали с oracle.
Обычный запуск явно пропускает этот тест без внешнего fixture.
Локальные evidence: `build/inspect-warhammer-nif.py`,
`build/phantom-warhammer-nif-evidence.json`, `build/phantom-warhammer-vertices.bin`,
`build/phantom-warhammer-failed-capture.json`.

Итог проверки: 380/380 native-тестов, 25464 assertions, один явный skip
реального UDP; внешний vertex fixture включён в этот запуск. Логи:
`build/phantom-vertex-test-build.log`, `build/phantom-vertex-tests.log`,
`build/phantom-vertex-diag-build.log`. Окончательная diagnostic DLL собрана
за 116.516 s и установлена вместе с PDB в MO2. SHA256 DLL:
`5b90ce8da3551381f001433cb6d0be41976e6e00f429675c9b8bcd0b4c606f01`.
Backup и install manifest: `build/phantom-vertex-install-backup-20261006-204722`.
ESP, client.toml, aliases.toml и theme.user.css сохранены по SHA256.
Normal dist не пересобирался. Самопроверка охватила определение precision,
сохранение FP16 и dynamic override, отказ на NaN/Inf без fallback и отсутствие
дублирования Half/Read в engine adapter. Полная запись и внешность персонажа
в игре пока не подтверждены; достаточно одной записи 15 секунд при 20 Гц.

## Сверка с сохранённым прототипом, 06.10.2026

Запись `1791294883503-idle-0`: 0 поз, 1091 movement, 60 ошибок, `no-samples`.
После исправления позиций захват дошёл до `Body [Ovl0]` / `Body [Ovl1]`:
`graphics.alpha-pending`, затем `graphics.alpha-read-budget`. Readback 4K
маски резервирует 128 MiB под R32_UINT output/staging; вместе с предыдущими
ресурсами это превышает asset budget. Причина включения этих поверхностей —
потерянные при переносе исключения Ghostify из `codex/phantom-local-se`.

Сверены все шесть завершённых архивов в локальном DreamsleevePhantoms:
idle, movement, combat, equipment, camera и movement с requested 40 Hz.
В каждом 87 поверхностей, 25 исключённых прототипом. Восстановлен отбор
18 RaceMenu overlays, shaderless helpers и почти прозрачных dummy surfaces.
Отдельное уже существующее исключение EdgeBlood12 сохранено. Базовое тело,
волосы, одежда и оружие не исключаются по одному decal flag.

Старая модель также выявила следующий потенциальный отказ: одна маска волос
2048x1024 используется 11 meshes, что прежняя проверка считала как 22 MiB
при лимите 16 MiB. AlphaMaskPool теперь разделяет неизменяемые одинаковые
маски в capture, validation и decode, проверяя dimensions и все pixels.
Тест с 11 независимыми исходными копиями проводит модель через Validate,
Prepare/Zstd и ReadAsset; маски после чтения разделяются, пиксели сохраняются.
Expanded asset limit и worker reservation не уменьшены; wire не менялся.

Повторяемая проверка старых архивов, без изменения оригиналов:

```powershell
python Scripts/phantom_prototype_fixture.py 'C:\Users\newri\Documents\My Games\Skyrim Special Edition\SKSE\DreamsleevePhantoms' --output build/phantom-prototype-fixtures-v2
$env:DREAMSLEEVE_PHANTOM_PROTOTYPE_FIXTURES = "$PWD/build/phantom-prototype-fixtures-v2"
$env:DREAMSLEEVE_PHANTOM_VERTEX_FIXTURE = $env:DREAMSLEEVE_PHANTOM_PROTOTYPE_FIXTURES
xmake run Dreamsleeve.Client.Tests
```

Для повторного экспорта нужно новое имя output (существующий каталог не
перезаписывается). Скрипт читает узкий SSE stream-100 NIF layout, сохраняет
материальные признаки с oracle `metadata.excluded` и packed position fixtures.
Он не загружает engine factories, не следует путям текстур и не копирует их.
Архивы/fixtures не входят в Git или dist. Без переменных внешние тесты явно
пропускаются; обычные unit cases политики и масок остаются включёнными.

Проверено 522 решения отбора и 1 521 528 FP32 positions в 276 сохранённых
streams. По 16 streams на архив не имеют packed positions: их отдельные
живые dynamic buffers эта проверка не покрывает. Исходные GPU alpha pixels
в старых архивах отсутствуют, поэтому end-to-end GPU capture ещё не проверен.
Это проверка сохранённых данных, не эмуляция полного игрового кадра.

Последний полный capture error теперь виден в меню и сохраняется в
summary.json (`lastCaptureError`, UTF-8 лимит 512 байт), включая отказ
ValidatedAsset после Open. Binary failure record остаётся числовым.

Итог: 386/386 native tests, 7 619 793 assertions, один skip real UDP;
format/diff checks и Python syntax check пройдены. Логи:
`build/phantom-prototype-tests.log`, `build/phantom-prototype-diag-build.log`.
Diagnostic DLL собрана за 133.718 s и установлена с PDB в MO2.
SHA256 DLL: `3f893702b9d538d4128db3e3417ded04a1e9383339286cf436f4079d61da2f44`.
Backup/install manifest: `build/phantom-prototype-install-backup-20261006-212818`.
ESP, client.toml, aliases.toml, theme.user.css сохранены по SHA256.
Normal dist не пересобирался. Полная запись в игре после этого исправления
ещё не подтверждена.

## Реальный набор alpha-ресурсов: лимит 16 MiB, 06.10.2026

Игровая проверка предыдущего исправления прошла отбор оверлеев, но отказала
на `mask.unique-bytes` для `_BDO__PHW69_Fa2_45`. Причина — уже не дубликаты:
к моменту отказа кеш содержал семь различных масок общей длиной 17 MiB +
16 байт. Набор: 4x4, 1024x1024, три 2048x2048, две 2048x1024. Маски ушей
и волос с одинаковыми размерами имеют разные пиксели; объединять их нельзя.

После повторного запуска по просьбе автора выполнено неинвазивное чтение
через cdb с локальным PDB, затем ReadProcessMemory с правами чтения.
Сохранены только alpha buffers, dimensions и локальный inventory; игра
и её память не изменялись. Структура кеша и указатели проверены повторным
чтением. Данные находятся в ignored `build/phantom-live-alpha/*.alpha`,
`inventory.json`; layout evidence — `build/phantom-live-masks-layout.txt`.
Они не входят в Git, dist или отправку на сервер. Исходных RGB textures нет.

Тест `Live character alpha resources survive complete model validation and
codec` читает fixtures по `DREAMSLEEVE_PHANTOM_ALPHA_FIXTURES`. Он воспроизводит
отказ при прежних 16 MiB и пропускает тот же набор через Validate, Prepare/Zstd,
ReadAsset с побайтовой проверкой pixels и dimensions. Треугольники тестовой
модели синтетические; alpha bytes взяты из живой игры. Это не полный захват
персонажа и не проверка следующих ещё не прочитанных игровых ресурсов.
Без внешних файлов этот тест явно пропускается.

Лимит суммы уникальных масок теперь 64 MiB в единственном `Phantom::Limits`.
Отдельная маска остаётся ограничена 4096x4096, модель — 128 MiB; worker и
общий client RAM budget сохранены. Разрешение и пиксели не сокращаются.
Граничный обычный тест допускает четыре разные 4K маски и отказывает при
добавлении ещё одного байта, а дубликат не расходует бюджет повторно.
Ошибка содержит used/incoming/limit/resources/shape для следующей диагностики.
Wire layout и protocol version не менялись: изменён локальный ресурсный лимит.

Red evidence: `build/phantom-live-alpha-red.log` (старый лимит отклонил реальные
данные). После изменения — 388/388 native tests, 7 619 651 assertions, один
skip real UDP. Включены все три внешних fixture набора: prototype selection,
packed vertices, live alpha. Логи: `build/phantom-live-alpha-tests.log`,
`build/phantom-live-alpha-test-build.log`, `build/phantom-live-alpha-diag-build.log`.

Diagnostic DLL/PDB собрана за 133.047 s и установлена в MO2; SHA256 DLL:
`d53bf8430ff28b22ce7ce722be52724f5c9f2a199985b0142140495b5ddf7a45`.
Backup/install manifest: `build/phantom-alpha-budget-install-backup-20261006-215307`.
Четыре пользовательских файла сохранены по SHA256; normal dist не пересобирался.
Самопроверка: лимит задан в одном месте, одинаков в capture/validation/decode;
expanded asset и общий memory budgets не сняты, новые данные не теряют pixels.


## Изоляция ошибок геометрии и исходные веса, 06.10.2026

`mesh.weight-sum` воспроизведён на вершине 291 mesh `(000C891A)[1]/
(000C891B) [ 0%]`: веса 0.79150390625, 0.0775146484375,
0.07733154296875, 0.03204345703125 дают 0.9783935546875. Эти же байты
есть в рабочем prototype appearance.nif и CPU stream живого процесса.
Шейдерный blend использует исходные веса. Условие близости суммы к 1 и
принудительная нормировка удалены; finite/range/index проверяет один
`Vertex::ValidWeights`. Conservative bound теперь учитывает диапазон сумм
в mesh space до world translation, поэтому сохранение весов не сужает bound.

Неинвазивно прочитаны 967 scene nodes, 87 meshes, 136 CPU streams. GPU
readback для этих vertex/index streams не требовался. Alpha extraction —
отдельный путь. Ignored `build/phantom-live-meshes` содержит `.mesh`,
`.positions`, inventory; сами модели/имена/указатели не входят в Git/dist.
`.mesh`: `DLPMESH1`, descriptor:u64, stride/count/indexCount/boneCount:u32,
packed vertices и u16 indices. `.positions`: float32 XYZ для dynamic override.

Новый `Game/PhantomMesh` — тот же полный декодер, который вызывает native
адаптер. Тест с `DREAMSLEEVE_PHANTOM_MESH_FIXTURES` пропускает все сохранённые
streams через Decode → Validate → Prepare → ReadAsset и сравнивает веса/индексы.
Также проверяется общая dynamic pose по одному stream на shape, включая
исключаемые в игре helpers/overlays, через локальный Write/ReadSnapshot.
Матрицы/links skin в этом тесте синтетические: это проверка данных/codec,
а не эмуляция полного engine capture. Обычный тест содержит только числовую
регрессию весов и работает без внешних файлов.

Граница отказа — отдельная деталь:

- Open пропускает неподдержанную/повреждённую геометрию и сохраняет причину.
  Общая `ValidateGeometry` применяется до включения mesh в модель; бюджеты
  списываются после её успешной подготовки. Ограничение общей позы также
  проверяется при добавлении очередной детали.
- Sample скрывает только проблемный slot, сохраняя полный cached payload
  для его bounds/deformation. Остальные слоты продолжают движение. Busy
  повторяется через 50 мс, обычная ошибка — через 1 с; смена схемы требует
  нового asset. Некорректный локальный transform скрывает зависимые meshes.
- Для пропущенных деталей и изменённой внешности кандидат перестраивается
  не чаще раза в 5 с. Старый source остаётся до готовности кандидата;
  pending readback не заменяет его уменьшенной моделью. Совпавшая внешность
  обновляет bindings без новой generation/повторной отправки asset.
- Повреждённый общий graph/skeleton, отсутствующий root и отсутствие вообще
  пригодной геометрии остаются общими отказами. Такая сцена не угадывается.
- `omittedGeometry`, `hiddenGeometry`, `partialSamples`, `partialDetail`
  сохраняются в summary.json; меню явно показывает частичный захват.
  Это не полная визуальная репрезентация, даже если samples успешно растут.

Полный live набор без prototype-excluded meshes содержит 62 meshes,
295820 vertices; dynamic-потоки — 42232 вершины, 1013568 байт positions/normals
на кадр до каналов/bounds. Прежние 512 КиБ сетевого лимита не вмещали их.
Поэтому локальная запись получила отдельный буфер 4 МиБ; production admission
и серверные лимиты остаются прежними. Пока pose codec не оптимизирован,
production может отображать частичный phantom по этой причине. Сетевой
протокол/формат позы этим исправлением не меняется.

Проверки включают отказ одного из трёх slots с продолжающимся движением
соседей, atomic hidden snapshot codec, recovery, stale schema, bounds для
nonunit blend и запись 45000 dynamic vertices сверх production packet budget.
Игровой capture/playback этой сборки, AE/VR и сетевой визуал ещё требуют проверки.

Итог проверки этой сборки: 396/396 native tests, 10 674 646 assertions,
один skip real UDP. Включены prototype surfaces/vertices, live alpha и
136 live mesh streams. Тестовый архив с 45000 dynamic vertices (поза >1 МиБ)
проверен `inspect_phantom_diagnostics.py`. Логи находятся в ignored
`build/phantom-recovery-tests.log`, `build/phantom-recovery-test-build.log`,
`build/phantom-recovery-dll-build.log`; отчёт инспектора —
`build/phantom-large-pose-inspection.json`. Format/diff checks пройдены.

Диагностическая DLL/PDB установлена в MO2 06.10.2026 22:41, сборка 100.235 s.
SHA256 DLL: `b9285d201e66255fcf77fa4e81558b11ea718edeb2d7d3420e24a05616f8510d`.
Backup: `build/phantom-recovery-install-backup-20261006-224137`.
Четыре конфигурационных/пользовательских файла, включая ESP, сохранены по
SHA256. Normal dist и сервер не пересобирались. Проверка записи в Skyrim
после установки остаётся за игровым запуском; тесты её не заменяют.

## Исправление квоты и периодических задержек 06.10.2026

Архив `1791302413545-walk-turn-0` проверен инспектором: 92 целых кадра,
6.372485 с, 296457018 байт, без capture errors. Старое завершение `disk-limit`
было суммой файлов каталога, а не нехваткой места. Удалены эта квота,
обход прошлых записей и общий предел 1 ГиБ в инспекторе; границы отдельных
records и проверки структуры сохранены.

В игровом логе повторные Open занимали 90–170 мс. Неизменные permanent
omissions больше не запускают Open каждые пять секунд. Pending mesh/mask
проверяется до полной пересборки, остальные omissions — при смене cheap stamp.
Проверки статических потоков распределены по одному mesh на sample с
интервалом не меньше пяти секунд для каждого mesh. Shared immutable alpha
не хешируется повторно на каждом audit; mesh refresh не инвалидирует её.
Маска, удерживаемая активной моделью, не выкидывается по TTL. Смена source/SRV
обнаруживается, сравнение разных масок по содержимому сохранено. Обновление
текстуры на месте с тем же SRV требует явной invalidation; автоматического
отслеживания GPU-записей в тот же ресурс нет.
Затраты успешного audit >=8 мс выводятся в лог с интервалом не чаще 5 секунд.
Фактическую плавность новой DLL необходимо проверить в игре.

Проверка исправления: native suite 398/398 (один opt-in UDP test пропущен).
Отдельный тест пишет кадр при уже существующем файле >1 ГиБ и проверяет,
что он не изменился. Инспектор прочитал составленный из настоящего Sample
архив 1234939670 байт / 420 повторов; временная тестовая копия удалена,
исходный пользовательский архив сохранён.

Оставшаяся задача производительности: настоящий Open при смене внешности
пока синхронный. Для устранения и этих длинных кадров нужна поэтапная сборка
с бюджетом времени в игровом потоке и обработкой detached данных на worker;
готовая старая модель должна оставаться активной до атомарной замены.
Текущее исправление убирает лишние повторные Open и дорогую работу с масками,
но не заявляет отсутствие задержек при действительной смене модели.

## Регрессия границы пространства, 06.10.2026

Ветка `codex/phantom-local-se` уже использовала `Space::Contains`: наружные
CELL одного WRLD принадлежат одной записи, отсутствие cell на одном кадре
не удаляет запись. В replication это правило ошибочно заменили сравнением
пары CELL/WRLD и общим Clear для readiness/settings/lifecycle.

Правило восстановлено через общий `World::CurrentSpace` (WRLD снаружи,
CELL внутри) и тестируемый capture Context. Readiness без наблюдаемого
пространства не меняет Context. Пока 3D недоступно, источник и маски
удерживаются, удалённые визуалы скрываются, захват пропускает кадр.
Сброс ресурсов из-за настроек/сети не останавливает локальный recorder.
Настоящий переход пространств даёт `space-changed`, загрузка сохранения —
`save-load`, выход — `left-game`; причины пишутся в лог. Прежний общий
`context-changed` больше не маскирует разные ветви.

Архив `1791303679872-walk-turn-0` содержит 9 кадров за 1.153128 с без ошибок
capture; прежний лог не сохранил точную ветку общего Clear, поэтому конкретный
триггер этого запуска задним числом не установлен. Исправление покрывает
обнаруженные регрессии lifecycle, а не предполагает причину по одному скриншоту.

Проверка прототипа также нашла уже существовавший тест
`Phantom recording can cross exterior cells but not worlds or interiors`.
Его инварианты теперь покрыты Context-тестом в production suite: пропуск
наблюдения сохраняет пространство, смена интерьера/мира завершает его,
идентичность наружного CELL в ключ не входит. Тот же PhantomSpace используется
Scene, чтобы смена наружной ячейки не меняла ключ полученного фантома.
Native suite: 399/399, один opt-in UDP test пропущен; после вынесения общего
типа повторно прошли 15 целевых capture/visibility/context tests.

## Запись покоя: частота, очередь и цена деформаций, 06.10.2026

Архив `1791303989274-idle-0` действительно содержит 202 полные позы за
15.077727 с (13.3309 Гц), 597613470 байт, две модели и ноль capture errors.
Это запись DLL до context-fix; найденные ниже cadence/queue ошибки были и
в context-fix. Инспектор теперь умеет воспроизводимо сравнивать сохранённые
потоки вершин без Skyrim:

```powershell
python Scripts/inspect_phantom_diagnostics.py <каталог-записи> --compare-deformations --output build/phantom-idle-202frames.json
```

Измерения именно этого архива:

- Capture: медиана 2.227 мс, p95 4.0039 мс. Начальные позы поколений 18/19
  включают создание модели и занимают 121.7886/155.5771 мс. Это совокупное
  время пути захвата, не замер только native engine calls.
- Интервал кадров: медиана 68.26 мс вместо целевых 50 мс. Старый
  `nextCapture = now + period` каждый раз терял опоздание игрового кадра.
  Cadence теперь сохраняет сетку сроков, пропускает прошедшие слоты и берёт
  максимум один свежий снимок за tick; после паузы нет серии одинаковых поз.
  Тест при 24 мс на игровой кадр получает 301 sample за 15 с, старое правило
  даёт 209. Это проверка алгоритма, не заявление о достигнутых Гц в Skyrim.
- Пропуски последовательности: gen18 1→10 (8), gen19 1→12 (10). Они совпадают
  с 18 dropped в summary; паузы 689.652/849.024 мс сразу после первых поз моделей.
  Очередь повторно начисляла 73/92 МиБ за каждый shared_ptr на ту же модель.
  Теперь immutable Asset учитывается один раз на все queued и выполняемый job,
  вплоть до освобождения последнего; копии ValidatedAsset разделяют этот учёт.
  Ограничение памяти очереди не увеличено. Проверка burst из 64 поз сохраняет
  все 64; тот же тест на прежнем recorder сохраняет 1 и отбрасывает 63.
- У полной gen19 325 channels, 60 bounds, 42232 dynamic vertices. До Zstd
  channels занимают 6175 байт, bounds 600, deformation blocks 1013696.
  Медианная полная сжатая поза — 814533 байта. При 20 Гц это 15.54 МиБ/с
  payload одного автора до fan-out и заголовков. Это диагностическая полная
  поза сверх production limits, а не фактически отправленный сетевой трафик:
  локальная запись приостанавливает публикацию.
- В gen19 десять из шестнадцати deformation blocks (25879 вершин) не менялись
  ни разу за её 137 кадров. При сравнении соседних кадров около 97.15% объёма
  этих блоков приходится на побайтово одинаковые целые блоки. Это основание
  пересматривать передачу полных потоков, но не обещание такого же процента
  сжатия независимых пакетов и не вывод о поведении в движении/бою.

Архитектурный долг остаётся: synchronous model build и полная повторная
передача deformation streams. Обычный захват в этом примере укладывается в
2–4 мс; сам по себе результат 13.3 Гц не доказывает предел FPS/движка.
Для следующего изменения формата следует проверять отклонения от базовой
геометрии поколения модели, чтобы потеря предыдущей unreliable позы не
делала следующую непригодной. Нельзя объявлять поток статическим навсегда
только потому, что он не менялся в покое. Нужен сравнительный прогон дампов
и проверка визуала; исходный NIF-прототип остаётся контрольным вариантом.

Проверка текущих исправлений: 403/403 native tests (один opt-in UDP skip),
включая fixtures прототипа, live masks и mesh streams; diagnostic DLL собрана.
Новый результат частоты в игре ещё не измерялся. Протокол и формат архивов
не менялись; сервер пересобирать не требуется.

DLL/PDB установлены в указанный мод MO2 06.10.2026 23:46; backup:
`build/phantom-cadence-install-backup-20261006-234609`. Установщик подтвердил
SHA256 DLL/PDB и неизменность четырёх пользовательских файлов. Самопроверка
учёта очереди: admission/release учитывают и выполняемый job, reset/error
очищает ledger, копия ValidatedAsset не дублирует его storage. Новый тест
проверен также на старом recorder: 1/64 samples и 63 drops; после возврата
исправления — 64/64, без drops.

## Локальное воспроизведение принимающего пути, 07.10.2026

В diagnostic DLL добавлены кнопки **«Воспроизвести последнюю запись»** и
**«Остановить воспроизведение»** в SKSE Menu Framework → Dreamsleeve →
«Запись фантомов». Выбранный сценарий определяет последнюю завершённую
запись с позами в `DreamsleevePhantomDiagnostics`; `.partial` и пустые
записи не выбираются. Новая запись и сервер для проверки не нужны.

Порядок проверки: загрузить того же персонажа в том же мире/интерьере рядом
с точкой записи, отойти немного в сторону, выбрать «Покой», нажать воспроизведение
и закрыть меню. После создания первой сцены есть задержка три секунды.
Архив проигрывается в исходных мировых координатах; после конца последний
кадр остаётся для осмотра до кнопки остановки. Старый формат не хранит ключ
WRLD/CELL, поэтому принадлежность сохранённых координат пространству нельзя
проверить автоматически. Смена текущего WRLD/интерьера или загрузка сохранения
останавливает сеанс; обычная смена наружной CELL — нет.

Путь проверки: `ReplayReader` → SHA256 compressed model → `ReadAsset` →
`ReadSnapshot` compressed pose → `Motion::Between` → штатные
`PhantomScene::Begin/Advance/Apply/Attach` → `PhantomGraphics` factories/upload.
Исходные float/quantized blobs не используются как обход production decoder.
Чтение и декодирование выполняются одним worker, очередь ограничена четырьмя
позами. При ожидании worker время воспроизведения не продвигается. Stop не
ждёт декодер на игровом потоке; native scene освобождается на main thread.
Во время локального просмотра собственный capture и обработка сетевых
фантомов приостановлены, чтобы не смешивать их стоимость с измерением.

Это проверка одной полной модели из диагностики: scene reservation вычисляется
тем же Requirements по полной геометрии, поза читается с CaptureLimits.
Локальный проигрыватель не доказывает прохождение production network budgets,
AOI, ENet и массовой нагрузки. Политика сетевого клиента не изменена.
В меню и логе есть max model build total/step, max apply (включая интерполяцию
и attach), max pose decode, число кадров и память сцены. Ошибка конкретного
этапа выводится на странице и как `Phantom replay failed` в лог.

Архив `1791303989274-idle-0` проверен этим reader/production codec вне игры:
**202 позы, 2 модели**, все CheckSnapshot успешны. Дополнительно проверены
отмена заполненной очереди, повторный запуск и отказ повреждённого SHA256.
В первых примерно пяти секундах этого архива модель ещё частичная (gen18),
затем идёт полная gen19; при оценке полноты визуала нужно дождаться её.
Сам игровой результат, native factories/upload и визуальное соответствие
этим тестом не подтверждены: это следующий ручной запуск через кнопку.

Итог сборки: 406/406 native tests, один opt-in UDP skip; все реальные fixtures
подключены. DLL build 118.781 s, format/diff checks чистые. Самопроверка:
рендер получает только декодированные compressed bytes; очередь look-ahead
ограничена, cancellation не join-ит worker на main thread; game resources
создаются/удаляются только в игровом Tick/Stop. Код и UI исключены из normal
сборки флагом DREAMSLEEVE_DIAGNOSTICS, network policy/protocol не менялись.
DLL/PDB установлены 07.10.2026 00:04, четыре пользовательских файла сохранены;
backup `build/phantom-replay-install-backup-20261007-000455`.

## Диагностика адаптивного воспроизведения, 07.10.2026

Нативный путь добавляет отдельные `[Phantom stages]` timings (см.
[PhantomRuntimeRu.md](PhantomRuntimeRu.md)). Сетевой diagnostic log каждые5 секунд
показывает `target_delay_ms` и `speed` из production Playback вместе с возрастом,
разрывами и количеством snapshots. Это чтение истории Exchange; отдельного
диагностического clock/renderer нет. Старые captureMs архивов остаются суммарной
ценой CapturePlayer и не переименовываются в NiStream или GPU time.
