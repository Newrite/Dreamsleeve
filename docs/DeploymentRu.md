# Развёртывание сервера Dreamsleeve

Инструкция для владельца сервера: что нужно, как собрать и запустить сервер, как пустить
игроков через nginx с TLS, какие параметры менять и что раздать игрокам. Все ключи
конфигурации с допустимыми значениями описаны в комментариях
[server.example.toml](../src/Dreamsleeve.Server/server.example.toml) и
[client.example.toml](../src/Dreamsleeve.Client.Core/client.example.toml).

В примерах: сервер с публичным адресом `203.0.113.10`, домены `auth.example.org` (вход игроков)
и `admin.example.org` (веб-админка). Замените их своими.

## Схема

```text
Игрок (Skyrim + Dreamsleeve.Client.dll)
  ├── HTTPS 443 ──► nginx (TLS) ──► 127.0.0.1:8779   вход, регистрация, сохранённый вход (/auth/*)
  └── UDP 8778 ─────────────────►  0.0.0.0:8778     игровое соединение ENet: чат, онлайн, движение, метки
Администратор
  └── HTTPS 443 ──► nginx (TLS) ──► 127.0.0.1:8780   веб-админка (или SSH-туннель без nginx)
```

Один процесс `Dreamsleeve.Server` обслуживает все три порта:

| Порт | Протокол | Кто подключается | Наружу |
|---|---|---|---|
| 8778 | UDP (ENet) | игровой клиент, напрямую | открыть в firewall |
| 8779 | HTTP | nginx; клиент ходит на `https://auth.example.org` | только loopback |
| 8780 | HTTP | nginx или SSH-туннель администратора | только loopback |

Почему так:

- **UDP не проксируется через nginx.** ENet — свой протокол поверх UDP; сервер различает клиентов
  по адресу и порту, а лимиты считает по аккаунту. Порт 8778 открывается напрямую.
- **ENet не шифруется.** Пароль по UDP не передаётся: клиент входит по HTTPS и получает
  одноразовый билет на 60 с, которым открывает игровую сессию. Чат и положения идут по UDP
  открытым текстом.
- **HTTP сервера без TLS остаётся на loopback.** TLS завершает nginx, сертификат обновляется без
  перезапуска игрового сервера. Прямой HTTPS в самом сервере тоже возможен (см. «Свой
  сертификат»), но PFX-файл читается только при запуске.

## Что понадобится

- **Машина** с Linux x64 (примеры для Ubuntu 24.04 и systemd) или Windows x64. ENet в сервере
  управляемый (yENet), SQLite поставляется в публикации. На Linux проверено на Ubuntu 24.04
  (x86_64): все серверные тесты, публикация обоих видов, вход по HTTP, игровые ENet-сессии с чатом
  и онлайном, служба systemd с FIFO и остановка по SIGTERM. Что поставить на хост — в
  «Требования к Linux-хосту» ниже.
- **Публичный IPv4.** В `client.toml` игрока `serverHost` — IPv4-адрес или DNS-имя с A-записью
  (`play.example.org`). Имя клиент разрешает заново при каждой попытке подключения, поэтому при
  смене адреса достаточно обновить запись (с учётом её TTL); с IPv4-литералом игрокам придётся
  поменять конфиг. IPv6 (AAAA) клиент не использует.
- **Домен** с A-записью на этот адрес для `auth.example.org` (и `admin.example.org`, если админка
  будет за nginx). Нужен для сертификата, которому доверяет Windows у игроков. Для игрового
  адреса подойдёт то же имя или отдельное, например `play.example.org`.
- **nginx** и **certbot** (Let's Encrypt) — или другой сертификат от доверенного центра.
- **Открытые порты:** UDP 8778; TCP 80 (выпуск сертификата и редирект) и 443. Порты 8779 и 8780 наружу
  не открывать.
- **Ресурсы:** на десятки игроков хватит 1 vCPU и 1 ГБ памяти. Ориентиры для сотен и тысячи
  клиентов — в разделе «Ёмкость».

## Требования к Linux-хосту

Сервер бывает в двух видах публикации, обе работают на одной и той же машине:

| Вид | Чем запускать | Что нужно на хосте |
|---|---|---|
| Framework-dependent, переносимая (`dist/Server`) | `dotnet Dreamsleeve.Server.dll` | ASP.NET Core Runtime 10 |
| Самодостаточная `linux-x64` | `./Dreamsleeve.Server` | только системные библиотеки ниже |

- **ASP.NET Core Runtime 10.** В Ubuntu 24.04 он есть в обычном репозитории:
  `sudo apt install aspnetcore-runtime-10.0`; пакет сам ставит ICU и OpenSSL. Для других
  дистрибутивов — [инструкция Microsoft](https://learn.microsoft.com/dotnet/core/install/linux).
- **Системные библиотеки** (самодостаточной сборке их надо поставить самому): glibc, libstdc++ и
  libgcc; **ICU** (`libicu74` в Ubuntu 24.04) — сервер нормализует тексты (NFC, NFKC в словаре), и
  invariant-режим .NET ему не подходит; **OpenSSL 3** (`libssl3t64`) — на нём в .NET на Linux
  работают хеширование паролей (PBKDF2) и TLS; `ca-certificates` и `tzdata` — по желанию.
- **Архитектура и libc:** проверена x86_64 с glibc. Под ARM64 — публикация `linux-arm64`
  (собирается, около 145 МБ; на ARM не запускалась). Alpine (musl) — `linux-musl-x64` плюс пакет
  `icu-libs`.
- **Каталог данных** — на локальной файловой системе: SQLite в режиме WAL не работает надёжно на
  NFS, SMB и дисках Windows, подключённых в WSL (`/mnt/c`).

## 1. Сборка публикации

На машине разработчика (Windows, .NET SDK 10):

```powershell
python Scripts/package_dist.py            # клиент + сервер в dist/Client и dist/Server
dotnet publish src/Dreamsleeve.Server -c Release -o dist/Server   # только сервер
```

`package_dist.py` собирает и клиентский мод (нужны xmake, MSVC, npm), кладёт рядом с сервером
`server.example.toml`, `moderation.toml`, `pseudonyms.toml` и README. При публикации одной командой
`dotnet publish` скопируйте эти файлы из `src/Dreamsleeve.Server` сами:
`moderation.example.toml` → `moderation.toml`, `pseudonyms.example.toml` → `pseudonyms.toml`.

Содержимое `dist/Server`: `Dreamsleeve.Server.dll` и зависимости, `db/migrations/*.sql` (применяются
при запуске, путь — рядом с DLL), примеры конфигов. `server.toml`, база и логи появляются у вас.
Эта публикация переносимая: на Linux её запускают через `dotnet Dreamsleeve.Server.dll`
(`Dreamsleeve.Server.exe` — только для Windows).

Самодостаточная сборка под Linux собирается на любой ОС, в том числе на Windows:

```bash
dotnet publish src/Dreamsleeve.Server -c Release -r linux-x64 --self-contained true -o build/linux-x64
```

Около 136 МБ, внутри весь runtime .NET; запускается `./Dreamsleeve.Server`, `db/migrations` лежит
рядом. С `--self-contained false` выходит 31 МБ с тем же запускателем, но на хосте нужен ASP.NET
Core Runtime. Обе публикации проверены и при сборке на Linux: SDK из репозитория Ubuntu
(`dotnet-sdk-10.0`) собирает проект и тянет пакеты под `linux-x64` с nuget.org. Версия
`FSharp.Core` закреплена в `Directory.Build.props`, поэтому SDK любой полосы даёт одну и ту же
сборку. Сервер пишет свою версию первой строкой лога и в `--help`.

## 2. Установка на Linux

Для переносимой публикации — ASP.NET Core Runtime 10 (`sudo apt install aspnetcore-runtime-10.0` в
Ubuntu 24.04; проверка: `dotnet --list-runtimes` показывает `Microsoft.AspNetCore.App 10.x`). Для
самодостаточной — `sudo apt install libicu74 libssl3t64 ca-certificates`, а в юните службы ниже
`ExecStart=/opt/dreamsleeve/Dreamsleeve.Server --config /opt/dreamsleeve/server.toml`.

```bash
sudo useradd --system --home-dir /opt/dreamsleeve --shell /usr/sbin/nologin dreamsleeve
sudo mkdir -p /opt/dreamsleeve
sudo cp -r dist/Server/* /opt/dreamsleeve/          # скопировать публикацию на сервер (scp/rsync)
sudo cp /opt/dreamsleeve/server.example.toml /opt/dreamsleeve/server.toml
sudo mkdir -p /opt/dreamsleeve/data /opt/dreamsleeve/logs
sudo chown -R dreamsleeve:dreamsleeve /opt/dreamsleeve
sudo chmod 750 /opt/dreamsleeve/data                 # база: хеши паролей и токенов
```

## 3. server.toml

Файл читается только при запуске. Отсутствующие ключи берут значения по умолчанию, поэтому
`server.toml` может содержать только то, что вы меняете. Минимум для публичного сервера за nginx:

```toml
[Server]
ServerName = "Мой сервер Dreamsleeve"   # видят игроки
BindAddress = "0.0.0.0"                  # UDP на всех интерфейсах (или публичный IPv4)
Port = 8778
PeerLimit = 72                           # >= MaxSessions, с запасом

[Runtime]
MaxSessions = 64                         # одновременных клиентов, включая не вошедших
ControlReserve = 260                     # не меньше 4 * MaxSessions + 4

[Authentication]
AllowRegistration = true                 # false — только существующие аккаунты

[Authentication.Listener]
ListenUrl = "http://127.0.0.1:8779"      # только loopback: снаружи его закрывает nginx
TrustForwardedHeaders = true             # адрес игрока берётся из X-Forwarded-For от nginx

[Admin.Listener]
ListenUrl = "http://127.0.0.1:8780"
TrustForwardedHeaders = true             # только если админка за nginx; для SSH-туннеля — false
```

Обязательные связи между ключами (сервер не стартует и называет ключ, если они нарушены):

| Правило | Зачем |
|---|---|
| `Runtime.MaxSessions` ≤ `Server.PeerLimit` ≤ 4095 | каждому клиенту нужен слот ENet |
| `Runtime.ControlReserve` ≥ 4 × `MaxSessions` + 4 | место для подтверждений очистки сессий |
| `Server.MaxInitialPlayers` ≥ `MaxSessions` | начальный снимок вмещает всех онлайн |
| `Server.MaxRecentMessages` ≥ `Runtime.Chat.HistoryCapacity` и `Announcements.HistoryCapacity` | история влезает в снимок |
| `Admin.Listener` и `Authentication.Listener` на разных портах | два отдельных HTTP-хоста |
| HTTP (`http://`) не на loopback — только с `AllowInsecureRemote = true` | пароли не уходят в сеть без TLS |

`MaxSessions` считает **все** запущенные клиенты: не вошедший игрок держит гостевое соединение.
Лишнее подключение сервер сразу закрывает (в логе — `the server is full`).

**`TrustForwardedHeaders = true` за nginx обязателен.** Без него все запросы приходят с
`127.0.0.1`: лимит `RequestsPerMinute` (120 в минуту) становится общим на всех игроков, а у админки
не совпадает схема (`http` вместо `https`), и она отклоняет изменения (403) и не ставит `Secure` у
cookie. Заголовки принимаются только от `127.0.0.1`/`::1`, поэтому nginx должен быть на той же
машине. Без прокси (SSH-туннель, локальный сервер) оставляйте `false`.

Восстановление после сбоев — `[Recovery]`: если игровая часть (ENet, runtime, писатель меток)
падает, сервер перезапускает её сам, а HTTP входа, аккаунты, админка и база продолжают работать;
клиенты переподключаются сами. По умолчанию задержка 1 с, удваивается до 30 с, больше 5 отказов
за 10 минут — сервер останавливается с кодом 1, и его поднимает systemd (`Restart=on-failure`).
Сбой самого первого запуска (занятый порт, сломанная база) останавливает сервер сразу.

Остальное по желанию: антиспам `[Runtime.Chat.Rate]`, словарь `[Moderation]`, скрытое имя и смена
имени `[Identity]`, объявления `[Announcements]` и расписание `[[Announcements.Scheduled]]`, метки
`[GroundMarks]`, логи `[Logging]` — всё с пояснениями в `server.example.toml`. Словарь
(`moderation.toml`) описан в `moderation.example.toml`, псевдонимы (`pseudonyms.toml`) —
в `pseudonyms.example.toml`.

## 4. Первый запуск вручную

```bash
cd /opt/dreamsleeve
sudo -u dreamsleeve dotnet Dreamsleeve.Server.dll --config server.toml
```

Ожидаемо: миграции SQLite применены, строки `Listening on 0.0.0.0:8778. Authentication:
http://127.0.0.1:8779`, `Admin panel: http://127.0.0.1:8780` и, пока нет администраторов,
`Admin panel setup code (one-time, 15 min; open /setup of the panel): ...`. Остановка — `quit`
или Ctrl+C. Ошибка конфигурации печатается одной строкой с именем ключа, сервер не стартует.

## 5. Служба systemd

**Консоль сервера должна оставаться открытой.** Сервер читает команды из stdin и завершается,
когда stdin закрыт (EOF равен `quit`). У службы systemd stdin по умолчанию `/dev/null`: сервер
остановится сразу после старта. Поэтому stdin подключается к FIFO, который держит открытым сам
systemd; туда же администратор пишет консольные команды.

`/etc/systemd/system/dreamsleeve.socket`:

```ini
[Unit]
Description=Dreamsleeve server console
PartOf=dreamsleeve.service

[Socket]
ListenFIFO=/run/dreamsleeve.stdin
SocketMode=0600
RemoveOnStop=true
```

`/etc/systemd/system/dreamsleeve.service`:

```ini
[Unit]
Description=Dreamsleeve server
Wants=network-online.target
After=network-online.target dreamsleeve.socket
Requires=dreamsleeve.socket

[Service]
User=dreamsleeve
Group=dreamsleeve
WorkingDirectory=/opt/dreamsleeve
ExecStart=/usr/bin/dotnet /opt/dreamsleeve/Dreamsleeve.Server.dll --config /opt/dreamsleeve/server.toml
Sockets=dreamsleeve.socket
StandardInput=socket
# Без явного journal вывод ушёл бы в тот же FIFO.
StandardOutput=journal
StandardError=journal
Restart=on-failure
RestartSec=5
NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=full
ProtectHome=true

[Install]
WantedBy=multi-user.target
```

Если сертификат для прямого HTTPS (не через nginx) защищён паролем, добавьте в `[Service]`
`Environment=DREAMSLEEVE_AUTH_CERTIFICATE_PASSWORD=...` или `EnvironmentFile=` с правами 600.

```bash
sudo systemctl daemon-reload
sudo systemctl enable --now dreamsleeve.socket dreamsleeve.service
journalctl -u dreamsleeve -f                                     # лог и ответы консоли
echo "announce Перезапуск через 5 минут" | sudo tee /run/dreamsleeve.stdin
journalctl -u dreamsleeve | grep "setup code"                     # код первичной настройки админки
sudo systemctl stop dreamsleeve                                   # SIGTERM: штатная остановка
```

Схема проверена с настоящим сервером на systemd 255 (Ubuntu 24.04): команда через FIFO доходит
(`announce` публикуется), закрытие пишущей стороны не даёт EOF, `systemctl stop` присылает SIGTERM,
и сервер, как от Ctrl+C, закрывает HTTP, завершает сессии и выходит с кодом 0. Без FIFO служба
останавливается сразу после старта.

Команды консоли: `quit`, `announce <текст>`, `reset-password <username>` (одноразовый код сброса
для игрока), `revoke-access <username>` (отзыв сохранённых входов и билетов), `admin-setup`
(новый код настройки, пока нет администраторов), `admin-reset <имя>` (сброс пароля администратора).
Коды печатаются только в консоль (в journal), в JSON-лог не попадают.

## 6. nginx и сертификат Let's Encrypt

### 6.1. Выпуск сертификата

Сначала nginx только на порту 80: блок с `listen 443` ссылается на файлы сертификата, которых ещё нет.

```bash
sudo apt install nginx certbot
sudo mkdir -p /var/www/letsencrypt
```

`/etc/nginx/sites-available/dreamsleeve`:

```nginx
server {
    listen 80;
    listen [::]:80;
    server_name auth.example.org admin.example.org;

    location /.well-known/acme-challenge/ { root /var/www/letsencrypt; }
    location / { return 301 https://$host$request_uri; }
}
```

```bash
sudo ln -s /etc/nginx/sites-available/dreamsleeve /etc/nginx/sites-enabled/
sudo nginx -t && sudo systemctl reload nginx
sudo certbot certonly --webroot -w /var/www/letsencrypt \
    -d auth.example.org -d admin.example.org \
    --deploy-hook "systemctl reload nginx"
```

Certbot ставит таймер продления; `--deploy-hook` перезагружает nginx после каждого продления.
Игровой сервер при этом не перезапускается.

### 6.2. Вход игроков: auth.example.org

Добавьте в тот же файл:

```nginx
server {
    listen 443 ssl;
    listen [::]:443 ssl;
    server_name auth.example.org;

    ssl_certificate     /etc/letsencrypt/live/auth.example.org/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/auth.example.org/privkey.pem;
    # TLS 1.2 обязателен: WinHTTP на Windows 10 не умеет TLS 1.3.
    ssl_protocols TLSv1.2 TLSv1.3;
    ssl_session_cache shared:dreamsleeve:10m;
    add_header Strict-Transport-Security "max-age=31536000" always;

    # Тела запросов /auth/* — не больше 4096 байт.
    client_max_body_size 8k;

    location /auth/ {
        proxy_pass http://127.0.0.1:8779;
        proxy_http_version 1.1;
        proxy_set_header Connection "";
        proxy_set_header Host $host;
        # Ровно адрес игрока: сервер берёт последний адрес списка, один переход.
        proxy_set_header X-Forwarded-For $remote_addr;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_read_timeout 30s;
    }

    location / { return 404; }
}
```

Требования клиента к этому адресу:

- `authUrl` — только origin без пути: `https://auth.example.org` (порт можно указать). Клиент
  сам добавляет `/auth/login`, `/auth/register`, `/auth/resume`, `/auth/logout`,
  `/auth/reset-password`, поэтому маршрут `/auth/` должен быть в корне хоста; разместить его под
  префиксом (`https://example.org/dreamsleeve/auth/...`) нельзя. Сам хост может обслуживать и
  другие сайты, если `/auth/` свободен.
- Сертификат должен проходить обычную проверку Windows: доверенная цепочка и имя хоста в SAN.
  Let's Encrypt подходит без действий со стороны игроков.
- Редиректы клиент не выполняет: `authUrl` сразу `https://`, иначе запрос получит 301 и вход не пройдёт.
- Системный прокси Windows клиент не использует, ходит напрямую.

### 6.3. Веб-админка: admin.example.org

Админка — страницы для браузера и REST API (`/api/v1/*`). Проще и надёжнее оставить её на loopback
и ходить через SSH-туннель — тогда этот блок не нужен:

```bash
ssh -L 8780:127.0.0.1:8780 user@203.0.113.10     # затем http://127.0.0.1:8780 у себя
```

Если нужен доступ из браузера без туннеля:

```nginx
server {
    listen 443 ssl;
    listen [::]:443 ssl;
    server_name admin.example.org;

    ssl_certificate     /etc/letsencrypt/live/auth.example.org/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/auth.example.org/privkey.pem;
    ssl_protocols TLSv1.2 TLSv1.3;
    add_header Strict-Transport-Security "max-age=31536000" always;

    # Необязательно, но полезно: админка только с ваших адресов.
    # allow 198.51.100.7;
    # deny all;

    location / {
        proxy_pass http://127.0.0.1:8780;
        proxy_http_version 1.1;
        proxy_set_header Connection "";
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-For $remote_addr;
        proxy_set_header X-Forwarded-Proto $scheme;
    }
}
```

Сертификат выпускался одной командой на оба имени, поэтому путь у обоих блоков — каталог
первого имени (`live/auth.example.org`). В `[Admin.Listener]` должно быть
`TrustForwardedHeaders = true`, а nginx обязан передавать исходный `Host`: каждый `POST`
принимается только при `Origin`, равном `https://admin.example.org`. Если nginx слушает
нестандартный порт, передавайте `Host $http_host`.

```bash
sudo nginx -t && sudo systemctl reload nginx
```

## 7. Firewall

```bash
sudo ufw allow OpenSSH
sudo ufw allow 8778/udp
sudo ufw allow 80,443/tcp
sudo ufw enable
```

8779 и 8780 не открываются: сервер и так слушает их только на `127.0.0.1`. У облачного
провайдера правила группы безопасности настраиваются отдельно — там тоже нужен UDP 8778.

## 8. Первичная настройка админки

1. Возьмите код из консоли: `journalctl -u dreamsleeve | grep "setup code"`. Код одноразовый и
   живёт `Admin.Service.CodeLifetimeMinutes` (15 минут). Истёк — `echo admin-setup | sudo tee
   /run/dreamsleeve.stdin`.
2. Откройте `https://admin.example.org/setup` (или `http://127.0.0.1:8780/setup` через туннель),
   введите код, имя и пароль администратора (12–128 байт UTF-8).
3. Модераторов назначают на странице игрока (роль «Модератор»): они получают инструменты
   модерации в игре. Подробно: [AdminPanelRu.md](AdminPanelRu.md).

## 9. Что раздать игрокам

Мод из `dist/Client` и три значения в `SKSE/Plugins/Dreamsleeve/client.toml`:

```toml
serverHost = "play.example.org"    # A-запись на сервер или IPv4, например "203.0.113.10"
serverPort = 8778                  # [Server] Port
authUrl = "https://auth.example.org"
```

Удобнее всего отредактировать `client.toml` в `dist/Client` перед упаковкой архива: плагин
создаёт файл только при его отсутствии и никогда не перезаписывает. Остальные ключи клиента
оставьте по умолчанию; если на сервере увеличены `Server.MaxInitialPlayers`,
`Server.MaxRecentMessages`, `Server.PlayerInput.MaxActorValues` или `Server.MaxPacketBytes`,
поднимите соответствующие `maxInitialPlayers`, `maxRecentMessages`, `maxActorValues` и
`client.network.maxPacketBytes` у клиента — иначе клиент отвергнет данные сервера.

Сохранённый вход хранится в Windows Credential Manager отдельно для каждого `authUrl`.
Версия протокола проверяется при входе: клиент и сервер обновляются вместе, совместимости между
версиями нет.

## 10. Проверка после развёртывания

```bash
# HTTP-контур: nginx → сервер. Ожидается 400 {"code":"invalid_request",...} в JSON.
curl -i https://auth.example.org/auth/login -H "Content-Type: application/json" -d '{}'
# UDP слушается:
sudo ss -ulpn | grep 8778
```

- 502 от nginx — сервер не запущен или слушает другой порт (`ListenUrl`).
- 404 в формате nginx — запрос не попал в `location /auth/`.
- Проверка сертификата с Windows: `curl.exe -i https://auth.example.org/auth/login ...` без `-k`.
- Игровой вход: клиент в игре (Enter → ☰ → «Аккаунт») или `Client.Dev` с тем же `client.toml`:
  `xmake run Dreamsleeve.Client.Dev --config client.toml <username>`.

## 11. Свой сертификат вместо Let's Encrypt

**Сертификат от другого доверенного центра** ставится в nginx так же: `ssl_certificate` —
сертификат с промежуточными (full chain), `ssl_certificate_key` — ключ.

**Свой центр сертификации** подходит для закрытой группы или теста: Windows у каждого игрока
должна доверять вашему CA, иначе вход не пройдёт. Сертификат обязан содержать имя хоста (или
IP) из `authUrl` в subjectAltName.

```bash
# Центр сертификации (ca.key храните офлайн)
openssl req -x509 -new -newkey rsa:3072 -nodes -sha256 -days 3650 \
    -keyout ca.key -out ca.crt -subj "/CN=Dreamsleeve Private CA" \
    -addext "basicConstraints=critical,CA:TRUE" -addext "keyUsage=critical,keyCertSign,cRLSign"
# Ключ и запрос сервера
openssl req -new -newkey rsa:2048 -nodes -keyout auth.key -out auth.csr -subj "/CN=auth.example.org"
cat > auth.ext <<'EOF'
basicConstraints=CA:FALSE
keyUsage=critical,digitalSignature,keyEncipherment
extendedKeyUsage=serverAuth
subjectAltName=DNS:auth.example.org,IP:203.0.113.10
EOF
openssl x509 -req -in auth.csr -CA ca.crt -CAkey ca.key -CAcreateserial \
    -days 825 -sha256 -extfile auth.ext -out auth.crt
cat auth.crt ca.crt > auth.fullchain.crt     # для nginx: ssl_certificate
```

Игрок импортирует `ca.crt` (только его, не ключ) в «Доверенные корневые центры сертификации»:
`certutil -user -addstore Root ca.crt` для своей учётной записи Windows, или
`certutil -addstore -f Root ca.crt` из консоли администратора для всей машины. С IP в SAN
`authUrl` может быть `https://203.0.113.10`.

**Прямой HTTPS без nginx.** Сервер сам принимает TLS, если `ListenUrl` начинается с `https://` и
`CertificatePath` указывает на PKCS#12 (`.pfx`) с ключом. Пароль — в переменной окружения
`DREAMSLEEVE_AUTH_CERTIFICATE_PASSWORD` (админка: `DREAMSLEEVE_ADMIN_CERTIFICATE_PASSWORD`).

```bash
openssl pkcs12 -export -in auth.fullchain.crt -inkey auth.key -out auth.pfx -passout env:PFX_PASSWORD
```

```toml
[Authentication.Listener]
ListenUrl = "https://0.0.0.0:8443"
CertificatePath = "/opt/dreamsleeve/tls/auth.pfx"
TrustForwardedHeaders = false
```

Клиенту — `authUrl = "https://auth.example.org:8443"`. Порт ниже 1024 без root потребует
`AmbientCapabilities=CAP_NET_BIND_SERVICE` в службе. Сертификат читается при запуске: после
продления переэкспортируйте PFX и перезапустите сервер. Этот вариант проверен локально: PFX с
паролем из переменной окружения, собственный CA, проверка имени хоста. Без `CertificatePath`
URL `https://` использует сертификат Kestrel по умолчанию — для публичного сервера не годится.

## 12. Резервное копирование и обновление

Всё состояние — в `data/dreamsleeve.db` (SQLite в режиме WAL: рядом файлы `-wal` и `-shm`):
аккаунты, профили, сохранённые входы, роли, наказания, метки, админка и аудит. История чата
живёт только в памяти и после перезапуска пуста. Кроме базы сохраняйте `server.toml`,
`moderation.toml`, `pseudonyms.toml`.

Копировать один `.db` работающего сервера нельзя: часть записей ещё в `-wal`. Варианты:

```bash
# онлайн, согласованная копия (пакет sqlite3)
sqlite3 /opt/dreamsleeve/data/dreamsleeve.db ".backup '/var/backups/dreamsleeve-$(date +%F).db'"
# или остановить сервер и скопировать каталог data целиком
```

Обновление:

1. Объявите остановку (`announce ...`), сделайте резервную копию базы.
2. `sudo systemctl stop dreamsleeve`.
3. Замените файлы публикации, не трогая `server.toml`, `moderation.toml`, `pseudonyms.toml`,
   `data/`, `logs/`. Новые ключи конфигурации берут значения по умолчанию; описание —
   в новом `server.example.toml`.
4. `sudo systemctl start dreamsleeve`: миграции базы применяются при запуске.
5. Раздайте игрокам новую версию мода: протокол без обратной совместимости.

Вернуть старую версию сервера на обновлённую базу нельзя (сервер откажется открыть схему новее
своей) — только вместе с резервной копией.

## 13. Логи

- Консоль (journal): человекочитаемые строки, коды настройки и ответы команд.
- `logs/server-YYYYMMDD.json`: JSON-строки, новый файл каждый день и по `FileSizeLimitBytes`,
  хранится `RetainedFileCount` файлов.
- Уровень — `[Logging] MinimumLevel`; `Debug` показывает каждый отказ запроса с кодом. Пароли,
  коды, токены и тексты чата в лог не пишутся. Что пишется на каком уровне:
  [Server.Core README](../src/Dreamsleeve.Server.Core/README.ru.md).

## 14. Ёмкость

По умолчанию сервер рассчитан на 32 клиента. Для большего числа `N`:

| Ключ | Правило | N = 1000 (нагрузочные прогоны) |
|---|---|---|
| `Runtime.MaxSessions` | N | 1000 |
| `Server.PeerLimit` | ≥ N, с запасом | 1000–1100 |
| `Runtime.ControlReserve` | ≥ 4N + 4 | 4004 |
| `Runtime.MailboxCapacity` | растёт с N | 65536 |
| `Runtime.Player.MailboxCapacity`, `MaxPendingOutput` | ≈ 2N + 128 | 2128 |
| `Runtime.Player.MaxBootstrapEvents` | ≈ N | 1000 |
| `Runtime.Presence.MailboxCapacity` / `MaxControlDeliveries` | | 8192 / 1024 |
| `Server.EventBudget` | | 512 |
| `Server.MaxOutgoingPacketsPerPeer` / `MaxOutgoingBytesPerPeer` | | 4096 / 16 MiB |
| `Server.MaxOutgoingPackets` / `MaxOutgoingBytes` | | 262144 / 256 MiB |
| `Server.ReceiveBufferBytes` / `SendBufferBytes` | больше при плотной видимости: меньше потерь, но длиннее очередь | 256 KiB; контроль с 8 MiB |

Столбец N = 1000 — значения из нагрузочных прогонов 27–28 сентября (loopback, протокол того
времени): [движение v6](benchmarks/movement-v6-owner-2026-09-27.md),
[1000 клиентов при 20 Гц](benchmarks/movement-workers-2026-09-27.md),
[чат через ENet](benchmarks/enet-2026-09-27.md). Это ориентир, а не гарантия вместимости.
Нагрузку определяет плотность: сколько игроков видят друг друга в пределах
`Runtime.Presence.VisibilityDistance`.

Linux молча ограничивает буферы сокета значением `net.core.rmem_max`/`wmem_max`. Для 8 MiB:

```bash
echo -e "net.core.rmem_max=8388608\nnet.core.wmem_max=8388608" | sudo tee /etc/sysctl.d/90-dreamsleeve.conf
sudo sysctl --system
```

## 15. Windows

Сервер работает так же: ASP.NET Core Runtime 10, `dotnet Dreamsleeve.Server.dll --config server.toml`.
Правило про stdin действует и здесь: служба Windows не имеет консоли, stdin закрыт, и сервер
остановится сразу. Запускайте его в открытой консоли (например, задачей Планировщика «при входе
в систему» или в постоянном сеансе RDP). TLS — nginx для Windows или Caddy перед loopback-портами
(те же правила: `Host`, `X-Forwarded-For`, `X-Forwarded-Proto`, `TrustForwardedHeaders = true`),
либо прямой HTTPS с PFX. Firewall: входящее правило для UDP 8778 и TCP 443.

## 16. Частые проблемы

| Симптом | Причина |
|---|---|
| Служба завершается сразу после старта, в логе `Server stopped` без ошибки | stdin закрыт: нет `dreamsleeve.socket` / `StandardInput=socket` |
| В логе `ENet could not send to some peers ... HostUnreachable` | сеть или VPN сервера переключились, маршрут к игроку пропал; это не сбой, игрок отключится по таймауту ENet и переподключится |
| В логе `Restarting the game runtime in ... ms` | игровая часть упала (причина строкой выше) и перезапускается; игроки переподключаются сами |
| `Game runtime failed N times within ... s; stopping the server` | больше `[Recovery] MaxRestarts` отказов за окно; код выхода 1, systemd перезапустит процесс — смотрите причину в логе |
| Сервер: `Remote HTTP Authentication.Listener requires Authentication.Listener.AllowInsecureRemote...` | `ListenUrl` с `http://` не на `127.0.0.1`/`::1`; за nginx укажите `http://127.0.0.1:8779` |
| Сервер: `Runtime.ControlReserve must allow 4 * MaxSessions + 4...` | увеличили `MaxSessions`, не увеличив резерв |
| Лог клиента: `Plain HTTP authentication is permitted only on loopback; use HTTPS remotely` | `authUrl` начинается с `http://` |
| Лог клиента: `Auth URL must be an origin without a path` | в `authUrl` есть путь, например `/auth` |
| Лог клиента: `Invalid client setting: serverHost` | в `serverHost` пусто, пробел, `_`, IPv6, точка в конце или число больше 255 в IPv4; имена с не-ASCII символами — в punycode (`xn--…`) |
| Лог клиента: `Invalid client TOML: …: unknown_key` под строкой `serverIp = …` | конфиг до версии 1.0: переименуйте `serverIp` в `serverHost` |
| Вход в игре: `Cannot resolve host …` | у имени нет A-записи, опечатка или DNS недоступен у игрока; проверьте `nslookup play.example.org` |
| Ошибка TLS при входе в игре | цепочка не доверена Windows, имя не совпадает с SAN, нет TLS 1.2 или `authUrl` ведёт на редирект |
| Игроки массово получают 429 `rate_limited` | за nginx не включён `TrustForwardedHeaders`: все запросы с одного адреса |
| Админка: изменения отвечают 403 | нет `TrustForwardedHeaders = true` в `[Admin.Listener]` или nginx не передаёт `Host` |
| 502 Bad Gateway | сервер не запущен или `ListenUrl` не совпадает с `proxy_pass` |
| Вход проходит, игровое соединение нет | UDP 8778 закрыт в firewall/облаке, неверные `serverHost`/`serverPort`, `BindAddress = "127.0.0.1"` |
| Клиент сразу отключается, в логе сервера `the server is full` | достигнут `Runtime.MaxSessions` (не вошедшие клиенты тоже считаются) |
