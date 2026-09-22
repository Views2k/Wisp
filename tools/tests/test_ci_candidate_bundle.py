"""Run the real CI checksum gate against local inert bundles; never run an installer."""
import hashlib
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest
import zipfile


ROOT = Path(__file__).resolve().parents[2]


def candidate_verification_script():
    lines = (ROOT / ".github" / "workflows" / "ci.yml").read_text(encoding="utf-8").splitlines()
    matches = [index for index, line in enumerate(lines)
               if line.strip() == "- name: Verify candidate installer bundle"]
    if len(matches) != 1:
        raise AssertionError("CI must contain exactly one candidate-bundle verification step.")
    start = matches[0]
    step_indent = len(lines[start]) - len(lines[start].lstrip())
    end = next((index for index in range(start + 1, len(lines))
                if lines[index].strip() and len(lines[index]) - len(lines[index].lstrip()) <= step_indent), len(lines))
    step = lines[start:end]
    if not any(line.strip() == "shell: pwsh" for line in step):
        raise AssertionError("The candidate gate must run with PowerShell 7.")
    runs = [index for index, line in enumerate(step) if line.strip() == "run: |"]
    if len(runs) != 1:
        raise AssertionError("The candidate gate must have one literal run block.")
    run = runs[0]
    body_indent = len(step[run]) - len(step[run].lstrip()) + 2
    body = []
    for line in step[run + 1:]:
        if line.strip() and len(line) - len(line.lstrip()) < body_indent:
            break
        body.append(line[body_indent:] if line.strip() else "")
    if not any(body):
        raise AssertionError("The candidate verification block is empty.")
    return "\n".join(body) + "\n"


class CiCandidateBundleTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory(prefix="wisp-candidate-bundle-",
                                                    dir=os.environ.get("WISP_CI_PLAN_TEST_TEMP"))
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        self.shell = shutil.which("pwsh")
        self.assertIsNotNone(self.shell, "PowerShell 7 is required for candidate-bundle fixtures.")
        project = self.root / "src" / "Wisp.App" / "Wisp.App.csproj"
        project.parent.mkdir(parents=True)
        project.write_text("<Project><PropertyGroup><Version>2.3.4</Version></PropertyGroup></Project>",
                           encoding="utf-8")
        self.output = self.root / "outputs"
        self.output.mkdir()
        self.exe = self.output / "Wisp-Setup-2.3.4.exe"
        self.archive = self.output / "Wisp-Setup-2.3.4.zip"
        self.exe.write_bytes(b"Inert checksum fixture; not an executable.\n")
        with zipfile.ZipFile(self.archive, "w") as archive:
            archive.writestr("fixture.txt", "Inert checksum fixture.\n")
        for artifact in (self.exe, self.archive):
            digest = hashlib.sha256(artifact.read_bytes()).hexdigest()
            artifact.with_name(artifact.name + ".sha256").write_text(
                f"{digest} *{artifact.name}\n", encoding="ascii")
        self.script = self.root / "verify.ps1"
        self.script.write_text(candidate_verification_script(), encoding="utf-8")

    def verify(self):
        return subprocess.run([self.shell, "-NoLogo", "-NoProfile", "-NonInteractive", "-File", str(self.script)],
                              cwd=self.root, capture_output=True, text=True, timeout=20)

    def test_exact_four_file_bundle_with_matching_hashes_passes(self):
        result = self.verify()
        self.assertEqual(result.returncode, 0, result.stderr)

    def test_corrupt_executable_is_rejected(self):
        self.exe.write_bytes(self.exe.read_bytes() + b"changed")
        result = self.verify()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("SHA-256 checksum", result.stderr)

    def test_corrupt_zip_is_rejected(self):
        self.archive.write_bytes(self.archive.read_bytes() + b"changed")
        result = self.verify()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("SHA-256 checksum", result.stderr)

    def test_missing_checksum_is_rejected(self):
        self.exe.with_name(self.exe.name + ".sha256").unlink()
        result = self.verify()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("exactly its EXE, ZIP and two checksums", result.stderr)

    def test_extra_file_is_rejected(self):
        (self.output / "unexpected.txt").write_text("Unexpected bundle content.", encoding="utf-8")
        result = self.verify()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("exactly its EXE, ZIP and two checksums", result.stderr)


if __name__ == "__main__":
    unittest.main()
