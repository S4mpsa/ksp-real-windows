#!/usr/bin/env python3
"""Build GameData/RealWindows/Shaders/realwindows-<platform>.shaders
(windows, osx, linux) with Unity in batch mode (no editor window).

    python3 tools/build_shaders.py [--unity PATH] [--targets windows,osx,linux]

Needs the exact Unity version of KSP 1.12 (2019.4.18f1, as recorded in
unity/ProjectSettings/ProjectVersion.txt; shaders from other versions may not
load in KSP), with the Mac and Linux Build Support modules for those targets.
Python 3.8+, standard library only; runs on Windows, macOS and Linux.

Where Unity comes from, first found wins: --unity PATH, the UNITY_EDITOR
environment variable, then Unity Hub's install folders (the default one and
the one set in the Hub's preferences) for the project's version.

Unity sometimes stays running after the build has finished (its licensing
client); the build is judged from its log, and Unity is stopped once the log
says it is done.
"""

import argparse
import json
import os
import re
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
PROJECT = ROOT / "unity"
OUT_DIR = ROOT / "GameData" / "RealWindows" / "Shaders"
LOG = ROOT / "tools" / "build-shaders.log"
DONE_OK = "Exiting batchmode successfully"
DONE_FAIL = ("Aborting batchmode due to failure", "Exiting without the bug reporter")
SHOW = re.compile(r"\[RealWindows\]|error CS|Shader error|Compiling shader")


def fail(msg, code=1):
    print("SHADER BUILD FAILED: " + msg)
    sys.exit(code)


def project_version():
    pv = PROJECT / "ProjectSettings" / "ProjectVersion.txt"
    m = re.search(r"m_EditorVersion:\s*(\S+)", pv.read_text(encoding="utf-8", errors="replace"))
    if not m:
        fail("no m_EditorVersion in " + str(pv))
    return m.group(1)


def hub_secondary_path():
    if sys.platform == "win32":
        cfg = Path(os.environ.get("APPDATA", "")) / "UnityHub" / "secondaryInstallPath.json"
    elif sys.platform == "darwin":
        cfg = Path.home() / "Library/Application Support/UnityHub/secondaryInstallPath.json"
    else:
        cfg = Path.home() / ".config/UnityHub/secondaryInstallPath.json"
    try:
        p = json.loads(cfg.read_text(encoding="utf-8"))
        return Path(p) if isinstance(p, str) and p else None
    except (OSError, ValueError):
        return None


def editor_in(folder):
    """The Unity executable inside a version folder of a Hub install."""
    if sys.platform == "win32":
        return folder / "Editor" / "Unity.exe"
    if sys.platform == "darwin":
        return folder / "Unity.app" / "Contents" / "MacOS" / "Unity"
    return folder / "Editor" / "Unity"


def find_unity(arg, version):
    for cand in [arg, os.environ.get("UNITY_EDITOR")]:
        if cand:
            if Path(cand).is_file():
                return Path(cand)
            fail("Unity not found: " + cand)
    roots = []
    secondary = hub_secondary_path()
    if secondary:
        roots.append(secondary)
    if sys.platform == "win32":
        pf = Path(os.environ.get("ProgramFiles", r"C:\Program Files"))
        roots += [pf / "Unity" / "Hub" / "Editor"]
        legacy = pf / ("Unity " + version) / "Editor" / "Unity.exe"
    elif sys.platform == "darwin":
        roots += [Path("/Applications/Unity/Hub/Editor")]
        legacy = Path("/Applications/Unity/Unity.app/Contents/MacOS/Unity")
    else:
        roots += [Path.home() / "Unity" / "Hub" / "Editor"]
        legacy = None
    tried = []
    for r in roots:
        exe = editor_in(r / version)
        tried.append(str(exe))
        if exe.is_file():
            return exe
    if legacy is not None:
        tried.append(str(legacy))
        if legacy.is_file():
            return legacy
    fail("Unity %s not found (looked for: %s). Install it with Unity Hub, or pass --unity PATH "
         "or set UNITY_EDITOR." % (version, ", ".join(tried)))


def read_log():
    try:
        return LOG.read_text(encoding="utf-8", errors="replace")
    except OSError:
        return ""


def main():
    ap = argparse.ArgumentParser(description="Build the Real Windows shader bundles")
    ap.add_argument("--unity", help="the Unity editor executable (default: UNITY_EDITOR, then Unity Hub's folders)")
    ap.add_argument("--targets", default="windows,osx,linux", help="comma-separated: windows, osx, linux")
    ap.add_argument("--timeout", type=int, default=1800, help="seconds before giving up (the first build imports the project)")
    a = ap.parse_args()

    version = project_version()
    unity = find_unity(a.unity, version)
    targets = [t.strip().lower() for t in a.targets.split(",") if t.strip()]
    OUT_DIR.mkdir(parents=True, exist_ok=True)
    if LOG.exists():
        LOG.unlink()

    print("Unity %s: %s" % (version, unity))
    print("building shader bundles (%s) -> %s" % (",".join(targets), OUT_DIR))
    start = time.time()
    proc = subprocess.Popen(
        [str(unity), "-batchmode", "-quit", "-nographics",
         "-projectPath", str(PROJECT),
         "-executeMethod", "BuildRealWindowsBundle.BuildFromCommandLine",
         "-rwOut", str(OUT_DIR),
         "-rwTargets", ",".join(targets),
         "-logFile", str(LOG)],
        stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)

    # wait for Unity, or for its log to say it is done (it may linger after that)
    finished_at = None
    code = None
    while True:
        code = proc.poll()
        if code is not None:
            break
        log = read_log()
        if finished_at is None and (DONE_OK in log or any(f in log for f in DONE_FAIL)):
            finished_at = time.time()
        if finished_at is not None and time.time() - finished_at > 30:
            print("Unity is still running 30 s after finishing; stopping it")
            proc.kill()
            proc.wait()
            break
        if time.time() - start > a.timeout:
            proc.kill()
            proc.wait()
            fail("timed out after %d s - full log: %s" % (a.timeout, LOG))
        time.sleep(2)

    log = read_log()
    shown = [line for line in log.splitlines() if SHOW.search(line)]
    for line in shown[-40:]:
        print(line)

    built = []
    for t in targets:
        f = OUT_DIR / ("realwindows-%s.shaders" % t)
        if f.is_file() and f.stat().st_mtime >= start - 1:
            built.append(f)
    ok = DONE_OK in log and len(built) == len(targets)
    if not ok:
        missing = [t for t in targets if not any(b.name == "realwindows-%s.shaders" % t for b in built)]
        fail("exit %s, bundles not built: %s - full log: %s" % (code, ", ".join(missing) or "none", LOG))
    for f in built:
        print("  %s  %d bytes" % (f.name, f.stat().st_size))
    print("SHADER BUILD OK (%.0f s)" % (time.time() - start))


if __name__ == "__main__":
    main()
