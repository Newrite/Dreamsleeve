# Метки на земле

Реализовано 29 сентября 2026 года (часть 1: домен, протокол v8 (v9 с частью 3), сервер, ядро клиента и
`Client.Dev`); часть 2 — SKSE-плагин и веб-UI, см. [SkseClientRu.md](SkseClientRu.md#метки-на-земле). Доменная модель —
[DomainSpecRu.MD §4.9](DomainSpecRu.MD); здесь — устройство реализации, конфигурация,
границы проверок и открытые вопросы.

## Что это

Аналог сообщений на полу из Dark Souls. Два вида меток:

| Вид | Кто создаёт | Текст |
|---|---|---|
| `Note` (надпись) | игрок вручную | до `ChatInput.GroundNoteText` (200) скаляров Unicode, тот же конвейер, что у сообщения чата: переносы и tab допустимы, пустой текст нет |
| `Death` (место смерти) | клиент при смерти персонажа | подпись убийцы или одно слово причины: до `ChatInput.DeathMarkText` (64), одна строка без управляющих, пустая допустима |

Метка — постоянные серверные данные: переживает перезапуск сервера и перезагрузку
сохранения у автора. Показывается игрокам рядом по тому же принципу, что позы: только
то же пространство (WRLD/CELL) и радиус `GroundMarks.VisibilityDistance`. Это не поток
чата и не объявление: в историю каналов не попадает, над светлячками не всплывает.
Смерть фиксирует клиент ([DeathAndActorValuesRu.md](DeathAndActorValuesRu.md)); сервер
подпись не проверяет по игровым правилам, только по длине и словарю.

## Поля метки

`GroundMarkId` (uint64, выдаёт сервер, монотонно и без повтора между запусками),
автор `PlayerId`, снимок опубликованного имени персонажа на момент размещения (как
`ChatMessage.CharacterName`; отсутствует вне персонажа или при скрытом имени), вид,
текст, flagged-диапазоны (как `ChatMessage.flagged`), положение
(`LocationId` + `Position` + курс — угол Z в радианах, чтобы клиент повернул статик по
взгляду автора), серверное время создания (Unix ms). Имён в метке нет: на wire автор
отдаётся снимком профиля (`PlayerProfile`), как у сообщения, чтобы имя было и у
офлайн-автора; при загрузке из SQLite берётся текущий профиль через ту же публичную
проекцию словаря, что у чата (`Moderation.publicProfile`).

## Домен (`Dreamsleeve.Server.Domain/GroundMarks.fs`)

- `GroundNoteText.create`, `DeathMarkText.create` (в `Primitives.fs`), `GroundMarkId.create`.
- `GroundMarkBody = Note | Death` — текст, типизированный видом; `GroundMarkPlacement`
  (`isNear` — мягкая проверка положения, `isVisibleFrom` — видимость).
- `GroundMarkRules.create` — квоты (≥ 1 на вид), сроки жизни (дни, 0 — бессрочно),
  радиусы (конечные, неотрицательные); `quota`, `ttl`.
- `GroundMark.create/withFlagged/isExpired/expiresAt/isVisibleFrom`.
- `GroundMarkStorage` — владелец всех меток в памяти: `add` с вытеснением самой старой
  метки того же автора и вида при достижении квоты (мягкое правило §10: лишние метки
  после снижения квоты не удаляются, уходит только одна старейшая на каждое новое
  размещение), `remove`, `expired`, `evictionCandidate`, `snapshot`.

Клиентские типы: `Domain::GroundMark`, `GroundMarkPlacement`, `GroundMarkKind`
(`Domain.ixx`), `Spatial::IsMarkWithinRadius` (`Logic.ixx`).

## Сервер

`GroundMarksAgent` (Server.Core) — один владелец: все метки, пространственный индекс
(`SpatialIndex<GroundMarkId>`, ячейка = `VisibilityDistance`), снимки профилей авторов,
набор видимых id на наблюдателя и его `ViewRevision`.

| Команда | Кто | Что делает |
|---|---|---|
| `Join` | PlayerSession при входе | регистрирует наблюдателя; повторный вход того же аккаунта заменяет устаревшего наблюдателя |
| `Observe(connection, generation, location)` | PlayerSession при `SetLocation`, `BeginCharacter`, `LeaveGame` и каждом принятом sample | при смене ячейки индекса — дельта «добавлены/удалены»; при смене пространства или поколения — `clear` + новый baseline; без позиции — `clear` |
| `Place` | PlayerSession после проверок | частота, плотность, id, квота с вытеснением, запись, ответ `Placed`, дельты наблюдателям |
| `Remove` | PlayerSession | только своя метка, иначе `GROUND_MARK_NOT_FOUND` |
| `Expire` | тикер `ExpiryCheckIntervalMs` и первый шаг после старта | удаляет истёкшие, пишет `Delete`, шлёт дельты |
| `Detach` | PlayerSession при остановке | снимает наблюдателя и подтверждает |

Свои метки (часть 3, протокол v9): при `Join` и после каждого изменения набора автора
(размещение, вытеснение, удаление, истечение) агент шлёт `GroundMarkEvent.Own` — полный
список `GroundMarkStorage.ofAuthor`, где бы метки ни стояли. Это отдельная от видимого
набора проекция: сервер знает квоты и хранение, клиент сам решает, что и как рисовать.

Порядок проверок при размещении: в `PlayerSession` — `RequestId`, лимит ожидающих
запросов (общий с чатом, `Runtime.Player.MaxPendingChat`), положение
(`GroundMarkPlacement.isNear` с последним известным `Player.Location`: неизвестное
проходит, другое пространство — отказ, `MaxPlacementDistance = 0` отключает
расстояние), словарь `[block]` → `TEXT_NOT_ALLOWED`, `[flag]` → диапазоны; в агенте —
частота (`RATE_LIMITED`), плотность ячейки (`GROUND_MARK_AREA_FULL`, без вытеснения
чужих; собственная метка, которую вытеснит квота из той же ячейки, освобождает место),
квота с вытеснением. Каждая проверка живёт в одном слое.

Наблюдатель, который ничего не видел и не увидит, не получает пустой baseline.
Свои метки автор видит через ту же дельту, что остальные; `GroundMarkPlaced` только
завершает запрос и сообщает id вытесненной метки.

Хранение: таблица `ground_marks` (миграция `1790640000000_ground_marks.sql`, схема 3;
`1790726400000_ground_mark_pseudonym.sql`, схема 4, добавляет `author_pseudonym`;
проверка в `verifySchema`), `author_id REFERENCES profiles ON DELETE CASCADE`,
`character_name` и `author_pseudonym` nullable. Метка, оставленная при скрытом имени автора,
хранит его псевдоним того момента и показывает его вместо профиля всё время жизни, в
том числе после перезапуска; остальные метки показывают текущий профиль автора
([скрытое имя](ModerationAndNamesRu.md#скрытое-имя)). `SqliteGroundMarkStore` работает через SqlHydra
(`Generated/AccountSchema.fs` перегенерирован с `main/ground_marks`) на общем
`SqliteAccountStore.withContext`; сырой SQL остался только для `sqlite_sequence`.
`loadAll` читает все метки с профилями авторов и high-water mark (`NextId`);
`startWriter` — последовательный агент записи
(`GroundMarkWrite.Insert/Delete`), которому агент меток отдаёт изменения через
ограниченный `AgentOutbox(MaxPendingWrites)`; переполнение очереди записи
останавливает владельца (хранилище сломано). Ошибка отдельной записи логируется,
память остаётся авторитетной до перезапуска. При загрузке в `Program.fs` тексты,
которые текущий словарь запрещает, в БД остаются, но владельцу не передаются: игроки их
не получают, пока словарь их снова не пропустит; флаги пересчитываются.

Runtime владеет агентом как остальными источниками и после завершения сессии шлёт ему
`Detach` тем же путём, что чату и присутствию: `SessionTable` ждёт `GroundMarksDetached`
до освобождения `PlayerId`, поэтому `Runtime.ControlReserve` должен быть не меньше
`4 * MaxSessions + 4` (умолчание поднято до 160). Второе соединение живого аккаунта в
`Join` — нарушение инварианта, закрывается как у присутствия
(`ground_marks_identity_conflict`).

## Протокол v9

`Protocol/ground.proto`: `GroundMarkKind`, `GroundMarkPlacement`, `GroundMark`,
`PlaceGroundNote`, `ReportDeath`, `RemoveGroundMark`, `GroundMarksChanged`,
`GroundMarkPlaced`, `GroundMarkRemoved`, `OwnGroundMarks`; в `protocol.proto` — элементы
oneof 14–16 и 21–24 и коды `GROUND_MARK_AREA_FULL = 12`, `GROUND_MARK_NOT_FOUND = 13`. Все
команды и дельты — Control-канал ENet; подтверждения несут `request_id` в оболочке.
`OwnGroundMarks` (24) — полный список меток получателя без `request_id`, после открытия
сессии и при каждом изменении набора. Версия 9 несовместима с 8: клиент и сервер
обновляются вместе. Подробнее —
[Protocol/README](../Protocol/README.ru.md).

## Конфигурация

`[GroundMarks]` в `server.example.toml` (все ключи с умолчаниями и проверкой диапазонов
в `GroundMarkOptions.validate`, ошибки при старте) и лимиты текста в
`[Server.ChatInput]` (`GroundNoteText`, `DeathMarkText`). Старый `server.toml` без секции
получает умолчания. `VisibilityDistance` меток по умолчанию равен
`Runtime.Presence.VisibilityDistance`, но задаётся отдельно: клиентская дальность
прорисовки не может быть больше серверной доставки.

## Ядро клиента

- Команды `PlaceGroundNote`, `ReportDeath`, `RemoveGroundMark` в `ClientExchange`
  (с `RequestId`, как `SendChat`); результат — `GroundMarkConfirmation`
  (`ClientOutput.groundMarkConfirmations`: id метки, id вытесненной, признак удаления),
  `ServerRejection` или `CommandFailure`; общий ограниченный бюджет результатов.
- `GroundMarkStore` (видимые метки, `viewRevision`, и список своих меток `own` из
  `OwnGroundMarksReplaced` — замена целиком, `ClientStateDelta.ownGroundMarks`,
  `ClientSnapshot.groundMarks.own`), обновление модели `GroundMarksChanged`; `ChangeBatch.groundMarks` / `ClientStateDelta.groundMarks` —
  упорядоченные переходы `Cleared / Removed / Added`; `ClientSnapshot.groundMarks`.
  Повтор или откат `viewRevision` — ошибка протокола (`InvalidCursor`), как и у
  чата, а не дубликат; сброс сессии очищает хранилище.
- Кодек: `Protocol/GroundCodec.cpp`; `ClientRuntime` держит отдельный набор
  ожидающих mark-запросов на Control-канале.
- `Client.Dev`: `note <текст>`, `death <подпись>`, `unmark <id>`, `marks`; метка ставится в
  последнюю отправленную позицию (`move`/`location`). Вывод: `mark <id> kind=<1|2>
  author=<имя> x=<x> text=<текст>`, `mark-removed <id>`, `marks-cleared`,
  `request <n> placed mark <id>[ evicted <id>]`, `request <n> removed mark <id>`,
  `own-marks <n>` и `own <id> kind=<1|2> text=<текст>` при каждой замене своего списка.

Метки не попадают в `ChatCache`, `freshMessages` и облачка.

## Проверки

Managed: `GroundMarkDomainTests` (текст, правила, TTL, видимость, положение, квота с
вытеснением, индекс автора) и `GroundMarkTests` (агент: доставка по радиусу и
пространству, дельты по движению, `clear` при смене пространства/поколения/потере
позиции, удаление только автором, вытеснение с одной дельтой, плотность ячейки,
частота надписей и смертей, загрузка/истечение/продолжение id, замена устаревшего
наблюдателя; кодек; конфигурация; SQLite: миграция с нуля и поверх версии 2,
round-trip с профилем, каскад удаления аккаунта, писатель). Native:
`Tests.GroundMarks.cpp` (видимость, хранилище, модель, обмен, кодек) и сценарий
`ClientRuntime` через настоящий ENet. Smoke: `Scripts/smoke_chat.py` — надпись у
соседа и невидимость вдали, `marks-cleared` при смене WRLD/CELL, метка смерти и
`RATE_LIMITED`, вытеснение по квоте с id в ответе, `GROUND_MARK_AREA_FULL`,
`TEXT_NOT_ALLOWED`, удаление своей и отказ чужой, перезапуск сервера с сохранением.

## Открытые вопросы и принятые решения

| Вопрос | Решение |
|---|---|
| Полный список своих меток (часть 3) | Сервер шлёт `OwnGroundMarks` при `Join` и при каждом изменении набора автора: БД знает все метки игрока, клиенту не нужно их собирать из видимых дельт; серверные квоты и клиентские настройки показа — разные понятия |
| Как отдать имя офлайн-автора | Профиль (username, display name) — по `PlayerId` из `profiles`/`accounts`, на wire снимком через `publicProfile`; имя персонажа хранится в метке снимком на момент размещения, как у сообщения |
| Метка, текст которой новый словарь запрещает | Остаётся в БД, но не загружается и никому не отправляется (предупреждение в логе); квоту автора в этот запуск не занимает; флаги остальных пересчитываются |
| Ошибка записи в SQLite | Логируется, память остаётся авторитетной; переполнение очереди записи останавливает владельца |
| Монотонность id после удаления верхней метки | `sqlite_sequence` таблицы с AUTOINCREMENT: SQLite поднимает `seq` и при вставке явного id больше текущего, поэтому `loadAll` берёт `max(seq, MAX(id)) + 1`; отдельная таблица-счётчик не нужна |
| Ответ на `RemoveGroundMark` | Отдельное `GroundMarkRemoved{mark_id}`; наблюдатели получают удаление обычной дельтой |
| Пустой baseline при первой позиции без меток | Не отправляется: нечего очищать и нечего добавлять |
| Плотность и собственное вытеснение | Метка, которую квота вытеснит из той же ячейки, освобождает место |
| Потерянный `Observe` при переполнении outbox | Допустимо: владелец сравнивает с последним известным состоянием, следующий sample восстанавливает |
| Очистка наблюдателя после падения сессии | Штатная runtime-очистка (`Detach` + `GroundMarksDetached` в `SessionTable`), резерв `4 * MaxSessions + 4` |
| Лимит частоты чата и меток | Общий модуль `RateLimit` (`ChatRoomAgent` переведён на него без изменения поведения) |
| SqlHydra-схема для `ground_marks` | Регенерирована (`generate_sqlite_schema.fsx`); хранилище на SqlHydra `select`/`insert`/`delete`, как аккаунты |
