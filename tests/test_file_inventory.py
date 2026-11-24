"""Git-tree-only inventory tests; temporary repositories, never production files."""
import hashlib
import json
import base64
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts"))
from file_inventory import build_inventory, validate_path, bounded_output, artifact_digest, load_allowlist


class FileInventoryTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.base = Path(self.temp.name)
        self.source = self.base / "source"
        self.source.mkdir()
        self.git("init", "-q")
        self.git("config", "user.email", "fixture@example.invalid")
        self.git("config", "user.name", "Fixture")
        (self.source / "dir").mkdir()
        (self.source / "dir" / "present").write_text("fixture")
        (self.source / "Case").write_text("case")
        self.git("add", ".")
        self.git("commit", "-qm", "fixture")
        self.artifact = self.base / "artifact.json"
        self.artifact.write_text("{}")
        self.digest = hashlib.sha256(self.artifact.read_bytes()).hexdigest()

    def git(self, *args):
        return subprocess.check_output(["git", "-C", str(self.source), *args], text=True).strip()

    def build(self, **changes):
        values = dict(source_revision=self.git("rev-parse", "HEAD"),
                      source_tree=self.git("rev-parse", "HEAD^{tree}"),
                      artifact=self.artifact, artifact_sha256=self.digest)
        values.update(changes)
        return build_inventory(self.source, **values)

    def test_regular_files_are_exact_and_bound_to_provenance(self):
        result = self.build()
        self.assertEqual(result["files"], ["Case", "dir/present"])
        self.assertEqual(result["blocked_paths"], [])
        self.assertEqual(result["artifact_sha256"], self.digest)
        self.assertEqual(result["source_tree"], self.git("rev-parse", "HEAD^{tree}"))
        self.assertNotIn(".git/config", result["files"])
        self.assertNotIn("dir", result["files"])

    def test_symlinks_and_submodules_are_blocked_without_dereferencing(self):
        (self.source / "link").symlink_to("../outside-does-not-exist")
        self.git("add", "link")
        self.git("update-index", "--add", "--cacheinfo", "160000," + self.git("rev-parse", "HEAD") + ",submodule")
        (self.source / "submodule").mkdir()
        self.git("commit", "-qm", "blocked entries")
        result = self.build()
        self.assertEqual(result["blocked_paths"], ["link", "submodule"])
        self.assertNotIn("link", result["files"])
        self.assertFalse((self.base / "outside-does-not-exist").exists())

    def test_symlink_root_and_ancestor_are_rejected(self):
        alias = self.base / "alias"
        alias.symlink_to(self.source, target_is_directory=True)
        with self.assertRaisesRegex(ValueError, "Symlink source"):
            build_inventory(alias, source_revision=self.git("rev-parse", "HEAD"),
                            source_tree=self.git("rev-parse", "HEAD^{tree}"),
                            artifact=self.artifact, artifact_sha256=self.digest)
        parent_alias = self.base / "parent-alias"
        parent_alias.symlink_to(self.base, target_is_directory=True)
        with self.assertRaisesRegex(ValueError, "Symlink source"):
            build_inventory(parent_alias / "source", source_revision=self.git("rev-parse", "HEAD"),
                            source_tree=self.git("rev-parse", "HEAD^{tree}"),
                            artifact=self.artifact, artifact_sha256=self.digest)

    def test_dirty_and_changed_sources_are_rejected(self):
        (self.source / "dir" / "present").write_text("modified")
        with self.assertRaisesRegex(ValueError, "clean"):
            self.build()
        self.git("checkout", "--", "dir/present")
        (self.source / "untracked").write_text("not in snapshot")
        with self.assertRaisesRegex(ValueError, "clean"):
            self.build()

    def test_mismatched_commit_tree_and_stale_artifact_are_rejected(self):
        for key in ("source_revision", "source_tree"):
            with self.assertRaisesRegex(ValueError, "mismatch"):
                self.build(**{key: "0" * 40})
        self.artifact.write_text("stale")
        with self.assertRaisesRegex(ValueError, "artifact hash mismatch"):
            self.build()

    def test_unsafe_tree_names_and_modes_do_not_become_missing_files(self):
        for path in ("/root", "../sibling", "a/../b", "C:/drive", "a\\b", ".git/config",
                     "a/.GIT/config", "a//b", "a/./b", "a/", "", "a\0b"):
            with self.subTest(path=path), self.assertRaises(ValueError):
                validate_path(path)
        with self.assertRaisesRegex(ValueError, "length"):
            validate_path("\U0001f600" * 2049)
        with patch("file_inventory.tree_records", return_value=b"100600 blob " + b"0" * 40 + b"\tbad\0"):
            with self.assertRaisesRegex(ValueError, "mode"):
                self.build()
        with patch("file_inventory.MAX_PATHS", 1):
            with self.assertRaisesRegex(ValueError, "size limit"):
                self.build()
        with patch("file_inventory.MAX_PATH_BYTES", 1):
            with self.assertRaisesRegex(ValueError, "size limit"):
                self.build()

    def test_tree_metadata_deadline_and_size_cap_cover_stream_reads(self):
        with self.assertRaises(subprocess.TimeoutExpired):
            bounded_output([sys.executable, "-c", "import time; time.sleep(5)"], timeout=0.05)
        with self.assertRaisesRegex(ValueError, "metadata limit"):
            bounded_output([sys.executable, "-c", "print('x' * 1000)"], limit=100)
        self.assertEqual(bounded_output([sys.executable, "-c", "print('ok')"]), b"ok\n")

    def test_git_replacements_cannot_rebind_source_tree(self):
        revision = self.git("rev-parse", "HEAD")
        tree = self.git("rev-parse", "HEAD^{tree}")
        (self.source / "Case").write_text("replacement")
        self.git("commit", "-qam", "replacement")
        replacement = self.git("rev-parse", "HEAD")
        self.git("checkout", "-q", "--detach", revision)
        self.git("replace", revision, replacement)
        self.assertNotEqual(self.git("rev-parse", "HEAD^{tree}"), tree)
        result = self.build(source_revision=revision, source_tree=tree)
        self.assertEqual(result["source_tree"], tree)
        self.assertEqual(result["files"], ["Case", "dir/present"])

    def test_dirty_status_output_is_bounded(self):
        for i in range(20):
            (self.source / ("untracked-" + str(i) + "-" + "x" * 80)).touch()
        with patch("file_inventory.MAX_METADATA_BYTES", 256):
            with self.assertRaisesRegex(ValueError, "metadata limit"):
                self.build()

    def test_artifact_hash_reads_fixed_chunks(self):
        self.artifact.write_bytes(b"x" * (128 * 1024 + 7))
        expected = hashlib.sha256(self.artifact.read_bytes()).hexdigest()
        with patch.object(Path, "read_bytes", side_effect=AssertionError("unbounded read")):
            self.assertEqual(artifact_digest(self.artifact), expected)
            self.assertEqual(self.build(artifact_sha256=expected)["artifact_sha256"], expected)

    def allowlist(self):
        raw = (self.source / "Case").read_bytes()
        return {"schema": 1, "source_revision": self.git("rev-parse", "HEAD"),
                "source_tree": self.git("rev-parse", "HEAD^{tree}"), "paths": ["Case"],
                "blobs": {"Case": {"oid": self.git("rev-parse", "HEAD:Case"), "bytes": len(raw),
                                  "sha256": hashlib.sha256(raw).hexdigest()}}}

    def test_content_is_explicit_exact_git_blob_not_all_tracked_files(self):
        allowed = self.allowlist()
        snapshot = self.build(content_allowlist=allowed, content_allowlist_sha256="1" * 64)
        self.assertEqual([entry["path"] for entry in snapshot["contents"]], ["Case"])
        self.assertEqual(base64.b64decode(snapshot["contents"][0]["base64"]), b"case")
        self.assertNotIn("contents", self.build())

    def test_allowlist_reader_bounds_shape_duplicates_and_binding(self):
        path = self.base / "allowed.json"
        valid = self.allowlist()
        path.write_text(json.dumps(valid))
        data, digest = load_allowlist(path, valid["source_revision"], valid["source_tree"])
        self.assertEqual(data, valid)
        self.assertEqual(digest, hashlib.sha256(path.read_bytes()).hexdigest())
        for changed in (dict(valid, source_tree="0" * 40), dict(valid, extra=True),
                        dict(valid, paths=[]), dict(valid, paths=["Case", "Case"]),
                        dict(valid, blobs={}), dict(valid, paths=["../escape"])):
            path.write_text(json.dumps(changed))
            with self.assertRaises(ValueError):
                load_allowlist(path, valid["source_revision"], valid["source_tree"])
        path.write_text('{"schema":1,"schema":1}')
        with self.assertRaisesRegex(ValueError, "Duplicate"):
            load_allowlist(path, valid["source_revision"], valid["source_tree"])
        path.write_bytes(b" " * (128 * 1024 + 1))
        with self.assertRaisesRegex(ValueError, "size limit"):
            load_allowlist(path, valid["source_revision"], valid["source_tree"])

    def test_content_blob_binding_and_limits_fail_before_blob_read(self):
        import file_inventory
        original = file_inventory.bounded_output
        calls = []
        def tracked(command, **options):
            calls.append(command)
            return original(command, **options)
        for field, value in (("oid", "0" * 40), ("bytes", 65537), ("bytes", -1), ("bytes", 3)):
            allowed = self.allowlist()
            allowed["blobs"]["Case"][field] = value
            calls.clear()
            with patch("file_inventory.bounded_output", side_effect=tracked), self.assertRaises(ValueError):
                self.build(content_allowlist=allowed, content_allowlist_sha256="1" * 64)
            self.assertFalse(any("blob" in command for command in calls))
        allowed = self.allowlist()
        allowed["blobs"]["Case"]["sha256"] = "0" * 64
        with self.assertRaisesRegex(ValueError, "digest mismatch"):
            self.build(content_allowlist=allowed, content_allowlist_sha256="1" * 64)

    def test_content_blob_replacement_is_ignored(self):
        allowed = self.allowlist()
        replacement = self.git("hash-object", "-w", "dir/present")
        self.git("replace", allowed["blobs"]["Case"]["oid"], replacement)
        snapshot = self.build(content_allowlist=allowed, content_allowlist_sha256="1" * 64)
        self.assertEqual(base64.b64decode(snapshot["contents"][0]["base64"]), b"case")

    def test_non_regular_content_and_total_limits_rejected(self):
        allowed = self.allowlist()
        allowed["paths"] = ["dir"]
        allowed["blobs"] = {"dir": allowed["blobs"]["Case"]}
        with self.assertRaisesRegex(ValueError, "regular source-tree"):
            self.build(content_allowlist=allowed, content_allowlist_sha256="1" * 64)
        allowed = self.allowlist()
        allowed["paths"] = ["x" + str(i) for i in range(17)]
        allowed["blobs"] = {name: dict(allowed["blobs"]["Case"], bytes=65536) for name in allowed["paths"]}
        with self.assertRaisesRegex(ValueError, "total byte"):
            self.build(content_allowlist=allowed, content_allowlist_sha256="1" * 64)

    def test_wrapper_keeps_private_content_out_of_reports_and_uploads(self):
        import contextlib
        import io
        import check_civ13_runtime_probe as wrapper
        root = self.base / "engine"
        artifact = root / "build/civ13/civ13.json"
        artifact.parent.mkdir(parents=True)
        artifact.write_bytes(b"{}")
        revision, tree = "1" * 40, "2" * 40
        (artifact.parent / "reproduction.json").write_text(json.dumps({
            "source_revision": revision, "source_tree": tree, "compile_exit_code": 0,
            "source_clean_after": True, "artifact_sha256": self.digest}))
        (root / "compatibility").mkdir()
        (root / "compatibility/targets.json").write_text(json.dumps({"targets": [{"id": "civ13", "revision": revision}]}))
        names = {"schema": 1, "scope": "tracked-source-filesystem-snapshot",
                 "source_revision": revision, "source_tree": tree, "artifact_sha256": self.digest,
                 "files": ["public.txt"], "blocked_paths": []}
        marker = "PRIVATE_TEST_CONTENT_NOT_FOR_REPORTS"
        encoded = base64.b64encode(marker.encode()).decode()
        payload = dict(names, content_allowlist_sha256="3" * 64, contents=[
            {"path": "public.txt", "base64": encoded, "byte_length": len(marker)}])
        for outcome in ({"measurement_status": "completed", "runtime_status": "blocked"},
                        {"measurement_status": "error", "runtime_status": "unknown", "reason": "timeout"},
                        {"measurement_status": "error", "runtime_status": "unknown", "reason": "launch_failed"}):
            output = io.StringIO()
            with patch.object(wrapper, "ROOT", root), patch.object(wrapper, "find_dotnet", return_value="unused"), \
                 patch.object(wrapper, "git", return_value="4" * 40), \
                 patch.object(wrapper, "build_inventory", side_effect=[names, payload]), \
                 patch.object(wrapper, "load_allowlist", return_value=({}, "3" * 64)), \
                 patch.object(wrapper, "capture_probe", return_value=dict(outcome)), \
                 patch.object(sys, "argv", ["probe", "--artifact", str(artifact), "--tracked-source-files",
                                          "--content-allowlist", str(root / "trusted.json")]), \
                 contextlib.redirect_stdout(output):
                code = wrapper.main()
            self.assertEqual(code, 0 if outcome["measurement_status"] == "completed" else 1)
            report = json.loads((artifact.parent / "runtime-probe.json").read_text())
            self.assertEqual(report.get("reason"), outcome.get("reason"))
            self.assertNotIn("contents", json.loads((artifact.parent / "file-inventory.json").read_text()))
            private = artifact.parent / "file-inventory-content.private.json"
            self.assertIn(encoded, private.read_text())
            for public in (output.getvalue(), json.dumps(report)):
                self.assertNotIn(marker, public)
                self.assertNotIn(encoded, public)
        workflow = (Path(__file__).resolve().parents[1] / ".github/workflows/compile-civ13.yml").read_text()
        upload = workflow.split("      - name: Preserve diagnostics and provenance", 1)[1]
        self.assertIn("build/civ13/file-inventory.json", upload)
        self.assertNotIn("file-inventory-content", upload)
        self.assertNotIn("*", upload)
