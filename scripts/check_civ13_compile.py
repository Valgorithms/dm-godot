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


def map_override_inventory(data: dict, limit: int = 256) -> dict:
    """Describe only serialized map instances, without constructing runtime objects."""
    if limit < 0:
        raise ValueError("Inventory limit must be nonnegative.")
    entries = {}
    fields = {}
    override_count = 0
    types = data.get("Types", [])

    def category(value):
        if value is None:
            return "null"
        if isinstance(value, bool):
            return "boolean"
        if isinstance(value, (int, float)):
            return "number"
        if isinstance(value, str):
            return "string"
        if isinstance(value, list):
            return "array"
        if isinstance(value, dict):
            kind = value.get("type")
            return f"compiler_tag_{kind}" if type(kind) is int else "object"
        return "unknown"

    for map_index, map_data in enumerate(data.get("Maps", [])):
        definitions = map_data.get("CellDefinitions", {})
        for block_index, block in enumerate(map_data.get("Blocks", [])):
            for cell_index, cell_key in enumerate(block.get("Cells", [])):
                cell = definitions[cell_key]
                for entity_index, entity in enumerate(cell.get("Objects", [])):
                    overrides = entity.get("VarOverrides") or {}
                    if not overrides:
                        continue
                    override_count += 1
                    type_path = types[entity["Type"]]["Path"]
                    first = {"map": map_index, "block": block_index, "cell": cell_index, "entity": entity_index}
                    for field, value in sorted(overrides.items()):
                        value_category = category(value)
                        key = (type_path, field, value_category)
                        if key not in entries:
                            entries[key] = {"type": type_path, "field": field, "category": value_category,
                                            "count": 0, "first": first}
                        entries[key]["count"] += 1
                        if field not in fields:
                            fields[field] = {"field": field, "count": 0, "first_type": type_path, "first": first}
                        fields[field]["count"] += 1
    return {
        "scope": "Data-only inventory of overrides on USED map entity instances; no runtime support or memory-fit claim. Positions are zero-based serialized indexes; counts include repeated cell uses. Values are never included.",
        "limit": limit,
        "overridden_entity_instances": override_count,
        "distinct_type_field_categories": len(entries),
        "distinct_fields": len(fields),
        "entries_truncated": len(entries) > limit,
        "fields_truncated": len(fields) > limit,
        "entries": list(entries.values())[:limit],
        "fields": [fields[field] for field in sorted(fields)[:limit]],
    }


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
        report["map_override_inventory"] = map_override_inventory(data)
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
