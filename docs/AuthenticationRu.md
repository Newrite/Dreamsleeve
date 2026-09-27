# Авторизация, профили и серверные зависимости

Реализован вход по локальной учётной записи. Register создаёт account и profile
в одной SQLite-транзакции; login проверяет пароль и выдаёт одноразовый билет.
PlayerSession погашает билет и получает профиль, затем резервирует PlayerId и
собирает начальные снимки. Имя клиента больше не является доказательством личности.
Протокол ENet версии 2 несовместим со старым входом по имени.

## Запуск и восстановление

```powershell
dotnet run --project src/Dreamsleeve.Server -c Release -- --write-config server.json
dotnet run --project src/Dreamsleeve.Server -c Release -- --config server.json
xmake run Dreamsleeve.Client.Dev --connect 127.0.0.1 8778 player --register "Player Name"
# Следующие входы, в том числе после перезапуска сервера:
xmake run Dreamsleeve.Client.Dev --connect 127.0.0.1 8778 player
```

Клиент запрашивает пароль скрыто. Для автоматизации доступна переменная окружения
`DREAMSLEEVE_PASSWORD`; пароль не передаётся аргументом командной строки.
`--auth-url https://host:port` выбирает адрес входа. Команда `connect` получает
новый билет; повторно использовать прежний нельзя.

По умолчанию ENet слушает 127.0.0.1:8778, HTTP auth — 127.0.0.1:8779.
База `data/dreamsleeve.db` и логи `logs/server-.json` задаются относительно
рабочего каталога процесса. При повторном запуске нужен тот же DatabasePath.
Восстанавливаются PlayerId, Username, DisplayName и хеш пароля. Онлайн,
история чата и билеты не сохраняются. DisplayName при login не перезаписывается.
Аккаунт и профиль имеют отдельные ID; миграция уже позволяет развивать их отдельно.

Конфигурация JSON читается до запуска listeners, неизвестные поля отклоняются.
Секции: Server, Runtime, Database, Authentication, Logging. Миграции из `db/migrations`
копируются в выходной каталог и применяются до допуска клиентов. Неизвестная версия,
чужой application_id или повреждённая схема останавливают startup.
[Схема и генерация SqlHydra](../db/README.md).

## HTTP-контракт и ограничения

| Маршрут | JSON | Успех |
|---|---|---|
| POST /auth/register | username, displayName, password | 201: playerId, username, displayName |
| POST /auth/login | username, password | 200: sessionTicket, expiresInSeconds, playerId, username, displayName |

Ошибка возвращает `{code,message}`. Неверный пароль или отсутствующий аккаунт —
одинаковый 401, занятый Username — 409, перегрузка — 503, rate limit — 429.
Регистрацию можно выключить `Authentication.AllowRegistration=false`.
Username нормализуется доменной фабрикой, DisplayName — Trim/NFC.
Пароль не обрезается и не нормализуется: 12..128 UTF-8 байт.

PasswordHasher из ASP.NET Identity хранит соль, параметры и хеш; успешный вход
может обновить устаревший хеш через compare-and-swap. Значение PasswordIterations
по умолчанию 210000, настраивается. Неизвестный аккаунт тоже выполняет проверку
фиктивного хеша. Ответ и логи не содержат пароль, hash или билет.

AuthService владеет словарём хешей билетов и pending-операциями. Билет — 32 случайных
байта в base64url, по умолчанию живёт 60 секунд, атомарно погашается один раз.
Истечение измеряется монотонными часами. MaxTickets и MaxConcurrentOperations
ограничивают память и CPU; HTTP body ограничен 4096 байтами, ожидание и частота
запросов ограничены конфигурацией. SQLite и password hashing выполняются в
отслеживаемых bounded workers; ответы возвращаются сообщениями агенту.

Отмена HTTP-ожидания не откатывает принятую регистрацию. После timeout можно
попробовать login; если регистрация завершилась, профиль уже сохранён. При shutdown
агент дожидается принятых операций, включая ещё ожидающие отправки worker-у.
SQLite имеет WAL, foreign keys, конечный busy timeout; каждая операция владеет
своим соединением. Одновременная запись ограничена самим SQLite.

## Транспорт и область защиты

HTTP без TLS разрешён только при явном AllowInsecureLoopback и буквальном
loopback-адресе; это локальный dev-режим. Для удалённого входа нужен HTTPS:
Authentication.ListenUrl, CertificatePath и при необходимости пароль сертификата
в `DREAMSLEEVE_AUTH_CERTIFICATE_PASSWORD`. Без CertificatePath используются
стандартные настройки сертификата Kestrel. Клиент проверяет сертификат и не
следует redirect при передаче credentials.

Билет не делает ENet зашифрованным транспортом: перехват игровых UDP-пакетов
остаётся вне этой реализации. Для открытой сети потребуется отдельное решение
защиты игрового канала. Нет refresh/resume, восстановления пароля, MFA,
подтверждения почты или внешнего identity provider.

## Логирование

Core зависит от ILogger abstraction; composition root подключает Serilog.
Есть структурированные lifecycle/error-события с ConnectionId, PlayerId и причиной,
вывод в консоль и JSON-файлы с ротацией по дню/размеру и ограничением числа файлов.
Минимальный уровень, sinks и retention задаёт Logging. Payload auth и содержимое
пакетов не логируются. Framework HTTP logging ограничен Warning.
Console/File sinks синхронны; неблокирующая очередь логирования пока не добавлена.
При медленном диске запись события может задержать вызывающего владельца.

## Проверка NuGet-стека

Подключены только используемые зависимости:

| Пакет | Версия | Назначение |
|---|---|---|
| Microsoft.Data.Sqlite | 10.0.12 | SQLite provider |
| SqlHydra.Query / SqlHydra.Cli | 5.0.0 | Typed queries и воспроизводимая генерация schema |
| Migrondi.Core | 1.3.0 | SQL migrations до запуска listeners |
| Microsoft.Extensions.Identity.Core | 10.0.12 | Стандартный PasswordHasher |
| Microsoft.Extensions.Logging.Abstractions | 10.0.12 | Logging port в Core |
| Serilog | 4.4.0 | Structured logging |
| Serilog.Extensions.Hosting | 10.0.0 | Интеграция ILogger/host |
| Serilog.Sinks.Console / File | 6.1.1 / 7.0.0 | Консоль и rotating JSON files |

ASP.NET Core входит в shared framework через Web SDK. Для двух JSON-маршрутов
используются встроенные routing, Kestrel и rate limiting. yENet, protobuf, UMX,
Expecto и Faqt сохраняют текущие версии; массового обновления несвязанных пакетов нет.

- Fling отложен до сложных связанных агрегатов; две строки регистрации не требуют ORM-слоя.
- Falco/Markup/Htmx отложены до реальной SSR-админки.
- FSharp.Logf не нужен для текущих именованных структурированных ILogger-шаблонов.
- Serilog.Settings.Configuration не добавлен: logger уже настраивается валидируемой типизированной секцией JSON.
- Tomlyn не добавлен: переход JSON → TOML не нужен для авторизации.
- FsToolkit.ErrorHandling приходит транзитивно через Migrondi; прямой зависимости без использования нет.

Migrondi также приносит другие DB providers транзитивно. Это цена выбранного runner,
а не реализация PostgreSQL/MySQL в приложении. SqlHydra 5 больше не использует
SqlKata; ранние handbook-примеры требуют адаптации, актуален SqliteAccountStore.

Основания выбора: [SQLite async methods](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/async)
выполняются синхронно; [Microsoft password hashing guidance](https://learn.microsoft.com/en-us/aspnet/core/security/data-protection/consumer-apis/password-hashing?view=aspnetcore-10.0)
рекомендует PasswordHasher для новых приложений. Версии и API сверены с
[SqlHydra releases](https://github.com/JordanMarr/SqlHydra/releases),
[Migrondi library](https://github.com/AngelMunoz/Migrondi/blob/vnext/docs/library.md)
и фактическим NuGet restore.

27.09.2026 выполнен `dotnet list src/Dreamsleeve.Server/Dreamsleeve.Server.fsproj
package --vulnerable --include-transitive --no-restore --format json` с доступом
к api.nuget.org: известных уязвимых прямых или транзитивных пакетов не обнаружено.
Это результат advisory-проверки на эту дату, а не гарантия отсутствия дефектов.

## Проверка реализации

Общая Release-сборка и managed-набор: 218/218, без предупреждений. Native-набор:
168/168 (2702 assertions). `Scripts/smoke_chat.py` проверяет настоящие процессы
сервера и двух Client.Dev: регистрацию, свежие билеты, двусторонний чат,
повторный вход, сохранение PlayerId после полного перезапуска сервера и чистую
остановку. Отдельно проверяется валидность JSON-логов и отсутствие тестового
пароля/билета в stdout и файлах. Временная БД изолирована от рабочей.

Короткий сетевой runner с двумя клиентами: ready=2, sent=2, received=expected=4,
errors/rejections/unexpected disconnects=0. Это проверка нового auth-пути,
не повтор прежних нагрузочных измерений; исторические benchmark-отчёты сохранены.
