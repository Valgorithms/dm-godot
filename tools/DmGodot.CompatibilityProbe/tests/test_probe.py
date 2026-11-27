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

    def run_artifact(self, artifact, expected_digest=None, *, probe_options=()):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "probe.json"
            path.write_text(json.dumps(artifact))
            return capture_probe([self.dotnet, str(PROBE_DLL), *probe_options, str(path)], timeout=15, expected_digest=expected_digest)

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
        self.assertIsNone(report["log_count"])
        self.assertIsNone(report["pending_threads"])
        self.assertIsNone(report["global_step_work"])
        self.assertIsNone(report["global_work_remaining"])

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

    def test_boot_fault_preserves_partial_counts(self):
        artifact = self.clone_smoke()
        # world.New logs "boot" and queues a spawn before calling fixture_step.
        step = next(artifact["Procs"][i] for i in artifact["GlobalProcs"]
                    if artifact["Procs"][i]["Name"] == "fixture_step")
        step["Bytecode"] = base64.b64encode(bytes([0x3d])).decode()
        report = self.run_artifact(artifact)
        self.assertEqual((report["runtime_status"], report["phase"]), ("blocked", "boot"))
        self.assertIn("fixture_step", report["message"])
        self.assertEqual((report["log_count"], report["pending_threads"]), (1, 1))

    def test_global_initializer_continues_after_a_probe_batch(self):
        # 128 PushNull/Pop pairs consume one batch. The following opcode must still execute.
        prefix = bytes([0x11, 0x51]) * 128
        for tail, status in [(bytes([0x11, 0x10]), "boot_returned"), (bytes([0x3d]), "blocked")]:
            with self.subTest(status=status):
                artifact = self.clone_smoke()
                artifact["GlobalInitProc"] = {"Name": "probe_global_init", "SourceInfo": [],
                                              "Bytecode": base64.b64encode(prefix + tail).decode()}
                report = self.run_artifact(artifact)
                self.assertEqual((report["runtime_status"], report["phase"]), (status, "boot"))
                # Each tail byte is one charged opcode, including the unsupported one.
                self.assertEqual(report["global_step_work"], len(tail))
                self.assertEqual(report["global_work_remaining"], 100_000 - len(prefix) - len(tail))
                if status == "blocked":
                    self.assertIn("probe_global_init, byte 256", report["message"])
                    self.assertEqual((report["log_count"], report["pending_threads"]), (0, 0))
                else:
                    self.assertGreater(report["pending_threads"], 0)

    def test_extended_profile_completes_a_longer_global_initializer(self):
        artifact = self.clone_smoke()
        code = bytes([0x11, 0x51]) * 50_000 + bytes([0x11, 0x10])
        artifact["GlobalInitProc"] = {"Name": "long_global_init", "SourceInfo": [],
                                      "Bytecode": base64.b64encode(code).decode()}
        standard = self.run_artifact(artifact)
        self.assertEqual((standard["runtime_status"], standard["phase"]), ("blocked", "boot"))
        self.assertEqual((standard["global_work_budget"], standard["global_work_remaining"]), (100_000, 0))
        extended = self.run_artifact(artifact, probe_options=["--extended-globals"])
        self.assertEqual((extended["runtime_status"], extended["phase"]), ("boot_returned", "boot"))
        self.assertEqual(extended["instruction_budget"], 100_000)
        self.assertEqual((extended["global_work_budget"], extended["global_work_remaining"]),
                         (200_000, 200_000 - len(code)))

    def test_extended_profile_requires_one_leading_flag_and_an_artifact(self):
        with tempfile.TemporaryDirectory() as directory:
            artifact = Path(directory) / "smoke.json"
            artifact.write_text(json.dumps(self.smoke))
            for args in (["--extended-globals"], [str(artifact), "--extended-globals"],
                         ["--extended-globals", "--extended-globals", str(artifact)]):
                with self.subTest(args=args):
                    report = capture_probe([self.dotnet, str(PROBE_DLL), *args], timeout=15)
                    self.assertEqual((report["measurement_status"], report["probe_exit_code"]), ("error", 1))
                    error = json.loads(report["stdout_tail"])
                    self.assertEqual(error["exception_type"], "System.ArgumentException")
                    self.assertIsNone(error["artifact_sha256"])

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
            extended = capture_probe(command[:2] + ["--extended-globals"] + command[2:], timeout=15,
                                     expected_digest=digest, expected_inventory_hash=report["file_inventory_sha256"])
            self.assertEqual((extended["runtime_status"], extended["global_work_budget"]), ("boot_returned", 200_000))
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


    def test_map_icon_grant_binds_one_project_and_is_separate_from_file_queries(self):
        smoke = self.clone_smoke()
        index, token = next((i, item) for i, item in enumerate(smoke["Resources"]) if item.endswith("/smoke.dmi"))
        entity = smoke["Maps"][0]["CellDefinitions"]["c"]["Objects"][0]
        entity["VarOverrides"] = {"icon": {"type": 0, "resourcePath": token}}
        path = "fixtures/smoke/smoke.dmi"
        raw = (ROOT / path).read_bytes()
        binding = {"index": index, "token": token, "path": path, "bytes": len(raw),
                   "oid": hashlib.sha1(b"blob " + str(len(raw)).encode() + b"\0" + raw).hexdigest(),
                   "sha256": hashlib.sha256(raw).hexdigest()}
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            artifact = root / "probe.json"
            artifact.write_text(json.dumps(smoke))
            source = {"schema": 1, "source_revision": "1" * 40, "source_tree": "2" * 40,
                      "artifact_sha256": hashlib.sha256(artifact.read_bytes()).hexdigest(),
                      "files": [path], "blocked_paths": []}
            files = root / "files.json"
            files.write_text(json.dumps(dict(source, scope="tracked-source-filesystem-snapshot")))
            icons = root / "icons.json"
            manifest = dict(source, scope="tracked-map-icon-bindings", bindings=[binding])
            icons.write_text(json.dumps(manifest))
            command = [self.dotnet, str(PROBE_DLL), str(artifact)]
            pins = ["--source-revision", source["source_revision"], "--source-tree", source["source_tree"]]
            file_args = ["--file-inventory", str(files), *pins]
            icon_args = ["--map-icons", str(icons)]
            # A regular filename in the DM query inventory grants no icon override.
            denied = capture_probe(command + file_args, timeout=15)
            self.assertEqual((denied["runtime_status"], denied["phase"]), ("blocked", "runtime_construct"))
            for args, file_hash in ((icon_args + pins, None),
                                    (file_args + icon_args, hashlib.sha256(files.read_bytes()).hexdigest())):
                report = capture_probe(command + args, timeout=15)
                self.assertEqual(report["runtime_status"], "boot_returned")
                self.assertEqual(report["map_icon_inventory_sha256"], hashlib.sha256(icons.read_bytes()).hexdigest())
                self.assertEqual(report["file_inventory_sha256"], file_hash)
            for changes in ({"artifact_sha256": "0" * 64}, {"bindings": [dict(binding, index=-1)]},
                            {"bindings": [dict(binding, sha256="invalid")]}, {"bindings": [binding, binding]}):
                with self.subTest(changes=changes):
                    icons.write_text(json.dumps(dict(manifest, **changes)))
                    report = capture_probe(command + icon_args + pins, timeout=15)
                    self.assertEqual(report["measurement_status"], "error")
                    self.assertEqual(json.loads(report["stdout_tail"])["phase"], "artifact_load")

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
