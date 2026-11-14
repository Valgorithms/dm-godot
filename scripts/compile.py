#!/usr/bin/env python3
"""Compile unchanged DM sources to the pinned OpenDream bytecode format.

The compiler receives a generated include wrapper, so its JSON output never
overwrites an output next to the original game's source. No source is rewritten.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile

from bootstrap_opendream import (
    COMPILER_DLL, OPENDREAM_REVISION, ROBUST_REVISION, ROOT, find_dotnet, verify_source,
)


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("project", type=Path, help="Existing .dme or .dm entry point")
    parser.add_argument("--output", type=Path, help="Output JSON (default: build/<project>.json)")
    parser.add_argument("--version", default="516.1688", help="DM_VERSION.DM_BUILD compatibility macros")
    parser.add_argument("--dotnet", help="Path to dotnet executable")
    parser.add_argument("--optimize", action="store_true", help="Enable upstream bytecode optimizations; bootstrap runtime coverage may be smaller")
    parser.add_argument("--define", action="append", default=[], metavar="NAME[=VALUE]", help="Additional upstream compiler macro (repeatable)")
    args = parser.parse_args()
    project = args.project.resolve(strict=True)
    if project.suffix.lower() not in (".dme", ".dm"):
        parser.error("project must be a .dme or .dm file")
    if not re.fullmatch(r"[0-9]+\.[0-9]+", args.version):
        parser.error("--version must have the form 516.1688")
    if any(character in project.as_posix() for character in ('"', '\n', '\r')):
        parser.error("project path cannot contain quotes or line breaks")
    output = (args.output or ROOT / "build" / f"{project.stem}.json").resolve()
    if output.suffix.lower() != ".json":
        parser.error("--output must end in .json")
    if output == project:
        parser.error("output must not overwrite the source project")
    dotnet = find_dotnet(args.dotnet)
    verify_source()
    if not COMPILER_DLL.is_file():
        raise RuntimeError("Pinned compiler is not built. Run python3 scripts/bootstrap_opendream.py first.")
    output.parent.mkdir(parents=True, exist_ok=True)
    descriptor, wrapper_name = tempfile.mkstemp(prefix=f".{output.stem}-", suffix=".dme", dir=output.parent)
    wrapper = Path(wrapper_name)
    compiled = wrapper.with_suffix(".json")
    try:
        with os.fdopen(descriptor, "w", encoding="utf-8") as stream:
            stream.write(f'#define FILE_DIR "{project.parent.as_posix()}"\n#include "{project.as_posix()}"\n')
        command = [dotnet, str(COMPILER_DLL), f"--version={args.version}"]
        if not args.optimize:
            command.append("--no-opts")
        for definition in args.define:
            command.extend(["--define", definition])
        command.append(str(wrapper))
        completed = subprocess.run(command, cwd=project.parent, text=True,
                                   stdout=subprocess.PIPE, stderr=subprocess.STDOUT, check=False)
        print(completed.stdout, end="")
        output.with_suffix(".compiler.log").write_text(completed.stdout, encoding="utf-8")
        if completed.returncode:
            print("Compilation failed; any previous output was left unchanged.", file=sys.stderr)
            return completed.returncode
        artifact = json.loads(compiled.read_text(encoding="utf-8"))
        if not all(key in artifact for key in ("Metadata", "Strings", "Types", "Procs")):
            raise RuntimeError("Compiler output is missing required OpenDream artifact fields.")
        # The wrapper and final artifact share a directory. Keep upstream resource
        # paths verbatim everywhere, including bytecode's resource string table.
        os.replace(compiled, output)
    finally:
        wrapper.unlink(missing_ok=True)
        compiled.unlink(missing_ok=True)
    provenance = {
        "schema": 1,
        "compiler": "OpenDream DMCompiler",
        "compiler_revision": OPENDREAM_REVISION,
        "robust_revision": ROBUST_REVISION,
        "compiler_dll_sha256": sha256(COMPILER_DLL),
        "dm_version": args.version,
        "optimized": args.optimize,
        "defines": args.define,
        "source_project": str(project),
        "source_project_sha256": sha256(project),
        "source_root": str(project.parent),
        "artifact_sha256": sha256(output),
        "bytecode_version": artifact["Metadata"]["Version"],
        "note": "Source-project hash covers the entry file only, not all includes or resources. Compilation is not a runtime compatibility claim.",
    }
    output.with_suffix(".provenance.json").write_text(json.dumps(provenance, indent=2) + "\n", encoding="utf-8")
    print(f"DM artifact: {output}")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (RuntimeError, OSError, subprocess.CalledProcessError, json.JSONDecodeError) as error:
        print(f"compile: {error}", file=sys.stderr)
        sys.exit(1)
