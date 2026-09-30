# SKSE-клиент Dreamsleeve

`src/Dreamsleeve.Client` — SKSE DLL (CommonLibSSE-NG, C++23 modules), адаптер между
игрой и `Client.Core`. Core владеет сетью, авторизацией, моделью и интерполяцией;
адаптер читает игровые данные, преобразует их в доменные типы, передаёт команды через
`ClientExchange` и применяет выходные изменения к PrismaUI и светлячкам. Второй
модели, сетевого runtime и протокола здесь нет.

## Модули и владение

| Модуль | Ответственность |
|---|---|
| `Main.cpp` | Только экспорт `SKSEPlugin_Load`; заголовки CommonLib не включает (см. ниже) |
| `Plugin.ixx` | `SKSE::Init`, listener сообщений SKSE, порядок инициализации |
| `Runtime.ixx` | Единственный владелец `ClientApplication`, `MovementView`, `Host::Session`, `ui.toml`; ограниченная очередь уведомлений (64) с других потоков; снимок для страницы SKSE Menu |
| `Logic.ixx` | Кадр: уведомления → Drain (события UI, свежие сообщения → облачки) → политика сессии → готовность мира → телеметрия → один HUD-кадр надписей (светлячки + метки) → focus |
| `Hooks.ixx` | Все патчи игры: ID Address Library, смещения callsite, проверка байтов и thunks для `Main::Update`, `HUDMenu::AdvanceMovie` (имена над светлячками) и рассылки `InputEvent` (захват клавиатуры) |
| `Events.ixx` | Sinks: меню, ввод, `TESDeathEvent` (флаг `dead` и handle убийцы, без резолва), `TESActivateEvent`; только `Runtime::Post` |
| `Game/Telemetry.ixx` | Персонаж, пространство, позиция, activity, place, actor values |
| `Game/PlacedReferences.ixx` | Временные placed reference плагина: резолв STAT, `Spawn` (`SetTemporary`, масштаб, поворот), проверка присоединённости ячейки, удаление, набор `Set<Key>` по ключу |
| `Game/Raycast.ixx` | Один havok pick по слою line-of-sight: видимость (`Clear`) и пол под точкой (`GroundBelow`) |
| `Game/Fireflies.ixx` | Placed reference на каждого видимого игрока из `MovementView` |
| `Game/GroundMarks.ixx` | Метки на земле: отбор ближайших из снимка Core, статики со снапом на пол, подписи, отправка `ReportDeath` один раз на смерть |
| `Game/Input.ixx` | Состояние захвата клавиатуры и фильтрация цепочки `InputEvent` до всех sinks; адресов не содержит |
| `UI/PrismaUI.ixx` | View, listener, доставка событий, focus/visibility |
| `UI/SKSEMenu.ixx` | Страница настроек и статуса |
| `API/ModApi.ixx`, `API/DreamsleeveAPI.h` | API для других модов: интерфейс `IVDreamsleeve1` через экспорт `RequestPluginAPI` из `ModApi.ixx` (как PrismaUI и TrueFlasksNG), Papyrus `DreamsleeveClient`, callbacks итогов объявлений ([DreamsleeveModApiRu.md](DreamsleeveModApiRu.md)) |
| `Host/Bridge.ixx`, `Host/UiSettings.ixx`, `Host/Session.ixx`, `Host/Bubbles.ixx`, `Host/InputCapture.ixx`, `Host/Announcements.ixx` | Без CommonLib: JSON-контракт UI, TOML настроек UI, корреляция запросов и проекция онлайна, таймеры облачков чата, политика захвата клавиатуры, типы запроса и итога объявлений API. Компилируются также в `Dreamsleeve.Client.Tests` |

`Runtime::Get()` хранит единственный экземпляр приложения; getter не перемещает
владение (прежний вариант возвращал `std::move` статического `unique_ptr` и
опустошал его при втором вызове).

Все публичные вызовы `ClientApplication` и весь доступ к игровым объектам выполняются
на главном потоке из хука `Main::Update`. Игровые указатели сетевому потоку не передаются:
телеметрия отправляет самостоятельные `Domain::*` значения, а `MovementView::Sample`
даёт координаты для светлячков на игровом потоке.

## Сборка: почему Main.cpp пустой

MSVC 14.51 отклоняет единицу трансляции, где текстово включён `<istream>` (через
`RE/Skyrim.h`) и одновременно импортируются модули Core с `import std;`
(C2079: `std::basic_istream::sentry` не определён, точка — `REL/IDDB.h`). Модульные
единицы с теми же include в global module fragment компилируются. Поэтому весь код
с CommonLib находится в `.ixx`, а `Main.cpp` содержит только forward-declaration
`SKSE::LoadInterface` и вызов `Plugin::Load`. Это конкретная причина разделения, а не шаблон.

## Сообщения SKSE

Семантика проверена по skse64 `25b72352adb6543fa6d0bd3795780672b2e238e0`
(`PluginManager.cpp`, `Hooks_Event.cpp`, `Hooks_Data.cpp`, `Hooks_SaveLoad.cpp`).
`Dispatch_Message` вызывает listener напрямую на потоке отправителя, без очереди
и копирования payload, поэтому обработчики только записывают уведомления, а
главный поток применяет их в следующем кадре.

| Сообщение | Источник | Payload | Гарантии | Действие Dreamsleeve |
|---|---|---|---|---|
| kPostLoad / kPostPostLoad | `PluginManager::LoadComplete` → `CallPostLoad`, синхронно после загрузки DLL | нет | Загружены DLL-плагины, не мир и не сохранение; view PrismaUI ещё нет | PostPostLoad: запрос API PrismaUI, создание `ClientApplication` (сетевой поток), чтение `client.toml`/`ui.toml` |
| kInputLoaded | `PlayerControls::ctor_Hook` | нет | Источник ввода существует; персонажа нет | ничего (sink ввода регистрируется на kDataLoaded, когда доступны все источники) |
| kDataLoaded | `DataHandler::LoadScripts_Hook` | нет | Forms загружены; не загрузка сохранения и не готовность cell/3D | хук `Main::Update`, sinks, разрешение формы светлячка, `CreateView` |
| kNewGame | `TESQuest::NewGame_Hook` | `TESQuest*` CharGen | До завершения загрузки игры | контекст → Loading; готовность персонажа проверяется покадрово |
| kPreLoadGame | `BGSSaveLoadManager::LoadGame_Hook`, под `g_loadGameLock` | заимствованное имя сохранения, `dataLen` без `\0` | Начало попытки загрузки | завершение прежнего контекста, очистка светлячков, прекращение семплирования |
| kPostLoadGame | после `LoadGame_HookTarget`, под тем же lock | `data != nullptr` означает успех (не `bool*`) | Успех не гарантирует cell/3D и завершения загрузочного UI | контекст остаётся Loading; при неуспехе тоже; возобновление — после покадровой проверки готовности |
| kSaveGame | `SaveGame_Hook` до записи | имя сохранения | Не подтверждение записи | визуальные объекты не пересоздаются: `SetTemporary` вызывается при создании |
| kDeleteGame | `DeleteSavegame_Hook` | строка локального `std::string` | Удалён файл; текущая игра и сеть не затронуты | ничего |

Универсального «вернулись в главное меню» нет: этот переход берётся из
`MenuOpenCloseEvent` («Main Menu» открыт при контексте Playing). «Мир готов» —
покадровая проверка `PlayerCharacter` + `Is3DLoaded` + `parentCell` без Loading/Main/TitleSequence
в течение 500 мс. «Завершение приложения» — `Main::quitGame` в хуке: `Stop()` с join на главном потоке.

## Хук Main::Update

Пользовательский hook сохранён: `REL::RelocationID(35551, 36544)` + `REL::Relocate(0x11F, 0x160)`.
Проверено в IDA:

| Runtime | Функция по ID | Callsite | Цель |
|---|---|---|---|
| SE 1.5.97 (`SkyrimSE.exe`) | 35551 = цикл сообщений окна `0x1405AF3D0` | +0x11F → `call 0x1405B2FF0` | 35565 `Main::Update` |
| AE 1.6.1170 (unpacked) | 36544 = `WinMain` `0x14063E970` (цикл встроен) | +0x160 → `call 0x140645EA0` | 36564 `Main::Update` |
| VR 1.4.15 (`SkyrimVR.exe`) | 35551 → `0x1405B6D70` | +0x11F → `call 0x1405BAB10` | `Main::Update` VR |

Вызов происходит на каждой итерации цикла, пока окно активно (или включён
bAlwaysActive): при паузе, в главном меню и во время загрузочных экранов, которые
крутит этот же цикл. Сигнатура `void(Main*)`; прежний второй параметр `float` не существовал.
Адрес берётся из Address Library плюс смещение callsite для runtime; диагностика
patch-site остаётся у трамплина CommonLib (`skse_patch_safety`). Трамплин выделяется через
`SKSE::Init(..., {.trampoline = true, .trampolineSize = 64})`; второй 5-байтовый call в нём —
хук рассылки ввода (см. «Захват клавиатуры»).

Не проверялись реально: другие версии AE (1.6.640, 1.7.99, 1.7.104, GOG/Epic) —
смещение callsite для них нужно снять с соответствующего binary и добавить отдельно.

## Потоки

| Источник | Поток | Обработка |
|---|---|---|
| Сообщения SKSE | поток отправителя (на практике главный) | `Runtime::Post` → кадр |
| `MenuOpenCloseEvent`, `InputEvent` | главный (UI/input) | `Runtime::Post` → кадр |
| Хук рассылки `InputEvent` (`PollInputDevices`) | главный, внутри `Main::Update` | фильтрация цепочки на месте; состояние фильтра под mutex |
| `TESDeathEvent`, `TESActivateEvent` | могут приходить с AI/скриптовых потоков | `Runtime::Post` → кадр; в уведомление смерти кладутся только `dead` и `ObjectRefHandle` убийцы, `handle.get()` и имя — в кадре |
| PrismaUI: DOM ready, JS listener, console | PrismaUI 1.5.1 оборачивает каждый callback в `SKSE::GetTaskInterface()->AddTask`, т.е. главный поток (проверено по `src/API/API.cpp` framework) | прямой вызов `Host::Session`/`ClientApplication` |
| Рендер SKSE Menu Framework | вне игрового потока | читает `MenuSnapshot` под mutex, действия — `Runtime::Post` |
| API модов (`IVDreamsleeve1`, Papyrus) | любой поток | `Runtime::RequestAnnouncement`: очередь (32) под mutex, пока сессия готова; отправка в Core — в кадре |

Очередь уведомлений ограничена 64 записями; переполнение сбрасывается флагом, по
которому кадр пересчитывает видимость из текущего набора меню.

## Жизненный цикл сессии

- Сеть не зависит от игры: чат работает в главном меню. При наличии сохранённого входа
  (Windows Credential Manager) один раз выполняется `ConnectSaved()`; при потере
  соединения после Ready — повтор с backoff 5→60 с, пока пользователь не отключится/выйдет.
  Ручное отключение (`disconnect`, `signOut`, `forgetLogin`, кнопка в SKSE Menu) ставит
  `manualDisconnect`, который снимает только явный вход (`signIn`, `signInSaved`, «Войти»
  в меню). Статус Ready флаг не снимает: в кадре запроса Drain ещё видит Ready, и сброс
  по нему возвращал клиента в сеть сразу после отключения.
- Новая игра / загрузка: контекст Loading, `GameExited` отправляется только если сервер
  знал персонажа; после готовности мира — `CharacterStarted` (сервер сам сбрасывает
  телеметрию), затем details/AV/движение заново.
- Возврат в главное меню: `GameExited`, светлячки удалены.
- Reconnect: новая generation в `ClientExchange` → телеметрия отправляет всё заново,
  `Host::Session` берёт свежий снимок для UI, `MovementView` сбрасывает историю.
- Пересоздание view (Prisma reload): `Session::ResetView()` отбрасывает соответствия
  requestId и запрашивает снимок; ответы старого view не попадают в новый.
- Завершение: `Main::quitGame` → `Nameplates::Shutdown()` (GFx-объекты освобождаются,
  пока Scaleform жив) → `Stop()` (join сетевого потока) до выхода процесса.

## PrismaUI и bridge

View `Data/PrismaUI/views/Dreamsleeve/index.html` создаётся на kDataLoaded, listener
`dreamsleeveCommand` регистрируется сразу; события идут через
`InteropCall(view, "dreamsleeveReceive", json)` — строка, разбираемая `JSON.parse`, а не
исполняемый скрипт. `Invoke` не используется. uint64 передаются строками
(`Host/Bridge.ixx`). Контракт событий/команд — [UI README](../src/Dreamsleeve.Client.UI/README.ru.md).

Дополнительно к контракту UI host посылает `{"type":"settings"}` сразу после готовности
страницы, чтобы положение и оформление применялись до снимка и без соединения.

Снимок UI посылается только для Ready-сессии (UI трактует любой снимок как
«подключено»); снимки отключения обновляют состояние host молча, а UI получает
`connection`. Чат: `sendChat` → `Session::SendChat` (`NextRequestId`, `Post`);
результат команды с известным RequestId → `sendResult`, чужие RequestId (команды
телеметрии) игнорируются. `Session` держит один реестр ожидающих запросов (чат, объявление
мода, метка, скрытое имя, отображаемое имя) и по его виду решает, что показать; команду
отправляет одна функция `Submit`. Авторизация: `signIn`
(с `displayName` — регистрация), `signInSaved`, `signOut`, `forgetLogin`, `disconnect`;
пароль передаётся в `Connect` и затирается, в TOML/логи/JS не возвращается.
Событие `auth` посылается при любом изменении статуса и при каждом завершении операции
(`ClientStatus::authSequence`), даже если результат совпал с предыдущим: страница сама
ставит `authenticating` и ждёт ответа. Текст ошибки для UI — код и сообщение
(`ConnectTimeout: OpenSession timed out`) без сигнатур функций из `ToLogString()`; он
обрезается до 512 байт по границе UTF-8, потому что `parse.ts` отвергает более длинные события.
`saveSettings` пишет `ui.toml` атомарно (временный файл + rename) и отвечает `settingsResult`.

Скрытое имя ([ModerationAndNamesRu.md](ModerationAndNamesRu.md#скрытое-имя)). `hideIdentity`
из `ui.toml` передаётся в `ClientExchange::SetHideIdentity` при запуске и после каждого
подтверждённого переключения (`off` / `everywhere` / `exceptGroundMarks` ↔
`Domain::HiddenIdentity` через `Bridge::HidingOf`/`HidingName`); ClientRuntime кладёт его в
`OpenSession.hidden_identity` при открытии сессии. Команда UI `setIdentityVisibility{hiding}` в
Ready-сессии идёт в `Session::SetIdentityVisibility` → Core `SetIdentityVisibility{requestId, hiding}`
(одна за раз); результат — `IdentityChanged` (вариант; псевдоним — в статусе) или отказ. Только
подтверждение меняет `hideIdentity` и сохраняет `ui.toml` (`Session::Frame::hideIdentity`,
`Logic::Drain`); поле лежит в `[ui]`, вне настроек UI, и `saveSettings` его не касается. Без сессии UI
меняет только выбор для следующего входа; пока сессия открывается, переключение
отклоняется. `ClientStatus::pseudonym` — текущий псевдоним (из `SessionOpened.own_pseudonym` и
подтверждений; `ClientStatus::hiding` — применённый вариант); host шлёт UI событие `identity`
(`mode`, `pending`, `pseudonym`, `error`) при каждом изменении. Отказ открытия с
`HIDDEN_IDENTITY_NOT_ALLOWED` (`Frame::identityRefused`) останавливает автоматические
переподключения: игрок выключает режим и входит сам. Профили `pseudonymous` приходят в
проекции UI без username и персонажа (`UiPlayer.pseudonymous`); `NameFor` называет их
псевдонимом в любом режиме имени.

Смена отображаемого имени ([ModerationAndNamesRu.md](ModerationAndNamesRu.md#смена-отображаемого-имени)).
Команда UI `changeDisplayName{displayName}` в Ready-сессии идёт в `Session::ChangeDisplayName` →
Core `ChangeDisplayName{requestId, displayName}` (одна за раз). Итог — `NameChanged`
или отказ; host шлёт UI событие `displayName` (`pending`, `changed` — сохранённое имя, один раз,
`error` — текст отказа на русском из `Bridge::DisplayNameRejectionText`, с минутами до следующей
смены). Обрыв соединения до ответа снимает ожидание с ошибкой. Свой профиль с новым именем
приходит обычным обновлением онлайна.

Видимость: `visible = !hideUi && !menuBlocked && domReady`, `menuBlocked` пересчитывается
по всему набору открытых меню (список `HidingMenus` в `PrismaUI.ixx`: загрузка, главное
меню, инвентарь, контейнер, торговля, крафт, магия, карта, журнал, навыки, tween,
сон/ожидание, взлом, RaceSex, книга, подарок, обучение, MessageBox, LevelUp, избранное,
tutorial, Creation Club, Mod Manager, титры, диалог, консоль, Mist). Скрытие —
JS `hide` (сброс ввода, данные сохраняются) плюс `Hide(view)`; показ — `Show(view)` + JS `show`.
Полное отключение из SKSE Menu (`hideUi` в `ui.toml`) применяется тем же путём; закрытие
игрового меню его не отменяет. Сеть при скрытии не останавливается.

Активация: Enter (или F2 по настройке) через sink `BSInputDeviceManager`, только когда
UI видим, чат не активен, `!UI::GameIsPaused()`. `Focus(view, false, false)` без паузы игры:
PrismaUI открывает свой FocusMenu (контекст MenuMode, блокировка gameplay-controls) и
перехватывает клавиатуру через WndProc. Escape в JS → команда `close` → `Unfocus`. Если игра
сама сняла focus, через 1,5 с после активации `HasFocus` даёт false и UI получает `deactivate`.
Enter по умолчанию не занят в контексте Gameplay; в контекстах меню активация заблокирована списком меню.

### Захват клавиатуры

FocusMenu PrismaUI блокирует только gameplay-controls; sinks `BSInputDeviceManager` при
активном чате продолжали получать каждую клавишу, и хоткеи любых SKSE-модов срабатывали во
время набора. `Hooks.ixx` ставит `write_call<5>` на вызов рассылки внутри
`BSInputDeviceManager::PollInputDevices` (ID 67315/68617, смещение +0x7B, VR +0x81) —
единственную точку, через которую цепочка событий кадра попадает во все sinks, включая
`MenuControls`/`PlayerControls`. Перед патчем сверяются байты `mov [rsp+40h], rcx; mov rcx, rsi;
call` и цель вызова (ID 67355/68655; VR — только диапазон): незнакомая версия оставляет ввод
нетронутым с ошибкой в логе, чужой хук на том же вызове — цепляется за нами как оригинал.

Политика — `Host::InputCapture::Filter` (без CommonLib, doctest): фильтруются только
`ButtonEvent` клавиатуры. `SetActive(true)` в PrismaUI начинает захват: клавиши, которые
игра к этому моменту видит нажатыми (в том числе открывший чат Enter), доставляют лишь
отпускание, всё остальное с клавиатуры отбрасывается. Мышь (нужна PrismaUI через тот же
sink), геймпад, VR, `CharEvent`, движение мыши и thumbstick проходят всегда. `SetActive(false)`
(команда `close`, потеря фокуса, пересоздание страницы) снимает захват целиком.
Цепочка правится на месте (`Input::Dispatch` в `Game/Input.ixx`) — `next` допущенных событий перекидывается через отброшенные,
голова передаётся через свой стек-слот, после возврата связи восстанавливаются; события
принадлежат игре и не освобождаются. Отключение: `captureKeyboard = false` в `client.toml`.
Хук не защищает от `GetAsyncKeyState` и чужих оконных хуков. Подробности и адреса:
[InputCaptureHookRu.md](InputCaptureHookRu.md). В игре не проверялось.

## Телеметрия

Сбор только при контексте Playing и `PlayerReady()`, при Ready-сессии:

- `CharacterStarted{GetName()}` один раз на контекст и generation, `CharacterRenamed` при изменении имени (RaceSex).
- Движение: `LocalMovement{PlayerLocation}` каждые `playerSampleIntervalMs` (50 мс),
  `LocalLocation` явно при смене пространства и при скачке больше `movement.teleportDistance`.
  Пространство: интерьер → CELL, экстерьер → WRLD; `FormKey = {имя файла-источника в нижнем регистре, GetLocalFormID()}`,
  для динамических форм — `{"runtime", FormID}` (сессионная идентичность, совпадений между клиентами не будет).
- Details каждые 250 мс, отправка reliable только при изменении: раса (`NamedForm`), уровень (uint16, 0 допустим),
  activity, place, `gameStartedAtUnixMs` (время начала контекста).
- Actor values каждые 250 мс при изменении (порог 0.05): `skyrim:health/magicka/stamina` как
  Resource{`GetActorValue`, `GetPermanentActorValue + GetActorValueModifier(kTemporary)`}. Диапазоны не ограничиваются.
- `TESDeathEvent` для игрока ускоряет следующий тик details/AV; сама смерть читается `IsDead()`.
  Исследование покрытия событий и хуков: [DeathAndActorValuesRu.md](DeathAndActorValuesRu.md).

Activity (приоритет сверху вниз): Loading; меню с целью — Bartering (контрагент:
`BarterMenu::GetTargetRefHandle`, а если это игрок — `MenuTopicManager::speaker/lastSpeaker`),
Reading, Crafting (furniture), Lockpicking (+сложность), Training (speaker), container/gift (Menu с целью),
Talking (Dialogue Menu); прочие меню — `Menu{menuKey}`; затем Riding, Dead, Ragdoll, Combat (цель),
Sneaking, Flying, Swimming, UsingObject, Exploring.

Проверки по жалобам Discord Rich Presence:

- Торговля: имя игрока вместо торговца — контрагент берётся из speaker, если target handle указывает на игрока.
- Застрявшая активность (Unarmoured shrine): `UsingObject` требует `SitSleepState != kNormal` и загруженного 3D
  furniture; стейл handle без состояния сидения не считается использованием. Смена локации/меню/смерть/загрузка
  пересчитывают активность на следующем тике. Причина в самом моде не установлена (не воспроизводилось).
- Добыча руды («Using this should not be visible»): имя берётся от объекта, который игрок реально активировал
  за 3 с до входа в furniture (`TESActivateEvent`), если furniture скрытая; иначе имя furniture.
  Скрытой считается furniture с флагом `kIsMarker`, без имени, с моделью `*marker*` или с
  placeholder-именем «should not be visible»: у `PickaxeMining{Floor,Wall,Table}Marker` в Skyrim.esm
  флаги записи равны 0, модель `Furniture\PickaxeFloorMarker.nif`, FULL «This should not be visible»
  (проверено по данным load order), так что одного флага недостаточно.

Place: `worldspaceName` (цепочка `parentWorld`), `locationName` (цепочка `BGSLocation`, иначе имя
CELL/WRLD), ближайший видимый map marker персистентной ячейки в радиусе 16384 (пересчёт при смене
пространства и раз в 5 с), `markerKind` — нормализованный ключ `MARKER_TYPE` (castle/capital/...).

## Светлячки

`Fireflies.ixx`: для каждого игрока из проекции онлайна, кроме себя, в том же WRLD/CELL и в радиусе
`client.visibilityDistance`, `MovementView::Sample(id, now)` даёт позу; создаётся одна placed reference
`FXGlowFillRoundXBrt` (Skyrim.esm 0x02EB0F) через общий модуль `Game/PlacedReferences.ixx`
(`TESDataHandler::CreateReferenceAtLocation`, `SetTemporary`, масштаб) и каждый кадр перемещается
`SetPosition` + `Update3DPosition(true)` (выше позы на `fireflyHeightOffset` из `[ui.chat]`,
по умолчанию 110, диапазон 0..512; scale 0.25). Между кадрами ссылки хранятся только как
`RE::ObjectRefHandle` в `PlacedReferences::Set<PlayerId>`; резолв `handle.get()` — в кадре,
владение — `RE::NiPointer` на время вызова. Тот же модуль используют метки на земле.
Первый вызов меняет координаты REFR, второй переносит их в загруженную 3D-модель;
интерполяция уже выполнена в `MovementView`. В SE 1.5.97 проверено в IDA:
`SetPosition` — ID 19363 / RVA 0x296910, `Update3DPosition` — vtable slot 0x3F / RVA 0x286130.
Используются методы CommonLib, адреса не зашиты в плагин.
`SetPosition` не меняет parent cell: ссылка остаётся в ячейке, где создана. В экстерьере ключ
пространства — WRLD, поэтому при удалении от ячейки спавна она отсоединяется, 3D выгружается,
а handle остаётся валидным. `PlacedReferences::Set::Resolve` проверяет `GetParentCell()->IsAttached()` и удаляет
отсоединённую ссылку, чтобы `Tick` пересоздал её в текущей ячейке игрока.
При временной неготовности мира (включая LoadingMenu) телеметрия один раз отправляет
reliable-снятие позиции; после загрузки отправляет новую границу локации. Игрок ушёл/потерял видимость/сменил
пространство — `Disable` + `SetDelete`. Смена собственного пространства, загрузка, главное меню,
`showFireflies=false` и недоступность 3D удаляют все ссылки; пересоздание лениво.
Выключение светлячков не меняет сетевой трафик. Полноценные акторы, бой и инвентарь не воспроизводятся.

## Конфигурация

| Файл | Кто пишет | Содержимое |
|---|---|---|
| `Data/SKSE/Plugins/Dreamsleeve/client.toml` | пользователь; без файла плагин пишет встроенный [client.example.toml](../src/Dreamsleeve.Client.Core/client.example.toml) (правило xmake `dreamsleeve.embed`), dist кладёт тот же файл | сервер, auth URL, интервалы, радиус, формы светлячка и меток; `LoadClientSettings` только разбирает, `ValidateClientSettings` в `ClientApplication::TryCreate` проверяет один раз, runtime, codec и `MovementView` доверяют проверенному |
| `Data/SKSE/Plugins/Dreamsleeve/ui.toml` | плагин, атомарно | `[ui] hideUi` и `hideIdentity` (скрытое имя: `off`/`everywhere`/`exceptGroundMarks`; пишет только host после подтверждения сервера), `[ui.chat]` — положение, размер, оформление, клавиша активации, имена и облачки над светлячками (в том числе цвета и рамка), высота светлячка, метки на земле, `nameMode`/`streamerMode`; пределы и варианты — таблицы правил `Host/UiSettings.ixx`, все ключи — [ui.example.toml](../src/Dreamsleeve.Client.UI/ui.example.toml); `[[names.aliases]]` и `[[names.ignored]]` — псевдонимы и игнор по адресу сервера (до 1 MiB) |
| `Data/SKSE/Plugins/Dreamsleeve/aliases.toml` | пользователь (поставляется в dist) | словарь псевдонимов режима стримера; при ошибке — встроенный список |

Разделение выбрано, чтобы запись настроек UI никогда не переписывала пользовательский
сетевой конфиг и не требовала сохранения неизвестных ключей. Пароля и токена нет ни в одном файле.

## Dist

`python Scripts/package_dist.py` собирает DLL (`xmake build Dreamsleeve.Client`), UI (`npm run build`),
публикует сервер (`dotnet publish -c Release`, framework-dependent) и раскладывает `dist/`:

| Каталог | Содержимое |
|---|---|
| `dist/Client` | Раскладка мода относительно Data: `SKSE/Plugins/Dreamsleeve.Client.dll(+pdb)`, `SKSE/Plugins/Dreamsleeve/client.toml`, `PrismaUI/views/Dreamsleeve/*`, `Scripts/DreamsleeveClient.pex`, `Scripts/Source/DreamsleeveClient.psc`, `Dreamsleeve/API/DreamsleeveAPI.h`, `Dreamsleeve/README.md`, `Dreamsleeve/THIRD_PARTY_NOTICES.md` |
| `dist/Server` | `Dreamsleeve.Server.dll` с зависимостями, `db/migrations`, `server.example.toml`, `README.md` (нужен ASP.NET Core Runtime 10) |

`--skip-build` использует готовые DLL и UI, `--no-server` собирает только клиент. Сторонние DLL
(PrismaUI, SKSE Menu Framework, Address Library, Media Keys Fix) не включаются: у них свои
лицензии и страницы. `node_modules`, demo, dev-server, отчёты тестов, БД и логи в dist не попадают.

## Объявления

Системный канал ([DomainSpecRu.MD §4.8](DomainSpecRu.MD)) приходит в `SessionOpened.channels`
с видом `SYSTEM` и своей историей. Core хранит вид канала в `ChatCache`; `Host::Session`
описывает каналы для UI по виду (`Bridge::ToUiChannel`: общий — `global`, запись разрешена;
системный — `system`, «Объявления», только чтение) и передаёт у объявлений
`announcement: {origin, kind, signature?}`. У серверных объявлений нет автора, у
клиентских — игрок, чей клиент их отправил. Неизвестные источник и вид показываются как
`thirdParty` и `announcement`: неизвестное не получает доверия сервера. Подпись мода уже
проверена (сервером или входом API) и перед мостом только обрезается до 64 символов.

- Облачка над светлячками следуют только общему каналу и игроку-автору (`Session::Fresh`).
- `sendChat` UI принимается только для канала вида `global`.
- Игнор: серверные объявления автора не имеют и не скрываются; объявления, отправленные
  клиентом игнорируемого игрока, скрываются вместе с его чатом.
- Фильтр помеченных слов (`textFilter`) применяется к клиентским объявлениям так же, как
  к чату.

Настройки → «Объявления» (`ui.toml`, `[ui.chat]`, применяются в UI сразу, без повторной
проекции host):

| Ключ | По умолчанию | Значения |
|---|---|---|
| `announcementChannels` | `all` | `tab` — только вкладка «Объявления»; `all` — также «Все»; `current` — также любая выбранная вкладка |
| `announcementsServer` | true | показывать объявления сервера |
| `announcementsTrustedClient` | true | объявления клиента Dreamsleeve |
| `announcementsThirdParty` | true | объявления других модов |
| `announcementsEvents` | true | вид «событие» |
| `announcementsPeriodic` | true | вид «напоминание» (периодические) |

Виды `announcement` и `admin` показываются всегда, если показан их источник. Старый
`ui.toml` без этих ключей получает значения по умолчанию (тест TOML round-trip);
неизвестное значение `announcementChannels` заменяется на `all`.

API для других модов (C++ `IVDreamsleeve1` и Papyrus `DreamsleeveClient`) описан в
[DreamsleeveModApiRu.md](DreamsleeveModApiRu.md), там же — какое правило проверяет
какой слой. Кадр после Drain отдаёт запросы `Session::PostAnnouncement`, который берёт `RequestId` и
хранит соответствие до ответа. Итог (подтверждение, отказ сервера, локальный отказ Core или
смена сессии) проходит через `Session::Finish` и уходит в `ModApi::Report`: строка лога, callbacks, зарегистрированные
плагинами через `AddAnnouncementResultCallback`, и mod event `Dreamsleeve_AnnouncementResult`;
отказ дополнительно показывается строкой «Не отправлено» в системном канале (событие
моста `announcementResult` с его `channelId`). Новых хуков игры API не добавляет.

## Проверки и границы

См. итог в [CurrentStateRu.md](CurrentStateRu.md#skse-клиент). Игровые проверки, которые
нельзя выполнить без запуска Skyrim, перечислены там как ручные.

### Форма светлячка

В `client.toml`, внутри существующей секции `[client]`:

```toml
fireflyPlugin = "Skyrim.esm"
fireflyFormId = 0x02EB0F
fireflyScale = 0.25
```

Можно указать собственный ESP/ESM/ESL с базовой формой **STAT** и своей NIF-моделью
без коллизии. Нужен локальный FormID базовой формы, не placed REFR и не полный
ID с индексом загрузки. Для ESL тоже указывается локальный ID, без FE-префикса.
Настройка локальная, применяется после перезапуска игры. Отсутствующая форма или
неподходящий тип отключают светлячков с сообщением в логе; скрытого fallback нет.

Новые reference помечаются `SetTemporary()` сразу после создания. Динамический
FormID сам по себе не исключает запись в сохранение. Отложенная обработка
`kSaveGame` не является очисткой до сериализации, поэтому сохранение больше не
удаляет и не пересоздаёт светлячков. При удалении модель сначала скрывается,
затем вызываются `Disable` и `SetDelete`. Лог содержит player ID и FormID при
создании, FormID при удалении. Старые объекты, уже попавшие в save прежней версией,
автоматически не удаляются: общая базовая форма может использоваться самой игрой.
Проверка отсутствия дублей/попадания в save требует прогона в игре.

`fireflyScale`: 0.01..10.0, по умолчанию 0.25. Skyrim округляет масштаб до сотых.

Проверка SE 1.5.97 в IDA: `SetTemporary` (ID 14485, RVA 0x194D20)
снимает уже накопленные изменения и выставляет флаг 0x4000; проверка допуска
`AddChange` (RVA 0x57B820) отвергает формы с этим флагом. `SetScale`
(ID 19239, RVA 0x28CCB0) хранит масштаб в сотых в диапазоне 1..1000.

## Имена над светлячками (Scaleform)

`UI/Nameplates.ixx` создаёт собственный MovieClip в существующем HUD и динамические
TextField, без SWF, PrismaUI и отдельной зависимости. Показывается имя из
`Host::Names::NameFor` — то же, что в UI (режим имени или псевдоним стримера,
см. [ModerationAndNamesRu.md](ModerationAndNamesRu.md)); игрок со скрытым именем получает
префикс `~` (`Names::PlateName`, «~Страж 2»), как знак перед именем в веб-UI; текст передаётся как обычный UTF-8, не HTML. Подпись — только текст без подложки,
с чёрной обводкой `flash.filters.GlowFilter` (blur 3, strength 12) на embedded-шрифте
`$EverywhereFont`; если класс фильтра в Scaleform недоступен, в лог пишется предупреждение
и имя рисуется без обводки. Класс в SE 1.5.97 есть. Падение в аллокаторе Scaleform (`GHeap`)
по Esc 29.09.2026 расследовалось: прежние такие вылеты были багом Community Shaders 1.9.0
(исправлен в 1.9.1, #2747), для случая с 1.9.1 виновник не установлен; безфильтровый вариант
с восемью теневыми копиями текста пробовался и отклонён по решению автора. Один TextField на видимый
PlayerId, текст и размеры обновляются при изменении имени; каждый кадр меняется
позиция. Удаление игрока, смена пространства, выход из игры и отключение светлячков
очищают опубликованный кадр. Замена HUD освобождает старые GFx-объекты до movie.
Рендерер живёт в куче и не уничтожается: статические деструкторы DLL выполняются уже после
остановки Scaleform. `Nameplates::Release()` (выход из контекста Playing) и
`Nameplates::Shutdown()` (quitGame) снимают TextField и MovieClip и отпускают movie, пока UI
жив; после Shutdown хук `AdvanceMovie` ничего не рисует.

Запись слоя ключуется `LabelKey {kind, id}` (игрок, надпись, место смерти), имена
GFx-объектов строятся из ключа (`name_player_<id>`, `bubble_note_<id>`), поэтому игрок и
метка с одинаковым числовым id не делят TextField. Стиль облачка и цвет имени —
свойства записи (`Label.style`, `Label.nameColor`), а не кадра: смена стиля пересоздаёт
только затронутые объекты. Цвет имени над светлячком — `fireflyNameColor` (`#RRGGBB`,
по умолчанию `#EEECE5`).

На основном потоке используется та же интерполированная поза, что перемещает
светлячок. После проверки дистанции и проекции выполняется один `Raycast::Clear`
(`TES::Pick`) от камеры до опорной точки подписи с `COL_LAYER::kLineOfSight` и группой коллизии
игрока. Это проверка игровой коллизии, а не depth buffer: прозрачные и модовые
меши следуют своим collision-данным. Штатный `Actor::HasLineOfSight` здесь не
используется: у игрока он проверяет frustum и до трёх высот габаритов цели.

HUD меняется только внутри `HUDMenu::AdvanceMovie`, а не из сетевого потока.
Между основным потоком и HUD передаётся небольшой снимок строк и экранных координат
под mutex; движковые указатели и GFxValue через него не передаются. Это отдельная
граница UI, она не добавляет блокировок в ClientApplication. Проекция может
отставать от основного кадра на один UI-проход.

Настройки имён — `[ui.chat]` в `ui.toml`: `showFireflyNames`, `fireflyNameOcclusion`,
`fireflyNameFontSize` (единицы HUD, 8..48), `fireflyNameOffset` (выше центра светлячка,
игровые единицы 0..512). Дистанция ограничивается существующей `visibilityDistance`. Имена скрываются при
паузе и выключенном HUD. Настройка скрытия Prisma-чата не выключает имена.

Пока реализован **SE/AE HUD-рендерер**. DLL продолжает поддерживать VR, но имена
в VR намеренно отключены: проекция на плоский HUD не обеспечивает привязку
к точке мира для обоих глаз. Для VR нужен отдельный стереокорректный рендерер.

Проверка хука (ставится в `Hooks.ixx`): slot 0x05 HUDMenu; SE 1.5.97 vtable ID 268816,
функция 0x14087EC70; AE 1.6.1170 ID 215362, функция 0x14091E760.
Адреса только для диагностики; код использует CommonLib relocation.
SE HasLineOfSight: ID 53029 / 0x14091C620, ветка игрока 0x1406A4A00;
TES::Pick ID 13221/13371. Проверено по исходникам CommonLib и IDA, не по имени enum.

Ручная проверка в игре остаётся обязательной: два клиента, кириллица в имени,
1-е/3-е лицо, стены/двери, перемещение камеры, ультраширокий экран,
`tm`, меню, загрузка сохранения, выход/повторный вход в локацию и дисконнект.

Настройки → «Имена над светлячками» содержит переключатели показа/LOS и ползунки
шрифта/высоты. Кнопка «Сохранить настройки» применяет их без перезапуска и сохраняет
в `ui.toml`, секция `[ui.chat]`.

## Облачка чат-сообщений над светлячками

Тот же слой `UI/Nameplates.ixx` рисует над ником одно облачко с последним сообщением
игрока: вложенный MovieClip `bubble_<id>` в слое `DreamsleeveNames` с фоном через
drawing API (`beginFill`/`lineStyle`) и TextField с `wordWrap` на `$EverywhereFont`.
Отдельного SWF, PrismaUI и новых зависимостей нет; в VR облачка отключены вместе с
именами (тот же плоский HUD-рендерер).

Источник текста — `Host::Session::Frame::freshMessages`: только `ChatMessagesAdded`
из дельты Core (подтверждённая публикация сервера), не снимок. Начальная история
каналов из `SessionOpened.channels`, снимок при reconnect и при пересоздании view
(`ResetView`) в облачка не попадают: снимок задаёт нижнюю границу `messageId`,
дельта принимает только большие ID, поэтому повтор события или страница истории
тоже отбрасываются. Фильтр каналов: облачка следуют каналу вида `global`; сообщения
других каналов (в том числе системного), без автора и собственные не показываются. Личных сообщений в протоколе нет; при их появлении фильтр в
`Session::Fresh` нужно расширить явно.

`Host::Bubbles` (Runtime, главный поток) хранит один текст на игрока и время получения:
новое сообщение заменяет текст и перезапускает таймер; alpha считается только от
прошедшего времени (`bubbleDuration`, затем fade `bubbleFadeDuration` или мгновенное
снятие при выключенном fade). Видимость на таймер не влияет: сообщение за стеной или за
краем экрана продолжает истекать и не показывается заново с начала. `Fireflies::Tick`
раз в кадр снимает истёкшие записи и записи ушедших игроков; `ClearAll` (смена
пространства, загрузка, главное меню, отключение светлячков), дисконнект и смена
generation сессии (`Logic::Drain`) очищают всё. Сообщение показывается только вместе с
видимым светлячком автора и не копится для его появления: оно просто истекает.

Ник и облачко передаются одной записью `Nameplates::Label` с одной проекцией и одним
`TES::Pick` (настройка `fireflyNameOcclusion` действует на оба). Опорная линия облачка —
`y − (fireflyNameFontSize + 8) − 6` от якоря имени независимо от того, показан ли ник:
выключение имён его не сдвигает, а рост текста увеличивает облачко вверх, нижняя граница
закреплена. Текст — обычный UTF-8 (`SetText`, не HTML), строки выровнены по центру облачка (поле
сужается до измеренной ширины после переноса); имена и подписи меток центрированы так же. Ширина ограничена
`bubbleMaxWidth`, число строк — 6: текст режется по code point до 320 символов, затем по
измеренной `textHeight` с многоточием; длинные слова переносит сам `wordWrap`. Фон —
тёмная заливка с непрозрачностью `bubbleBackground`, рамка 1 px включается `bubbleBorder`,
цвет текста — `bubbleTextColor`; «без фона вообще» — `bubbleBackground = 0` и рамка
выключена. Текст непрозрачный; fade применяется к `_alpha` всего клипа. Раскладка
пересчитывается при смене текста или стиля (шрифт, ширина, фон, рамка, цвет), каждый кадр
обновляются только позиция и alpha. Прежние константы `Nameplates` (рамка `0x9C9A90` на
55 %, заливка `0x0A0A0C`, текст `0xEEECE5`) стали полями `BubbleStyle`; заливка и цвет
рамки остаются константами.

Настройки → «Сообщения над игроками» (`ui.toml`, `[ui.chat]`, независимы от имён и
fade окна чата), применяются кнопкой сохранения без перезапуска:

| Ключ | По умолчанию | Диапазон |
|---|---|---|
| `showBubbles` | true | |
| `bubbleDuration` (с) | 8 | 1..60 |
| `bubbleFade` | true | |
| `bubbleFadeDuration` (с) | 1.0 | 0.1..5 |
| `bubbleFontSize` (HUD) | 16 | 8..48 |
| `bubbleMaxWidth` (HUD) | 320 | 120..800 |
| `bubbleBackground` | 0.65 | 0..1 |
| `bubbleBorder` | true | |
| `bubbleTextColor` | `#EEECE5` | `#RRGGBB`; иная строка → умолчание |

Старый `ui.toml` без этих ключей получает значения по умолчанию (проверено тестом).

Настройки → «В бою» (`[ui.chat]`): `combatHideFireflies`, `combatHideNames`,
`combatHideBubbles`, `combatHideGroundMarks`, `combatHideGroundText`, по умолчанию `false`. Пока `PlayerCharacter::IsInCombat()`,
`Fireflies::Tick` соответственно не держит светлячков (`ClearAll`, как при
`showFireflies = false`), не заполняет имя или облачко в `Label`. Скрытие светлячков
убирает и имена с облачками: другого якоря у них нет. Таймеры облачков идут своим
чередом: сообщение, пришедшее в бою, покажется после боя, если время показа не вышло.

Проверено без игры: `Client.Host` — фильтр свежих сообщений (история, повтор, другой
канал, объявление без автора, self, reset view), таймеры/замена/fade/без fade/prune, TOML
совместимость и границы; `Host.UiSettings` — правила и нормализация; Playwright — сохранение и
восстановление настроек блока. Ручная проверка в Skyrim остаётся обязательной:
появление один раз, замена, истечение с fade и без, длинный текст/кириллица/HTML-подобный
текст, ник на месте при росте облачка, включённые/выключенные имена, уход игрока,
дисконнект, временная невидимость, сохранение настроек и старый `ui.toml`.

## Метки на земле

Игровая часть [меток](GroundMarksRu.md) (часть 2, 29.09.2026): статики в мире, подписи над
ними, отправка смерти и веб-UI. Домен, протокол, сервер и ядро клиента — часть 1; часть 3
(протокол v9) добавила полный список своих меток с сервера и вкладку «Метки».

### Источник и отбор

Источник — снимок видимых меток Core: `Host::Session` собирает `ClientSnapshot.groundMarks` и
упорядоченные переходы `ClientStateDelta.groundMarks` (`Cleared / Removed / Added`) в
`VisibleMarks()`; сервер уже отфильтровал их по пространству и радиусу. Отдельно ведётся
`OwnMarks()` — полный список меток игрока, который сервер присылает сразу после открытия
сессии и заново при каждом изменении (`OwnGroundMarks`, протокол v9; в Core —
`ClientSnapshot.groundMarks.own` и `ClientStateDelta.ownGroundMarks`). Метка, оставленная в
прошлой сессии далеко отсюда, видна в «Моих метках» сразу после подключения.

`GroundMarks::Tick` в кадре: пространство игрока (`Telemetry::SpaceKey`) должно совпасть с
`LocationId` метки; метки игнорируемых игроков не рисуются; из остальных берутся ближайшие
`maxVisibleNotes` надписей и `maxVisibleDeaths` мест смерти в пределах `groundDrawDistance`.
Лишние ссылки удаляются набором `visible`, как у светлячков. Смена пространства, загрузка,
главное меню и выключенные переключатели удаляют все ссылки; пересоздание лениво.

### Статики

Одна временная placed reference на метку через `Game/PlacedReferences.ixx`
(`SetTemporary` сразу после создания, поэтому в сохранение они не попадают; удаление —
`SetAppCulled` / `Disable` / `SetDelete`). Форма вида — `[client]` в `client.toml`:

```toml
groundNotePlugin = "Skyrim.esm"
groundNoteFormId = 0x075DDB   # FXGlowFlatRndBrt
groundNoteScale = 0.5
deathMarkPlugin = "Skyrim.esm"
deathMarkFormId = 0x075DD9    # FXGlowFlatRndDim
deathMarkScale = 0.5
```

Выбор форм: нужны ванильные STAT из `Skyrim.esm` без коллизии, чтобы метка не мешала
движению и бою. Семейство `FXGlowFlatRnd*` (`meshes\Effects\Ambient\FXGlowFlatRnd*.nif`) —
плоские светящиеся диски с блоками `BSFadeNode / BSTriShape / BSEffectShaderProperty /
NiAlphaProperty / BSXFlags`, без `bhk*`-коллизии (проверено по данным load order через
housecarl); это та же семья, что светлячок `FXGlowFillRound*`, только плоская. Надпись —
яркий диск (`Brt`), место смерти — тусклый (`Dim`); красная подпись различает виды.
Рассматривались `HighPolyNote*` (`Clutter\Books\Note01\Note01.nif`, бумажная записка): тоже
без коллизии, но со скином и `BSBehaviorGraphExtraData`; без проверки в игре поведение
такого STAT как placed reference неизвестно, поэтому отклонено. Форма резолвится в
`kDataLoaded`; отсутствие или иной тип отключает соответствующий вид с ошибкой в логе, без
скрытого fallback. Собственный esp/esl — по тем же правилам, что у светлячка.

Снап на пол: при создании ссылки один `Raycast::GroundBelow` от точки метки +64 до −512;
`z` = точка попадания + `groundNoteOffset` / `deathMarkOffset` (по умолчанию 5, −64..256),
без попадания — `z` метки. Один луч на спавн, не на кадр; ссылки не двигаются. Поворот —
курс из метки (`heading` как угол Z).

### Подписи

Через `Nameplates` с ключами `LabelKey {Note|Death, id}`: имя автора — тот же резолвер
`PlayerNames().NameFor` (режим имени, стример), текст — `Bridge::FilterText` (тот же фильтр
помеченных диапазонов, что у строк чата и облачков; чужой скрытый текст не показывается).
Имя видно в пределах `groundNameDistance` (600), текст — `groundTextDistance` (150), оба с
гистерезисом 10 %: показанный элемент прячется на 10 % дальше, чем появляется. Окклюзия —
общая `fireflyNameOcclusion`. Якорь подписи — точка ссылки +24. Стиль надписи:
`groundFontSize`, `groundMaxWidth`, `groundBackground`, `groundBorder`, `groundTextColor`;
у места смерти текст и имя автора красные (`deathTextColor`, `#D9534F`), фон и рамка —
`deathBackground`, `deathBorder`. Над текстом в том же облачке стоит шапка — игровая дата
метки (`Host::FormatGameDate`), шрифтом 0,8 от текста, цветом `markDateColor` (`#A9A69B`), с
тонкой линией рамки между шапкой и текстом. По умолчанию шапка есть у мест смерти
(`deathDateHeader`) и нет у надписей (`noteDateHeader`); видна вместе с текстом. Календарь —
`markDateStyle`: `tamriel` («Тирдас, 17 Последнего зерна 4Э 201, 14:05») или `earth`
(«Вторник, 17 августа 4Э 201, 14:05»); эра и год в обоих — тамриэльские. У меток без даты
шапки нет. Облачко перед каждой раскладкой возвращает полям полную ширину переноса, затем
сужает их до измеренного текста — так центровка не сбивается при смене текста. Пауза, скрытый HUD и меню снимают подписи вместе с
именами (общий кадр в `Logic::PublishNameplates`); VR — статики есть, подписей нет.

### Смерть

`DeathEventHandler` кладёт в уведомление `dead` и `ObjectRefHandle` убийцы, ничего не
резолвя: событие может прийти не из главного потока. В кадре `GroundMarks::NoteDeath` по
первому уведомлению (`dead=false`) фиксирует положение и курс игрока, игровую дату
(`GroundMarks::CurrentGameDate()`) и подпись:
`handle.get()` → имя как у цели боя (`Telemetry::RefName`) в виде «Убийца: <имя>»; без
убийцы — «Причина смерти: Утопление», если персонаж плывёт, иначе «Причина смерти: Падение»
(так же читается смерть через консоль: убийцы и воды нет). Подпись локализуется на стороне
автора; сервер проверяет только длину и словарь; имя убийцы обрезается так, чтобы вся
подпись уложилась в лимит. Подпись — недоверенный текст: обрезается до 64 скаляров (умолчание
`ChatInput.DeathMarkText`; протокол v8 лимит не сообщает). `ReportDeath` уходит один раз на
смерть: повтор возможен после того, как `IsDead()` снова вернул false (воскрешение,
загрузка, новая игра сбрасывают флаг вместе с контекстом). Второе уведомление (`dead=true`)
без первого тоже создаёт метку. Без Ready-сессии смерть не отправляется (запись в логе).

### Веб-UI

Кнопка «Оставить здесь» рядом с «Отправить» посылает черновик командой `placeGroundNote`;
host берёт положение из `GroundMarks::CurrentPlacement()` и дату из
`GroundMarks::CurrentGameDate()` и коррелирует ответ в
`markResult` (id метки, id вытесненной, ошибка) — строка ожидания «[Метка] Вы: …» с «Не
оставлено: …», `GROUND_MARK_AREA_FULL` → «Здесь уже слишком много меток». Кнопка недоступна
без соединения, без `snapshot.groundMarksSupported` и при пустом черновике. Панель «Метки»
показывает два списка с подробностями (вид, текст, персонаж, игровая дата в выбранном
календаре, реальное время, пространство и координаты; дату форматирует host, смена
календаря пересылает снимок): «Мои метки» — `OwnMarks()` (событие `groundMarks`) с кнопкой удаления
(`removeGroundMark`), и «Метки рядом» — `VisibleMarks()` (событие `nearbyMarks` при каждом
изменении видимого набора) с именем автора из того же резолвера, без меток игнорируемых
игроков и без снимка персонажа в режиме стримера. Так метки в текущем пространстве можно
прочитать, не подходя к ним.
Все ключи меток и облачков — в `ui.toml` `[ui.chat]`, блоки «Метки на земле», «Сообщения
над игроками», «Имена над светлячками», «В бою»; старый `ui.toml` получает умолчания.

### Настройки `[ui.chat]`

| Ключ | По умолчанию | Диапазон |
|---|---|---|
| `showGroundNotes`, `showDeathMarks` | true | |
| `maxVisibleNotes`, `maxVisibleDeaths` | 16 | 1..64 |
| `groundDrawDistance` | 4096 | 0..16384 |
| `groundNoteOffset`, `deathMarkOffset` | 5 | −64..256 |
| `groundNameDistance` | 600 | 50..4096 |
| `groundTextDistance` | 150 | 50..4096 |
| `groundFontSize` | 16 | 8..48 |
| `groundMaxWidth` | 320 | 120..800 |
| `groundBackground`, `deathBackground` | 0.65 | 0..1 |
| `groundBorder`, `deathBorder` | true | |
| `groundTextColor` | `#EEECE5` | `#RRGGBB` |
| `deathTextColor` | `#D9534F` | `#RRGGBB` |
| `combatHideGroundMarks`, `combatHideGroundText` | false | |
| `markDateStyle` | `tamriel` | `tamriel`, `earth` |
| `deathDateHeader` | true | |
| `noteDateHeader` | false | |
| `markDateColor` | `#A9A69B` | `#RRGGBB` |
| `fireflyHeightOffset` | 110 | 0..512 |
| `fireflyNameColor` | `#EEECE5` | `#RRGGBB` |

### Проверки и границы

Без игры: `Client.Host` — `ui.toml` (умолчания, round-trip, цвета, границы), проекция
видимых и своих меток, корреляция надписи/удаления/смерти с `markResult` и заметками лога,
разбор команд; `Client.Application` — формы меток в `client.toml`; `Host.UiSettings` — умолчания и
границы; vitest — `placeNote`/`removeMark`, разбор событий; Playwright — настройки меток и облачков,
«Оставить здесь» с отказом и повтором, «Мои метки» с удалением. В Skyrim не проверялось:
статики и их вид/масштаб, снап на пол, дальности и гистерезис, отправка смерти (убийца,
утопление, падение, повтор после воскрешения), окклюзия, VR, шапка с датой и чтение
календаря игры.

### Игровая дата

`GroundMarks::CurrentGameDate()` читает `RE::Calendar` в кадре: год `GetYear()`, месяц
`GetMonth()` + 1, день `GetDay()`, день недели `GetDayOfWeek()` (0 — Сандас), час и минуты из
`GetHour()`. Переменной эры в ванильном календаре нет (строка даты игры печатает «4E»),
поэтому эра всегда 4. Значения, выбитые модом за пределы, обрезаются до диапазонов, а не
отменяют метку: дата — оформление. Без календаря надпись и смерть не отправляются.

### Открытые вопросы части 2

| Вопрос | Решение |
|---|---|
| Сервер не сообщает поддержку меток и лимит `DeathMarkText` | Серверные квоты хранения и клиентские настройки показа — разные понятия, сообщать нечего; поддержка = Ready-сессия протокола v9 (`groundMarksSupported = true` в снимке, поле оставлено для будущего host); лимит подписи — константа 64 в `GroundMarks.ixx` |
| Список своих меток | Часть 3: сервер присылает полный список (`OwnGroundMarks`, протокол v9) |
| Вид меток | Плоские glow-диски без коллизии; бумажная записка отклонена без игровой проверки |
| Размер имени над меткой | `groundFontSize` (общий с текстом), не `fireflyNameFontSize` |
| Шаг 1 отдельно | Правки стиля облачков (`BubbleStyle`, `bubbleBorder`/`bubbleTextColor`/`fireflyNameColor`, блок настроек, тесты) не зависят от остального и выделяются в отдельный коммит по файлам `Nameplates.ixx`, `UiSettings.ixx`, `types.ts`, `settings.ts`, `SettingsPanel.tsx` |
