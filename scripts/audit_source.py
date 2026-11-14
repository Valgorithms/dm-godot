#!/usr/bin/env python3
"""Inventory explicit DM engine dependencies; this is not a compatibility test.

Strings and comments are excluded from feature counts. DM interpolation inside
strings and macro expansion are intentionally not evaluated. Counts are lexical
occurrences, not resolved calls or a proof that a feature is reachable at runtime.
"""
from __future__ import annotations

import argparse
import bisect
import json
from pathlib import Path
import re
import sys


CALL_FEATURES = {
    "scheduling": ("sleep", "spawn"),
    "native": ("call_ext", "shell", "run"),
    "browser": ("browse", "browse_rsc", "winset", "winget", "winexists", "link"),
    "persistence": ("ImportText", "ExportText", "Read", "Write", "Flush"),
    "appearance": ("animate", "filter", "icon", "image", "mutable_appearance"),
    "world_runtime": ("view", "oview", "range", "orange", "locate", "get_step", "step", "walk"),
    "dynamic_dispatch": ("call", "arglist", "typesof", "ispath", "hascall"),
}
FIELD_FEATURES = {
    "appearance": ("plane", "layer", "render_source", "render_target", "appearance_flags", "filters", "overlays", "underlays", "vis_contents", "vis_locs", "screen_loc", "maptext"),
    "scheduling": ("tick_usage", "map_cpu", "tick_lag", "waitfor", "instant"),
}
TOKEN = re.compile(r"[A-Za-z_][A-Za-z_0-9]*|[^\s]")


def mask_non_code(source: str, *, keep_strings: bool = False) -> str:
    """Replace comments and optionally literals with spaces, retaining offsets.

    Recognizes nested block comments, line comments, escaped quoted literals,
    single-quoted resource literals, and DM's {\" ... \"} multiline strings.
    """
    chars = list(source)
    length = len(source)
    index = 0

    def erase(start: int, end: int) -> None:
        for position in range(start, end):
            if chars[position] not in "\r\n":
                chars[position] = " "

    while index < length:
        start = index
        if source.startswith("//", index):
            newline = source.find("\n", index)
            index = length if newline == -1 else newline
            erase(start, index)
        elif source.startswith("/*", index):
            index += 2
            depth = 1
            while index < length and depth:
                if source.startswith("/*", index):
                    depth += 1
                    index += 2
                elif source.startswith("*/", index):
                    depth -= 1
                    index += 2
                else:
                    index += 1
            erase(start, index)
        elif source.startswith('{"', index):
            index += 2
            while index < length:
                if source[index] == "\\":
                    index += 2
                elif source.startswith('"}', index):
                    index += 2
                    break
                else:
                    index += 1
            if not keep_strings:
                erase(start, min(index, length))
        elif source[index] in "\"'":
            quote = source[index]
            index += 1
            while index < length:
                if source[index] == "\\":
                    index += 2
                elif source[index] == quote:
                    index += 1
                    break
                else:
                    index += 1
            if not keep_strings:
                erase(start, min(index, length))
        else:
            index += 1
    return "".join(chars)


def inventory(root: Path, *, max_samples: int = 5) -> dict:
    root = root.resolve(strict=True)
    if not root.is_dir():
        raise ValueError("Source root must be a directory")
    if max_samples < 0:
        raise ValueError("max_samples must be nonnegative")
    source_files = sorted(path for path in root.rglob("*")
                          if path.suffix.lower() in {".dm", ".dme"}
                          and path.is_file() and ".git" not in path.parts)
    counts: dict[str, dict] = {}
    includes = []
    decoding_warnings = []
    line_count = 0
    call_lookup = {name: category for category, names in CALL_FEATURES.items() for name in names}
    field_lookup = {name: category for category, names in FIELD_FEATURES.items() for name in names}

    def record(category: str, feature: str, path: str, line: int) -> None:
        entry = counts.setdefault(category, {}).setdefault(feature, {"occurrences": 0, "samples": []})
        entry["occurrences"] += 1
        if len(entry["samples"]) < max_samples:
            entry["samples"].append({"file": path, "line": line})

    for path in source_files:
        relative = path.relative_to(root).as_posix()
        try:
            source = path.read_text(encoding="utf-8-sig")
        except UnicodeDecodeError:
            source = path.read_text(encoding="utf-8-sig", errors="replace")
            decoding_warnings.append(relative)
        line_count += len(source.splitlines())
        line_starts = [0] + [match.end() for match in re.finditer("\n", source)]
        code_only = mask_non_code(source)
        tokens = list(TOKEN.finditer(code_only))
        for index, token in enumerate(tokens):
            word = token.group()
            following = tokens[index + 1].group() if index + 1 < len(tokens) else None
            previous = tokens[index - 1].group() if index else None
            line = bisect.bisect_right(line_starts, token.start())
            if word in call_lookup and following == "(":
                record(call_lookup[word], word, relative, line)
            if word in field_lookup:
                record(field_lookup[word], word, relative, line)
            if word == "savefile" and previous == "/":
                record("persistence", "/savefile", relative, line)
        for directive in re.finditer(r'^[ \t]*#[ \t]*include\b', code_only, re.MULTILINE):
            # Locate directives in code, but read their quoted paths from the
            # original text. This excludes fake directives inside long strings.
            match = re.match(r'[ \t]+"([^"\r\n]+)"', source[directive.end():])
            if match is None:
                continue
            include = match.group(1).replace("\\", "/")
            resolved = (path.parent / include).resolve()
            inside = resolved.is_relative_to(root)
            includes.append({
                "file": relative,
                "line": bisect.bisect_right(line_starts, directive.start()),
                "target": include,
                "inside_root": inside,
                "exists": resolved.is_file() if inside else False,
            })
    return {
        "schema_version": 1,
        "kind": "lexical_dependency_inventory",
        "limitations": [
            "Not a compile result, compatibility score, reachability analysis, or runtime acceptance test.",
            "Scans all .dm/.dme files, including inactive preprocessor branches and unused files.",
            "Does not expand macros or evaluate interpolation inside strings.",
            "Counts identifier/call syntax; definitions and user-defined names can be included.",
            "Include paths are relative to the containing file; build search paths are not resolved.",
        ],
        "files_scanned": len(source_files),
        "source_lines": line_count,
        "decoding_warnings": decoding_warnings,
        "features": counts,
        "includes": includes,
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("root", type=Path)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--max-samples", type=int, default=5)
    args = parser.parse_args()
    try:
        report = inventory(args.root, max_samples=args.max_samples)
        output = json.dumps(report, indent=2, sort_keys=True) + "\n"
        if args.output:
            args.output.parent.mkdir(parents=True, exist_ok=True)
            args.output.write_text(output, encoding="utf-8")
        else:
            sys.stdout.write(output)
    except (OSError, ValueError) as error:
        print(f"audit_source: {error}", file=sys.stderr)
        return 2
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
