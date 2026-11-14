#!/usr/bin/env python3
"""Integration checks for real compiler invocation and artifact relocation.

Requires the pinned compiler to have been built. Run from any working directory.
"""

from pathlib import Path
import json
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
DRIVER = ROOT / "scripts" / "compile.py"


class CompilerIntegrationTests(unittest.TestCase):
    def compile(self, project: Path, output: Path) -> subprocess.CompletedProcess:
        return subprocess.run([sys.executable, str(DRIVER), str(project), "--output", str(output)],
                              cwd=project.parent, capture_output=True, text=True)

    def test_smoke_map_and_dmi_survive_relocation(self) -> None:
        project = ROOT / "fixtures" / "smoke" / "smoke.dme"
        original = project.read_bytes()
        with tempfile.TemporaryDirectory(prefix="dm output with spaces ") as directory:
            output = Path(directory) / "nested" / "world.json"
            result = self.compile(project, output)
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            artifact = json.loads(output.read_text())
            self.assertEqual((artifact["Maps"][0]["MaxX"], artifact["Maps"][0]["MaxY"]), (5, 5))
            self.assertEqual(len(artifact["Resources"]), 1)
            resource = (output.parent / artifact["Resources"][0]).resolve()
            self.assertEqual(resource, project.with_name("smoke.dmi"))
            self.assertTrue(resource.is_file())
            self.assertEqual(project.read_bytes(), original)
            provenance = json.loads(output.with_suffix(".provenance.json").read_text())
            self.assertEqual(provenance["compiler_revision"], "846775a06b8fa4639772cb3e759adcc0133d9711")

    def test_root_resource_and_failed_build_preserve_previous_artifact(self) -> None:
        with tempfile.TemporaryDirectory(prefix="dm source with spaces ") as directory:
            project_root = Path(directory)
            (project_root / "src").mkdir()
            (project_root / "assets").mkdir()
            asset = project_root / "assets" / "hello.txt"
            asset.write_text("fixture resource\n")
            source = project_root / "src" / "world.dm"
            source.write_text("var/global/resource_file = 'assets/hello.txt'\n")
            project = project_root / "world.dme"
            project.write_text('#include "src/world.dm"\n')
            output = project_root / "out" / "nested" / "world.json"
            result = self.compile(project, output)
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            original = output.read_bytes()
            artifact = json.loads(original)
            self.assertEqual((output.parent / artifact["Resources"][0]).resolve(), asset)
            resource_values = [value for value in artifact["Globals"]["Globals"].values()
                               if isinstance(value, dict) and "resourcePath" in value]
            self.assertTrue(resource_values)
            self.assertTrue(all(value["resourcePath"] in artifact["Resources"] for value in resource_values))
            source.write_text("#error intentional_compiler_failure\n")
            result = self.compile(project, output)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("intentional_compiler_failure", result.stdout)
            self.assertEqual(output.read_bytes(), original)


if __name__ == "__main__":
    unittest.main()
