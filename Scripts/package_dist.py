#!/usr/bin/env python3
"""Assemble the release layout in dist/: the client mod and the published server.

dist/Client (copy into Skyrim Data or install as a mod):
  SKSE/Plugins/Dreamsleeve.Client.dll (+ .pdb)
  DreamsleeveClient.esp                           the firefly and ground mark forms (needs Dawnguard)
  SKSE/Plugins/Dreamsleeve/client.toml            Plugin/client.toml: the example using the ESP forms
  SKSE/Plugins/Dreamsleeve/aliases.toml           streamer-mode pseudonym dictionary
  PrismaUI/views/Dreamsleeve/                     production web UI (index.html, assets, theme.user.css)
  Scripts/DreamsleeveClient.pex, Scripts/Source/DreamsleeveClient.psc   Papyrus API for other mods
  Dreamsleeve/README.md, THIRD_PARTY_NOTICES.md   install notes and third-party licenses
  Dreamsleeve/LICENSE, EXCEPTIONS.md              Dreamsleeve's own license (GPL-3.0-or-later with exceptions)
  Dreamsleeve/API/DreamsleeveAPI.h                C++ API header for other SKSE plugins (MIT, notice inside)

dist/Server (framework-dependent `dotnet publish` of Dreamsleeve.Server, Release):
  Dreamsleeve.Server.dll and dependencies, db/migrations, server.example.toml, README.md,
  moderation.example.toml and moderation.toml (word list, created only when absent),
  pseudonyms.example.toml and pseudonyms.toml (hidden-name dictionary, created only when absent),
  LICENSE, EXCEPTIONS.md, THIRD_PARTY_NOTICES.md (the admin panel's htmx is embedded in
  Dreamsleeve.Server.Web.dll)

The script never touches a game folder or a running server: installing is a copy,
and an existing client.toml/ui.toml/server.toml must not be overwritten on update.
User files already inside dist (configs, word list, database, logs) are kept across rebuilds.
"""
from __future__ import annotations

import argparse
import shutil
import subprocess
import sys
import tomllib
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
UI = ROOT / "src" / "Dreamsleeve.Client.UI"
CLIENT = ROOT / "src" / "Dreamsleeve.Client"
SERVER = ROOT / "src" / "Dreamsleeve.Server"
WEB = ROOT / "src" / "Dreamsleeve.Server.Web"
# Test, dev and benchmark builds never ship with the server.
SERVER_FORBIDDEN = ("Tests", "Benchmarks", "Client.Dev", "TraceReport", "Expecto", "Faqt")
BUILD = ROOT / "build" / "windows" / "x64" / "releasedbg"
FORBIDDEN = ("node_modules", "demo.html", "dist-demo", "test-results", "credentials", "logs", "data")
FORBIDDEN_SUFFIXES = (".map", ".db", ".log")
# Relative to dist/: user-owned files and folders that a rebuild must not replace.
PRESERVED = (
    "Client/SKSE/Plugins/Dreamsleeve/client.toml",
    "Client/SKSE/Plugins/Dreamsleeve/ui.toml",
    "Client/SKSE/Plugins/Dreamsleeve/aliases.toml",
    "Server/server.toml",
    "Server/moderation.toml",
    "Server/pseudonyms.toml",
    "Server/data",
    "Server/logs",
)

# Dreamsleeve's license, shipped with every build; the source of a build is its release tag there.
LICENSE_FILES = (ROOT / "LICENSE", ROOT / "EXCEPTIONS.md")
REPOSITORY = "https://github.com/Newrite/Dreamsleeve"
LICENSE_NOTE = ("Лицензия: GPL-3.0-or-later с Modding Exception и GPL-3.0 Linking Exception — тексты в\n"
                f"`LICENSE` и `EXCEPTIONS.md` рядом. Исходный код: {REPOSITORY} (тег релиза с той же версией).\n")

# The documented defaults, also embedded in the plugin as its first-run file.
CLIENT_TOML = ROOT / "src" / "Dreamsleeve.Client.Core" / "client.example.toml"
# The mod's own forms and the client.toml shipped with them.
ESP = ROOT / "Plugin" / "DreamsleeveClient.esp"
MOD_TOML = ROOT / "Plugin" / "client.toml"
# The only settings MOD_TOML may change: everything else follows the example.
ESP_KEYS = {f"client.{name}" for name in (
    "fireflyPlugin", "fireflyFormId", "groundNotePlugin", "groundNoteFormId", "deathMarkPlugin", "deathMarkFormId")}


def settings(path: Path) -> dict[str, object]:
    def flat(table: dict, prefix: str = ""):
        for key, value in table.items():
            if isinstance(value, dict):
                yield from flat(value, f"{prefix}{key}.")
            else:
                yield f"{prefix}{key}", value
    return dict(flat(tomllib.loads(path.read_text(encoding="utf-8-sig"))))


def check_mod_toml() -> None:
    mod, example = settings(MOD_TOML), settings(CLIENT_TOML)
    drift = sorted(key for key in mod.keys() | example.keys() if key not in ESP_KEYS and mod.get(key) != example.get(key))
    if drift:
        raise SystemExit(f"{MOD_TOML} differs from client.example.toml beyond the ESP forms: {', '.join(drift)}")


def run(command: list[str], cwd: Path) -> None:
    executable = shutil.which(command[0])
    if executable is None:
        raise SystemExit(f"Required tool not found: {command[0]}")
    print("> " + subprocess.list2cmdline(command), flush=True)
    subprocess.run([executable, *command[1:]], cwd=cwd, check=True)


def copy_tree(source: Path, target: Path) -> int:
    count = 0
    for item in source.rglob("*"):
        if item.is_dir():
            continue
        relative = item.relative_to(source)
        if any(part in FORBIDDEN for part in relative.parts) or item.suffix in FORBIDDEN_SUFFIXES:
            continue
        destination = target / relative
        destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(item, destination)
        count += 1
    return count


def copy_licenses(target: Path) -> None:
    for path in LICENSE_FILES:
        shutil.copy2(path, target / path.name)


def notices() -> str:
    packages = {
        "React / ReactDOM": UI / "node_modules" / "react" / "LICENSE",
        "Zustand": UI / "node_modules" / "zustand" / "LICENSE",
    }
    parts = ["# Third-party notices\n",
             "Dreamsleeve.Client.dll links CommonLibSSE-NG (https://github.com/alandtse/CommonLibSSE-NG, "
             "GPL-3.0-or-later with the Modding Exception and the GPL-3.0 Linking Exception, the same terms as "
             "Dreamsleeve: see LICENSE and EXCEPTIONS.md). Parts of the plugin follow skyrim-rich-presence "
             "(https://github.com/doodlum/skyrim-rich-presence, same terms).\n\n",
             "It also links ENet (MIT), protobuf (BSD-3-Clause), spdlog (MIT), Glaze (MIT) and magic_enum (MIT); "
             "their texts are in the respective upstream repositories.\n",
             "The web UI bundle contains the following packages:\n"]
    for name, path in packages.items():
        if path.exists():
            parts.append(f"\n## {name}\n\n```\n{path.read_text(encoding='utf-8').strip()}\n```\n")
        else:
            parts.append(f"\n## {name}\n\nLicense file not found at packaging time ({path}).\n")
    parts.append("\nPrismaUI, SKSE Menu Framework, Address Library and Media Keys Fix are separate downloads "
                 "with their own licenses and are not redistributed here.\n")
    return "".join(parts)


def client_readme() -> str:
    return """# Dreamsleeve Client

Установка: скопируйте содержимое папки `Client` в `Skyrim Special Edition/Data`
(или установите её как мод через MO2/Vortex). При обновлении не перезаписывайте
`SKSE/Plugins/Dreamsleeve/client.toml` и `ui.toml`, если вы их уже настроили.

Требуется (ставится отдельно):

- SKSE64 / SKSEVR;
- Address Library for SKSE Plugins (SE и/или AE, для VR — VR Address Library);
- Dawnguard (мастер `DreamsleeveClient.esp`: модель метки смерти);
- PrismaUI 1.5.1 или новее (https://www.nexusmods.com/skyrimspecialedition/mods/148718);
- Media Keys Fix SKSE (требование PrismaUI для клавиатурного ввода);
- SKSE Menu Framework (необязательно: страница настроек, F1).

`DreamsleeveClient.esp` включите в порядке загрузки: в нём светлячок над другими игроками и
метки на земле, `client.toml` мода ссылается на его формы. Без плагина удалите эти ключи
(`fireflyPlugin`, `groundNotePlugin`, `deathMarkPlugin` и их `FormId`) — вернутся ванильные формы.

Настройка: в `SKSE/Plugins/Dreamsleeve/client.toml` три значения даёт владелец сервера —
`serverHost` (IPv4-адрес или DNS-имя), `serverPort` и `authUrl` (`https://…` без пути).
Если у сервера есть прокси для тех, кому он недоступен, владелец даёт и таблицу `[[routes]]` в
конец файла: клиент сам перейдёт на прокси, когда сервер не отвечает, а во вкладке «Аккаунт»
маршрут выбирается вручную. Остальные ключи с пояснениями и допустимыми значениями описаны в
самом файле.
Вход и регистрация выполняются из окна чата (Enter → ☰ → «Аккаунт»); сохранённый вход
хранится в Windows Credential Manager, пароль в файлы не записывается.
Положение окна и внешний вид сохраняются в `ui.toml`; полное отключение интерфейса —
в SKSE Menu Framework (Dreamsleeve → Настройки).

Помеченные сервером сообщения: настройки чата → «Помеченные сообщения» (показывать,
звёздочки или скрывать). Правый клик по нику в чате — профиль и игнор; список
игнорируемых — панель «Игнор». Модераторам сервера там же доступны наказания и удаление
сообщений, а панель «Модерация» показывает действующие наказания.

Устройство: при входе клиент сообщает серверу хэш UUID материнской платы, смешанный с
адресом сервера (у каждого сервера своё значение, сам UUID с компьютера не уходит). Сервер
может распространить бан и на устройство.

Цвет своего имени в чате — настройки чата → «Цвет вашего имени в чате». Там же светлячков и
метки на земле можно оставить только от игроков из ваших гильдий.

Имена: в настройках чата выбирается имя пользователя, отображаемое имя или имя
персонажа; режим стримера заменяет все имена локальными псевдонимами из
`SKSE/Plugins/Dreamsleeve/aliases.toml` (назначение хранится в `ui.toml`, на сервер
не передаётся). Личный список игнора тоже хранится в `ui.toml`, отдельно для
каждого адреса сервера. Имена над светлячками — только SE/AE.

Объявления: вкладка «Объявления» показывает сообщения сервера и объявления других модов;
где их показывать и от каких источников — настройки чата → «Объявления». Моды публикуют
через C++ API (`DreamsleeveAPI.h`) или Papyrus (`Scripts/Source/DreamsleeveClient.psc`),
см. docs/DreamsleeveModApiRu.md в репозитории. Заголовок `Dreamsleeve/API/DreamsleeveAPI.h` — под
MIT (текст в нём самом): его можно подключать в плагин под любой лицензией.

Логи: `Documents/My Games/Skyrim Special Edition/SKSE/DreamsleeveClient.log`.

""" + LICENSE_NOTE


def server_notices() -> str:
    htmx = (WEB / "Resources" / "htmx.LICENSE").read_text(encoding="utf-8").strip()
    return ("# Third-party notices\n\n"
            "The admin panel (Dreamsleeve.Server.Web.dll) embeds htmx 2.0.11 (https://htmx.org), "
            "served from the assembly at /static/htmx.min.js; its license:\n\n"
            f"```\n{htmx}\n```\n\n"
            "Falco, Falco.Markup and Falco.Htmx (https://github.com/FalcoFramework) are licensed under Apache-2.0. "
            "Other packages (Serilog, Tomlyn, SqlHydra, Migrondi, Microsoft.Data.Sqlite, yENet, Google.Protobuf, FSharp.Core) "
            "keep their own licenses, listed in their NuGet packages.\n")


def check_server(server: Path) -> None:
    web = server / "Dreamsleeve.Server.Web.dll"
    if not web.exists():
        raise SystemExit(f"Missing {web}")
    htmx = (WEB / "Resources" / "htmx.min.js").read_bytes()
    if htmx not in web.read_bytes():
        raise SystemExit("Dreamsleeve.Server.Web.dll does not embed the vendored htmx.min.js")
    if "Zero-Clause BSD" not in (server / "THIRD_PARTY_NOTICES.md").read_text(encoding="utf-8"):
        raise SystemExit("THIRD_PARTY_NOTICES.md lacks the htmx license")
    for item in server.rglob("*"):
        if any(marker in item.name for marker in SERVER_FORBIDDEN):
            raise SystemExit(f"Test or dev file in dist/Server: {item}")
    # User files are stashed while dist is rebuilt: a server.toml here came from the build.
    if (server / "server.toml").exists():
        raise SystemExit("dist/Server must not ship a server.toml")


# How each kind of publish starts, and what the host needs for it.
SERVER_KINDS = {
    "framework": ("Публикация `dotnet publish -c Release` (framework-dependent): нужен установленный\n"
                  "ASP.NET Core Runtime 10.0 (https://dotnet.microsoft.com/download/dotnet/10.0).",
                  "powershell", "dotnet Dreamsleeve.Server.dll"),
    "win-x64": ("Самодостаточная сборка для Windows x64: .NET и все библиотеки внутри\n"
                "`Dreamsleeve.Server.exe`, ставить ничего не нужно. Нативные библиотеки (SQLite) при первом\n"
                "запуске распаковываются в `%TEMP%\\.net` (или в `DOTNET_BUNDLE_EXTRACT_BASE_DIR`).",
                "powershell", ".\\Dreamsleeve.Server.exe"),
    "linux-x64": ("Самодостаточная сборка для Linux x64 (glibc): .NET и все библиотеки внутри\n"
                  "`Dreamsleeve.Server`. От системы нужны только ICU и OpenSSL 3 (Ubuntu 24.04:\n"
                  "`sudo apt install libicu74 libssl3t64`). Нативные библиотеки (SQLite) при первом запуске\n"
                  "распаковываются в `~/.net` пользователя службы (или в `DOTNET_BUNDLE_EXTRACT_BASE_DIR`):\n"
                  "каталог должен быть доступен ему на запись.",
                  "bash", "./Dreamsleeve.Server"),
}


def server_readme(kind: str = "framework") -> str:
    intro, shell, start = SERVER_KINDS[kind]
    return f"""# Dreamsleeve Server

{intro}

```{shell}
{start} --write-config server.toml   # создать файл настроек
{start} --config server.toml         # запуск
```
""" + """
`server.example.toml` рядом — все ключи с пояснениями и допустимыми значениями; скопируйте
его в `server.toml` (или оставьте в `server.toml` только изменённые ключи). По умолчанию ENet
слушает 127.0.0.1:8778, HTTP входа — 127.0.0.1:8779, админка — 127.0.0.1:8780. Для игроков из
сети: `[Server] BindAddress = "0.0.0.0"` и открытый UDP 8778, вход — через nginx с TLS на
`127.0.0.1:8779` и `[Authentication.Listener] TrustForwardedHeaders = true`. Полная
инструкция (systemd, nginx, сертификаты, ёмкость, резервные копии) — docs/DeploymentRu.md
в репозитории. База SQLite и логи создаются относительно рабочего каталога (`data/`, `logs/`);
миграции лежат в `db/migrations` и применяются при старте.
Остановка: `quit` в консоли или Ctrl+C. Сервер читает команды из stdin и останавливается, когда
stdin закрыт: как службу его запускают со stdin из FIFO (см. инструкцию). Существующий
`server.toml` при обновлении не перезаписывайте.

Модерация: `moderation.toml` — словарь в двух уровнях (формат и поставляемый список
стрим-безопасности — в `moderation.example.toml`). `[block]` отклоняет новые
username/display name, сообщения и публикуемое имя персонажа; `[flag]` доставляет
сообщение с пометкой, а игрок сам выбирает показ, звёздочки или скрытие. Отключается
`[Moderation] Enabled = false`.
Антиспам (частота, всплеск, повторы) настраивается в `[Runtime.Chat.Rate]`. Это базовая
защита, а не полная модерация. Изменения читаются только при запуске.

Скрытое имя: `[Identity]` — разрешён ли игрокам режим «Скрывать моё имя от других
игроков» (`AllowHiddenIdentity`), не чаще какого интервала его можно переключать
(`ToggleIntervalMs`) и словарь псевдонимов `pseudonyms.toml` (формат — в
`pseudonyms.example.toml`; без файла — 24 встроенных имени). Лог сервера и база
хранят настоящие имена: псевдоним скрывает игрока только от других игроков.
Там же — смена отображаемого имени игроком из игры: `AllowDisplayNameChange` и
`DisplayNameChangeIntervalMinutes` (по умолчанию не чаще раза в минуту; смены администратором не ограничены)
и смена цвета имени в чате: `NameColorIntervalMs` (по умолчанию не чаще раза в 10 с).

Прокси: для игроков, которым адрес сервера недоступен, — прокси на другом хосте (UDP на игровой
порт и HTTPS на вход с `X-Forwarded-For`). Его адрес — в `[Proxies] Trusted`, его origin для входа
через Steam — в `[Authentication.Steam] ProxyUrls`; конфиг nginx и все нюансы — раздел «Прокси»
docs/DeploymentRu.md.

Объявления: `[Announcements]` — допуск объявлений клиентов (`TrustedClient`,
`ThirdParty`, по умолчанию оба разрешены), их отдельный лимит частоты и расписание
серверных объявлений `[[Announcements.Scheduled]]`. Разовое объявление администратора —
команда консоли `announce <текст>` или страница «Объявление» веб-админки.

Веб-админка: `[Admin]`, по умолчанию http://127.0.0.1:8780 — только loopback. Пока
администраторов нет, сервер при запуске печатает в консоль одноразовый код настройки
(`admin-setup` выдаёт новый); откройте `/setup`, введите код, имя и пароль. Забытый
пароль администратора: команда `admin-reset <имя>` и страница `/reset`. Панель
показывает онлайн (с настоящими именами скрытых игроков), игроков, роль, переименование,
сброс пароля и отзыв доступа, объявления, аудит, токены REST API и конфигурацию только
на чтение. Отключается `[Admin] Enabled = false`. Для удалённого доступа оставьте
loopback и ходите через SSH-туннель (`ssh -L 8780:127.0.0.1:8780 host`) или обратный
прокси с HTTPS (`TrustForwardedHeaders = true` только для прокси на этой же машине);
HTTP без TLS наружу — только явным `AllowInsecureRemote`. Подробно — docs/AdminPanelRu.md.

""" + LICENSE_NOTE


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--skip-build", action="store_true", help="reuse the existing DLL and UI bundle")
    parser.add_argument("--no-server", action="store_true", help="assemble only dist/Client")
    parser.add_argument("--output", default=str(ROOT / "dist"))
    args = parser.parse_args()

    if not args.skip_build:
        run(["xmake", "build", "Dreamsleeve.Client"], ROOT)
        run(["npm", "run", "build"], UI)

    dll = BUILD / "Dreamsleeve.Client.dll"
    if not dll.exists():
        raise SystemExit(f"Missing {dll}; build Dreamsleeve.Client first")
    ui_dist = UI / "dist"
    if not (ui_dist / "index.html").exists():
        raise SystemExit(f"Missing {ui_dist / 'index.html'}; run npm run build first")

    output = Path(args.output)
    stash = output.parent / (output.name + ".preserve")
    if stash.exists():
        shutil.rmtree(stash)
    kept = []
    for relative in PRESERVED:
        source = output / relative
        if source.exists():
            target = stash / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.move(str(source), str(target))
            kept.append(relative)
    if output.exists():
        shutil.rmtree(output)
    client = output / "Client"
    plugins = client / "SKSE" / "Plugins"
    plugins.mkdir(parents=True)
    shutil.copy2(dll, plugins / dll.name)
    pdb = dll.with_suffix(".pdb")
    if pdb.exists():
        shutil.copy2(pdb, plugins / pdb.name)
    config = plugins / "Dreamsleeve"
    config.mkdir()
    check_mod_toml()
    shutil.copy2(MOD_TOML, config / "client.toml")
    shutil.copy2(ESP, client / ESP.name)
    shutil.copy2(CLIENT / "aliases.toml", config / "aliases.toml")

    views = client / "PrismaUI" / "views" / "Dreamsleeve"
    copied = copy_tree(ui_dist, views)

    # Papyrus API for other mods: the source for their compiler, the checked-in
    # .pex for the game (dist builds do not need the Creation Kit).
    papyrus = CLIENT / "Papyrus"
    scripts = client / "Scripts"
    (scripts / "Source").mkdir(parents=True)
    shutil.copy2(papyrus / "DreamsleeveClient.psc", scripts / "Source" / "DreamsleeveClient.psc")
    shutil.copy2(papyrus / "DreamsleeveClient.pex", scripts / "DreamsleeveClient.pex")

    notes = client / "Dreamsleeve"
    notes.mkdir()
    (notes / "README.md").write_text(client_readme(), encoding="utf-8")
    (notes / "THIRD_PARTY_NOTICES.md").write_text(notices(), encoding="utf-8")
    copy_licenses(notes)
    (notes / "API").mkdir()
    shutil.copy2(CLIENT / "API" / "DreamsleeveAPI.h", notes / "API" / "DreamsleeveAPI.h")

    if not args.no_server:
        server = output / "Server"
        run(["dotnet", "publish", str(SERVER), "-c", "Release", "-o", str(server), "--nologo"], ROOT)
        shutil.copy2(SERVER / "server.example.toml", server / "server.example.toml")
        shutil.copy2(SERVER / "moderation.example.toml", server / "moderation.example.toml")
        # Enabled by default: a fresh server starts with the example word list.
        shutil.copy2(SERVER / "moderation.example.toml", server / "moderation.toml")
        shutil.copy2(SERVER / "pseudonyms.example.toml", server / "pseudonyms.example.toml")
        shutil.copy2(SERVER / "pseudonyms.example.toml", server / "pseudonyms.toml")
        (server / "README.md").write_text(server_readme(), encoding="utf-8")
        (server / "THIRD_PARTY_NOTICES.md").write_text(server_notices(), encoding="utf-8")
        copy_licenses(server)
        check_server(server)

    for item in output.rglob("*"):
        relative = item.relative_to(output).parts
        if any(part in FORBIDDEN for part in relative) or item.suffix in FORBIDDEN_SUFFIXES:
            raise SystemExit(f"Forbidden content in dist: {item}")
    for relative in kept:
        target = output / relative
        if target.is_dir():
            shutil.rmtree(target)
        elif target.exists():
            target.unlink()
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.move(str(stash / relative), str(target))
    if stash.exists():
        shutil.rmtree(stash)
    if kept:
        print("Kept user files: " + ", ".join(kept))
    print(f"dist assembled at {output}: Client (DLL, config, {copied} UI files){'' if args.no_server else ', Server (dotnet publish)'}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
