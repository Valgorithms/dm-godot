#!/usr/bin/env python3
"""Synthetic map construction baseline; no Civ13 attribution or optimization claim."""
from __future__ import annotations

import json
import os
from pathlib import Path
import statistics
import subprocess
import sys

from bootstrap_opendream import find_dotnet

ROOT = Path(__file__).resolve().parents[1]
DLL = ROOT / "tools/DmGodot.MapBenchmark/bin/Release/net10.0/DmGodot.MapBenchmark.dll"
CASES = tuple((side, fields, rep) for side in (32, 64, 128)
              for fields in (0, 32, 128) for rep in (1, 2, 3))
METRICS = ("construction_elapsed_ms", "construction_process_managed_allocated_bytes",
           "retained_managed_delta_bytes_after_forced_gc",
           "public_exposure_elapsed_ms", "public_exposure_process_managed_allocated_bytes",
           "public_exposure_retained_delta_bytes_after_forced_gc")


def run_sample(side, fields, repetition, revision, *, timeout=60):
    command = [find_dotnet(), str(DLL), str(side), str(fields), str(repetition), revision]
    env = dict(os.environ, DOTNET_GCHeapHardLimit="0x80000000")
    try:
        child = subprocess.run(command, capture_output=True, text=True, timeout=timeout, env=env, cwd=ROOT)
        raw = {"command": command, "exit_code": child.returncode,
               "stdout": child.stdout, "stderr": child.stderr}
        try:
            report = json.loads(child.stdout)
            if not isinstance(report, dict):
                raise ValueError("Expected object report")
        except (ValueError, TypeError) as error:
            return {"status": "error", "reason": "invalid_child_report", "message": str(error), "raw": raw}
        report["raw"] = raw
        if child.returncode != 0 or report.get("status") != "completed":
            report["status"] = "error"
        elif (report.get("side"), report.get("extra_inherited_fields"), report.get("repetition"),
              report.get("engine_revision"), report.get("configured_gc_heap_hard_limit")) != (
                  side, fields, repetition, revision, "0x80000000"):
            report.update(status="error", reason="case_provenance_mismatch")
        return report
    except subprocess.TimeoutExpired as error:
        def decoded(value):
            return value.decode(errors="replace") if isinstance(value, bytes) else value
        return {"status": "error", "reason": "timeout", "timeout_seconds": timeout,
                "stdout": decoded(error.stdout), "stderr": decoded(error.stderr)}
    except OSError as error:
        return {"status": "error", "reason": "launch_failure", "message": str(error)}


def aggregate(samples):
    expected = set(CASES)
    keys = [(s.get("side"), s.get("extra_inherited_fields"), s.get("repetition")) for s in samples]
    if len(samples) != len(CASES) or len(set(keys)) != len(CASES) or set(keys) != expected:
        raise ValueError("All 27 unique case/repetition results are required")
    if any(s.get("status") != "completed" for s in samples):
        raise ValueError("Failed samples cannot produce a successful aggregate")
    for field in ("engine_revision", "compiler_revision", "dotnet", "os", "architecture",
                  "configured_gc_heap_hard_limit", "bytecode_version", "gc_server", "gc_latency_mode", "processor_count"):
        if len({s[field] for s in samples}) != 1:
            raise ValueError(f"Inconsistent provenance: {field}")
    groups = []
    for side in (32, 64, 128):
        for fields in (0, 32, 128):
            group = [s for s in samples if (s["side"], s["extra_inherited_fields"]) == (side, fields)]
            if len({s["artifact_sha256"] for s in group}) != 1:
                raise ValueError("Repeated case artifact hashes differ")
            item = {"side": side, "extra_inherited_fields": fields,
                    "artifact_sha256": group[0]["artifact_sha256"], "samples": 3}
            for metric in METRICS:
                values = [s[metric] for s in group]
                item[metric] = {"median": statistics.median(values), "min": min(values), "max": max(values)}
            groups.append(item)
    return {"schema": 1, "status": "completed", "sample_count": len(samples),
            "scope": "Synthetic construction baseline, not Civ13 attribution or an optimization claim.",
            "engine_revision": samples[0]["engine_revision"], "groups": groups}


def prepare_output(output):
    output.mkdir(parents=True, exist_ok=True)
    if any(output.iterdir()):
        raise ValueError(f"Refusing nonempty evidence directory: {output}; use a fresh checkout or archive the old directory")
    return output


def main():
    output = prepare_output(ROOT / "build/map-memory-benchmark")
    revision = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip()
    samples = []
    for side, fields, rep in CASES:
        report = run_sample(side, fields, rep, revision)
        (output / f"side-{side}-fields-{fields}-rep-{rep}.json").write_text(json.dumps(report, indent=2) + "\n")
        samples.append(report)
        print(json.dumps({k: report.get(k) for k in ("status", "side", "extra_inherited_fields",
                                                   "repetition", *METRICS)}), flush=True)
        if report.get("status") != "completed":
            break
    try:
        summary = aggregate(samples)
    except (ValueError, KeyError, TypeError) as error:
        summary = {"schema": 1, "status": "error", "message": str(error), "sample_count": len(samples)}
    (output / "summary.json").write_text(json.dumps(summary, indent=2) + "\n")
    return 0 if summary["status"] == "completed" else 1


if __name__ == "__main__":
    sys.exit(main())
