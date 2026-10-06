# Живые фантомы игроков

Опциональный локальный recorder для измерения исходных поз и байтов кодека:
[PhantomDiagnosticsRu.md](PhantomDiagnosticsRu.md). Он доступен только при сборке
с `DREAMSLEEVE_DIAGNOSTICS` и не входит в обычный клиент/dist.

Фантом показывает модель и движение находящегося рядом игрока. Он использует
ту же авторитетную область видимости, что и движение светлячков. Это сцена из
геометрии, без Actor, AI, физики, активатора и постоянного REFR. Позы не
сохраняются для показа после выхода автора; кешируется только внешность.

Реализация находится в ветке `codex/phantom-replication`, основанной на
`7772d17`. Контракт клиента и сервера — версия 21. Оба компонента обновляются
вместе; совместимости с предыдущим wire contract нет. Исторический
`client.network.channelLimit = 3` при чтении конфигурации становится 5;
установленный пользовательский TOML при этом не переписывается.

## Модель и владение

| Ответственность | Место |
|---|---|
| Допустимый manifest, IDs, snapshot, выбор подписок | `Server.Domain/Phantoms.fs` |
| Сессия, Presence view, лимиты, последние позы, передачи | `Server.Core/PhantomAgent.fs`, `PhantomOptions.fs` |
| SHA256, opaque файлы, временные загрузки, TTL/LRU | `Server.Infrastructure/PhantomStorage.fs` |
| Единственный ENet owner, полосы и приоритеты | `TransportOwner.fs`, `EnetTransport.fs` |
| Проверенный нейтральный формат, Zstd, playback | `Client.Core/Phantom/Types`, `Validation`, `Codec`, `Playback` |
| Передачи и корреляция ответов | `Client.Core/Phantom/Streaming` |
| Межпоточный обмен и ограниченный decode/cache worker | `Client.Core/Phantom/Exchange`, `Worker` |
| Чтение живого 3D, адаптер D3D, скрытая сборка сцены | `Game/PhantomCapture`, `PhantomGraphics`, `PhantomScene` |
| Lifecycle, совместный бюджет кадра, выбор представления | `Game/Phantoms` |
| Подписи, имена, облачка | общий `Game/PlayerLabels` и существующий `Nameplates` |
| Engine addresses, factories, ABI, chaining | только `Hooks.ixx` |

ServerRuntime сериализует переходы PhantomAgent; отдельный ограниченный worker
владеет файловыми операциями. Он не ускоряет репликацию за счёт второго
независимого ENet host. Клиентский сетевой поток владеет передачами, worker
работает с detached immutable значениями, игровой поток владеет сценой.
TES references между кадрами разрешаются через ObjectRefHandle; владение
Ni-объектами выражено NiPointer. RE/GFx/D3D не входят в Client.Core.

Источнику нельзя назначить чужой PlayerId: сервер получает его из сессии.
Наблюдатель не выбирает произвольного отправителя через download request:
нужны текущая Presence view и разрешённая подписка. Уменьшение количества,
дальности или выключение получения отзывает views и незавершённые загрузки.
Позы старой generation/view/context не применяются к новой сцене.

## Внешность и поза

Модель имеет format version, appearance generation, SHA256 сжатого содержания,
число каналов и geometry, raw/compressed length. Capture канонизирует нужные
каналы и использует identity locals; анимация, рука ↔ ножны и камера не должны
менять идентичность внешности. Настоящая смена geometry/skin/material silhouette
создаёт новую generation. Дешёвые stamps проверяются при sampling; ограниченный
периодический audit покрывает изменения без событий. Bindings пересобираются
отдельно от содержимого модели.

Neutral asset содержит дерево, вершины и индексы, UV/нормали/касательные,
skin links/weights/bind transforms, alpha-test/blend/double-sided признаки и
маски прозрачности. Исходные diffuse textures, пути файлов, shader programs,
ESP-зависимости и pointers не передаются. Маски сохраняют силуэт волос и ресниц;
цвет и прозрачность задаёт получатель. Projected flag сам по себе не исключает
lighting geometry. Скиненная базовая геометрия тела/одежды с decal flags также
сохраняется; RGB-проекция не передаётся. Переключение этих RGB flags не меняет
neutral stamp. Нескиненные effect/decal meshes, BSSkinnedDecalTriShape и
weapon-blood meshes исключаются как вспомогательные до чтения их вершин.
Отбор не зависит от имени mesh; Open, Sample и Rebind используют одну политику
из PhantomCaptureRules. Canonicalize удаляет их ненужные pose channels.
Неподдерживаемые скиненные effect materials и layouts отклоняются явно.

Open не считывает геометрию со скрытым mesh/предком или полностью скрытыми
dismember partitions. Скрытие root камерой игнорируется; в первом лице
сохраняется политика видимости третьего лица. Ранее захваченный, затем скрытый
mesh остаётся в immutable asset и получает hidden channel; его readback,
deformation sampling и периодический audit пропускаются, используются валидные
кэшированные данные. При появлении ранее не захваченной видимой geometry
проверяется схема; при draw/sheath Rebind предпочитает видимого эквивалентного
двойника скрытому оригиналу. Старый stamp скрытого оригинала не обновляется
без аудита, чтобы при возврате видимости проверить изменение его содержимого.

Нескиненная авторская модель с effect/decal material тоже не передаётся:
поддержка всех модов, меняющих рендер персонажа, этим не заявляется. Счётчик
исключённых auxiliary/hidden meshes и до четырёх auxiliary имён выводятся в
лог с ограниченной частотой; ошибки первоначального захвата содержат имя mesh,
shader type и flags. Пути/файлы текстур в asset отсутствуют: только UV и
силуэтные alpha masks, извлечённые из уже загруженных ресурсов.

Asset не является сетевым NiStream/NIF. Получатель проверяет counts, связи,
finite values, индексы, веса, маски и бюджет распаковки. Только после этого
создаёт объекты через заранее выбранные доверенные локальные фабрики игры.
Сервер проверяет manifest, длину и SHA256, хранит сжатые байты и не запускает
парсер геометрии или распаковку. Zstandard 1.5.7 связан статически с клиентом;
его BSD license включена в пакет.

Каждый снимок независим и содержит generation/context/sequence/sample time,
origin, world TRS/hidden всех каналов, world sphere всех geometry и позиции/
нормали всех dynamic meshes. Пропуск обязательной деформации или bound делает
весь снимок недопустимым. TRS-позиции кодируются относительно origin с шагом
0.125 world unit; quaternion — четырьмя int16. Bounds и деформации находятся
в том же сжатом снимке. Snapshot не восстанавливается из предыдущего пакета.

Сжатие модели — Zstd level 3, позы — level 1. Декодер требует один законченный
frame с точной declared length и без хвоста. Размер до распаковки ограничен.
Одинаковая generation не может обозначать разные manifests; повтор того же
manifest после временного отказа или смены контекста допустим.

## Транспорт, задержка и нагрузка

Пять ENet lanes: Control=0, Chat=1, Realtime=2, Models=3, Poses=4. Models —
reliable поток блоков по 16 KiB с ACK window. RequestId отличает новую попытку
от запоздалого ответа предыдущей; TransferId появляется только после admission.
Upload и download делят лимит одновременных передач игрока. Клиент
ограничивает отправку token budget, а загрузку — pacing подтверждений окна;
одновременные download получают равные доли локального byte budget. Таймауты и
retryAfter не оставляют загрузку навсегда в состоянии ожидания.

Poses — sequenced `UNRELIABLE_FRAGMENT`. ENet собирает целый пакет; потеря
фрагмента отбрасывает снимок. Проверено, что на unreliable sequence FFFF ENet
иначе переводит большой пакет в reliable. Поэтому owner сначала отправляет
пустой reliable marker той же полосы, потребляемый транспортом до codec;
payload позы остаётся unreliable. Собственного сетевого дробления поз нет.

Сервер хранит одну последнюю позу источника, коалесцирует отправку и делит
immutable fanout payload. IO, hash и asset decode не входят в горячий проход
репликации. Control/Chat обслуживаются перед Realtime и bulk/poses; забитый
получатель моделей не блокирует остальных. Очереди и fanout ограничены.

По умолчанию sampling и replication — 20 Hz, клиентская задержка — 100 ms.
Частота ограничивается сервером и реальным количеством кадров игры, без
catch-up дубликатов. Playback хранит до восьми целых снимков, интерполирует
положение и quaternion, консервативно объединяет bounds. Экстраполяция
ограничена 100 ms, а после 1000 ms без обновлений фантом скрывается.
Большой скачок положения не экстраполируется.

Сцена строится скрытой ограниченными шагами. Старый фантом заменяется после
применения целой позы и присоединения готовой сцены. Общий main-thread бюджет
обслуживает игроков по очереди; при его исчерпании сохраняется предыдущий
целый кадр. При смене пространства, недоступном игроке, disconnect и quit
сцены освобождаются. Readback caches также освобождаются на этих границах.

13 MiB внешности при 5 MiB/s на игрока — минимум около 2.6 секунды в одну
сторону без учёта конкуренции и overhead. Холодная загрузка закономерно
занимает время; тёплый content cache устраняет повторную отправку. Количество
сессий и реальная ёмкость фантомов различаются: MaxSources=512 не является
обещанием одновременной холодной загрузки 512 моделей.

## Настройки клиента

В блоке «Фантомы» чата публикация своей модели и показ чужих независимы.
Светлячок используется при загрузке, отказе, истечении поз или отсутствии
поддерживаемой сцены, если включён fallback. Готовый фантом и светлячок не
дублируются. Личный игнор, собственный игрок и guildmates-only фильтр имеют
одно правило для обоих представлений. Имена используют существующую privacy
policy, включая streamer aliases. Скрытие в бою не останавливает таймеры.

| Ключ ui.toml | Default | Диапазон / назначение |
|---|---|---|
| publishPhantoms / showPhantoms | true / true | Публикация / получение |
| phantomFallback | true | Светлячок при недоступном фантоме |
| combatHidePhantoms | false | Скрыть готовую сцену в бою |
| maxVisiblePhantoms | 4 | 0–16, дополнительно ограничено сервером |
| phantomDrawDistance | 4096 | 0–16384 world units |
| phantomOpacity / phantomColor | 0.6 / #8CCCCC | 0–1 / RGB |
| phantomSampleRate | 20 | 1–50 Hz, дополнительно ограничено сервером |
| phantomDelayMs | 100 | 0–500 ms |
| phantomExtrapolationMs | 100 | 0–250 ms |
| phantomTimeoutMs | 1000 | 500–5000 ms |
| phantomMemoryMiB | 512 | 64–2048 MiB, aggregate working/scene/readback reservation |
| phantomCacheMiB | 1024 | 0–8192 MiB; 0 отключает чтение и запись кеша |
| phantomUploadKiB / phantomDownloadKiB | 5120 / 5120 | 64–8192 KiB/s; 5 MiB/s в каждом направлении, cap сервера действует дополнительно |

Путь кеша задаётся `client.phantomCacheDirectory` в client.toml. Относительный
путь — от `Data/SKSE/Plugins/Dreamsleeve`; default `phantom-cache`. Ключи,
default и нормализация определены в Host/UiSettings; TS-контракт генерируется
из них. В браузер не передаются geometry/pose arrays. SKSE-меню показывает
число фантомов, model/pose bytes, cache hits, rejects/drops, effective sample
rate и последнюю ошибку. Логи не содержат binary payload и секретов.

## Настройки сервера и хранение

Полный документированный список — секции `[Phantoms]` и `[Phantoms.Limits]`
в server.example.toml. Читаются при запуске, без hot reload.

Default: storage `phantoms`, disk 4 GiB, RAM cache 64 MiB, 1024 entries, TTL
86400 s; 4 фантома на наблюдателя, distance 4096, до 512 sources и 64
subscribers на источник; 64 transfers всего, 2 на игрока, ACK window 4,
timeout 30 s, publish cooldown 1 s. Model traffic ограничен 5 MiB/s всего
и 5 MiB/s на игрока; оба серверных бюджета общие для upload/download.
Pose traffic — 2 MiB/s на игрока и 32 MiB/s всего.
MaxPoseFanoutPerTick=2048, CommandsPerSecond=128.

Hard envelope модели: compressed 64 MiB / raw 128 MiB, 4096 channels,
512 geometry; поза compressed 256 KiB / raw 512 KiB. Клиент дополнительно
проверяет до 2 млн вершин суммарно, до 65535 на mesh, до 512 skin bones,
маску до 4096×4096 и 16 MiB масок суммарно. Его рабочий RAM budget может
отклонить формально допустимую по wire модель.

RAM admission учитывает декодированные позы, временные копии истории у
игрового, сетевого и рабочего потоков, scratch, readback и обе сцены при
замене. Старый Asset остаётся учтённым до освобождения старой сцены, даже
если новый manifest меньше. Default 512 MiB — общий бюджет, а не обещание
показать четыре тяжёлых персонажа одновременно. Операции драйвера D3D и
служебные данные системного allocator не имеют точного лимита этим счётчиком.

При `Phantoms.Enabled=false` Presence передаёт только lifecycle membership,
нужный для bootstrap policy; полные views/distances для фантомов не строятся.

Файлы именуются SHA256, запись идёт во временный файл с последующим atomic
completion. Worker учитывает reservations, upload partials, read pins,
TTL/LRU и RAM/disk quota. Кеш можно восстановить повторной загрузкой; он не
заменяет БД аккаунтов и не содержит историю движения. Собирать storage/cache
директории, логи и captures в dist запрещено.

## Проверка и границы подтверждения

Состав пакета, установка, коммиты и окончательные результаты:
[PhantomReleaseValidationRu.md](PhantomReleaseValidationRu.md).

Автоматические проверки включают malformed assets/poses, точную распаковку,
stale generations/views/completions, settings off/on, RAM shrink, retry,
cache misses, fragment loss/rollover, реальные UDP cold/warm transfers,
ACK windows, чат во время загрузки и отзыв подписок. Базовая нагрузка
protocol 20 зафиксирована отдельно в `benchmarks/phantom-baseline-2026-10-06.md`;
итоговое сравнение нужно читать вместе с измеренной частотой генератора,
плотностью AOI, размерами моделей и настройками, а не только числом клиентов.

Локальный прототип был проверен пользователем на SE 1.5.97. Полный сетевой
renderer этой ветки требует новой игровой проверки. Engine entry points
для AE 1.6.1170 и VR 1.4.15 проверяются статически; это не игровой результат.
Другие runtimes отключают только phantom adapter с диагностикой.

Игровой проход на двух клиентах: подключить оба к серверу версии 21 в одном
CELL/WRLD; дождаться холодной загрузки; проверить лицо/тело/волосы/броню,
оружие в руке и ножнах, первое лицо, экипировку и SMP; затем combat, ignore,
снижение max/distance/RAM, teleport, смену CELL/WRLD, reconnect, load и quit.
Повторить вход для проверки тёплого кеша и на получателе с другим модпаком.
Внешность переносится, но отсутствующая у получателя CELL/WRLD не создаётся.
