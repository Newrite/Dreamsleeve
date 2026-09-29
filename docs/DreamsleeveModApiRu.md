# API Dreamsleeve для других модов

Реализовано 29 сентября 2026 года. Сторонний мод может опубликовать **объявление** в
системном канале — его видят все игроки на сервере во вкладке «Объявления». Доступ
есть из C++ (другой SKSE-плагин) и из Papyrus. Модель доверия и правила системного
канала описаны в [DomainSpecRu.MD §4.8](DomainSpecRu.MD).

Объявления мода публикуются с источником `ThirdParty`. Строка `source` — самоназвание
мода: сервер хранит и показывает её как есть, но уровень доверия она не повышает.
Сервер может запретить объявления модов (`[Announcements.ThirdParty] Enabled = false`),
ограничивает их частоту по аккаунту игрока и проверяет текст словарём модерации.
Автор объявления — игрок, на чьём клиенте работает мод: его имя видно рядом с подписью,
и личный игнор этого игрока скрывает объявления его модов.

## C++

Устроено как API PrismaUI и TrueFlasksNG. Заголовок
`src/Dreamsleeve.Client/API/DreamsleeveAPI.h` (в dist — `Dreamsleeve/API/DreamsleeveAPI.h`)
копируется в проект мода и подключается после CommonLibSSE-NG (`SKSE::PluginHandle`).
Модули Dreamsleeve не нужны. `RequestPluginAPI` находит `Dreamsleeve.Client.dll`
(`GetModuleHandleW`) и вызывает экспортированную функцию `RequestPluginAPI`.

```cpp
#include <SKSE/SKSE.h>
#include "DreamsleeveAPI.h"

DreamsleeveAPI::IVDreamsleeve1* dreamsleeve = nullptr;

void OnSkseMessage(SKSE::MessagingInterface::Message* message)
{
  if (message->type != SKSE::MessagingInterface::kPostPostLoad) return;
  dreamsleeve = static_cast<DreamsleeveAPI::IVDreamsleeve1*>(DreamsleeveAPI::RequestPluginAPI(DreamsleeveAPI::InterfaceVersion::V1));
  if (!dreamsleeve) return;  // Dreamsleeve не установлен.
  dreamsleeve->AddAnnouncementResultCallback(SKSE::GetPluginHandle(), [](const DreamsleeveAPI::AnnouncementResult& result) {
    if (result.source != "MyMod") return;  // Итоги всех модов приходят всем подписчикам.
    if (result.result != DreamsleeveAPI::APIResult::Published) SKSE::log::warn("Dreamsleeve refused: {}", result.reason);
  });
}

void AnnounceDeath(std::string_view text)
{
  if (!dreamsleeve) return;
  const auto result = dreamsleeve->PostAnnouncement(text, DreamsleeveAPI::AnnouncementKind::Event, "MyMod");
  // Queued — только принято локально; итог придёт в callback.
}
```

### Версии интерфейса

Интерфейсы только дополняются: `IVDreamsleeve1` не меняет состав, порядок и сигнатуры
методов. Новые методы появятся в `IVDreamsleeve2 : IVDreamsleeve1` с новым значением
`InterfaceVersion::V2`; моды, запросившие `V1`, продолжают работать. Значения
`APIResult` могут добавляться, существующие номера не меняются. Интерфейс передаёт
`std::string_view`, `std::string` и `std::function`, как TrueFlasksNG: мод должен быть
собран тем же MSVC toolset (одна ABI стандартной библиотеки), что обычно для SKSE-плагинов.

| Метод `IVDreamsleeve1` | Смысл |
|---|---|
| `GetPluginVersion()` | версия DLL: `major << 24 \| minor << 16 \| patch << 4 \| build` |
| `IsConnected()` | есть готовая сессия с сервером |
| `PostAnnouncement(text, kind, source)` | поставить объявление в очередь; синхронный `APIResult` |
| `AddAnnouncementResultCallback(plugin, callback)` | один callback итогов на плагин; `AlreadyRegistered` при повторе |
| `RemoveAnnouncementResultCallback(plugin)` | снять callback; `NotRegistered`, если его нет |

Все методы можно вызывать с любого потока. `PostAnnouncement` копирует строки,
проверяет кодировку и кладёт запрос в очередь (32 места), если сессия готова;
игровой поток отправляет его в следующем кадре. Игровые и сетевые объекты вызов не
трогает. Callback вызывается на игровом потоке без удерживаемых блокировок, поэтому
внутри него можно регистрировать и снимать callbacks.

### Результаты

| `APIResult` | Когда | Смысл |
|---|---|---|
| `Queued` (0) | синхронно | принято локально, итог будет в callback |
| `Published` (1) | асинхронно | сервер опубликовал |
| `NotConnected` (2) | оба | нет готовой сессии или она закончилась до ответа |
| `Rejected` (3) | оба | синхронно: текст или подпись не UTF-8, пустые, подпись многострочная, вид не из `AnnouncementKind`; асинхронно: источник запрещён сервером, текст или подпись длиннее лимита, словарь, пробельный текст |
| `Busy` (4) | оба | очередь переполнена |
| `RateLimited` (5) | асинхронно | слишком часто или повтор; лимит серверный, по аккаунту игрока |
| `Failed` (6) | асинхронно | доставка неизвестна: сессия сменилась |

`AnnouncementResult` в callback содержит результат, подпись, текст и причину по-русски
(пустую при публикации). Итоги получают все зарегистрированные плагины; фильтруйте по
своей подписи. Синхронный отказ в callback не приходит: он виден по возвращённому
значению и строке лога.

## Papyrus

Скрипт `DreamsleeveClient` (`Scripts/DreamsleeveClient.pex`, исходник
`Scripts/Source/DreamsleeveClient.psc`) — `Hidden`, все функции `global`:

```papyrus
Scriptname MyModDeaths extends ReferenceAlias

Event OnInit()
    RegisterForModEvent("Dreamsleeve_AnnouncementResult", "OnDreamsleeveResult")
EndEvent

Event OnDeath(Actor akKiller)
    If DreamsleeveClient.IsConnected()
        DreamsleeveClient.PostAnnouncement("Довакин пал в бою", DreamsleeveClient.KIND_EVENT(), "MyMod")
    EndIf
EndEvent

Event OnDreamsleeveResult(string eventName, string strArg, float numArg, Form sender)
    If strArg == "MyMod" && numArg as int != DreamsleeveClient.RESULT_PUBLISHED()
        Debug.Trace("Dreamsleeve did not publish: " + numArg as int)
    EndIf
EndEvent
```

| Функция | Смысл |
|---|---|
| `bool PostAnnouncement(string asText, int aiKind, string asSource)` | `true` — поставлено в очередь (`Queued`); `false` — нет, причина в `DreamsleeveClient.log` |
| `bool IsConnected()` | есть готовая сессия |
| `int GetApiVersion()` | версия скриптового API, сейчас 1 |
| `KIND_ANNOUNCEMENT()` = 0, `KIND_EVENT()` = 1 | виды |
| `RESULT_PUBLISHED()` = 1 … `RESULT_FAILED()` = 6 | коды итога, те же, что `APIResult` |

Нативные функции принимают `std::string` и `std::int32_t` (Papyrus `string`/`int`) и
зарегистрированы без `callableFromTasklets`: виртуальная машина вызывает их в следующем
кадре. Итог приходит mod event `Dreamsleeve_AnnouncementResult`: `strArg` — подпись,
`numArg` — код `APIResult`. Строки Papyrus принимаются как UTF-8, как и все игровые
строки, которые читает клиент; строка, не являющаяся корректным UTF-8, отклоняется
(`PostAnnouncement` возвращает `false`).

## Лимиты и правила

Каждое правило проверяется в одном месте:

| Где | Что проверяет |
|---|---|
| Вход API (`ModApi`, синхронно) | текст и подпись — непустой корректный UTF-8, подпись одной строкой без управляющих символов; вид — `Announcement` или `Event`; есть готовая сессия; очередь (32) не полна |
| Core (перед отправкой) | источник разрешён политикой из приветствия; длина текста и подписи не больше лимитов из приветствия; 32 ожидающих ответа |
| Сервер | `ChatInput.AnnouncementText` = 500 скаляров Unicode, `ChatInput.AnnouncementSignature` = 64; правила текста (пробельный текст, управляющие символы); словарь `[block]` → `TEXT_NOT_ALLOWED`, `[flag]` — помеченные диапазоны; `[Announcements.ThirdParty] Enabled`, иначе `ANNOUNCEMENT_NOT_ALLOWED`; `[Announcements] RateBurst = 3`, `RateRefillMs = 20000`, `DuplicateWindowMs = 300000` на аккаунт |

Текст — обычный UTF-8, а не HTML: интерфейс показывает его текстовым узлом, над
светлячками объявления не показываются. Подпись в интерфейсе обрезается до 64 символов.

## Где видно отказ

- **Лог клиента** `Documents/My Games/Skyrim Special Edition/SKSE/DreamsleeveClient.log`:
  `Announcement from MyMod queued`, `... refused locally: not connected`,
  `... published`, `... not published: rejected (Сервер не принимает объявления от этого источника)`.
  Подпись пишется в лог как значение (одна строка, обрезанная); подпись, не прошедшая
  проверку на входе, в лог не пишется.
- **Чат**: отказ после постановки в очередь показывается строкой «Не отправлено:
  причина» во вкладке «Объявления» с подписью мода; «Повторить» у таких строк нет, «×»
  убирает строку, в пассивном режиме она исчезает через 15 с.
- **Лог сервера**: объявления клиентов и отказы по ним отдельно не пишутся; серверные
  объявления — строкой `Server announcement (Kind): text`.

## Не проверено

Сборка DLL и `.pex`, логика проверок и корреляции (doctest), сервер (Expecto) и
`smoke_chat.py` с настоящим сервером проверены автоматически. В Skyrim с настоящими
модами не проверялись: получение интерфейса через `RequestPluginAPI`, callbacks итогов,
mod event в Papyrus, кодировка строк Papyrus с кириллицей.
