"""Real-process checks for probe phases and supervisor failure handling."""
import base64
import json
import hashlib
import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "scripts"))
from bootstrap_opendream import find_dotnet
from check_civ13_runtime_probe import capture_probe, PROBE_DLL


class ProbeTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.dotnet = find_dotnet()
        cls.smoke = json.loads((ROOT / "build" / "probe-smoke.json").read_text())

    def run_artifact(self, artifact, expected_digest=None):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "probe.json"
            path.write_text(json.dumps(artifact))
            return capture_probe([self.dotnet, str(PROBE_DLL), str(path)], timeout=15, expected_digest=expected_digest)

    def clone_smoke(self):
        return json.loads(json.dumps(self.smoke))

    def test_load_failure_is_not_boot_failure(self):
        report = self.run_artifact({})
        self.assertEqual((report["measurement_status"], report["runtime_status"], report["phase"]),
                         ("completed", "blocked", "artifact_load"))

    def test_constructor_failure_is_distinct(self):
        artifact = self.clone_smoke()
        # A valid pinned-format proc reference is not yet a supported runtime value.
        # Preserve existing type-to-global mappings while adding the unsupported value.
        globals_data = artifact["Globals"]
        index = globals_data["GlobalCount"]
        globals_data["GlobalCount"] += 1
        globals_data["Names"].append("probe_proc")
        globals_data["Globals"][str(index)] = {"type": 2, "value": 0}
        report = self.run_artifact(artifact)
        self.assertEqual((report["runtime_status"], report["phase"]), ("blocked", "runtime_construct"))
        self.assertIn("value kind 2", report["message"])

    def world_new(self, artifact):
        world = next(i for i, item in enumerate(artifact["Types"]) if item["Path"] == "/world")
        return next(artifact["Procs"][proc_id] for chain in artifact["Types"][world]["Procs"]
                    for proc_id in reversed(chain) if artifact["Procs"][proc_id]["Name"] == "New")

    def test_boot_unsupported_opcode_retains_diagnostic(self):
        artifact = self.clone_smoke()
        # Browse (0x3d) is a valid pinned opcode, deliberately unsupported here.
        self.world_new(artifact)["Bytecode"] = base64.b64encode(bytes([0x3d])).decode()
        report = self.run_artifact(artifact)
        self.assertEqual((report["runtime_status"], report["phase"]), ("blocked", "boot"))
        self.assertIn("world.dm:", report["message"])

    def test_instruction_budget_bounds_boot(self):
        artifact = self.clone_smoke()
        self.world_new(artifact)["Bytecode"] = base64.b64encode(bytes([0x0e, 0, 0, 0, 0])).decode()
        report = self.run_artifact(artifact)
        self.assertEqual((report["runtime_status"], report["phase"]), ("blocked", "boot"))
        self.assertIn("budget exhausted", report["message"])
        self.assertEqual(report["instruction_budget"], 100000)

    def test_boot_return_does_not_claim_quiescence(self):
        with patch.dict(os.environ, {"DOTNET_GCHeapHardLimit": "0x10000000"}):
            report = self.run_artifact(self.smoke)
        self.assertEqual((report["measurement_status"], report["runtime_status"], report["phase"]),
                         ("completed", "boot_returned", "boot"))
        self.assertGreater(report["pending_threads"], 0)
        self.assertEqual(report["configured_gc_heap_hard_limit"], "0x10000000")
        self.assertIsNone(report["memory_measurement_error"])
        for key in ("gc_total_available_memory_bytes", "managed_memory_bytes", "working_set_bytes", "peak_working_set_bytes"):
            self.assertGreater(report[key], 0, key)

    def test_child_hash_must_match_compilation_provenance(self):
        report = self.run_artifact(self.smoke, expected_digest="0" * 64)
        self.assertEqual((report["measurement_status"], report["reason"]), ("error", "artifact_hash_mismatch"))
        self.assertEqual(report["expected_artifact_sha256"], "0" * 64)
        self.assertNotEqual(report["observed_artifact_sha256"], report["expected_artifact_sha256"])

    def test_unexpected_io_failure_is_ci_failure(self):
        report = capture_probe([self.dotnet, str(PROBE_DLL), "/nonexistent/probe-artifact.json"], timeout=15, expected_inventory_hash="0" * 64)
        self.assertEqual(report["measurement_status"], "error")
        self.assertEqual(report["probe_exit_code"], 1)

    def test_non_json_child_output_is_ci_failure(self):
        report = capture_probe([sys.executable, "-c", "print('not-json')"], timeout=5, expected_inventory_hash="0" * 64)
        self.assertEqual(report["measurement_status"], "error")

    def test_timeout_is_ci_failure(self):
        report = capture_probe([sys.executable, "-c", "import time; time.sleep(5)"], timeout=0.1, expected_inventory_hash="0" * 64)
        self.assertEqual((report["measurement_status"], report["reason"]), ("error", "timeout"))


    def test_explicit_inventory_binding_and_manifest_integrity(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            artifact = root / "probe.json"
            artifact.write_text(json.dumps(self.smoke))
            digest = hashlib.sha256(artifact.read_bytes()).hexdigest()
            manifest = root / "inventory.json"
            data = {"schema": 1, "scope": "tracked-source-filesystem-snapshot",
                    "source_revision": "1" * 40, "source_tree": "2" * 40,
                    "artifact_sha256": digest, "files": ["fixture/file"], "blocked_paths": ["link"]}
            command = [self.dotnet, str(PROBE_DLL), str(artifact), "--file-inventory", str(manifest),
                       "--source-revision", "1" * 40, "--source-tree", "2" * 40]
            manifest.write_text(json.dumps(data))
            report = capture_probe(command, timeout=15, expected_digest=digest)
            self.assertEqual(report["runtime_status"], "boot_returned")
            self.assertEqual(report["file_inventory_sha256"], hashlib.sha256(manifest.read_bytes()).hexdigest())
            mismatch = capture_probe(command, timeout=15, expected_inventory_hash="0" * 64)
            self.assertEqual(mismatch["reason"], "file_inventory_hash_mismatch")
            for key, wrong in (("source_revision", "3" * 40), ("source_tree", "3" * 40), ("artifact_sha256", "0" * 64)):
                bad = dict(data, **{key: wrong})
                manifest.write_text(json.dumps(bad))
                report = capture_probe(command, timeout=15)
                self.assertEqual(report["measurement_status"], "error", key)
                self.assertIn("binding mismatch", report["stdout_tail"])
            manifest.write_text(json.dumps(dict(data, files=["../escape"])))
            report = capture_probe(command, timeout=15)
            self.assertEqual(report["measurement_status"], "error")
            self.assertIn("Invalid file inventory configuration", report["stdout_tail"])
            self.assertIn("path component", report["stdout_tail"])


    def test_text_manifest_requires_independent_allowlist_and_exact_blob_binding(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            artifact = root / "probe.json"
            artifact.write_text(json.dumps(self.smoke))
            raw = b"fixture\r\n"
            blob_id = hashlib.sha1(b"blob " + str(len(raw)).encode() + b"\0" + raw).hexdigest()
            allowed = root / "allowed.json"
            allowed.write_text(json.dumps({"schema": 1, "source_revision": "1" * 40, "source_tree": "2" * 40,
                "paths": ["fixture.txt"], "blobs": {"fixture.txt": {"oid": blob_id, "bytes": len(raw), "sha256": hashlib.sha256(raw).hexdigest()}}}))
            entry = {"path": "fixture.txt", "blob_id": blob_id, "byte_length": len(raw),
                     "sha256": hashlib.sha256(raw).hexdigest(), "base64": base64.b64encode(raw).decode()}
            data = {"schema": 1, "scope": "tracked-source-filesystem-snapshot",
                    "source_revision": "1" * 40, "source_tree": "2" * 40,
                    "artifact_sha256": hashlib.sha256(artifact.read_bytes()).hexdigest(),
                    "files": ["fixture.txt", "excluded.txt"], "blocked_paths": [],
                    "content_allowlist_sha256": hashlib.sha256(allowed.read_bytes()).hexdigest(),
                    "contents": [entry]}
            manifest = root / "manifest.json"
            manifest.write_text(json.dumps(data))
            command = [self.dotnet, str(PROBE_DLL), str(artifact), "--file-inventory", str(manifest),
                       "--source-revision", "1" * 40, "--source-tree", "2" * 40]
            report = capture_probe(command, timeout=15)
            self.assertEqual(report["measurement_status"], "error")
            self.assertIn("separately supplied trusted allowlist", report["stdout_tail"])
            command += ["--content-allowlist", str(allowed)]
            self.assertEqual(capture_probe(command, timeout=15)["runtime_status"], "boot_returned")
            for field, value in (("path", "excluded.txt"), ("byte_length", len(raw) + 1),
                                 ("blob_id", "0" * 40), ("sha256", "0" * 64), ("base64", "%%%"),
                                 ("base64", "a" * 90000)):
                bad = json.loads(json.dumps(data))
                bad["contents"][0][field] = value
                manifest.write_text(json.dumps(bad))
                report = capture_probe(command, timeout=15)
                self.assertEqual(report["measurement_status"], "error", field)
            good_allowed = json.loads(allowed.read_text())
            variants = [
                dict(good_allowed, source_tree="3" * 40),
                dict(good_allowed, extra=True),
                dict(good_allowed, paths=["fixture.txt", "fixture.txt"]),
                dict(good_allowed, paths=[]),
                dict(good_allowed, blobs={}),
                dict(good_allowed, blobs=dict(good_allowed["blobs"], extra=good_allowed["blobs"]["fixture.txt"])),
            ]
            for key, value in (("oid", "0" * 40), ("sha256", "0" * 64), ("bytes", 65537)):
                variant = json.loads(json.dumps(good_allowed))
                variant["blobs"]["fixture.txt"][key] = value
                variants.append(variant)
            for variant in variants:
                allowed.write_text(json.dumps(variant))
                rebound = dict(data, content_allowlist_sha256=hashlib.sha256(allowed.read_bytes()).hexdigest())
                manifest.write_text(json.dumps(rebound))
                report = capture_probe(command, timeout=15)
                self.assertEqual(report["measurement_status"], "error")
                self.assertNotIn("fixture\\r", json.dumps(report))
            allowed.write_text(json.dumps(good_allowed))
            for field, value in (("content_allowlist_sha256", "0" * 64), ("contents", [])):
                bad = dict(data, **{field: value})
                manifest.write_text(json.dumps(bad))
                self.assertEqual(capture_probe(command, timeout=15)["measurement_status"], "error")


if __name__ == "__main__":
    unittest.main()
