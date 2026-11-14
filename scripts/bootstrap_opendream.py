#!/usr/bin/env python3
"""Fetch and build the pinned upstream DM compiler without modifying its source."""

from __future__ import annotations

import argparse
import os
from pathlib import Path
import shutil
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
OPENDREAM = ROOT / "vendor" / "OpenDream"
OPENDREAM_URL = "https://github.com/OpenDreamProject/OpenDream.git"
OPENDREAM_REVISION = "846775a06b8fa4639772cb3e759adcc0133d9711"
ROBUST_REVISION = "af2a7d0406b410c4d960f0f4b34513651c6dc742"
COMPILER_DLL = OPENDREAM / "bin" / "DMCompiler" / "DMCompiler.dll"


def find_dotnet(explicit: str | None = None) -> str:
    candidates = [explicit, os.environ.get("DM_GODOT_DOTNET")]
    executable = "dotnet.exe" if os.name == "nt" else "dotnet"
    candidates.extend(str(base / ".tools" / "dotnet" / executable) for base in (ROOT, ROOT.parent))
    candidates.append(shutil.which("dotnet"))
    for candidate in candidates:
        if candidate and Path(candidate).is_file():
            return str(Path(candidate).resolve())
    raise RuntimeError(".NET 10 SDK was not found. Install it, or pass --dotnet /path/to/dotnet.")


def run(*args: str, cwd: Path = ROOT) -> None:
    subprocess.run(args, cwd=cwd, check=True)


def revision(repository: Path) -> str:
    return subprocess.check_output(["git", "-C", str(repository), "rev-parse", "HEAD"], text=True).strip()


def verify_source() -> None:
    for repository, expected in ((OPENDREAM, OPENDREAM_REVISION), (OPENDREAM / "RobustToolbox", ROBUST_REVISION)):
        if revision(repository) != expected:
            raise RuntimeError(f"{repository} does not match the pinned revision {expected}; refusing to replace an existing checkout.")
        result = subprocess.run(["git", "-C", str(repository), "diff", "--quiet", "HEAD", "--", "."], check=False)
        if result.returncode:
            raise RuntimeError(f"{repository} has tracked modifications; a clean pinned compiler source is required.")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dotnet", help="Path to the .NET 10 SDK's dotnet executable")
    args = parser.parse_args()
    dotnet = find_dotnet(args.dotnet)
    if not OPENDREAM.exists():
        OPENDREAM.parent.mkdir(parents=True, exist_ok=True)
        run("git", "clone", "--no-checkout", "--filter=blob:none", OPENDREAM_URL, str(OPENDREAM))
        run("git", "checkout", "--detach", OPENDREAM_REVISION, cwd=OPENDREAM)
    elif revision(OPENDREAM) != OPENDREAM_REVISION:
        raise RuntimeError("Existing OpenDream checkout differs from the pin. Keep it intact and resolve the version explicitly.")
    run("git", "submodule", "update", "--init", "--depth", "1", "RobustToolbox", cwd=OPENDREAM)
    verify_source()
    # Robust's optional source analyzers are not needed to build the compiler.
    # The compiler and its math dependency are built from unmodified source.
    run(dotnet, "build", str(OPENDREAM / "DMCompiler" / "DMCompiler.csproj"),
        "-c", "Release", "--nologo", "-v:minimal", "-m:1", "-p:SkipRobustAnalyzer=true")
    if not COMPILER_DLL.is_file():
        raise RuntimeError("Build returned success but DMCompiler.dll is missing.")
    print(f"Pinned compiler ready: {COMPILER_DLL}")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (RuntimeError, OSError, subprocess.CalledProcessError) as error:
        print(f"bootstrap: {error}", file=sys.stderr)
        sys.exit(1)
