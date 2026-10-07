# Native NIF: HTTP-доставка, 08.10.2026

Protocol25 / asset2 / pose3, ветка `codex/phantom-native-nif`.

## Реализация

Модель передаётся PUT/GET `/phantoms/content` на существующем ASP.NET Core/Kestrel
origin авторизации. Новый процесс, отдельный порт, nginx или клиентская DLL-библиотека
не нужны. Windows-клиент использует системный **асинхронный WinHTTP** с отдельной
переиспользуемой session. Синхронный auth JSON wrapper не используется для файлов. Контракт времени
жизни callback/буфера проверен по документации Microsoft:
[WinHttpCloseHandle](https://learn.microsoft.com/en-us/windows/win32/api/winhttp/nf-winhttp-winhttpclosehandle),
[WinHttpReadData](https://learn.microsoft.com/en-us/windows/win32/api/winhttp/nf-winhttp-winhttpreaddata).

| Подсистема | Решение |
|---|---|
| NIF capture, сериализация, codec, scene/poses | Сохранены |
| Exchange/Worker, поколения, кеш, AOI, bridge старой сцены | Сохранены |
| ENet Models | Только настройки, manifest/offer, capability, cancel/complete/displayed |
| ENet Poses | Независимые полные unreliable sequenced snapshots, прежняя delivery policy |
| ModelFlow, Chunk/Progress, WindowChunks, ENet flight override | Удалены на обеих сторонах |
| Файлы | Один открытый async FileStream на передачу, переиспользуемый ограниченный буфер |
| Серверные горячие состояния HTTP | Инкапсулированные mutable dictionaries и поля; I/O вне actor |

PhantomAgent — единственный владелец допуска/публикации, сессии и подписок.
Infrastructure.PhantomHttp владеет временем жизни body operation и одноразовым
случайным 256-битным ключом. PhantomStorage владеет metadata/cache/pins; конкретная
передача сериализует свой I/O через gate. Первичная проверка кеша и заполнение RAM
кеша ещё выполняются metadata worker; они не блокируют replication actor.

На клиенте Http job владеет request context/буферами до HANDLE_CLOSING. Callback
не вызывает Exchange/Game. Streaming сопоставляет transfer **и request**, ждёт
HTTP тело и accepted Complete в любом порядке; Worker проверяет hash/asset и
возвращает результат через прежнюю epoch/revision границу. Временная ошибка не
подменяет новую попытку старым completion. Scene/готовность UI здесь не копируются.

Финальный accepted upload дополняет счётчик последними байтами даже если receipt
пришёл раньше последнего progress poll. Серверный отказ сразу отменяет клиентский
HTTP job. Smoke сверяет сумму полного body и всех отправленных control envelopes;
это application bytes, не TCP wire bytes.

Ключ передаётся в Authorization header, никогда в URL. Hash не даёт права скачать
asset. Direction/length/session/AOI задаются ранее допущенной передачей. Отмена
отзывает ключ и прерывает body; storage освобождается после завершения I/O.
Сервер не распаковывает и не интерпретирует NIF. TLS-политика та же, что для auth;
это не новое шифрование ENet, через который приходит capability.

## Настройки и пределы

- Клиентские upload/download budgets отдельны, каждый общий для своего направления,
  default5 MiB/s. Server policy может уменьшить их.
- Серверный ModelBytesPerSecond — общий upload+download всех peers; отдельный
  PlayerModelBytesPerSecond — общий upload+download одного peer. Оба default5 MiB/s.
  Два независимых этапа upload и download при таком лимите последовательно требуют
  примерно удвоенного времени передачи модели. 5 MiB/s на каждого одновременно
  не обещается при общем серверном потолке5 MiB/s.
- ChunkBytes (default16 KiB) — файловый буфер, а не TCP/ENet flight window.
  Фиксированного кредита32 KiB на runtime tick нет. TCP управляет потерями/flight.
- HttpRequestsPerMinute (default128/IP) ограничивает число файловых запросов,
  не число буферов. Auth имеет отдельную квоту. Для большого общего NAT квоту
  следует настроить. MaxTransfers, TransfersPerPlayer, лимиты asset/RAM/disk сохранены.
- Удалённый WindowChunks нужно убрать из старого server.toml: конфигурация строго
  отклоняет неизвестные ключи. Клиент и сервер обновляются вместе до protocol25.
- Неактивная передача истекает по прежнему transfer timeout; успешный I/O обновляет
  прогресс. Header/keepalive Kestrel используют существующие настройки listener.

## Проверки

406/406 diagnostic native tests; 392/392 обычных native tests; 638/638 server tests. Вместо тестов удалённого ACK/window
добавлены HTTP-контракты: одноразовость, неверное направление/размер/hash, усечение,
отзыв незапрошенного ключа, отмена блокирующего ReadAsync и независимость передач.
Native integration использует production WinHTTP + Streaming + Worker и реальный
Kestrel/ENet actor/storage. Пройдены fragmented pose loss/rollover, cached reentry,
замена поколений, чат во время скачивания, отмена **посередине HTTP тела** и повторный
допуск после старых callbacks. Auth HTTP tests также используют объединённый host.

Offline replay боевой записи `1791317877512-combat-0/capture.phdiag`: 301 кадр,
5 моделей, 608 assertions PASS. Пользовательская запись не изменялась. Это проверка
production asset/pose codec, не повторная визуальная проверка NiStream/Skyrim.

## Измерения

C++ smoke: реальный архив SHA256
`a9f4a1ebd1bfaac39f94c0a9f62236261171c929e10af66e0565706ca2607768`,
13 318 718 compressed bytes. Loopback, default5 MiB/s, без параллельной сборки.

| Этап | Время |
|---|---:|
| Prepare + upload + server commit | 2785,99 ms |
| Получение допуска download → загрузка + decode | 2601,22 ms |
| Полный cold путь | 5399,43 ms |
| Chat roundtrip во время download | 17,25 ms |

Это не чистое network time и не измерение NiStream Save/Load. Poll-gap метрика
smoke включает подготовку fixture между фазами; её максимум нельзя приписывать
production Poll или игровому кадру. Старый UDP relay с RTT100/loss не формирует
условия TCP, поэтому прямое сравнение этих5399 ms с ним было бы некорректным.

Отдельный .NET cold run через **обычный Program/auth host**:2 клиента,1 источник,
13 288 628 bytes, upload commit2609,49 ms, полный upload+verified download5187,65 ms,
один hash-verified download,0 unfinished/отказов. Пятиисекундный предварительный
прогон закончился до завершения download; окончательный15-секундный завершён полностью.

512 игроков, sparse-группы до25, максимум4 подписки,10 Гц, actor values4 Гц,
8 .NET workers,30 s. Те же301 payload и настройки сценария предыдущего отчёта;
server/client cache прогреты, model bytes в измеряемом steady окне =0.
Stopwatch/MeterListener/GC counters внутри .NET; Python только запускает процессы
и собирает отчёт/OS process counters, не генерирует пакеты и не задаёт их pacing.

| Показатель | Предыдущий ENet bulk build | HTTP build |
|---|---:|---:|
| Получено поз / active subscription / s | 9,43 | 9,96 |
| Возраст поз p95, диапазон8 workers | 157–170 ms | 92–93 ms |
| Gap p95, диапазон8 workers | 290–501 ms | 167–174 ms |
| Pose protobuf envelopes, server outbound | 72,45 MiB/s | 76,96 MiB/s |
| Аллокации server, .NET counters | 343,42 MiB/s | 337,50 MiB/s |
| GC pause / s | 63,21 ms | 46,38 ms |
| CPU, эквивалент одного ядра | 1,45 | 1,30 |
| Private memory peak | 123,46 MiB | 112,93 MiB |
| Runtime tick p99 | 11,65 ms | 8,29 ms |
| Native send failures в load | 0 | 0 |

Один прогон после изменения; улучшение задержек не доказывает причинность HTTP,
поскольку в steady окне нет файлов. Формат/размер pose-пакетов не изменён, рост
outbound соответствует лучшей доставке. Envelope bytes исключают ENet/UDP/IP и
TCP/HTTP overhead; полного wire capture для HTTP не снято. Все процессы локальные,
поэтому это не предел выделенного сервера и не интернет-тест.

Артефакты: `build/http-smoke-final`, `build/http-smoke-real-idle`,
`build/http-recording-replay.log`, `build/http-benchmark/{steady512,cold2-complete}`.
[Компактные исходные метрики](phantom-http-2026-10-08.json).

## Оставшиеся проверки и отдельные проблемы

Нужен двухклиентский игровой тест HTTP-сборки через реальный маршрут и отдельное
TCP/UDP сравнение при одинаковых RTT/loss/bandwidth. SE/AE/VR engine ABI не менялся;
нового игрового подтверждения здесь нет. NIF delta/серверная реконструкция базы
ещё не внедрялись. Захват и NiStream Save остались на прежнем допустимом потоке.

При массовом disconnect512 игроков обнаружено95 сообщений `transport_payload_budget`:
movement encode получает budget0 после исчезновения transport peer, до обработки
отключения runtime. Все после load, disconnect завершён; load native send failures0.
В предыдущем прогоне этого проявления не было. Это отдельная гонка shutdown/queued
movement, не ошибка HTTP тела; записана для отдельного исправления, не скрыта PASS
стенда. Аллокации337 MiB/s всё ещё велики: selection/копии pose требуют следующего
профилирования, HTTP их не устраняет.

## Выпуск и установка

Production change `85212bd`; финальная коррекция учёта/отмены `174be2c`,
smoke accounting regression `a9ec48d`. Полный dist собран штатным
`Scripts/package_dist.py`: `S:\Programming\Dreamsleeve\dist`.
Пакет содержит обычную DLL, ESP из Plugin, UI, опубликованный сервер и конфиги.
179 файлов; игровых NIF, архивов, IDB, credentials, DB и логов в dist нет.
Диагностический marker отсутствует в обычной DLL и присутствует в диагностической.

Установлены сервер `S:\Dreamsleeve` и диагностический клиент
`F:\MO2 - Skyrim - VanillaLike\mods\Dreamsleeve`. Игра/сервер перед копированием
не работали. Финальные файлы побайтно совпадают с собранными артефактами.
8 установленных конфигов сохранены побайтно; в активном server.example.toml
удалён только Phantoms.WindowChunks, остальные parsed values равны исходным.
Конфиг проверен Configuration.parse из установленного нового сервера.
Plugin/client.toml остаётся пользовательским изменением вне коммитов.
Dist сохраняет прежние пользовательские файлы; пример с ESP подготовлен
отдельно и проверен штатной проверкой packager, без подмены Plugin/client.toml.

После последней правки:392 обычных и406 диагностических native tests PASS;
сквозной HTTP+ENet smoke выполнен заново для обоих builds, включая точный
body+control accounting, active cancellation/reentry и потерю pose fragment.
Server638 tests PASS. Новых игровых проверок не было.

SHA256:

- Обычная DLL: `b5fb8d47decd654a9dd27754789dee20e5a9e2ae30f7f7ab45c28e1d5b261a1b`.
- Установленная диагностическая DLL: `04a8f7d567d4f674fed853733ab3460222cc79166f78b2832a2830caa3f349a9`.
- Установленный Server.Core: `1788ff2a1715cf74874e0fad0412d6aa252265c73ad2cf1700c12cac774f300f`.

Резервные копии установки и проверочные JSON находятся в
`build/http-benchmark/deployment-backup` и соседних `deployment*.json`,
`dist-validation.json`; в dist они не включены.
