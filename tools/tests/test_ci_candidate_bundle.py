"""Run CI's real release verifier against inert bundles; never run an installer."""
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest
import zipfile


ROOT = Path(__file__).resolve().parents[2]


def candidate_verification_command():
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
    runs = [line.strip()[5:] for line in step if line.strip().startswith("run: ")]
    if len(runs) != 1:
        raise AssertionError("The candidate gate must have one verifier command.")
    if runs[0] != "python ./tools/release_sources.py verify --directory outputs":
        raise AssertionError("The candidate gate must verify outputs with the production release verifier.")
    # Use this test's interpreter for CI's Python command, without relying on PATH aliases.
    return [sys.executable, *runs[0].split()[1:]]


class CiCandidateBundleTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory(prefix="wisp-candidate-bundle-",
                                                    dir=os.environ.get("WISP_CI_PLAN_TEST_TEMP"))
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        self.command = candidate_verification_command()
        (self.root / "tools").mkdir()
        shutil.copyfile(ROOT / "tools/release_sources.py", self.root / "tools/release_sources.py")
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
        self.sources = self.output / "Wisp-2.3.4-library-sources.zip"
        self.manifest = {
            "sourceStatus": "complete", "sourceClosure": {"status": "complete", "distributionReady": True},
            "nativeFiles": [{"path": "libmpv-2.dll", "sha256": "1" * 64}],
            "noticeFiles": [], "sourceArchives": [], "libvlcSourceCompanion": {"sha256": "2" * 64}}
        enclosed = json.dumps(self.manifest).encode("utf-8")
        with zipfile.ZipFile(self.sources, "w") as archive:
            archive.writestr("source-manifest.json", enclosed)
            archive.writestr("upstream/source.tar.gz", b"Inert source fixture.\n")
        source_digest = hashlib.sha256(self.sources.read_bytes()).hexdigest()
        self.manifest["sourceCompanion"] = {
            "bytes": self.sources.stat().st_size, "sha256": source_digest,
            "enclosedManifestSha256": hashlib.sha256(enclosed).hexdigest()}
        self.distribution = {"schemaVersion": 1, "version": "2.3.4", "archive": {
            "filename": self.sources.name,
            "url": f"https://github.com/Views2k/Wisp/releases/download/library-sources-2.3.4/{self.sources.name}",
            "bytes": self.sources.stat().st_size, "sha256": source_digest}}
        (self.root / "LICENSES").mkdir()
        self.write_manifests()
        for artifact in (self.exe, self.archive, self.sources):
            self.write_checksum(artifact)

    def write_manifests(self):
        (self.root / "LICENSES/library-sources-distribution.json").write_text(
            json.dumps(self.distribution), encoding="utf-8")
        (self.root / "LICENSES/libmpv-source-manifest.json").write_text(
            json.dumps(self.manifest), encoding="utf-8")

    def write_checksum(self, artifact):
        digest = hashlib.sha256(artifact.read_bytes()).hexdigest()
        artifact.with_name(artifact.name + ".sha256").write_text(
            f"{digest} *{artifact.name}\n", encoding="ascii")

    def verify(self):
        return subprocess.run(self.command, cwd=self.root, capture_output=True, text=True, timeout=20)

    def assert_rejected(self):
        result = self.verify()
        self.assertEqual(result.returncode, 1, result.stderr)
        self.assertEqual(result.stderr, "Release source validation failed; no release may be published.\n")
        self.assertEqual(result.stdout, "")

    def test_exact_six_file_bundle_with_matching_hashes_passes(self):
        result = self.verify()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(result.stdout, "Complete release bundle verified.\n")
        self.assertEqual(result.stderr, "")
        self.assertEqual(len(list(self.output.iterdir())), 6)

    def test_corrupt_executable_is_rejected(self):
        self.exe.write_bytes(self.exe.read_bytes() + b"changed")
        self.assert_rejected()

    def test_corrupt_zip_is_rejected(self):
        self.archive.write_bytes(self.archive.read_bytes() + b"changed")
        self.assert_rejected()

    def test_missing_checksum_is_rejected(self):
        self.exe.with_name(self.exe.name + ".sha256").unlink()
        self.assert_rejected()

    def test_extra_file_is_rejected(self):
        (self.output / "unexpected.txt").write_text("Unexpected bundle content.", encoding="utf-8")
        self.assert_rejected()

    def test_old_four_file_bundle_is_rejected(self):
        self.sources.unlink()
        self.sources.with_name(self.sources.name + ".sha256").unlink()
        self.assert_rejected()

    def test_missing_source_checksum_is_rejected(self):
        self.sources.with_name(self.sources.name + ".sha256").unlink()
        self.assert_rejected()

    def test_changed_sources_with_matching_checksum_are_rejected(self):
        with zipfile.ZipFile(self.sources, "a") as archive:
            archive.writestr("unreviewed.txt", "Changed sources.")
        self.write_checksum(self.sources)
        self.assert_rejected()

    def test_incomplete_source_closure_is_rejected(self):
        self.manifest["sourceClosure"]["distributionReady"] = False
        self.write_manifests()
        self.assert_rejected()

    def test_runtime_source_manifest_mismatch_is_rejected(self):
        self.manifest["nativeFiles"][0]["sha256"] = "0" * 64
        self.write_manifests()
        self.assert_rejected()

    def test_checksum_naming_another_artifact_is_rejected(self):
        checksum = self.exe.with_name(self.exe.name + ".sha256")
        checksum.write_text(checksum.read_text(encoding="ascii").replace(self.exe.name, self.archive.name),
                            encoding="ascii")
        self.assert_rejected()

    def test_wrong_version_source_pin_is_rejected(self):
        self.distribution["version"] = "2.3.5"
        self.write_manifests()
        self.assert_rejected()


if __name__ == "__main__":
    unittest.main()
