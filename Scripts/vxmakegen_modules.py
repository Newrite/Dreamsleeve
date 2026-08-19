#!/usr/bin/env python3
from __future__ import annotations

import argparse
import os
import re
import shutil
import subprocess
import sys
import xml.etree.ElementTree as ET
from dataclasses import dataclass, field
from pathlib import Path
from typing import Iterable

SCRIPT_DIR = Path(__file__).resolve().parent
SOLUTION_EXTENSIONS = (".sln", ".slnx")
CPP_SOURCE_EXTENSIONS = {
    ".c", ".cc", ".cpp", ".cxx", ".c++",
    ".ixx", ".cppm", ".mpp", ".mxx",
}
MODULE_INTERFACE_EXTENSIONS = {".ixx", ".cppm", ".mpp", ".mxx"}

SLN_PROJECT_RE = re.compile(
    r'^Project\("(?P<type_guid>\{[^}]+\})"\)\s*=\s*'
    r'"(?P<name>[^"]+)",\s*"(?P<path>[^"]+)",\s*"(?P<guid>\{[^}]+\})"',
    re.IGNORECASE,
)

MODULE_DECL_RE = re.compile(
    r"^\s*(?:export\s+)?module\s+"
    r"(?P<name>[A-Za-z_][A-Za-z0-9_\.]*)(?::[A-Za-z_][A-Za-z0-9_]*)?\s*;"
)
IMPORT_RE = re.compile(
    r"^\s*(?:export\s+)?import\s+"
    r"(?P<name>[A-Za-z_][A-Za-z0-9_\.]*)(?::[A-Za-z_][A-Za-z0-9_]*)?\s*;"
)


@dataclass(frozen=True)
class SolutionProject:
    name: str
    guid: str
    path: Path


@dataclass
class NativeProjectInfo:
    project: SolutionProject
    sources: set[Path] = field(default_factory=set)
    exported_modules: set[str] = field(default_factory=set)
    imported_modules: set[str] = field(default_factory=set)


def iter_ancestor_dirs(start: Path):
    current = start.resolve()
    if current.is_file():
        current = current.parent

    while True:
        yield current
        if current.parent == current:
            break
        current = current.parent


def find_repo_root(explicit: str | None) -> Path:
    if explicit:
        repo_root = Path(explicit).expanduser().resolve()
        if not (repo_root / "xmake.lua").is_file():
            raise FileNotFoundError(f"xmake.lua not found in repo root: {repo_root}")
        return repo_root

    seen: set[Path] = set()
    for start in (Path.cwd(), SCRIPT_DIR):
        for candidate in iter_ancestor_dirs(start):
            if candidate in seen:
                continue
            seen.add(candidate)
            if (candidate / "xmake.lua").is_file():
                return candidate

    raise FileNotFoundError(
        "Could not find repo root.\n"
        "Expected to find xmake.lua in the current directory, one of its parents,\n"
        "or one of the parent directories of this script.\n"
        "Use --repo-root to specify it explicitly."
    )


def run_command(command: list[str], cwd: Path) -> None:
    printable = " ".join(f'"{arg}"' if " " in arg else arg for arg in command)
    print(f"> {printable}")
    result = subprocess.run(command, cwd=str(cwd))
    if result.returncode != 0:
        raise RuntimeError(f"Command failed with exit code {result.returncode}")


def gather_solution_candidates(repo_root: Path) -> list[Path]:
    directories = [repo_root]
    directories.extend(sorted(path for path in repo_root.glob("vsxmake*") if path.is_dir()))

    dot_vs_dir = repo_root / ".vs"
    if dot_vs_dir.is_dir():
        directories.append(dot_vs_dir)

    candidates: dict[Path, Path] = {}
    for directory in directories:
        for extension in SOLUTION_EXTENSIONS:
            for path in directory.rglob(f"*{extension}"):
                if path.is_file():
                    candidates[path.resolve()] = path.resolve()

    return sorted(candidates.values())


def snapshot_solution_times(repo_root: Path) -> dict[Path, int]:
    return {path: path.stat().st_mtime_ns for path in gather_solution_candidates(repo_root)}


@dataclass(frozen=True)
class SolutionCandidate:
    path: Path
    changed: bool
    mtime_ns: int


def find_solution_path(repo_root: Path, before: dict[Path, int]) -> Path:
    candidates: list[SolutionCandidate] = []
    for path in gather_solution_candidates(repo_root):
        mtime_ns = path.stat().st_mtime_ns
        changed = before.get(path) != mtime_ns
        candidates.append(SolutionCandidate(path=path, changed=changed, mtime_ns=mtime_ns))

    if not candidates:
        raise FileNotFoundError(
            "Solution file was not generated.\n"
            "Expected xmake to produce a .sln/.slnx under the repo root, a vsxmake* directory, or .vs."
        )

    changed_candidates = [item for item in candidates if item.changed]
    preferred = changed_candidates or candidates
    preferred.sort(
        key=lambda item: (
            0 if item.path.suffix.lower() == ".sln" else 1,
            -item.mtime_ns,
            len(item.path.parts),
        )
    )
    return preferred[0].path


def collect_managed_projects(repo_root: Path) -> list[Path]:
    src_root = repo_root / "src"
    if not src_root.is_dir():
        return []

    projects = [
        path.resolve()
        for path in src_root.rglob("*")
        if path.is_file() and path.suffix.lower() in {".csproj", ".fsproj"}
    ]
    return sorted(projects)


def list_solution_projects(solution_path: Path) -> set[Path]:
    result = subprocess.run(
        ["dotnet", "sln", str(solution_path), "list"],
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        cwd=str(solution_path.parent),
    )
    if result.returncode != 0:
        return set()

    existing: set[Path] = set()
    for line in result.stdout.splitlines():
        trimmed = line.strip()
        lower = trimmed.lower()
        if not lower.endswith((".csproj", ".fsproj")):
            continue
        existing.add((solution_path.parent / trimmed).resolve())

    return existing


def add_projects_to_solution(
    solution_path: Path,
    projects: list[Path],
    solution_folder: str,
) -> tuple[int, int]:
    existing = list_solution_projects(solution_path)
    added = 0
    skipped = 0

    for project in projects:
        if project in existing:
            print(f"Already in solution: {project}")
            skipped += 1
            continue

        command = [
            "dotnet",
            "sln",
            str(solution_path),
            "add",
            str(project),
            "--solution-folder",
            solution_folder,
        ]
        run_command(command, cwd=solution_path.parent)
        existing = list_solution_projects(solution_path)
        added += 1

    return added, skipped


def parse_solution_projects(solution_path: Path) -> list[SolutionProject]:
    projects: list[SolutionProject] = []
    text = solution_path.read_text(encoding="utf-8-sig", errors="replace")
    for line in text.splitlines():
        match = SLN_PROJECT_RE.match(line.strip())
        if not match:
            continue

        relative = match.group("path").replace("\\", os.sep)
        project_path = (solution_path.parent / relative).resolve()
        projects.append(
            SolutionProject(
                name=match.group("name"),
                guid=match.group("guid").upper(),
                path=project_path,
            )
        )
    return projects


def xml_namespace(root: ET.Element) -> str:
    if root.tag.startswith("{"):
        return root.tag[1:].split("}", 1)[0]
    return ""


def qname(ns: str, local: str) -> str:
    return f"{{{ns}}}{local}" if ns else local


def iter_children_by_local_name(root: ET.Element, local: str) -> Iterable[ET.Element]:
    suffix = "}" + local
    for element in root.iter():
        if element.tag == local or element.tag.endswith(suffix):
            yield element


def normalize_project_path(path: Path) -> str:
    return str(path.resolve()).replace("/", "\\").lower()


def resolve_msbuild_path(raw: str, project_path: Path, repo_root: Path, solution_path: Path) -> Path | None:
    value = raw.strip()
    if not value:
        return None

    replacements = {
        "$(XmakeProjectDir)": str(repo_root),
        "$(SolutionDir)": str(solution_path.parent) + os.sep,
        "$(ProjectDir)": str(project_path.parent) + os.sep,
    }
    for macro, replacement in replacements.items():
        value = value.replace(macro, replacement)

    # Leave unknown macros unresolved. They are not useful for source scanning.
    if "$(" in value:
        return None

    value = value.replace("\\", os.sep)
    path = Path(value)
    if not path.is_absolute():
        path = project_path.parent / path
    return path.resolve()


def read_xml(path: Path) -> tuple[ET.ElementTree, ET.Element, str]:
    tree = ET.parse(path)
    root = tree.getroot()
    ns = xml_namespace(root)
    if ns:
        ET.register_namespace("", ns)
    return tree, root, ns


def collect_project_sources(
    project: SolutionProject,
    repo_root: Path,
    solution_path: Path,
    include_none_module_items: bool,
) -> set[Path]:
    if not project.path.is_file() or project.path.suffix.lower() != ".vcxproj":
        return set()

    _, root, _ = read_xml(project.path)
    sources: set[Path] = set()

    item_kinds = ["ClCompile"]
    if include_none_module_items:
        item_kinds.append("None")

    for local_name in item_kinds:
        for item in iter_children_by_local_name(root, local_name):
            raw = item.attrib.get("Include")
            if not raw:
                continue

            source = resolve_msbuild_path(raw, project.path, repo_root, solution_path)
            if source is None:
                continue

            if source.suffix.lower() in CPP_SOURCE_EXTENSIONS and source.is_file():
                sources.add(source)

    return sources


def strip_line_comment(line: str) -> str:
    # Good enough for module declarations. Avoids matching // import foo;
    index = line.find("//")
    if index >= 0:
        return line[:index]
    return line


def scan_modules_in_file(path: Path) -> tuple[set[str], set[str]]:
    exports: set[str] = set()
    imports: set[str] = set()

    try:
        text = path.read_text(encoding="utf-8-sig", errors="replace")
    except OSError:
        return exports, imports

    in_block_comment = False
    for raw_line in text.splitlines():
        line = raw_line

        # Small block-comment stripper for declarations that are normally one-line.
        if in_block_comment:
            end = line.find("*/")
            if end < 0:
                continue
            line = line[end + 2 :]
            in_block_comment = False

        while True:
            start = line.find("/*")
            if start < 0:
                break
            end = line.find("*/", start + 2)
            if end < 0:
                line = line[:start]
                in_block_comment = True
                break
            line = line[:start] + line[end + 2 :]

        line = strip_line_comment(line)

        export_match = MODULE_DECL_RE.match(line)
        if export_match:
            exports.add(export_match.group("name"))
            continue

        import_match = IMPORT_RE.match(line)
        if import_match:
            imports.add(import_match.group("name"))

    return exports, imports


def collect_native_project_infos(
    solution_path: Path,
    repo_root: Path,
    include_none_module_items: bool,
) -> list[NativeProjectInfo]:
    infos: list[NativeProjectInfo] = []
    for project in parse_solution_projects(solution_path):
        if project.path.suffix.lower() != ".vcxproj":
            continue

        sources = collect_project_sources(
            project=project,
            repo_root=repo_root,
            solution_path=solution_path,
            include_none_module_items=include_none_module_items,
        )
        if not sources:
            infos.append(NativeProjectInfo(project=project))
            continue

        info = NativeProjectInfo(project=project, sources=sources)
        for source in sources:
            exports, imports = scan_modules_in_file(source)
            info.exported_modules.update(exports)
            info.imported_modules.update(imports)
        infos.append(info)
    return infos


def existing_project_reference_keys(root: ET.Element) -> set[str]:
    keys: set[str] = set()
    for reference in iter_children_by_local_name(root, "ProjectReference"):
        include = reference.attrib.get("Include")
        if include:
            keys.add(include.replace("/", "\\").lower())

        for child in list(reference):
            if child.tag.endswith("}Project") or child.tag == "Project":
                if child.text:
                    keys.add(child.text.strip().upper())
    return keys


def relative_msbuild_path(from_project: Path, to_project: Path) -> str:
    relative = os.path.relpath(str(to_project), start=str(from_project.parent))
    return relative.replace("/", "\\")


def ensure_project_reference(
    importer: SolutionProject,
    provider: SolutionProject,
    dry_run: bool,
) -> bool:
    tree, root, ns = read_xml(importer.path)
    keys = existing_project_reference_keys(root)
    relative = relative_msbuild_path(importer.path, provider.path)

    if relative.lower() in keys or provider.guid.upper() in keys:
        return False

    if dry_run:
        print(f"Would add ProjectReference: {importer.name} -> {provider.name} ({relative})")
        return True

    item_group = ET.Element(qname(ns, "ItemGroup"))
    reference = ET.SubElement(item_group, qname(ns, "ProjectReference"), {"Include": relative})
    project_guid = ET.SubElement(reference, qname(ns, "Project"))
    project_guid.text = provider.guid.upper()

    # For native C++ these are harmless metadata. They help VS treat it as a project dependency
    # without copying managed assemblies. LinkLibraryDependencies is understood by VC++ projects.
    reference_output_assembly = ET.SubElement(reference, qname(ns, "ReferenceOutputAssembly"))
    reference_output_assembly.text = "false"
    link_library_dependencies = ET.SubElement(reference, qname(ns, "LinkLibraryDependencies"))
    link_library_dependencies.text = "true"

    root.append(item_group)
    try_indent(tree)
    tree.write(importer.path, encoding="utf-8", xml_declaration=True)
    print(f"Added ProjectReference: {importer.name} -> {provider.name} ({relative})")
    return True


def ensure_module_items_are_clcompile(project: SolutionProject, dry_run: bool) -> int:
    """Fix older/buggy generators that emit .ixx/.cppm files as <None> items.

    This is optional because recent xmake already emits <ClCompile CompileAs=CompileAsCppModule>.
    """
    tree, root, ns = read_xml(project.path)
    changed = 0

    for item_group in list(root):
        if not (item_group.tag == qname(ns, "ItemGroup") or item_group.tag.endswith("}ItemGroup")):
            continue

        for item in list(item_group):
            if not (item.tag == qname(ns, "None") or item.tag.endswith("}None")):
                continue

            include = item.attrib.get("Include", "")
            if Path(include.replace("\\", "/")).suffix.lower() not in MODULE_INTERFACE_EXTENSIONS:
                continue

            if dry_run:
                print(f"Would convert <None> to <ClCompile CompileAs=CompileAsCppModule>: {project.name}: {include}")
                changed += 1
                continue

            item.tag = qname(ns, "ClCompile")
            has_compile_as = any(child.tag == qname(ns, "CompileAs") or child.tag.endswith("}CompileAs") for child in list(item))
            if not has_compile_as:
                compile_as = ET.SubElement(item, qname(ns, "CompileAs"))
                compile_as.text = "CompileAsCppModule"
            changed += 1

    if changed and not dry_run:
        try_indent(tree)
        tree.write(project.path, encoding="utf-8", xml_declaration=True)
        print(f"Fixed module item kinds in {project.name}: {changed}")

    return changed


def try_indent(tree: ET.ElementTree) -> None:
    try:
        ET.indent(tree, space="  ")
    except AttributeError:
        # Python < 3.9 fallback: no pretty-print. Script still works.
        pass


def patch_module_project_references(
    solution_path: Path,
    repo_root: Path,
    dry_run: bool,
    fix_none_module_items: bool,
) -> tuple[int, int, int]:
    infos = collect_native_project_infos(
        solution_path=solution_path,
        repo_root=repo_root,
        include_none_module_items=True,
    )

    if fix_none_module_items:
        fixed_items = sum(ensure_module_items_are_clcompile(info.project, dry_run=dry_run) for info in infos)
    else:
        fixed_items = 0

    providers: dict[str, NativeProjectInfo] = {}
    duplicate_modules: dict[str, list[str]] = {}
    for info in infos:
        for module_name in info.exported_modules:
            previous = providers.get(module_name)
            if previous is not None and previous.project.path != info.project.path:
                duplicate_modules.setdefault(module_name, [previous.project.name]).append(info.project.name)
                continue
            providers[module_name] = info

    for module_name, project_names in sorted(duplicate_modules.items()):
        joined = ", ".join(sorted(set(project_names)))
        print(f"Warning: module {module_name!r} has multiple providers: {joined}. Skipping this module.")
        providers.pop(module_name, None)

    added_or_would_add = 0
    missing_imports: set[str] = set()
    planned: dict[Path, set[Path]] = {}

    for importer in infos:
        for module_name in sorted(importer.imported_modules):
            provider = providers.get(module_name)
            if provider is None:
                missing_imports.add(module_name)
                continue
            if provider.project.path == importer.project.path:
                continue
            planned.setdefault(importer.project.path, set()).add(provider.project.path)

    projects_by_path = {info.project.path: info.project for info in infos}
    for importer_path, provider_paths in sorted(planned.items(), key=lambda item: str(item[0])):
        importer = projects_by_path[importer_path]
        for provider_path in sorted(provider_paths, key=str):
            provider = projects_by_path[provider_path]
            if ensure_project_reference(importer, provider, dry_run=dry_run):
                added_or_would_add += 1

    if missing_imports:
        interesting = sorted(name for name in missing_imports if name not in {"std", "std.compat"})
        if interesting:
            print("Warning: imports without provider project in solution:")
            for name in interesting:
                print(f"  - {name}")

    print(f"Native projects scanned: {len(infos)}")
    print(f"Module providers found: {len(providers)}")
    print(f"ProjectReferences added/would add: {added_or_would_add}")
    if fix_none_module_items:
        print(f"Module item kind fixes: {fixed_items}")

    return len(infos), len(providers), added_or_would_add


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Generate a vsxmake Visual Studio solution, add managed projects, and patch C++ module ProjectReferences."
    )
    parser.add_argument(
        "--repo-root",
        default=None,
        help="Repository root that contains xmake.lua. Default: auto-detect from cwd or script path",
    )
    parser.add_argument(
        "--solution-folder",
        default="Managed",
        help="Solution folder for managed projects. Default: Managed",
    )
    parser.add_argument(
        "--xmake-kind",
        default="vsxmake",
        help="Project kind passed to 'xmake project -k'. Default: vsxmake",
    )
    parser.add_argument(
        "--no-managed",
        action="store_true",
        help="Do not add .csproj/.fsproj projects to the generated solution.",
    )
    parser.add_argument(
        "--no-module-deps",
        action="store_true",
        help="Do not patch native C++ module ProjectReference dependencies.",
    )
    parser.add_argument(
        "--fix-none-module-items",
        action="store_true",
        help="Convert .ixx/.cppm/.mpp/.mxx <None> items in .vcxproj to <ClCompile CompileAs=CompileAsCppModule>.",
    )
    parser.add_argument(
        "--dry-run",
        action="store_true",
        help="Print what would be patched without modifying generated .vcxproj files.",
    )
    args = parser.parse_args()

    try:
        if shutil.which("xmake") is None:
            raise FileNotFoundError("xmake was not found in PATH")
        if not args.no_managed and shutil.which("dotnet") is None:
            raise FileNotFoundError("dotnet was not found in PATH")

        repo_root = find_repo_root(args.repo_root)
        print(f"Repo root: {repo_root}")

        before = snapshot_solution_times(repo_root)
        run_command(["xmake", "project", "-k", args.xmake_kind, "-y"], cwd=repo_root)

        solution_path = find_solution_path(repo_root, before)
        print(f"Solution: {solution_path}")

        if not args.no_module_deps:
            patch_module_project_references(
                solution_path=solution_path,
                repo_root=repo_root,
                dry_run=args.dry_run,
                fix_none_module_items=args.fix_none_module_items,
            )

        if not args.no_managed:
            managed_projects = collect_managed_projects(repo_root)
            if managed_projects:
                added, skipped = add_projects_to_solution(
                    solution_path=solution_path,
                    projects=managed_projects,
                    solution_folder=args.solution_folder,
                )
                print(f"Managed projects added: {added}, skipped: {skipped}")
            else:
                print("No managed projects found under src.")

        return 0

    except Exception as ex:
        print(f"Error: {ex}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
