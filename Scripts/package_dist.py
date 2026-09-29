#!/usr/bin/env python3
"""Assemble the release layout in dist/: the client mod and the published server.

dist/Client (copy into Skyrim Data or install as a mod):
  SKSE/Plugins/Dreamsleeve.Client.dll (+ .pdb)
  SKSE/Plugins/Dreamsleeve/client.toml            defaults; edited by the user
  SKSE/Plugins/Dreamsleeve/aliases.toml           streamer-mode pseudonym dictionary
  PrismaUI/views/Dreamsleeve/                     production web UI (index.html, assets, theme.user.css)
  Dreamsleeve/README.md, THIRD_PARTY_NOTICES.md   install notes and licenses

dist/Server (framework-dependent `dotnet publish` of Dreamsleeve.Server, Release):
  Dreamsleeve.Server.dll and dependencies, db/migrations, server.example.toml, README.md,
  moderation.example.toml and moderation.toml (word list, created only when absent)

The script never touches a game folder or a running server: installing is a copy,
and an existing client.toml/ui.toml/server.toml must not be overwritten on update.
User files already inside dist (configs, word list, database, logs) are kept across rebuilds.
"""
from __future__ import annotations

import argparse
import shutil
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
UI = ROOT / "src" / "Dreamsleeve.Client.UI"
CLIENT = ROOT / "src" / "Dreamsleeve.Client"
SERVER = ROOT / "src" / "Dreamsleeve.Server"
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
    "Server/data",
    "Server/logs",
)

CLIENT_TOML = """# Dreamsleeve client. Omitted settings keep defaults; keys are case-sensitive.
# Full reference: src/Dreamsleeve.Client.Core/client.example.toml in the repository.
version = 1
serverIp = "127.0.0.1"
serverPort = 8778
authUrl = "http://127.0.0.1:8779"
allowInsecureRemoteAuth = false

[client]
visibilityDistance = 8192
showFireflies = true
# STAT base form: plugin-local ID, without the load-order prefix.
fireflyPlugin = "Skyrim.esm"
fireflyFormId = 0x02EB0F
fireflyScale = 0.25 # 0.01..10.0; engine precision is 0.01
showFireflyNames = true
fireflyNameOcclusion = true # Hide names behind collision geometry
fireflyNameFontSize = 18 # HUD units, 8..48
fireflyNameOffset = 35 # Height above the firefly, 0..512 game units
"""


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


def notices() -> str:
    packages = {
        "React / ReactDOM": UI / "node_modules" / "react" / "LICENSE",
        "Zustand": UI / "node_modules" / "zustand" / "LICENSE",
    }
    parts = ["# Third-party notices\n",
             "Dreamsleeve.Client.dll links CommonLibSSE-NG (MIT), ENet (MIT), protobuf (BSD-3-Clause), "
             "spdlog (MIT), Glaze (MIT) and magic_enum (MIT); their texts are in the respective upstream repositories.\n",
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
- PrismaUI 1.5.1 или новее (https://www.nexusmods.com/skyrimspecialedition/mods/148718);
- Media Keys Fix SKSE (требование PrismaUI для клавиатурного ввода);
- SKSE Menu Framework (необязательно: страница настроек, F1).

Настройка: укажите адрес сервера и auth URL в `SKSE/Plugins/Dreamsleeve/client.toml`.
Вход и регистрация выполняются из окна чата (Enter → ☰ → «Аккаунт»); сохранённый вход
хранится в Windows Credential Manager, пароль в файлы не записывается.
Положение окна и внешний вид сохраняются в `ui.toml`; полное отключение интерфейса —
в SKSE Menu Framework (Dreamsleeve → Настройки).

Помеченные сервером сообщения: настройки чата → «Помеченные сообщения» (показывать,
звёздочки или скрывать). Правый клик по нику в чате — профиль и игнор.

Имена: в настройках чата выбирается имя пользователя, отображаемое имя или имя
персонажа; режим стримера заменяет все имена локальными псевдонимами из
`SKSE/Plugins/Dreamsleeve/aliases.toml` (назначение хранится в `ui.toml`, на сервер
не передаётся). Личный список игнора тоже хранится в `ui.toml`, отдельно для
каждого адреса сервера. Имена над светлячками — только SE/AE.

Логи: `Documents/My Games/Skyrim Special Edition/SKSE/DreamsleeveClient.log`.
"""


def server_readme() -> str:
    return """# Dreamsleeve Server

Публикация `dotnet publish -c Release` (framework-dependent): нужен установленный
ASP.NET Core Runtime 10.0 (https://dotnet.microsoft.com/download/dotnet/10.0).

```powershell
dotnet Dreamsleeve.Server.dll --write-config server.toml   # создать файл настроек
dotnet Dreamsleeve.Server.dll --config server.toml         # запуск
```

`server.example.toml` рядом показывает все ключи. По умолчанию ENet слушает
127.0.0.1:8778, HTTP auth — 127.0.0.1:8779 (только loopback без TLS). Для удалённых
клиентов задайте `Server.BindAddress`, `Authentication.ListenUrl` с HTTPS и сертификат,
см. docs/AuthenticationRu.md в репозитории. База SQLite и логи создаются относительно
рабочего каталога (`data/`, `logs/`); миграции лежат в `db/migrations` и применяются при старте.
Остановка: `quit` в консоли или Ctrl+C. Существующий `server.toml` при обновлении не перезаписывайте.

Модерация: `moderation.toml` — словарь в двух уровнях (формат и поставляемый список
стрим-безопасности — в `moderation.example.toml`). `[block]` отклоняет новые
username/display name, сообщения и публикуемое имя персонажа; `[flag]` доставляет
сообщение с пометкой, а игрок сам выбирает показ, звёздочки или скрытие. Отключается
`[Moderation] Enabled = false`.
Антиспам (частота, всплеск, повторы) настраивается в `[Runtime.Chat]`. Это базовая
защита, а не полная модерация. Изменения читаются только при запуске.
"""


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
    (config / "client.toml").write_text(CLIENT_TOML, encoding="utf-8")
    shutil.copy2(CLIENT / "aliases.toml", config / "aliases.toml")

    views = client / "PrismaUI" / "views" / "Dreamsleeve"
    copied = copy_tree(ui_dist, views)

    notes = client / "Dreamsleeve"
    notes.mkdir()
    (notes / "README.md").write_text(client_readme(), encoding="utf-8")
    (notes / "THIRD_PARTY_NOTICES.md").write_text(notices(), encoding="utf-8")

    if not args.no_server:
        server = output / "Server"
        run(["dotnet", "publish", str(SERVER), "-c", "Release", "-o", str(server), "--nologo"], ROOT)
        shutil.copy2(SERVER / "server.example.toml", server / "server.example.toml")
        shutil.copy2(SERVER / "moderation.example.toml", server / "moderation.example.toml")
        # Enabled by default: a fresh server starts with the example word list.
        shutil.copy2(SERVER / "moderation.example.toml", server / "moderation.toml")
        (server / "README.md").write_text(server_readme(), encoding="utf-8")

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
