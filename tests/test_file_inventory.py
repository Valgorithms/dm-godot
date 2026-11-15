"""Git-tree-only inventory tests; temporary repositories, never production files."""
import hashlib
import base64
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts"))
from file_inventory import build_inventory, validate_path, bounded_output


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

    def test_contents_are_exact_git_blobs_with_length_and_hash_binding(self):
        result = self.build(content_paths=["dir/present"], content_allowlist_sha256="a" * 64)
        entry = result["contents"][0]
        self.assertEqual(base64.b64decode(entry["base64"]), b"fixture")
        self.assertEqual(entry["byte_length"], 7)
        self.assertEqual(entry["sha256"], hashlib.sha256(b"fixture").hexdigest())
        self.assertEqual(entry["blob_id"], self.git("rev-parse", "HEAD:dir/present"))
        self.assertEqual(result["content_allowlist_sha256"], "a" * 64)
        self.assertNotIn("Case", [item["path"] for item in result["contents"]])
        (self.source / "dir" / "present").write_text("changed after snapshot")
        self.assertEqual(base64.b64decode(entry["base64"]), b"fixture")

    def test_content_read_ignores_git_replace_and_rejects_missing_symlink_and_limits(self):
        original = self.git("rev-parse", "HEAD:dir/present")
        replacement = subprocess.check_output(["git", "-C", str(self.source), "hash-object", "-w", "--stdin"],
                                              input=b"replacement").decode().strip()
        self.git("replace", original, replacement)
        result = self.build(content_paths=["dir/present"], content_allowlist_sha256="a" * 64)
        self.assertEqual(base64.b64decode(result["contents"][0]["base64"]), b"fixture")
        for paths in (["missing"], ["dir"], ["../escape"], ["Case", "Case"]):
            with self.subTest(paths=paths), self.assertRaises(ValueError):
                self.build(content_paths=paths, content_allowlist_sha256="a" * 64)
        (self.source / "link").symlink_to("dir/present")
        self.git("add", "link")
        self.git("commit", "-qm", "link")
        with self.assertRaisesRegex(ValueError, "regular source-tree"):
            self.build(content_paths=["link"], content_allowlist_sha256="a" * 64)
        (self.source / "large").write_bytes(b"x" * 65537)
        self.git("add", "large")
        self.git("commit", "-qm", "large")
        with self.assertRaisesRegex(ValueError, "raw byte limit"):
            self.build(content_paths=["large"], content_allowlist_sha256="a" * 64)
