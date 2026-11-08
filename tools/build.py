#!/usr/bin/env python3
"""Compile src/*.cs into GameData/RealWindows/Plugins/RealWindows.dll.

    python3 tools/build.py [--ksp PATH] [--csc PATH]

Needs a KSP 1.12 install (its Managed assemblies are the only references, the
base class libraries included) and a C# compiler: the .NET SDK's Roslyn
(found through `dotnet`), or a `csc` on PATH (Mono). Python 3.8+, standard
library only; runs on Windows, macOS and Linux.

Where the KSP install comes from, first found wins:
  --ksp PATH, the KSPDIR environment variable, then every Steam library
  (Steam's libraryfolders.vdf) holding "Kerbal Space Program".
The compiler: --csc PATH (csc.dll or a csc executable), the CSC environment
variable, the newest .NET SDK, then csc on PATH.
"""

import argparse
import os
import re
import shutil
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "GameData" / "RealWindows" / "Plugins" / "RealWindows.dll"
RSP = ROOT / "tools" / "build.rsp"
GAME_FOLDER = "Kerbal Space Program"

# Where the managed assemblies sit inside a KSP install, per platform.
MANAGED = [
    "KSP_x64_Data/Managed",                       # Windows
    "KSP_Data/Managed",                           # Linux
    "KSP.app/Contents/Resources/Data/Managed",    # macOS
    "Contents/Resources/Data/Managed",            # macOS, --ksp pointing at KSP.app
]


def fail(msg):
    print("BUILD FAILED: " + msg)
    sys.exit(1)


# ---------------------------------------------------------------- KSP

def steam_roots():
    roots = []
    if sys.platform == "win32":
        try:
            import winreg
            for hive in (winreg.HKEY_CURRENT_USER, winreg.HKEY_LOCAL_MACHINE):
                for key in (r"Software\Valve\Steam", r"Software\WOW6432Node\Valve\Steam"):
                    try:
                        with winreg.OpenKey(hive, key) as k:
                            for name in ("SteamPath", "InstallPath"):
                                try:
                                    roots.append(Path(winreg.QueryValueEx(k, name)[0]))
                                except OSError:
                                    pass
                    except OSError:
                        pass
        except ImportError:
            pass
        roots.append(Path(os.environ.get("ProgramFiles(x86)", r"C:\Program Files (x86)")) / "Steam")
    elif sys.platform == "darwin":
        roots.append(Path.home() / "Library/Application Support/Steam")
    else:
        roots += [Path.home() / ".steam/steam", Path.home() / ".local/share/Steam",
                  Path.home() / ".var/app/com.valvesoftware.Steam/.local/share/Steam"]
    return roots


def steam_libraries():
    libs = []
    for root in steam_roots():
        if not root.is_dir():
            continue
        libs.append(root)
        vdf = root / "steamapps" / "libraryfolders.vdf"
        if vdf.is_file():
            text = vdf.read_text(encoding="utf-8", errors="replace")
            # new format: "path" "D:\\SteamLibrary"; old format: "1" "D:\\SteamLibrary"
            for m in re.finditer(r'"(?:path|\d+)"\s+"([^"]+)"', text):
                libs.append(Path(m.group(1).replace("\\\\", "\\")))
    seen, out = set(), []
    for lib in libs:
        key = str(lib).lower()
        if key not in seen:
            seen.add(key)
            out.append(lib)
    return out


def managed_dir(ksp):
    for rel in MANAGED:
        d = Path(ksp) / rel
        if (d / "Assembly-CSharp.dll").is_file():
            return d
    return None


def find_ksp(arg):
    if arg:
        if managed_dir(arg):
            return Path(arg)
        fail("no KSP Managed folder under --ksp " + arg)
    env = os.environ.get("KSPDIR")
    if env:
        if managed_dir(env):
            return Path(env)
        print("note: KSPDIR=%s has no KSP Managed folder; looking in the Steam libraries" % env)
    tried = []
    for lib in steam_libraries():
        cand = lib / "steamapps" / "common" / GAME_FOLDER
        tried.append(str(cand))
        if managed_dir(cand):
            return cand
    fail("KSP install not found (looked in: %s). Pass --ksp PATH or set KSPDIR."
         % (", ".join(tried) or "no Steam libraries found"))


# ---------------------------------------------------------------- compiler

def version_key(v):
    return [int(x) if x.isdigit() else 0 for x in re.split(r"[.\-]", v)]


def compiler_command(path):
    p = Path(path)
    if p.suffix.lower() == ".dll":
        dotnet = shutil.which("dotnet")
        if not dotnet:
            fail("csc.dll needs `dotnet` on PATH: " + path)
        return [dotnet, "exec", str(p)]
    return [str(p)]


def find_compiler(arg):
    for cand in [arg, os.environ.get("CSC")]:
        if cand:
            if not Path(cand).exists() and not shutil.which(cand):
                fail("compiler not found: " + cand)
            return compiler_command(shutil.which(cand) or cand)
    dotnet = shutil.which("dotnet")
    if dotnet:
        try:
            out = subprocess.run([dotnet, "--list-sdks"], capture_output=True, text=True).stdout
        except OSError:
            out = ""
        sdks = []
        for line in out.splitlines():
            m = re.match(r"\s*(\S+)\s+\[(.+)\]", line)
            if m:
                csc = Path(m.group(2)) / m.group(1) / "Roslyn" / "bincore" / "csc.dll"
                if csc.is_file():
                    sdks.append((version_key(m.group(1)), csc))
        if sdks:
            sdks.sort()
            return [dotnet, "exec", str(sdks[-1][1])]
    for name in ("csc", "csc.exe"):
        found = shutil.which(name)
        if found:
            return [found]
    fail("no C# compiler: install the .NET SDK (https://dotnet.microsoft.com) or Mono, or pass --csc")


# ---------------------------------------------------------------- build

def main():
    ap = argparse.ArgumentParser(description="Build RealWindows.dll")
    ap.add_argument("--ksp", help="KSP install folder (default: KSPDIR, then the Steam libraries)")
    ap.add_argument("--csc", help="csc.dll or a csc executable (default: CSC, then the newest .NET SDK)")
    a = ap.parse_args()

    ksp = find_ksp(a.ksp)
    managed = managed_dir(ksp)
    compiler = find_compiler(a.csc)
    sources = sorted((ROOT / "src").glob("*.cs"))
    if not sources:
        fail("no sources in src/")
    # Every managed assembly of the game, its own base class libraries included:
    # the code is compiled against exactly what it runs on.
    refs = sorted(managed.glob("*.dll"))

    OUT.parent.mkdir(parents=True, exist_ok=True)
    opts = ["/nologo", "/target:library", "/langversion:7.3", "/nostdlib+",
            "/nowarn:CS0612,CS0618,CS1701,CS1702", "/optimize+", "/debug-", "/out:" + str(OUT)]
    lines = ['"%s"' % o for o in opts]
    lines += ['"/reference:%s"' % r for r in refs]
    lines += ['"%s"' % s for s in sources]
    RSP.write_text("\n".join(lines) + "\n", encoding="ascii")

    print("KSP:      " + str(ksp))
    print("compiler: " + " ".join(compiler))
    res = subprocess.run(compiler + ["@" + str(RSP)], capture_output=True, text=True)
    output = (res.stdout + res.stderr).strip().splitlines()
    for line in output[:80]:
        print(line)
    if res.returncode != 0:
        fail("compiler exit %d" % res.returncode)
    print("BUILD OK: %s (%d bytes)" % (OUT, OUT.stat().st_size))


if __name__ == "__main__":
    main()
