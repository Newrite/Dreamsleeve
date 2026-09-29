# API Dreamsleeve для других модов

Реализовано 29 сентября 2026 года. Сторонний мод может опубликовать **объявление** —
сообщение системного потока, которое видят все игроки на сервере во вкладке
«Объявления». Доступ есть из C++ (другой SKSE-плагин) и из Papyrus. Модель доверия и
поведение потока описаны в [DomainSpecRu.MD §4.8](DomainSpecRu.MD).

Объявления мода публикуются с источником `ThirdParty`. Строка `source` — самоназвание
мода: сервер хранит и показывает её как есть, но уровень доверия она не повышает.
Сервер может запретить объявления модов (`[Announcements.ThirdParty] Enabled = false`),
ограничивает их частоту по аккаунту игрока и проверяет текст словарём модерации.
Автор объявления — игрок, на чьём клиенте работает мод: его имя видно рядом с подписью,
и личный игнор этого игрока скрывает объявления его модов.

## C++

Заголовок `src/Dreamsleeve.Client/API/DreamsleeveAPI.h` (в dist:
`Dreamsleeve/API/DreamsleeveAPI.h`) копируется в проект мода. Ему нужен только
`SKSE::MessagingInterface` из CommonLibSSE-NG: подключайте его после `<SKSE/SKSE.h>`.
Модули и заголовки Dreamsleeve не нужны.

```cpp
#include <SKSE/SKSE.h>
#include "DreamsleeveAPI.h"

DreamsleeveAPI::IVDreamsleeve1* dreamsleeve = nullptr;

void OnDreamsleeveMessage(SKSE::MessagingInterface::Message* message)
{
  if (message->type != DreamsleeveAPI::kMessage_AnnouncementResult) return;
  const auto* result = static_cast<const DreamsleeveAPI::AnnouncementResultMessage*>(message->data);
  if (std::string_view{result->source} != "MyMod") return;  // Результаты всех модов приходят всем.
  if (result->result != DreamsleeveAPI::APIResult::Published)
    SKSE::log::warn("Dreamsleeve refused: {}", result->reason);  // Строки живут только внутри вызова.
}

void OnSkseMessage(SKSE::MessagingInterface::Message* message)
{
  if (message->type == SKSE::MessagingInterface::kPostPostLoad)
  {
    dreamsleeve = DreamsleeveAPI::RequestPluginAPI(DreamsleeveAPI::InterfaceVersion::V1);
    SKSE::GetMessagingInterface()->RegisterListener(DreamsleeveAPI::PluginName, OnDreamsleeveMessage);
  }
}

void AnnounceDeath(const char* utf8Text)
{
  if (!dreamsleeve) return;  // Dreamsleeve не установлен.
  const auto result = dreamsleeve->PostAnnouncement(utf8Text, DreamsleeveAPI::AnnouncementKind::Event, "MyMod");
  // Queued — только принято локально; итог придёт в kMessage_AnnouncementResult.
}
```

### Получение интерфейса

`RequestPluginAPI` посылает через SKSE messaging сообщение `kMessage_RequestInterface`
плагину `DreamsleeveClient`; Dreamsleeve синхронно, внутри `Dispatch`, заполняет в
структуре указатель на функцию выдачи интерфейса. Запрашивать можно с `kPostLoad`,
надёжнее на `kPostPostLoad`. `nullptr` — Dreamsleeve не установлен или не знает
запрошенную версию (в логе SKSE тогда ожидаемая строка `Failed to dispatch message`).
Указатель на интерфейс действителен до конца процесса.

Так же устроены PrismaUI и TrueFlasksNG с той разницей, что они экспортируют функцию
`RequestPluginAPI` из DLL; Dreamsleeve отвечает через SKSE messaging, чтобы заголовку
не требовались `<Windows.h>` и имя файла DLL.

### Версии интерфейса

Интерфейсы только дополняются: `IVDreamsleeve1` не меняет состав, порядок и сигнатуры
методов. Новые методы появятся в `IVDreamsleeve2 : IVDreamsleeve1` с новым значением
`InterfaceVersion::V2`; старые моды продолжают запрашивать `V1`. Значения `APIResult`
могут добавляться, существующие номера не меняются.

| Метод `IVDreamsleeve1` | Смысл |
|---|---|
| `GetInterfaceVersion()` | `InterfaceVersion::V1` |
| `GetPluginVersion()` | версия DLL: `major << 24 \| minor << 16 \| patch << 4 \| build` |
| `IsConnected()` | есть готовая сессия с сервером |
| `PostAnnouncement(text, kind, source)` | поставить объявление в очередь; синхронный `APIResult` |

Все методы можно вызывать с любого потока. Вызов копирует строки, проверяет их по
последнему состоянию сессии и кладёт запрос в очередь (32 места), игровой поток
отправляет его в следующем кадре. Сетевые и игровые объекты вызов не трогает.

### Результаты

| `APIResult` | Когда | Смысл |
|---|---|---|
| `Queued` (0) | синхронно | принято локально, итог будет асинхронно |
| `Published` (1) | асинхронно | сервер опубликовал |
| `NotConnected` (2) | оба | нет готовой сессии или она закончилась до ответа |
| `Rejected` (3) | оба | сервер не принимает объявления модов (известно из приветствия сессии или ответом сервера), словарь, некорректный запрос |
| `TooLong` (4) | оба | текст длиннее лимита сервера |
| `InvalidText` (5) | синхронно | `nullptr`, пустой или пробельный текст, не UTF-8, управляющие символы кроме табуляции и перевода строки |
| `InvalidSource` (6) | синхронно | `nullptr`, пустая, пробельная, многострочная или слишком длинная подпись |
| `InvalidKind` (7) | синхронно | вид не из `AnnouncementKind` |
| `Busy` (8) | оба | очередь переполнена |
| `Unsupported` (9) | оба | сервер старше объявлений клиентов |
| `RateLimited` (10) | асинхронно | слишком часто или повтор; лимит серверный, по аккаунту игрока |
| `Failed` (11) | асинхронно | доставка неизвестна: сессия сменилась |

Асинхронный итог каждого объявления, получившего `Queued`, рассылается на игровом
потоке всем плагинам, подписанным на `DreamsleeveClient`
(`RegisterListener(DreamsleeveAPI::PluginName, ...)`), сообщением
`kMessage_AnnouncementResult` со структурой `AnnouncementResultMessage`: результат,
подпись, текст и причина по-русски (пустая при публикации). Строки действительны только
во время вызова слушателя. Результаты получают все подписчики; фильтруйте по своей подписи.

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
| `RESULT_PUBLISHED()` = 1 … `RESULT_FAILED()` = 11 | коды итога, те же, что `APIResult` |

Итог приходит mod event `Dreamsleeve_AnnouncementResult`: `strArg` — подпись, `numArg` —
код `APIResult`. Строки Papyrus — байты в кодировке игры: если строка не является
корректным UTF-8, Dreamsleeve считает её текстом кодовой страницы ANSI Windows
(CP1251 на русской системе) и перекодирует. Нативные функции зарегистрированы без
`callableFromTasklets`: виртуальная машина вызывает их в следующем кадре.

## Лимиты и правила

| Что | Локально (до очереди) | На сервере |
|---|---|---|
| Текст | UTF-8, не пустой и не пробельный, без управляющих символов кроме `\t \n \r`, до 2000 символов; после входа — до лимита из приветствия | `ChatInput.AnnouncementText` = 500 скаляров Unicode, словарь `[block]` → `TEXT_NOT_ALLOWED`, `[flag]` — помеченные диапазоны |
| `source` | UTF-8, одна строка без управляющих символов, не пустая, до 128; после входа — до лимита из приветствия | `ChatInput.AnnouncementSignature` = 64, словарь `[block]` |
| Вид | `Announcement`, `Event` | `Admin`, `Periodic` → `INVALID_REQUEST` |
| Источник | разрешён ли `ThirdParty` по приветствию сессии | `[Announcements.ThirdParty] Enabled`, иначе `ANNOUNCEMENT_NOT_ALLOWED` |
| Частота | очередь 32 запроса, 32 ожидающих ответа | `[Announcements] RateBurst = 3`, `RateRefillMs = 20000`, `DuplicateWindowMs = 300000` на аккаунт |

Текст — обычный UTF-8, а не HTML: интерфейс показывает его текстовым узлом, над
светлячками объявления не показываются. Подпись в интерфейсе очищается от управляющих
символов и обрезается до 64 символов.

## Где видно отказ

- **Лог клиента** `Documents/My Games/Skyrim Special Edition/SKSE/DreamsleeveClient.log`:
  `Announcement from MyMod queued`, `... refused locally: not connected`,
  `... published`, `... not published: rejected (Сервер не принимает объявления от этого источника)`.
  Подпись пишется в лог как значение, обрезанной и без управляющих символов.
- **Чат**: отказ сервера или клиента после постановки в очередь показывается строкой
  «Не отправлено: причина» во вкладке «Объявления» с подписью мода; «Повторить» у таких
  строк нет, «×» убирает строку, в пассивном режиме она исчезает через 15 с.
- **Лог сервера**: объявления клиентов и отказы по ним отдельно не пишутся; серверные
  объявления — строкой `Server announcement (Kind): text`.

## Не проверено

Сборка DLL и `.pex`, логика проверок и корреляции (doctest), сервер (Expecto) и
`smoke_chat.py` с настоящим сервером проверены автоматически. В Skyrim с настоящими
модами не проверялись: получение интерфейса через SKSE messaging, рассылка
`kMessage_AnnouncementResult`, mod event в Papyrus, перекодировка строк Papyrus.
