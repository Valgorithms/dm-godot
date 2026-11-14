#!/usr/bin/env python3
"""Record a bounded Civ13 loader/Boot probe without starting a game host."""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import sys

from bootstrap_opendream import ROOT, find_dotnet

PROBE_DLL = ROOT / "tools" / "DmGodot.CompatibilityProbe" / "bin" / "Release" / "net10.0" / "DmGodot.CompatibilityProbe.dll"
TIMEOUT_SECONDS = 60


def capture_probe(command: list[str], timeout: float = TIMEOUT_SECONDS) -> dict:
    try:
        result = subprocess.run(command, cwd=ROOT, capture_output=True, text=True, timeout=timeout, check=False)
    except subprocess.TimeoutExpired:
        return {"measurement_status": "error", "reason": "timeout", "runtime_status": "unknown"}
    except OSError as error:
        return {"measurement_status": "error", "reason": "launch_failed", "message": str(error), "runtime_status": "unknown"}
    try:
        report = json.loads(result.stdout)
        if not isinstance(report, dict) or report.get("schema") != 1:
            raise ValueError("Invalid probe report schema.")
        expected = {0: "boot_returned", 3: "blocked"}.get(result.returncode)
        if expected is None or report.get("runtime_status") != expected:
            raise ValueError("Unexpected probe exit/status.")
        if report.get("phase") not in ("artifact_load", "runtime_construct", "boot"):
            raise ValueError("Invalid probe phase.")
        if expected == "blocked" and report.get("exception_type") != "DmGodot.Runtime.CompatibilityException":
            raise ValueError("Blocked result must identify CompatibilityException.")
        if expected == "boot_returned" and (report.get("phase") != "boot" or report.get("exception_type") is not None):
            raise ValueError("Invalid boot-returned result.")
    except (ValueError, TypeError) as error:
        return {"measurement_status": "error", "reason": str(error), "runtime_status": "unknown",
                "probe_exit_code": result.returncode, "stdout_tail": result.stdout[-4000:], "stderr_tail": result.stderr[-4000:]}
    report["measurement_status"] = "completed"
    report["probe_exit_code"] = result.returncode
    return report


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--artifact", type=Path, default=ROOT / "build" / "civ13" / "civ13.json")
    args = parser.parse_args()
    artifact = args.artifact.resolve()
    output = artifact.parent / "runtime-probe.json"
    try:
        provenance = json.loads((artifact.parent / "reproduction.json").read_text())
        target = next(item for item in json.loads((ROOT / "compatibility" / "targets.json").read_text())["targets"] if item["id"] == "civ13")
        digest = hashlib.sha256(artifact.read_bytes()).hexdigest()
        if provenance.get("source_revision") != target["revision"] or provenance.get("compile_exit_code") != 0 or provenance.get("source_clean_after") is not True:
            raise ValueError("Expected a successful clean pinned Civ13 compilation.")
        if digest != provenance.get("artifact_sha256"):
            raise ValueError("Artifact differs from compilation provenance.")
        report = capture_probe([find_dotnet(), str(PROBE_DLL), str(artifact)])
        report.update({
            "engine_revision": subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip(),
            "target_revision": target["revision"], "artifact_sha256": digest,
            "wall_timeout_seconds": TIMEOUT_SECONDS,
            "scope": "Loader/Boot measurement only. Completed means the probe ran; blocked is not runtime support. Boot returning does not establish completed startup, gameplay, networking or persistence.",
        })
    except (OSError, ValueError, RuntimeError, subprocess.SubprocessError) as error:
        report = {"schema": 1, "measurement_status": "error", "runtime_status": "unknown", "reason": str(error)}
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(report, indent=2), flush=True)
    return 0 if report["measurement_status"] == "completed" else 1


if __name__ == "__main__":
    sys.exit(main())
