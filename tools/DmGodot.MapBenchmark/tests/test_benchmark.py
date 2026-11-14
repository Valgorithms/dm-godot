"""Real subprocess checks and completeness guards; no memory/timing thresholds."""
import copy
from pathlib import Path
import sys
import unittest

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "scripts"))
from benchmark_map_memory import aggregate, run_sample, CASES

REVISION = "0" * 40  # Test identity only; workflow supervisor records actual git HEAD.


class BenchmarkTests(unittest.TestCase):
    def test_real_small_cases_and_deterministic_artifact(self):
        first = run_sample(2, 0, 1, REVISION)
        repeated = run_sample(2, 0, 2, REVISION)
        fields = run_sample(2, 3, 1, REVISION)
        for sample, variable_count in ((first, 2), (repeated, 2), (fields, 5)):
            self.assertEqual(sample["status"], "completed", sample)
            self.assertEqual(sample["actual_turf_count"], 4)
            self.assertEqual(sample["actual_area_count"], 1)
            self.assertEqual(sample["actual_entity_count"], 0)
            self.assertEqual(sample["actual_turf_variable_count"], variable_count)
            self.assertEqual(sample["configured_gc_heap_hard_limit"], "0x80000000")
            self.assertEqual(sample["engine_revision"], REVISION)
            self.assertGreater(sample["construction_process_managed_allocated_bytes"], 0)
            self.assertGreater(sample["gc_total_available_memory_bytes"], 0)
            self.assertGreater(sample["process_peak_working_set_bytes"], 0)
            self.assertEqual(sample["retained_managed_delta_bytes_after_forced_gc"],
                             sample["managed_bytes_after_construction_after_forced_gc"] -
                             sample["managed_bytes_before_construction_after_forced_gc"])
        self.assertEqual(first["artifact_sha256"], repeated["artifact_sha256"])
        self.assertNotEqual(first["artifact_sha256"], fields["artifact_sha256"])

    def test_real_invalid_case_fails(self):
        sample = run_sample(129, 0, 1, REVISION)
        self.assertEqual(sample["status"], "error")
        self.assertEqual(sample["raw"]["exit_code"], 1)

    def test_real_process_timeout_fails(self):
        sample = run_sample(128, 128, 1, REVISION, timeout=0.000001)
        self.assertEqual((sample["status"], sample["reason"]), ("error", "timeout"))

    def test_aggregate_rejects_partial_duplicate_failure_and_hash_mismatch(self):
        samples = [dict(status="completed", side=s, extra_inherited_fields=f, repetition=r,
                        engine_revision=REVISION, compiler_revision="pin", dotnet="test", os="test",
                        architecture="test", configured_gc_heap_hard_limit="cap", bytecode_version="v",
                        artifact_sha256=f"{s}-{f}", construction_elapsed_ms=r,
                        construction_process_managed_allocated_bytes=r,
                        retained_managed_delta_bytes_after_forced_gc=-r) for s, f, r in CASES]
        summary = aggregate(samples)
        self.assertEqual(summary["sample_count"], 27)
        self.assertEqual(summary["groups"][0]["retained_managed_delta_bytes_after_forced_gc"]["median"], -2)
        variants = [samples[:-1], samples[:-1] + [samples[0]]]
        failed = copy.deepcopy(samples)
        failed[0]["status"] = "error"
        variants.append(failed)
        mismatch = copy.deepcopy(samples)
        mismatch[0]["artifact_sha256"] = "different"
        variants.append(mismatch)
        for variant in variants:
            with self.assertRaises(ValueError):
                aggregate(variant)


if __name__ == "__main__":
    unittest.main()
