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
from file_inventory import build_inventory

PROBE_DLL = ROOT / "tools" / "DmGodot.CompatibilityProbe" / "bin" / "Release" / "net10.0" / "DmGodot.CompatibilityProbe.dll"
TIMEOUT_SECONDS = 60


def capture_probe(command: list[str], timeout: float = TIMEOUT_SECONDS, expected_digest: str | None = None,
                  expected_inventory_hash: str | None = None) -> dict:
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
    if expected_digest is not None and report.get("artifact_sha256") != expected_digest:
        return {"measurement_status": "error", "runtime_status": "unknown", "reason": "artifact_hash_mismatch",
                "expected_artifact_sha256": expected_digest, "observed_artifact_sha256": report.get("artifact_sha256"),
                "probe_exit_code": result.returncode}
    if expected_inventory_hash is not None and report.get("file_inventory_sha256") != expected_inventory_hash:
        return {"measurement_status": "error", "runtime_status": "unknown", "reason": "file_inventory_hash_mismatch",
                "expected_file_inventory_sha256": expected_inventory_hash,
                "observed_file_inventory_sha256": report.get("file_inventory_sha256"), "probe_exit_code": result.returncode}
    report["measurement_status"] = "completed"
    report["probe_exit_code"] = result.returncode
    return report


def load_content_allowlist(path: Path, target: dict) -> tuple[dict, str]:
    with path.open("rb") as stream:
        raw = stream.read(128 * 1024 + 1)
    if len(raw) > 128 * 1024:
        raise ValueError("Content allowlist size limit exceeded.")
    data = json.loads(raw)
    if data.get("schema") != 1 or data.get("source_revision") != target["revision"] or data.get("source_repository") != target["repository"]:
        raise ValueError("Content allowlist source binding mismatch.")
    return data, hashlib.sha256(raw).hexdigest()


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--artifact", type=Path, default=ROOT / "build" / "civ13" / "civ13.json")
    parser.add_argument("--tracked-source-files", action="store_true",
                        help="Opt in to a pinned Git-tree file-existence snapshot (not live filesystem access).")
    parser.add_argument("--allowlisted-source-text", action="store_true",
                        help="Opt in to exact pinned public names configuration contents.")
    args = parser.parse_args()
    if args.allowlisted_source_text and not args.tracked_source_files:
        parser.error("--allowlisted-source-text requires --tracked-source-files")
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
        command = [find_dotnet(), str(PROBE_DLL), str(artifact)]
        snapshot = None
        manifest_hash = None
        if args.tracked_source_files:
            content_paths = None
            allowlist_hash = None
            if args.allowlisted_source_text:
                allowlist_path = ROOT / "compatibility" / "civ13-text-files.json"
                allowlist, allowlist_hash = load_content_allowlist(allowlist_path, target)
                content_paths = allowlist["paths"]
            snapshot = build_inventory(ROOT / "vendor" / "Civ13", source_revision=target["revision"],
                                       source_tree=provenance["source_tree"], artifact=artifact, artifact_sha256=digest,
                                       content_paths=content_paths, content_allowlist_sha256=allowlist_hash)
            manifest = artifact.parent / "file-inventory.json"
            manifest_bytes = (json.dumps(snapshot, ensure_ascii=True, separators=(",", ":")) + "\n").encode("utf-8")
            if len(manifest_bytes) > 32 * 1024 * 1024:
                raise ValueError("File inventory manifest exceeds size limit.")
            manifest.write_bytes(manifest_bytes)
            manifest_hash = hashlib.sha256(manifest_bytes).hexdigest()
            command += ["--file-inventory", str(manifest), "--source-revision", target["revision"],
                        "--source-tree", provenance["source_tree"]]
            if args.allowlisted_source_text:
                command += ["--content-allowlist", str(allowlist_path)]
        report = capture_probe(command, expected_digest=digest, expected_inventory_hash=manifest_hash)
        if snapshot is not None:
            report["file_inventory"] = {
                "scope": snapshot["scope"], "sha256": manifest_hash,
                "source_revision": snapshot["source_revision"], "source_tree": snapshot["source_tree"],
                "artifact_sha256": digest, "regular_files": len(snapshot["files"]),
                "blocked_paths": len(snapshot["blocked_paths"]),
                "content_files": len(snapshot.get("contents", [])),
                "content_raw_bytes": sum(entry["byte_length"] for entry in snapshot.get("contents", [])),
                "content_allowlist_sha256": snapshot.get("content_allowlist_sha256"),
            }
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
