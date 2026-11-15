"""Data-only map override inventory; does not instantiate or execute DM."""
import copy
import json
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts"))
from check_civ13_compile import map_override_inventory


class MapOverrideInventoryTests(unittest.TestCase):
    def fixture(self):
        return {
            "Types": [{"Path": "/obj/a"}, {"Path": "/obj/b"}],
            "Maps": [{
                "CellDefinitions": {
                    "used": {"Objects": [
                        {"Type": 0, "VarOverrides": {"name": "private-value", "dir": 4}},
                        {"Type": 1, "VarOverrides": {"name": None}},
                    ]},
                    # This malformed unused definition must never be inspected.
                    "unused": {"Objects": [{"Type": 99, "VarOverrides": {"unused": "secret"}}]},
                },
                "Blocks": [{"Cells": ["used", "used"]}, {"Cells": ["used"]}],
            }],
        }

    def test_only_used_instances_and_weighted_counts(self):
        report = map_override_inventory(self.fixture())
        self.assertEqual(report["overridden_entity_instances"], 6)
        self.assertEqual(report["distinct_type_field_categories"], 3)
        self.assertEqual(report["distinct_fields"], 2)
        self.assertEqual([entry["count"] for entry in report["entries"]], [3, 3, 3])
        self.assertEqual(report["entries"][0]["first"], {"map": 0, "block": 0, "cell": 0, "entity": 0})
        self.assertEqual(report["entries"][2]["first"]["entity"], 1)
        self.assertEqual([field["count"] for field in report["fields"]], [3, 6])
        self.assertFalse(report["entries_truncated"])

    def test_categories_without_values_or_mutation(self):
        data = self.fixture()
        data["Maps"][0]["CellDefinitions"]["used"]["Objects"][0]["VarOverrides"] = {
            "a": None, "b": False, "c": 1, "d": "secret", "e": [123],
            "f": {"type": 0, "resourcePath": "secret.dmi"}, "g": {"type": 6, "values": []},
        }
        before = copy.deepcopy(data)
        report = map_override_inventory(data)
        categories = {entry["field"]: entry["category"] for entry in report["entries"]}
        self.assertEqual([categories[key] for key in "abcdefg"],
                         ["null", "boolean", "number", "string", "array", "compiler_tag_0", "compiler_tag_6"])
        self.assertNotIn("secret", json.dumps(report))
        self.assertEqual(data, before)

    def test_bounded_output_retains_totals_and_marks_truncation(self):
        report = map_override_inventory(self.fixture(), limit=1)
        self.assertEqual(len(report["entries"]), 1)
        self.assertEqual(len(report["fields"]), 1)
        self.assertEqual(report["distinct_type_field_categories"], 3)
        self.assertEqual(report["distinct_fields"], 2)
        self.assertTrue(report["entries_truncated"])
        self.assertTrue(report["fields_truncated"])
        self.assertEqual(report["overridden_entity_instances"], 6)
        self.assertEqual(map_override_inventory(self.fixture(), limit=0)["entries"], [])
        with self.assertRaises(ValueError):
            map_override_inventory(self.fixture(), limit=-1)

    def test_empty_maps_and_unused_overrides_are_not_support_claims(self):
        self.assertEqual(map_override_inventory({})["overridden_entity_instances"], 0)
        data = self.fixture()
        data["Maps"][0]["Blocks"] = []
        report = map_override_inventory(data)
        self.assertEqual(report["entries"], [])
        self.assertIn("no runtime support", report["scope"])


if __name__ == "__main__":
    unittest.main()
