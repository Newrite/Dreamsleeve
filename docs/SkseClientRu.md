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
`chatConfirmations`/`rejections`/`commandFailures` с известным RequestId → `sendResult`,
чужие RequestId (команды телеметрии) игнорируются. Авторизация: `signIn`
(с `displayName` — регистрация), `signInSaved`, `signOut`, `forgetLogin`, `disconnect`;
пароль передаётся в `Connect` и затирается, в TOML/логи/JS не возвращается.
Событие `auth` посылается при любом изменении статуса и при каждом завершении операции
(`ClientStatus::authSequence`), даже если результат совпал с предыдущим: страница сама
ставит `authenticating` и ждёт ответа. Текст ошибки для UI — код и сообщение
(`ConnectTimeout: OpenSession timed out`) без сигнатур функций из `ToLogString()`; он
обрезается до 512 байт по границе UTF-8, потому что `parse.ts` отвергает более длинные события.
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
`FXGlowFillRoundXBrt` (Skyrim.esm 0x02EB0F) через `TESDataHandler::CreateReferenceAtLocation`
и каждый кадр перемещается `SetPosition` + `Update3DPosition(true)` (+110 по Z, scale 0.25).
Первый вызов меняет координаты REFR, второй переносит их в загруженную 3D-модель;
интерполяция уже выполнена в `MovementView`. В SE 1.5.97 проверено в IDA:
`SetPosition` — ID 19363 / RVA 0x296910, `Update3DPosition` — vtable slot 0x3F / RVA 0x286130.
Используются методы CommonLib, адреса не зашиты в плагин.
`SetPosition` не меняет parent cell: ссылка остаётся в ячейке, где создана. В экстерьере ключ
пространства — WRLD, поэтому при удалении от ячейки спавна она отсоединяется, 3D выгружается,
а handle остаётся валидным. `Tick` проверяет `GetParentCell()->IsAttached()` и пересоздаёт
ссылку в текущей ячейке игрока.
При временной неготовности мира (включая LoadingMenu) телеметрия один раз отправляет
reliable-снятие позиции; после загрузки отправляет новую границу локации. Игрок ушёл/потерял видимость/сменил
пространство — `Disable` + `SetDelete`. Смена собственного пространства, загрузка, главное меню,
`showFireflies=false` и недоступность 3D удаляют все ссылки; пересоздание лениво.
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
TextField, без SWF, PrismaUI и отдельной зависимости. Показывается `displayName`
аккаунта; текст передаётся как обычный UTF-8, не HTML. Подпись — только текст без подложки,
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

На основном потоке используется та же интерполированная поза, что перемещает
светлячок. После проверки дистанции и проекции выполняется один `TES::Pick`
от камеры до опорной точки подписи с `COL_LAYER::kLineOfSight` и группой коллизии
игрока. Это проверка игровой коллизии, а не depth buffer: прозрачные и модовые
меши следуют своим collision-данным. Штатный `Actor::HasLineOfSight` здесь не
используется: у игрока он проверяет frustum и до трёх высот габаритов цели.

HUD меняется только внутри `HUDMenu::AdvanceMovie`, а не из сетевого потока.
Между основным потоком и HUD передаётся небольшой снимок строк и экранных координат
под mutex; движковые указатели и GFxValue через него не передаются. Это отдельная
граница UI, она не добавляет блокировок в ClientApplication. Проекция может
отставать от основного кадра на один UI-проход.

Начальные настройки `[client]` в `client.toml`:
```toml
showFireflyNames = true
fireflyNameOcclusion = true
fireflyNameFontSize = 18 # единицы HUD, диапазон 8..48
fireflyNameOffset = 35 # выше центра светлячка, игровые единицы 0..512
```
Дистанция ограничивается существующей `visibilityDistance`. Имена скрываются при
паузе и выключенном HUD. Настройка скрытия Prisma-чата не выключает имена.

Пока реализован **SE/AE HUD-рендерер**. DLL продолжает поддерживать VR, но имена
в VR намеренно отключены: проекция на плоский HUD не обеспечивает привязку
к точке мира для обоих глаз. Для VR нужен отдельный стереокорректный рендерер.

Проверка хука: slot 0x05 HUDMenu; SE 1.5.97 vtable ID 268816,
функция 0x14087EC70; AE 1.6.1170 ID 215362, функция 0x14091E760.
Адреса только для диагностики; код использует CommonLib relocation.
SE HasLineOfSight: ID 53029 / 0x14091C620, ветка игрока 0x1406A4A00;
TES::Pick ID 13221/13371. Проверено по исходникам CommonLib и IDA, не по имени enum.

Ручная проверка в игре остаётся обязательной: два клиента, кириллица в имени,
1-е/3-е лицо, стены/двери, перемещение камеры, ультраширокий экран,
`tm`, меню, загрузка сохранения, выход/повторный вход в локацию и дисконнект.

Настройки → «Имена над светлячками» содержит переключатели показа/LOS и ползунки
шрифта/высоты. Кнопка «Сохранить настройки» применяет их без перезапуска и сохраняет
в `ui.toml`, секция `[ui.chat]`. Сохранённые значения имеют приоритет над
`client.toml`; старый `ui.toml` без новых полей наследует их из `client.toml`.
