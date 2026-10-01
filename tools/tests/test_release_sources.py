"""Exercise source identity, bounded downloads and complete bundle enforcement."""
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import tempfile
import unittest
import zipfile


SPEC = importlib.util.spec_from_file_location("release_sources", Path(__file__).resolve().parents[1] / "release_sources.py")
sources = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(sources)


class ReleaseSourcesTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(
            prefix="wisp-release-sources-", dir=os.environ.get("WISP_RELEASE_SOURCES_TEST_TEMP"))
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.output = self.root / "outputs"
        (self.root / "src/Wisp.App").mkdir(parents=True)
        (self.root / "LICENSES").mkdir()
        (self.root / "src/Wisp.App/Wisp.App.csproj").write_text(
            "<Project><PropertyGroup><Version>2.6.0</Version></PropertyGroup></Project>", encoding="utf-8")
        self.manifest = {
            "sourceStatus": "complete", "sourceClosure": {"status": "complete", "distributionReady": True},
            "nativeFiles": [{"path": "libmpv-2.dll", "sha256": "1" * 64}],
            "noticeFiles": [], "sourceArchives": [], "libvlcSourceCompanion": {"sha256": "2" * 64}}
        enclosed = json.dumps(self.manifest).encode()
        self.archive = self.root / "reviewed.zip"
        with zipfile.ZipFile(self.archive, "w") as bundle:
            bundle.writestr("source-manifest.json", enclosed)
            bundle.writestr("upstream/source.tar.gz", b"source-fixture")
        self.pin = {
            "filename": "Wisp-2.6.0-library-sources.zip",
            "url": "https://github.com/Views2k/Wisp/releases/download/library-sources-2.6.0/Wisp-2.6.0-library-sources.zip",
            "bytes": self.archive.stat().st_size, "sha256": sources.digest(self.archive)}
        self.manifest["sourceCompanion"] = {
            "bytes": self.pin["bytes"], "sha256": self.pin["sha256"],
            "enclosedManifestSha256": hashlib.sha256(enclosed).hexdigest()}
        self.distribution = {"schemaVersion": 1, "version": "2.6.0", "archive": self.pin}
        self.write_manifests()

    def write_manifests(self):
        (self.root / "LICENSES/library-sources-distribution.json").write_text(json.dumps(self.distribution), encoding="utf-8")
        (self.root / "LICENSES/libmpv-source-manifest.json").write_text(json.dumps(self.manifest), encoding="utf-8")

    def complete_bundle(self):
        sources.prepare(self.root, self.output, self.archive)
        for name in ("Wisp-Setup-2.6.0.exe", "Wisp-Setup-2.6.0.zip"):
            artifact = self.output / name
            artifact.write_bytes(b"fixture")
            (self.output / (name + ".sha256")).write_text(f"{sources.digest(artifact)} *{name}\n", encoding="utf-8")

    def test_matching_sources_and_six_file_bundle_pass(self):
        self.complete_bundle()
        sources.verify_bundle(self.root, self.output)
        sources.prepare(self.root, self.output, self.archive)
        self.assertEqual(6, len(list(self.output.iterdir())))

    def test_old_four_file_bundle_is_rejected(self):
        self.complete_bundle()
        (self.output / self.pin["filename"]).unlink()
        (self.output / (self.pin["filename"] + ".sha256")).unlink()
        with self.assertRaises(ValueError):
            sources.verify_bundle(self.root, self.output)

    def test_extra_file_is_rejected(self):
        self.complete_bundle()
        (self.output / "unexpected.exe").write_bytes(b"extra")
        with self.assertRaises(ValueError):
            sources.verify_bundle(self.root, self.output)

    def test_tampered_installer_is_rejected(self):
        self.complete_bundle()
        (self.output / "Wisp-Setup-2.6.0.exe").write_bytes(b"changed")
        with self.assertRaises(ValueError):
            sources.verify_bundle(self.root, self.output)

    def test_self_consistent_but_wrong_source_archive_is_rejected(self):
        self.complete_bundle()
        target = self.output / self.pin["filename"]
        target.write_bytes(b"other source")
        target.with_suffix(".zip.sha256").write_text(f"{sources.digest(target)} *{target.name}\n", encoding="utf-8")
        with self.assertRaises(ValueError):
            sources.verify_bundle(self.root, self.output)

    def test_mismatched_dependency_source_pin_is_rejected(self):
        self.distribution["archive"]["sha256"] = "0" * 64
        self.write_manifests()
        with self.assertRaises(ValueError):
            sources.read_pins(self.root)

    def test_wrong_version_or_incomplete_sources_are_rejected(self):
        for field, value in (("status", "incomplete"), ("distributionReady", False)):
            with self.subTest(field=field):
                original = self.manifest["sourceClosure"][field]
                self.manifest["sourceClosure"][field] = value
                self.write_manifests()
                with self.assertRaises(ValueError):
                    sources.read_pins(self.root)
                self.manifest["sourceClosure"][field] = original
        self.distribution["version"] = "2.5.2"
        self.write_manifests()
        with self.assertRaises(ValueError):
            sources.read_pins(self.root)

    def test_changed_enclosed_manifest_is_rejected(self):
        self.manifest["sourceCompanion"]["enclosedManifestSha256"] = "0" * 64
        with self.assertRaises(ValueError):
            sources.verify_archive(self.archive, self.pin, self.manifest)

    def test_runtime_source_mismatch_is_rejected(self):
        self.manifest["nativeFiles"][0]["sha256"] = "0" * 64
        with self.assertRaises(ValueError):
            sources.verify_archive(self.archive, self.pin, self.manifest)

    def test_unexpected_existing_source_file_is_preserved(self):
        self.output.mkdir()
        target = self.output / self.pin["filename"]
        target.write_bytes(b"preserve")
        with self.assertRaises(ValueError):
            sources.prepare(self.root, self.output, self.archive)
        self.assertEqual(b"preserve", target.read_bytes())

    def test_checksum_cannot_name_a_different_artifact(self):
        self.complete_bundle()
        checksum = self.output / "Wisp-Setup-2.6.0.exe.sha256"
        checksum.write_text(f"{sources.digest(self.output / 'Wisp-Setup-2.6.0.exe')} *another.exe\n", encoding="utf-8")
        with self.assertRaises(ValueError):
            sources.verify_bundle(self.root, self.output)

    def test_download_and_redirect_destinations_are_restricted(self):
        for url in ("http://github.com/archive.zip", "https://example.invalid/source.zip",
                    "https://github.com:444/archive.zip", "https://account@github.com/archive.zip"):
            with self.subTest(url=url), self.assertRaises(ValueError):
                sources.check_url(url)
        sources.check_url(self.pin["url"])
        sources.check_url("https://release-assets.githubusercontent.com/source.zip?signed=fixture")


if __name__ == "__main__":
    unittest.main()
