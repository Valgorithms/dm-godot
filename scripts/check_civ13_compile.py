#!/usr/bin/env python3
"""Reproduce pinned Civ13 frontend compilation on a disposable checkout."""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import re
import subprocess
import sys
import time

from bootstrap_opendream import ROOT


def git(source: Path, *arguments: str) -> str:
    return subprocess.check_output(["git", "-C", str(source), *arguments], text=True).strip()


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path, help="Complete disposable Civ13 checkout")
    parser.add_argument("--output-dir", type=Path, default=ROOT / "build" / "civ13")
    args = parser.parse_args()
    source = args.source.resolve(strict=True)
    output_dir = args.output_dir.resolve()
    if output_dir == source or source in output_dir.parents:
        parser.error("Output must be outside the maintained source checkout.")
    targets = json.loads((ROOT / "compatibility" / "targets.json").read_text())
    target = next(item for item in targets["targets"] if item["id"] == "civ13")
    actual_revision = git(source, "rev-parse", "HEAD")
    if actual_revision != target["revision"]:
        raise RuntimeError(f"Civ13 revision mismatch: {actual_revision}")
    if git(source, "status", "--porcelain", "--untracked-files=all"):
        raise RuntimeError("Civ13 checkout must be clean before compilation.")
    output_dir.mkdir(parents=True, exist_ok=True)
    artifact = output_dir / "civ13.json"
    # Never allow a stale artifact to masquerade as the current compiler result.
    if artifact.exists():
        raise RuntimeError("Use a fresh output directory; civ13.json already exists.")
    command = [
        sys.executable, str(ROOT / "scripts" / "compile.py"),
        str(source / target["entrypoint"]), "--output", str(artifact),
        "--version", target["byond_compatibility_version"],
    ]
    started = time.monotonic()
    result = subprocess.run(command, cwd=ROOT, check=False, timeout=600)
    source_status = git(source, "status", "--porcelain", "--untracked-files=all")
    report = {
        "schema": 1,
        "scope": "Frontend compilation only; no runtime, gameplay or persistence acceptance.",
        "source_repository": target["repository"],
        "source_revision": actual_revision,
        "source_tree": git(source, "rev-parse", "HEAD^{tree}"),
        "source_clean_after": not source_status,
        "source_status_after": source_status,
        "compile_exit_code": result.returncode,
        "elapsed_seconds": round(time.monotonic() - started, 3),
        "dm_version": target["byond_compatibility_version"],
        "optimized": False,
    }
    compiler_log = artifact.with_suffix(".compiler.log")
    if compiler_log.exists():
        log = compiler_log.read_text(encoding="utf-8")
        match = re.search(r"Compilation succeeded with (\d+) warnings", log)
        report["warning_count"] = int(match[1]) if match else None
    if result.returncode == 0:
        data = json.loads(artifact.read_text(encoding="utf-8"))
        report["artifact_sha256"] = hashlib.sha256(artifact.read_bytes()).hexdigest()
        report["artifact_bytes"] = artifact.stat().st_size
        report["counts"] = {key: len(data.get(key, [])) for key in ("Types", "Procs", "Strings", "Maps")}
        report["compiler_provenance"] = json.loads(artifact.with_suffix(".provenance.json").read_text())
    report_path = output_dir / "reproduction.json"
    report_path.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(report, indent=2), flush=True)
    return result.returncode if result.returncode else (0 if not source_status else 1)


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (OSError, RuntimeError, subprocess.SubprocessError, ValueError) as error:
        print(f"Civ13 reproduction failed: {error}", file=sys.stderr)
        sys.exit(1)
