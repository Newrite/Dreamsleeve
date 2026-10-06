# Полная реализация фантомов: выпуск 06.10.2026

Ветка `codex/phantom-replication` основана на master `7772d17`.
Протокол 21 обновляет клиент и сервер одновременно. Старых wire-путей нет;
чтение прежних пользовательских TOML сохраняет defaults новых настроек.

## Коммиты и проверки

| Commit | Объём | Проверка |
|---|---|---|
| `3885afa` | План, prototype references, журнал | Исходная база protocol20 заморожена для BEFORE |
| `3ca0808` | Domain/format, protocol21, Core streaming/cache/playback, UI | Native 359/359, UI 94/94, full Edge 41/41; fixed review воспроизвёл три ошибки |
| `69e8487` | Native capture/renderer, shared labels, SE/AE/VR hooks | DLL build; IDA/layout/factory audit; scoped native review |
| `06f7420` | Сервер, authoritative views, opaque storage, ENet delivery | Server 601/601, настоящий managed/native UDP; fixed review выявил late View после departure |
| `0c67ccf` | RAM/cache/ACK fixes, geometry512, texture helper, lifecycle | Native 364/364, UI 94/94, Edge 2/2; независимое ревью без оставшихся блокеров |
| `d1b4dea` | View привязан к текущему source connection epoch | Targeted 39/39, full server 602/602, native production UDP 1/1; frozen-commit review без блокеров |

Проверки после финальных изменений:

- Native: 364/364, 9135 assertions. Единственный opt-in real-UDP test
  запускается отдельно: 1/1, в данном прогоне 2765 assertions.
- Real UDP: production C++ Streaming/worker/codec/cache и production F#
  Runtime/Presence/TransportOwner/storage. Cold 262250 bytes, окно ACK,
  чат, fragmented pose 2615 bytes, loss discard, rollover без reliable
  pose payload, warm cache без model chunks. Authenticator контролируемый;
  этот тест не проверяет игровые RE/D3D или внешний HTTP вход.
- Сервер: 602/602, 0 failed/errored/ignored; server и fixture builds —
  0 warnings/errors. Benchmark project также собирается без warnings/errors.
- UI: Vitest 94/94; production TypeScript/bundle check; Edge 2/2 с сохранением
  всех 16 настроек, включая default RAM 512 MiB. Полный Edge 41/41 выполнен
  до последних исправлений Core и изменения default памяти.
- DLL SE/AE/VR и Client.Dev собраны. CommonLib warnings C4200 и прежний
  native test warning C4834 не подавлялись. `git diff --check` и Python
  compilation scripts прошли.

Точные команды, outputs и hashes сохранены в ignored `build`:
`phantom-native-release-{build,tests}.log`, `phantom-dll-release-build.log`,
`phantom-dev-release-build.log`, `phantom-ui-reviewfix-{unit,build,browser}.log`,
`phantom-server-departure-fix-2026-10-06`,
`phantom-native-smoke/departure-epoch-server-20261006-final`.

## Нагрузка

[BEFORE](benchmarks/phantom-baseline-2026-10-06.md) и
[AFTER](benchmarks/phantom-after-2026-10-06.md) сохраняют hashes исходников,
параметры и первоначальные результаты. Число подключённых клиентов, achieved
Hz и готовность всех моделей — разные показатели. Cold/warm/overload rows
не означают, что все 128 наблюдателей загрузили все модели за окно замера.

После выключения ненужных full observations повторён только off-dense128:
128/128, 19.624–19.662 Hz, без disconnects и ошибок oracle; GC 78.28 ms,
private peak 91.46 MiB. Этот замер использует отдельный freeze до geometry512
и source-epoch fix; полный phantom-on matrix после этих изменений не повторялся.
Контрольные тесты и UDP smoke проверяют их корректность, а не общую ёмкость.

## Пакет и установка

Чистый пакет клиента и сервера: `build/phantom-distribution`.
`dist` тоже обновлён, с сохранением пяти прежних пользовательских TOML.
Распространять следует чистый пакет. Он проверен на запрещённые caches,
captures, logs, credentials, IDB, dumps, dev/test артефакты; в проверенном
manifest 170 файлов без TOML. BSD-текст Zstandard 1.5.7 полностью включён
в `Client/Dreamsleeve/THIRD_PARTY_NOTICES.md`. Сторонние SKSE DLL не добавлены.

Клиент установлен в `F:\MO2 - Skyrim - VanillaLike\mods\Dreamsleeve`:
12 файлов, три существующих конфигурации и пользовательская тема сохранены.
Существующий ESP не заменялся: эта функция не добавляет игровые records.
Резервная копия заменённых файлов:
`build/phantom-install-backup-20261006-081445`.
SHA256 DLL совпадает у исходной сборки, обоих пакетов и установленного клиента:
`1c0a19e9175f67a6a98036665627e03ba7fd3687af023fa369ded31de2e69c03`.
Manifest и install evidence: `build/phantom-package-sha256.json`,
`phantom-release-package-check.log`, `phantom-release-install.log`.

Повторная пересборка 06.10.2026 устанавливает default передачи моделей
5 MiB/s: клиент `phantomUploadKiB=5120`, `phantomDownloadKiB=5120`, сервер
`ModelBytesPerSecond=5242880`, `PlayerModelBytesPerSecond=5242880`.
Серверные бюджеты общие для upload/download; лимиты поз не изменены.
Новые build/package/test evidence находятся в `build/phantom-5m-*.log`.
Normal DLL и архивы dist собираются без диагностического флага; отдельная
diagnostic DLL сохраняет меню записи для игровой проверки. Исторические
SHA256 и install manifest выше относятся к первой сборке функции.
Проверки повторной сборки: normal native 364/364, diagnostic native 371/371,
server 602/602 (существующий тестовый FS3511 подавлен через `NoWarn=3511`),
UI 94/94. `phantom-5m-package-verification.json` проверяет конфиги обоих
серверных архивов, bundle defaults, отсутствие диагностического маркера в
release DLL и `SHA256SUMS.txt`. Диагностическая DLL и UI установлены в MO2;
пять сборочных файлов заменены, четыре пользовательских/data-файла сохранены
по SHA256. Backup: `build/phantom-5m-install-backup-20261006-155833`;
install evidence: `build/phantom-5m-install-verification.json`.

Исправление capture thread от 06.10.2026: первая diagnostic-запись дала
0 поз, 81 movement и 66 `capture.main-thread` failures. Привязка Graphics
перенесена из kDataLoaded в первый Main::Update; capture и playback теперь
идут через один Phantoms::Tick после исходного обновления игры.
Самопроверка: readiness/cell reset выполняется до capture, offline recording
сохраняет источник, initial pose больше не сбрасывает источник в том же Tick,
проверки thread ownership и release на Quit сохранены. Native diagnostic
371/371 (1 явный skip), normal и diagnostic DLL собраны; игровая проверка
нового capture ещё не выполнена. Логи: `build/phantom-thread-*.log`.
Диагностическая DLL/PDB установлены в MO2 с проверкой SHA256, четыре
пользовательских файла сохранены; backup:
`build/phantom-thread-install-backup-20261006-163424`.
Подробности причины и повторного теста: [PhantomDiagnosticsRu.md](PhantomDiagnosticsRu.md).
`dist/Client`, `dist/Server` и три release archives пересобраны; normal DLL
совпадает с dist и client ZIP, diagnostic DLL — с установленной в MO2.
Пять существующих конфигов dist сохранены по SHA256; server defaults 5 MiB/s
и SHA256SUMS обоих серверных архивов проверены. Evidence:
`build/phantom-thread-package-verification.json`.

## Что проверить в игре

Полный сетевой renderer ещё не проходил игровую проверку. Предыдущая
пользовательская проверка локального прототипа на SE её не заменяет.
Адреса и ABI подтверждены статически для SE 1.5.97, AE 1.6.1170, VR 1.4.15;
остальные runtimes отключают только phantom adapter.

1. Запустить сервер и два клиента protocol21 в одной CELL/WRLD; публикация
   и получение включены по умолчанию. Кнопка локальной записи не требуется.
2. Дождаться холодной загрузки: 13 MiB при 5 MiB/s — минимум около 2.6 секунды
   на каждую сторону, плюс конкуренция и overhead. Пока сцена не готова,
   работает разрешённый fallback на светлячок. Повторный вход проверяет кеш.
3. Проверить лицо/тело/волосы/броню, SMP, оружие в руке и ножнах,
   первое лицо, смену экипировки; затем combat, ignore и уменьшение лимитов.
4. Проверить teleport, cell/world, disconnect/reconnect, загрузку сохранения
   и quit. Повторить на получателе с другим модпаком, затем на AE/VR.

Настройки и ограничения формата: [PhantomsRu.md](PhantomsRu.md).
Статические runtime evidence: [PhantomRuntimeRu.md](PhantomRuntimeRu.md).
Отдельный журнал старых и новых замечаний: [PhantomArchitectureNotesRu.md](PhantomArchitectureNotesRu.md).
