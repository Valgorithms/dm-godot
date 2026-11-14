import importlib.util
from pathlib import Path
import tempfile
import unittest

MODULE_PATH = Path(__file__).resolve().parents[1] / "scripts" / "audit_source.py"
SPEC = importlib.util.spec_from_file_location("audit_source", MODULE_PATH)
audit_source = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(audit_source)


class SourceInventoryTests(unittest.TestCase):
    def scan(self, sources):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            for name, content in sources.items():
                path = root / name
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_text(content, encoding="utf-8")
            return audit_source.inventory(root)

    def test_comments_literals_and_identifier_boundaries(self):
        result = self.scan({"game.dm": '''// sleep(1) and call_ext("x")
/* browse("x") /* spawn(1) */ sleep(2) */
var/message = "sleep(1) and \\"browse()\\""
var/icon = 'sleep.dmi'
var/help = {"sleep(4)
browse("x")"}
mysleep(3)
sleep(4)
world << "[sleep(2)]"
'''})
        self.assertEqual(result["features"]["scheduling"]["sleep"]["occurrences"], 1)
        self.assertEqual(result["features"]["scheduling"]["sleep"]["samples"], [{"file": "game.dm", "line": 8}])
        self.assertNotIn("native", result["features"])
        self.assertNotIn("browser", result["features"])

    def test_includes_comments_windows_paths_and_escape(self):
        result = self.scan({"game.dme": '#include "code\\main.dm"\n// #include "fake.dm"\n#include "../outside.dm"\n', "code/main.dm": "sleep(1)\n"})
        self.assertEqual(result["files_scanned"], 2)
        self.assertEqual(len(result["includes"]), 2)
        self.assertTrue(result["includes"][0]["exists"])
        self.assertEqual(result["includes"][0]["target"], "code/main.dm")
        self.assertFalse(result["includes"][1]["inside_root"])

    def test_savefile_appearance_native_and_scheduler_are_detected(self):
        result = self.scan({"game.dm": '''var/savefile/F = new()
F.ImportText("/", "data")
call_ext("rust_g", "fn")()
set waitfor = FALSE
var/remaining = world.tick_usage
image.render_source = "map"
spawn(1)
    world << "hello"
'''})
        features = result["features"]
        self.assertEqual(features["persistence"]["/savefile"]["occurrences"], 1)
        self.assertEqual(features["persistence"]["ImportText"]["occurrences"], 1)
        self.assertEqual(features["native"]["call_ext"]["occurrences"], 1)
        self.assertEqual(features["scheduling"]["waitfor"]["occurrences"], 1)
        self.assertEqual(features["appearance"]["render_source"]["occurrences"], 1)

    def test_fake_include_inside_multiline_string_is_ignored(self):
        result = self.scan({"game.dm": 'var/x = {"\n#include "fake.dm"\n"}\n#include "real.dm"\n', "real.dm": ""})
        self.assertEqual([item["target"] for item in result["includes"]], ["real.dm"])

    def test_offsets_and_newlines_preserved(self):
        text = '"/* foo */"\n/* first\nsecond */\nsleep(1)'
        masked = audit_source.mask_non_code(text)
        self.assertEqual(len(masked), len(text))
        self.assertEqual([i for i, char in enumerate(masked) if char == "\n"], [i for i, char in enumerate(text) if char == "\n"])
        self.assertTrue(masked.endswith("sleep(1)"))

    def test_zero_sample_limit_keeps_counts(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory)
            (path / "x.dm").write_text("sleep(1)\nsleep(2)\n", encoding="utf-8")
            feature = audit_source.inventory(path, max_samples=0)["features"]["scheduling"]["sleep"]
            self.assertEqual(feature, {"occurrences": 2, "samples": []})


if __name__ == "__main__":
    unittest.main()
