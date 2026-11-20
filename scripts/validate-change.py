"""Plan and validate a committed Echoes change on the Windows development host."""
import argparse
import ast
from contextlib import contextmanager
from datetime import datetime, timezone
import errno
import hashlib
import json
import os
from pathlib import Path
import re
import stat
import subprocess
import sys
import time
from urllib.parse import unquote, urlsplit
import uuid
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
MODULE = "BepInEx/plugins/MirthwoodEchoes/Mirthwood.Echoes.dll"
BUNDLE = "app/bundle/"
RECEIPT = "mirthwood-echoes-validation-v1"
PRODUCT = {"module", "server", "launcher", "shared", "voices", "scripts", "tests"}
PROJECT_FILES = {"Directory.Build.props", "Directory.Build.targets"}


def git(*args):
    return subprocess.check_output(["git", *args], cwd=ROOT)


def revision(ref):
    return git("rev-parse", "--verify", "--end-of-options", ref + "^{commit}").decode().strip()


def committed_change(args):
    head = revision("HEAD")
    if args.initial:
        if len(git("rev-list", "--parents", "-n1", head).split()) != 1:
            raise ValueError("--initial is only for the first commit of a repository.")
        names = git("ls-tree", "-rz", "--name-only", head)
        base = None
    else:
        base = git("merge-base", revision(args.base or "origin/unstable"), head).decode().strip()
        names = git("diff", "--no-renames", "--name-only", "-z", base, head, "--")
    paths = [entry.decode("utf-8") for entry in names.split(b"\0") if entry]
    return base, head, paths


def plan(paths):
    groups = set()
    for name in paths:
        parts = name.split("/")
        if any(p in ("", ".", "..") for p in parts) or ":" in name:
            raise ValueError(f"Expected a repository-relative path: {name}")
        if parts[0] in PRODUCT or name in PROJECT_FILES:
            groups.add("package")
        elif (parts[0] == "docs" or parts[:2] == [".agents", "skills"]
              or parts[:2] == [".github", "workflows"] or name in {".gitattributes", ".gitignore"}
              or (len(parts) == 1 and name.endswith(".md"))):
            groups.add("policy")
        else:
            raise ValueError(f"Unclassified path: {name}. Define its checks before running validation.")
    if not groups:
        raise ValueError("There are no changed paths at this base and head.")
    return "package" if "package" in groups else "policy"


def assert_clean():
    if git("status", "--porcelain=v1", "-z", "--untracked-files=all"):
        raise ValueError("The source worktree has changes. Commit the intended files in the task worktree first.")


@contextmanager
def validation_lane():
    if os.name != "nt":
        raise ValueError("Run the Echoes gate in Windows on the development PC.")
    import msvcrt
    directory = Path(os.environ["LOCALAPPDATA"]) / "MirthwoodEchoes" / "Validation"
    directory.mkdir(parents=True, exist_ok=True)
    # All Echoes worktrees for this Windows account contend on the same byte.
    with (directory / "lane.lock").open("a+b") as lane:
        lane.seek(0)
        try:
            msvcrt.locking(lane.fileno(), msvcrt.LK_NBLCK, 1)
        except OSError as exc:
            if exc.errno in (errno.EACCES, errno.EAGAIN, errno.EDEADLK):
                raise ValueError("The Echoes validation lane is busy.") from exc
            raise
        try:
            yield
        finally:
            lane.seek(0)
            msvcrt.locking(lane.fileno(), msvcrt.LK_UNLCK, 1)


def sha256(path):
    with path.open("rb") as source:
        return hashlib.file_digest(source, "sha256").hexdigest()


def package_inventory(package):
    if not package.is_dir():
        raise ValueError(f"Missing server package: {package}")
    files = {}
    for directory, dirs, names in os.walk(package):
        for name in [*dirs, *names]:
            path = Path(directory) / name
            info = path.lstat()
            if stat.S_ISLNK(info.st_mode) or getattr(info, "st_file_attributes", 0) & stat.FILE_ATTRIBUTE_REPARSE_POINT:
                raise ValueError(f"Unexpected filesystem link in package: {path}")
        for name in names:
            path = Path(directory) / name
            files[path.relative_to(package).as_posix()] = sha256(path)
    return files


def check_bundle(package, inventory):
    manifest = json.loads((package / BUNDLE / "manifest.json").read_bytes())
    version = ET.parse(ROOT / "Directory.Build.props").findtext("PropertyGroup/Version")
    if (manifest["id"], manifest["name"], manifest["version"]) != ("mirthwood-echoes", "Mirthwood: Echoes", version):
        raise ValueError("Server bundle identity or version differs from the source.")
    rows = manifest["files"]
    declared = {row["path"]: row["sha256"].lower() for row in rows}
    if len({path.casefold() for path in declared}) != len(rows):
        raise ValueError("Duplicate Windows destination in the bundle manifest.")
    bundled = {key[len(BUNDLE):]: digest for key, digest in inventory.items()
               if key.startswith(BUNDLE) and key != BUNDLE + "manifest.json"}
    if declared != bundled or [row["path"] for row in rows if row["kind"] == "module"] != [MODULE]:
        raise ValueError("Bundle contents, module path or file hashes differ from the manifest.")
    compiled = ROOT / "module/bin/Release/net472/Mirthwood.Echoes.dll"
    if declared[MODULE] != sha256(compiled):
        raise ValueError("The server contains a different module from the one just built.")
    required = {"Update-Server.ps1", "install-service.sh", "Test-Game.ps1", "app/Mirthwood.Echoes.Server.dll",
                "app/web-control.py", "app/libpocketsphinx.so", "launcher/Mirthwood.Echoes.Play.exe"}
    if missing := required.difference(inventory):
        raise ValueError("Missing package files: " + ", ".join(sorted(missing)))
    return {"version": version, "moduleSha256": declared[MODULE], "files": inventory}


def check_sources(paths):
    powershell = []
    for relative in paths:
        path = ROOT / relative
        if not path.is_file():  # A deleted path still contributes its component to the plan.
            continue
        suffix = path.suffix.lower()
        if suffix == ".py":
            ast.parse(path.read_text(encoding="utf-8-sig"), filename=relative)
        elif suffix == ".json":
            json.loads(path.read_bytes())
        elif suffix in {".csproj", ".props", ".targets"}:
            ET.parse(path)
        elif suffix in {".cjs", ".js"}:
            subprocess.run(["node", "--check", str(path)], check=True)
        elif suffix == ".ps1":
            powershell.append(str(path))
        elif suffix == ".md":
            content = path.read_text(encoding="utf-8-sig")
            for match in re.finditer(r"\]\((?:<([^>]+)>|([^\s)]+))\)", content):
                url = urlsplit(match.group(1) or match.group(2))
                if not url.scheme and not url.netloc and url.path:
                    if not (path.parent / unquote(url.path)).exists():
                        raise ValueError(f"Broken local Markdown link in {relative}: {url.path}")
    if powershell:
        parser = """
        $ErrorActionPreference = 'Stop'
        foreach ($path in ([Console]::In.ReadToEnd() | ConvertFrom-Json)) {
            $errors = $null
            [void][Management.Automation.Language.Parser]::ParseFile($path, [ref]$null, [ref]$errors)
            if ($errors) { throw ($errors | Out-String) }
        }
        """
        subprocess.run(["pwsh", "-NoProfile", "-NonInteractive", "-Command", parser],
                       input=json.dumps(powershell), encoding="utf-8", check=True)


def run_gate(args, base, head, paths, tier):
    run_id = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ-") + uuid.uuid4().hex[:10]
    directory = ROOT / ".work" / "validation" / run_id
    directory.mkdir(parents=True)
    receipt = directory / "receipt.json"
    result = dict(schema=RECEIPT, profile="changed", runId=run_id, base=base, head=head,
                  tier=tier, paths=paths, checks=[], status="failed")
    started = time.monotonic()
    try:
        assert_clean()
        check_sources(paths)
        result["checks"].append("Changed source syntax, structured files and local Markdown links")
        if tier == "package" or args.foundation:
            package = directory / "Mirthwood-Echoes-Server"
            command = ["pwsh", "-NoProfile", "-NonInteractive", "-File", str(ROOT / "scripts/Build-Server.ps1"),
                       "-PackageDirectory", str(package)]
            if args.game_dir:
                command.extend(["-GameDir", args.game_dir])
            if args.loader_zip:
                command.extend(["-LoaderZip", args.loader_zip])
            subprocess.run(command, cwd=ROOT, check=True)
            result["package"] = check_bundle(package, package_inventory(package))
            result["checks"].append("Release server, companion, compiled module and complete package inventory")
            for project in ("tests/native_talk/NativeTalkProbe.csproj", "tests/provider_paste/ProviderPaste.csproj",
                            "tests/talk_model/TalkModel.csproj"):
                if (ROOT / project).is_file() and any(p.startswith(project.rsplit("/", 1)[0] + "/") for p in paths):
                    command = ["dotnet", "build", project, "-c", "Release", "--nologo"]
                    if args.game_dir:
                        command.append("-p:GameDir=" + args.game_dir)
                    subprocess.run(command, cwd=ROOT, check=True)
                    result["checks"].append("Compiled " + project)
            if args.foundation:
                subprocess.run(["wsl.exe", "-d", args.distro, "-u", "root", "--cd", str(ROOT),
                                "--exec", "env", "-u", "MIRTHWOOD_ECHOES_LIVE_CODEX_MODEL",
                                "-u", "MIRTHWOOD_ECHOES_LIVE_CODEX_REPORT", "python3", "-B",
                                "tests/foundation_smoke.py", package.relative_to(ROOT).as_posix(), ".work"],
                               cwd=ROOT, check=True)
                result["checks"].append("Disposable PostgreSQL, installation and local provider fixtures")
        assert_clean()
        if revision("HEAD") != head:
            raise ValueError("HEAD changed during validation.")
        result["status"] = "passed"
    except Exception as exc:
        result["error"] = str(exc)
        raise
    finally:
        result["durationSeconds"] = round(time.monotonic() - started, 2)
        receipt.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
        print(f"{result['status'].upper()} {run_id}  {receipt}", flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    modes = parser.add_subparsers(dest="mode", required=True)
    preview = modes.add_parser("plan")
    run = modes.add_parser("run")
    for mode in (preview, run):
        source = mode.add_mutually_exclusive_group()
        source.add_argument("--base", help="Review base ref (defaults to origin/unstable)")
        source.add_argument("--initial", action="store_true", help="Inspect a repository's root commit")
        if mode == preview:
            source.add_argument("--paths", nargs="+", help="Proposed repository-relative paths")
    run.add_argument("--game-dir", help="Licensed Steam Mirthwood directory")
    run.add_argument("--loader-zip", help="Verified BepInEx archive")
    run.add_argument("--foundation", action="store_true", help="Include the isolated WSL foundation suite")
    run.add_argument("--distro", default="ReignServer", help="Prepared WSL distro containing the Echoes prerequisites")
    modes.add_parser("status")
    verify = modes.add_parser("verify-package")
    verify.add_argument("receipt", type=Path)
    verify.add_argument("package", type=Path)
    args = parser.parse_args()
    if args.mode == "status":
        with validation_lane():
            print("Echoes validation lane available.")
        return
    if args.mode == "verify-package":
        report = json.loads(args.receipt.read_bytes())
        if report.get("schema") != RECEIPT or report.get("status") != "passed" or not report.get("package"):
            raise ValueError("A passing Echoes package receipt is required.")
        if package_inventory(args.package) != report["package"]["files"]:
            raise ValueError("Package inventory differs from the validated artifact.")
        print(f"Verified complete server package for {report['head']} ({report['runId']}).")
        return
    if args.mode == "plan" and args.paths:
        paths = list(dict.fromkeys(path.replace("\\", "/") for path in args.paths))
        base, head = None, None
    else:
        base, head, paths = committed_change(args)
    tier = plan(paths)
    if args.mode == "plan":
        print(json.dumps(dict(profile="changed", tier=tier, base=base, head=head, paths=paths), indent=2))
    else:
        with validation_lane():
            run_gate(args, base, head, paths, tier)


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, KeyError, SyntaxError, ET.ParseError, subprocess.CalledProcessError) as error:
        sys.exit(str(error))
