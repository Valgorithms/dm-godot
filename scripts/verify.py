#!/usr/bin/env python3
"""Run the required executable-foundation checks; missing tools are failures."""
from __future__ import annotations

import os
from pathlib import Path
import shutil
import subprocess
import sys
import time

from bootstrap_opendream import ROOT, find_dotnet


def main() -> int:
    dotnet = find_dotnet()
    godot = os.environ.get("GODOT") or shutil.which("godot") or shutil.which("godot4")
    if not godot:
        raise RuntimeError("Godot is required. Set GODOT to its executable path.")
    env = dict(os.environ, DM_GODOT_DOTNET=dotnet, GODOT=godot)
    commands = [
        [sys.executable, "scripts/bootstrap_opendream.py", "--dotnet", dotnet],
        [sys.executable, "scripts/compile.py", "fixtures/smoke/smoke.dme", "--output", "build/smoke.json"],
        [sys.executable, "scripts/compile.py", "tests/DmGodot.Runtime.Tests/Fixtures/semantics.dme", "--output", "build/semantics.json"],
        [sys.executable, "scripts/compile.py", "tests/DmGodot.Runtime.Tests/Fixtures/semantics.dme", "--output", "build/semantics-optimized.json", "--optimize"],
        [sys.executable, "scripts/compile.py", "tests/DmGodot.Runtime.Tests/Fixtures/map-new.dme", "--output", "build/map-new.json"],
        [sys.executable, "scripts/compile.py", "tests/DmGodot.Runtime.Tests/Fixtures/area-init.dme", "--output", "build/area-init.json"],
        [sys.executable, "scripts/compile.py", "tests/DmGodot.Runtime.Tests/Fixtures/area-init.dme", "--output", "build/area-init-optimized.json", "--optimize"],
        [dotnet, "build", "DmGodot.slnx", "-c", "Release", "-m:1", "-p:UseSharedCompilation=false", "--nologo"],
        [sys.executable, "fixtures/smoke/check_compiler.py", "-v"],
        [dotnet, "tests/DmGodot.Runtime.Tests/bin/Release/net10.0/DmGodot.Runtime.Tests.dll", "build/smoke.json", "build/semantics.json", "build/semantics-optimized.json", "build/map-new.json", "build/area-init.json", "build/area-init-optimized.json"],
        [godot, "--headless", "--path", "client", "--editor", "--quit"],
        [godot, "--headless", "--path", "client", "--script", "res://tests/protocol_test.gd"],
        [sys.executable, "-m", "unittest", "discover", "-s", "client/tests", "-p", "test_*.py", "-v"],
        [sys.executable, "-m", "unittest", "discover", "-s", "tests", "-p", "test_*.py", "-v"],
        [sys.executable, "scripts/check_end_to_end.py"],
    ]
    start = time.monotonic()
    for command in commands:
        print("RUN", " ".join(map(str, command)), flush=True)
        subprocess.run(command, cwd=ROOT, env=env, check=True, timeout=600)
    print(f"VERIFICATION_PASSED: {len(commands)} stages in {time.monotonic() - start:.1f}s")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (OSError, RuntimeError, subprocess.SubprocessError) as error:
        print(f"VERIFICATION_FAILED: {error}", file=sys.stderr)
        sys.exit(1)
