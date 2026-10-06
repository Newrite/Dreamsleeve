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
