#!/usr/bin/env python3
"""Build a bounded, immutable tracked-source filesystem snapshot; never follow tree paths."""
from __future__ import annotations

import hashlib
from pathlib import Path
import re
import selectors
import os
import time
import subprocess

MAX_PATHS = 100_000
MAX_PATH_CHARS = 4096
MAX_PATH_BYTES = 16 * 1024 * 1024
MAX_TREE_BYTES = 32 * 1024 * 1024
MAX_METADATA_BYTES = 64 * 1024


def validate_path(path: str) -> None:
    # Stored paths are regular names, not directory queries. No normalization.
    if not path or len(path.encode("utf-16-le")) // 2 > MAX_PATH_CHARS:
        raise ValueError("Invalid snapshot path length.")
    if any(ord(c) < 32 or 127 <= ord(c) <= 159 or c in "\\\\:*?" for c in path):
        raise ValueError("Unsafe snapshot path.")
    if any(part in ("", ".", "..") or part.lower() == ".git" for part in path.split("/")):
        raise ValueError("Unsafe snapshot path component.")


def git(root: Path, *args: str) -> str:
    return bounded_output(["git", "--no-replace-objects", "-C", str(root), *args],
                          limit=MAX_METADATA_BYTES).decode("utf-8", errors="strict").strip()


def bounded_output(command: list[str], *, timeout: float = 30, limit: int = MAX_TREE_BYTES) -> bytes:
    # The deadline covers pipe reads as well as child exit; no communicate() buffer
    # or blocking read before timeout enforcement.
    deadline = time.monotonic() + timeout
    process = subprocess.Popen(command, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL)
    data = bytearray()
    try:
        with selectors.DefaultSelector() as selector:
            selector.register(process.stdout, selectors.EVENT_READ)
            while True:
                remaining = deadline - time.monotonic()
                if remaining <= 0 or not selector.select(remaining):
                    raise subprocess.TimeoutExpired(command, timeout)
                chunk = os.read(process.stdout.fileno(), min(8192, limit + 1 - len(data)))
                if not chunk:
                    break
                data.extend(chunk)
                if len(data) > limit:
                    raise ValueError("Snapshot tree metadata limit exceeded.")
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            raise subprocess.TimeoutExpired(command, timeout)
        if process.wait(timeout=remaining) != 0:
            raise ValueError("Cannot read source tree metadata.")
        return bytes(data)
    finally:
        if process.poll() is None:
            process.kill()
        process.wait()
        process.stdout.close()


def tree_records(root: Path, tree: str) -> bytes:
    return bounded_output(["git", "--no-replace-objects", "-C", str(root), "ls-tree", "-r", "-z", tree])


def artifact_digest(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(64 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def build_inventory(source: Path, *, source_revision: str, source_tree: str,
                    artifact: Path, artifact_sha256: str) -> dict:
    if not re.fullmatch(r"[0-9a-f]{40}", source_revision) or not re.fullmatch(r"[0-9a-f]{40}", source_tree):
        raise ValueError("Invalid source commit/tree binding.")
    if not re.fullmatch(r"[0-9a-f]{64}", artifact_sha256):
        raise ValueError("Invalid artifact hash binding.")
    # Do not follow a symlink root/ancestor even though only Git object metadata is read.
    absolute = source.absolute()
    if any(path.is_symlink() for path in (absolute, *absolute.parents)):
        raise ValueError("Symlink source root or ancestor is unsupported.")
    if Path(git(absolute, "rev-parse", "--show-toplevel")).resolve() != absolute.resolve():
        raise ValueError("Snapshot source must be the repository root.")
    def verify_source() -> None:
        if git(absolute, "rev-parse", "HEAD") != source_revision or git(absolute, "rev-parse", "HEAD^{tree}") != source_tree:
            raise ValueError("Snapshot source commit/tree mismatch.")
        if git(absolute, "status", "--porcelain", "--untracked-files=all"):
            raise ValueError("Snapshot source must be clean.")
    verify_source()
    if artifact_digest(artifact) != artifact_sha256:
        raise ValueError("Snapshot artifact hash mismatch.")
    data = tree_records(absolute, source_tree)
    if data and not data.endswith(b"\0"):
        raise ValueError("Truncated Git tree metadata.")
    files, blocked = [], []
    count = total = 0
    for record in data.split(b"\0"):
        if not record:
            continue
        metadata, raw_path = record.split(b"\t", 1)
        mode, kind, object_id = metadata.decode("ascii").split(" ")
        path = raw_path.decode("utf-8", errors="strict")
        validate_path(path)
        count += 1
        total += len(raw_path)
        if count > MAX_PATHS or total > MAX_PATH_BYTES:
            raise ValueError("Snapshot inventory size limit exceeded.")
        if mode in ("100644", "100755") and kind == "blob":
            files.append(path)
        elif (mode == "120000" and kind == "blob") or (mode == "160000" and kind == "commit"):
            blocked.append(path)
        else:
            raise ValueError("Unsupported Git tree entry mode.")
    verify_source()
    if artifact_digest(artifact) != artifact_sha256:
        raise ValueError("Snapshot artifact changed during inventory construction.")
    return {
        "schema": 1,
        "scope": "tracked-source-filesystem-snapshot",
        "source_revision": source_revision,
        "source_tree": source_tree,
        "artifact_sha256": artifact_sha256,
        "files": files,
        "blocked_paths": blocked,
    }
