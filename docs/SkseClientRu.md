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
| `Logic.ixx` | Кадр: уведомления → Drain → политика сессии → готовность мира → телеметрия → светлячки → focus |
| `Hooks.ixx` | Вызов `Main::Update` внутри главного цикла окна; поиск callsite с проверкой |
| `Events.ixx` | Sinks: меню, ввод, `TESDeathEvent`, `TESActivateEvent`; только `Runtime::Post` |
| `Game/Telemetry.ixx` | Персонаж, пространство, позиция, activity, place, actor values |
| `Game/Fireflies.ixx` | Placed reference на каждого видимого игрока из `MovementView` |
| `UI/PrismaUI.ixx` | View, listener, доставка событий, focus/visibility |
| `UI/SKSEMenu.ixx` | Страница настроек и статуса |
| `Host/Bridge.ixx`, `Host/UiSettings.ixx`, `Host/Session.ixx` | Без CommonLib: JSON-контракт UI, TOML настроек UI, корреляция запросов и проекция онлайна. Компилируются также в `Dreamsleeve.Client.Tests` |

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
| kSaveGame | `SaveGame_Hook` до записи | имя сохранения | Не подтверждение записи | удаление placed-ссылок светлячков (пересоздаются следующим кадром) |
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
`SKSE::Init(..., {.trampoline = true, .trampolineSize = 64})`.

Не проверялись реально: другие версии AE (1.6.640, 1.7.99, 1.7.104, GOG/Epic) —
смещение callsite для них нужно снять с соответствующего binary и добавить отдельно.

## Потоки

| Источник | Поток | Обработка |
|---|---|---|
| Сообщения SKSE | поток отправителя (на практике главный) | `Runtime::Post` → кадр |
| `MenuOpenCloseEvent`, `InputEvent` | главный (UI/input) | `Runtime::Post` → кадр |
| `TESDeathEvent`, `TESActivateEvent` | могут приходить с AI/скриптовых потоков | `Runtime::Post` → кадр |
| PrismaUI: DOM ready, JS listener, console | PrismaUI 1.5.1 оборачивает каждый callback в `SKSE::GetTaskInterface()->AddTask`, т.е. главный поток (проверено по `src/API/API.cpp` framework) | прямой вызов `Host::Session`/`ClientApplication` |
| Рендер SKSE Menu Framework | вне игрового потока | читает `MenuSnapshot` под mutex, действия — `Runtime::Post` |

Очередь уведомлений ограничена 64 записями; переполнение сбрасывается флагом, по
которому кадр пересчитывает видимость из текущего набора меню.

## Жизненный цикл сессии

- Сеть не зависит от игры: чат работает в главном меню. При наличии сохранённого входа
  (Windows Credential Manager) один раз выполняется `ConnectSaved()`; при потере
  соединения после Ready — повтор с backoff 5→60 с, пока пользователь не отключится/выйдет.
- Новая игра / загрузка: контекст Loading, `GameExited` отправляется только если сервер
  знал персонажа; после готовности мира — `CharacterStarted` (сервер сам сбрасывает
  телеметрию), затем details/AV/движение заново.
- Возврат в главное меню: `GameExited`, светлячки удалены.
- Reconnect: новая generation в `ClientExchange` → телеметрия отправляет всё заново,
  `Host::Session` берёт свежий снимок для UI, `MovementView` сбрасывает историю.
- Пересоздание view (Prisma reload): `Session::ResetView()` отбрасывает соответствия
  requestId и запрашивает снимок; ответы старого view не попадают в новый.
- Завершение: `Main::quitGame` → `Stop()` (join сетевого потока) до выхода процесса.

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
`chatConfirmations`/`rejections`/`commandFailures` с известным RequestId → `sendResult`,
чужие RequestId (команды телеметрии) игнорируются. Авторизация: `signIn`
(с `displayName` — регистрация), `signInSaved`, `signOut`, `forgetLogin`, `disconnect`;
пароль передаётся в `Connect` и затирается, в TOML/логи/JS не возвращается.
`saveSettings` пишет `ui.toml` атомарно (временный файл + rename) и отвечает `settingsResult`.

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
  за 3 с до входа в furniture (`TESActivateEvent`), если furniture помечена `kIsMarker` или без имени; иначе имя furniture.
  Сравнения с конкретной строкой нет.

Place: `worldspaceName` (цепочка `parentWorld`), `locationName` (цепочка `BGSLocation`, иначе имя
CELL/WRLD), ближайший видимый map marker персистентной ячейки в радиусе 16384 (пересчёт при смене
пространства и раз в 5 с), `markerKind` — нормализованный ключ `MARKER_TYPE` (castle/capital/...).

## Светлячки

`Fireflies.ixx`: для каждого игрока из проекции онлайна, кроме себя, в том же WRLD/CELL и в радиусе
`client.visibilityDistance`, `MovementView::Sample(id, now)` даёт позу; создаётся одна placed reference
`FXGlowFillRoundXBrt` (Skyrim.esm 0x02EB0F) через `TESDataHandler::CreateReferenceAtLocation`
и каждый кадр перемещается `SetPosition` + `Update3DPosition(true)` (+110 по Z, scale 0.25).
Первый вызов меняет координаты REFR, второй переносит их в загруженную 3D-модель;
интерполяция уже выполнена в `MovementView`. В SE 1.5.97 проверено в IDA:
`SetPosition` — ID 19363 / RVA 0x296910, `Update3DPosition` — vtable slot 0x3F / RVA 0x286130.
Используются методы CommonLib, адреса не зашиты в плагин.
При временной неготовности мира (включая LoadingMenu) телеметрия один раз отправляет
reliable-снятие позиции; после загрузки отправляет новую границу локации. Игрок ушёл/потерял видимость/сменил
пространство — `Disable` + `SetDelete`. Смена собственного пространства, загрузка, главное меню,
`kSaveGame`, `showFireflies=false` и недоступность 3D удаляют все ссылки; пересоздание лениво.
Выключение светлячков не меняет сетевой трафик. Полноценные акторы, бой и инвентарь не воспроизводятся.

## Конфигурация

| Файл | Кто пишет | Содержимое |
|---|---|---|
| `Data/SKSE/Plugins/Dreamsleeve/client.toml` | пользователь (при отсутствии плагин создаёт минимальный файл) | сервер, auth URL, интервалы, радиус, светлячки — формат `LoadClientSettings` |
| `Data/SKSE/Plugins/Dreamsleeve/ui.toml` | плагин, атомарно | `[ui] hideUi`, `[ui.chat]` — положение, размер, оформление, клавиша активации |

Разделение выбрано, чтобы запись настроек UI никогда не переписывала пользовательский
сетевой конфиг и не требовала сохранения неизвестных ключей. Пароля и токена нет ни в одном файле.

## Dist

`python Scripts/package_dist.py` собирает DLL (`xmake build Dreamsleeve.Client`), UI (`npm run build`),
публикует сервер (`dotnet publish -c Release`, framework-dependent) и раскладывает `dist/`:

| Каталог | Содержимое |
|---|---|
| `dist/Client` | Раскладка мода относительно Data: `SKSE/Plugins/Dreamsleeve.Client.dll(+pdb)`, `SKSE/Plugins/Dreamsleeve/client.toml`, `PrismaUI/views/Dreamsleeve/*`, `Dreamsleeve/README.md`, `Dreamsleeve/THIRD_PARTY_NOTICES.md` |
| `dist/Server` | `Dreamsleeve.Server.dll` с зависимостями, `db/migrations`, `server.example.toml`, `README.md` (нужен ASP.NET Core Runtime 10) |

`--skip-build` использует готовые DLL и UI, `--no-server` собирает только клиент. Сторонние DLL
(PrismaUI, SKSE Menu Framework, Address Library, Media Keys Fix) не включаются: у них свои
лицензии и страницы. `node_modules`, demo, dev-server, отчёты тестов, БД и логи в dist не попадают.

## Проверки и границы

См. итог в [CurrentStateRu.md](CurrentStateRu.md#skse-клиент). Игровые проверки, которые
нельзя выполнить без запуска Skyrim, перечислены там как ручные.
