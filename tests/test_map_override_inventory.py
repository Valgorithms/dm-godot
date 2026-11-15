"""Data-only map override inventory; does not instantiate or execute DM."""
import copy
import json
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts"))
from check_civ13_compile import map_override_inventory, map_storage_inventory


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


class MapStorageInventoryTests(unittest.TestCase):
    def fixture(self):
        return {
            "Types": [
                {"Path": "/", "Variables": {"shared": 1}},
                {"Path": "/world", "Parent": 0, "Variables": {"area": {"type": 1, "value": 3}, "turf": {"type": 1, "value": 2}}},
                {"Path": "/turf", "Parent": 0, "Variables": {"shared": 2, "local": 3}},
                {"Path": "/area", "Parent": 0},
                {"Path": "/area/used", "Parent": 3},
                {"Path": "/obj", "Parent": 0, "InitProc": 0},
                {"Path": "/obj/child", "Parent": 5, "Variables": {"extra": None}},
            ],
            "Maps": [{"MaxZ": 1, "CellDefinitions": {
                "a": {"Turf": {"Type": 2, "VarOverrides": {"name": "hidden"}}, "Area": {"Type": 4},
                      "Objects": [{"Type": 6}, {"Type": 6, "VarOverrides": {"extra": 2}}]},
                "unused": {"Objects": [{"Type": 999}]},
            }, "Blocks": [{"Cells": ["a", "a"]}]}],
        }

    def test_weighted_fields_identity_roles_and_unused_default_area(self):
        data = self.fixture()
        before = copy.deepcopy(data)
        report = map_storage_inventory(data)
        self.assertEqual(report["planned_instances"], 9)
        self.assertEqual(report["referenced_area_cells"], 2)
        self.assertEqual(report["planned_unique_area_instances_including_default"], 2)
        self.assertEqual(report["weighted_instance_field_slots"], 35)
        self.assertEqual(report["ancestor_closure_types"], 7)
        self.assertEqual(report["ancestor_closure_effective_field_slots"], 25)
        self.assertEqual(report["instances_with_nonempty_overrides"], {"turf": 2, "entity": 2})
        obj = next(entry for entry in report["entries"] if entry["role"] == "entity")
        self.assertEqual((obj["instances"], obj["effective_fields_including_metadata"], obj["has_inherited_initializer"]), (4, 4, True))
        self.assertEqual(data, before)
        self.assertNotIn("hidden", json.dumps(report))

    def test_area_occurrences_are_not_allocations_and_fallbacks(self):
        data = self.fixture()
        cell = data["Maps"][0]["CellDefinitions"]["a"]
        del cell["Area"]
        del cell["Turf"]
        report = map_storage_inventory(data)
        self.assertEqual(report["planned_unique_area_instances_including_default"], 1)
        self.assertEqual(report["planned_instances"], 8)
        self.assertEqual(report["instances_with_nonempty_overrides"]["turf"], 0)

    def test_malformed_used_references_and_cycles_fail(self):
        data = self.fixture()
        data["Types"][0]["Parent"] = 6
        with self.assertRaisesRegex(ValueError, "Cyclic"):
            map_storage_inventory(data)
        data = self.fixture()
        data["Maps"][0]["Blocks"][0]["Cells"] = ["missing"]
        with self.assertRaises(KeyError):
            map_storage_inventory(data)
        data = self.fixture()
        data["Maps"][0]["CellDefinitions"]["a"]["Objects"][0]["Type"] = 999
        with self.assertRaisesRegex(ValueError, "type reference"):
            map_storage_inventory(data)

    def test_no_maps_and_metadata_deduplication(self):
        data = self.fixture()
        data["Maps"] = []
        data["Types"][1]["Variables"]["type"] = None
        report = map_storage_inventory(data)
        self.assertEqual(report["planned_instances"], 1)
        self.assertEqual(report["weighted_instance_field_slots"], 5)
        self.assertEqual(report["planned_unique_area_instances_including_default"], 0)
        self.assertEqual(map_storage_inventory({})["planned_instances"], 0)

if __name__ == "__main__":
    unittest.main()
