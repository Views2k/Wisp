"""Exercise the real release-content gate in disposable, untagged fixtures."""
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest
import xml.etree.ElementTree as ET


ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / ".github" / "scripts" / "Test-ReleaseTag.ps1"


class ReleaseContentTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory(
            prefix="wisp-release-content-", dir=os.environ.get("WISP_RELEASE_CONTENT_TEST_TEMP"))
        self.addCleanup(self.directory.cleanup)
        self.repo = Path(self.directory.name)
        self.shell = shutil.which("pwsh")
        self.assertIsNotNone(self.shell, "PowerShell 7 is required for release-content fixtures.")
        self.version = ET.parse(ROOT / "src/Wisp.App/Wisp.App.csproj").findtext("./PropertyGroup/Version")
        self.assertIsNotNone(self.version)
        self.script = self.repo / ".github/scripts/Test-ReleaseTag.ps1"
        self.script.parent.mkdir(parents=True)
        shutil.copyfile(SCRIPT, self.script)
        self.write("src/Wisp.App/Wisp.App.csproj",
                   f"<Project><PropertyGroup><Version>{self.version}</Version></PropertyGroup></Project>\n")
        self.write("installer/Wisp.iss", f'#define MyAppVersion "{self.version}"\n')
        self.notes = f"docs/releases/Wisp-{self.version}-release-notes.md"
        self.write(self.notes, f"# Wisp {self.version}\n\nRelease fixture.\n")
        self.write("CHANGELOG.md", f"# Changelog\n\n## {self.version} - 2026-09-21\n")

    def write(self, name, content):
        target = self.repo / name
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(content, encoding="utf-8", newline="\n")

    def evaluate(self, content_only=True):
        command = [self.shell, "-NoLogo", "-NoProfile", "-NonInteractive", "-File", str(self.script),
                   "-Tag", f"v{self.version}-stable"]
        if content_only:
            command.append("-ContentOnly")
        environment = os.environ.copy()
        # Never let the untagged fixture discover a containing workspace repository.
        environment["GIT_CEILING_DIRECTORIES"] = str(self.repo.parent)
        for name in ("GIT_DIR", "GIT_WORK_TREE", "GIT_COMMON_DIR"):
            environment.pop(name, None)
        return subprocess.run(command, cwd=self.repo, env=environment, capture_output=True,
                              text=True, timeout=20)

    def test_current_release_and_stable_tag_pass_without_git(self):
        self.assertFalse((self.repo / ".git").exists())
        process = self.evaluate()
        self.assertEqual(process.returncode, 0, process.stderr)
        self.assertIn(f"Validated release content for {self.version}", process.stdout)
        self.assertIn("tag provenance was not requested", process.stdout)

    def test_wrong_heading_fails(self):
        self.write(self.notes, f"# Wisp {self.version} hotfix\n")
        process = self.evaluate()
        self.assertNotEqual(process.returncode, 0)
        self.assertIn("release-notes heading does not match", process.stderr)

    def test_missing_dated_changelog_fails(self):
        self.write("CHANGELOG.md", f"# Changelog\n\n## {self.version}\n")
        process = self.evaluate()
        self.assertNotEqual(process.returncode, 0)
        self.assertIn("must contain a dated", process.stderr)

    def test_installer_version_mismatch_fails(self):
        different = "0.0.0" if self.version != "0.0.0" else "0.0.1"
        self.write("installer/Wisp.iss", f'#define MyAppVersion "{different}"\n')
        process = self.evaluate()
        self.assertNotEqual(process.returncode, 0)
        self.assertIn("does not match the application and installer version", process.stderr)

    def test_full_gate_still_requires_git_tag_and_main(self):
        process = self.evaluate(content_only=False)
        self.assertNotEqual(process.returncode, 0)
        self.assertNotIn("Validated release content", process.stdout)
        self.assertIn("release tag, checked-out commit, or main branch could not be resolved", process.stderr)


if __name__ == "__main__":
    unittest.main()
