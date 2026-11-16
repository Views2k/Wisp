"""Validate upgrade provenance without downloading or running an installer."""
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest


SCRIPT = Path(__file__).resolve().parents[2] / ".github/scripts/Get-InstallerUpgradeBaseline.ps1"
HARNESS = r"""
param([string]$Source, [string]$FixtureDirectory)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($Source, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw 'Baseline source does not parse.' }
$names = @('Select-UpgradeBaselineRelease', 'Get-UpgradeInstallerAsset', 'Assert-UpgradeInstallerBytes')
$functions = @($ast.FindAll({ param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -cin $names
}, $false))
if ($functions.Count -ne $names.Count) { throw 'Baseline helpers are missing.' }
foreach ($function in $functions) { . ([scriptblock]::Create($function.Extent.Text)) }
$fixture = Get-Content -LiteralPath (Join-Path $FixtureDirectory 'fixture.json') -Raw | ConvertFrom-Json
__BODY__
"""


def release(version="2.7.4"):
    name = f"Wisp-Setup-{version}.exe"
    return {"tag_name": f"v{version}", "draft": False, "prerelease": False, "immutable": True,
            "assets": [{"name": name, "state": "uploaded", "size": 7,
                        "digest": "sha256:" + hashlib.sha256(b"fixture").hexdigest(),
                        "browser_download_url": f"https://github.com/Views2k/Wisp/releases/download/v{version}/{name}"}]}


class InstallerUpgradeBaselineTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory(
            prefix="wisp-upgrade-baseline-", dir=os.environ.get("WISP_CI_PLAN_TEST_TEMP"))
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        self.shell = shutil.which("pwsh")
        self.assertIsNotNone(self.shell, "PowerShell 7 is required.")

    def evaluate(self, fixture, body):
        (self.root / "fixture.json").write_text(json.dumps(fixture), encoding="utf-8")
        script = self.root / "helpers.ps1"
        script.write_text(HARNESS.replace("__BODY__", body), encoding="utf-8")
        result = subprocess.run(
            [self.shell, "-NoLogo", "-NoProfile", "-NonInteractive", "-File", str(script),
             "-Source", str(SCRIPT), "-FixtureDirectory", str(self.root)],
            capture_output=True, text=True, timeout=20)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(result.stderr, "")
        return json.loads(result.stdout)

    def test_immutable_asset_and_matching_bytes(self):
        (self.root / "installer.fixture").write_bytes(b"fixture")
        result = self.evaluate(release(), r"""
            $asset = Get-UpgradeInstallerAsset $fixture '2.7.5'
            Assert-UpgradeInstallerBytes (Join-Path $FixtureDirectory 'installer.fixture') $asset
            $asset | ConvertTo-Json -Compress
        """)
        self.assertEqual(result["Version"], "2.7.4")
        self.assertEqual(result["Name"], "Wisp-Setup-2.7.4.exe")
        self.assertEqual(result["Sha256"], hashlib.sha256(b"fixture").hexdigest())

    def test_release_selection_uses_newest_older_immutable_stable_version(self):
        draft, prerelease, mutable = release("2.7.4"), release("2.7.3"), release("2.7.2")
        draft["draft"], prerelease["prerelease"], mutable["immutable"] = True, True, False
        fixtures = [release("2.6.9"), release("2.7.5"), release("2.10.0"),
                    draft, prerelease, mutable, release("2.7.1")]
        result = self.evaluate(fixtures, r"""
            (Select-UpgradeBaselineRelease $fixture '2.7.5').tag_name | ConvertTo-Json
        """)
        self.assertEqual(result, "v2.7.1")

    def test_invalid_or_nonpreceding_release_fails_closed(self):
        cases = [release("2.7.5"), release("2.7.6")]
        for key, value in [("draft", True), ("prerelease", True), ("immutable", False),
                           ("tag_name", "v2.7.04"), ("tag_name", "v2.7.4\n")]:
            item = release()
            item[key] = value
            cases.append(item)
        result = self.evaluate(cases, r"""
            @(foreach ($item in $fixture) {
                try { $null = Get-UpgradeInstallerAsset $item '2.7.5'; $false } catch { $true }
            }) | ConvertTo-Json -Compress
        """)
        self.assertEqual(result, [True] * len(cases))

    def test_missing_older_release_fails_closed(self):
        result = self.evaluate([release("2.7.5"), release("2.7.6")], r"""
            $rejected = $false
            try { $null = Select-UpgradeBaselineRelease $fixture '2.7.5' } catch { $rejected = $true }
            $rejected | ConvertTo-Json
        """)
        self.assertTrue(result)

    def test_untrusted_or_ambiguous_asset_metadata_fails_closed(self):
        cases = []
        for key, value in [("state", "new"), ("size", 0), ("size", 536870913),
                           ("digest", "sha256:" + "x" * 64),
                           ("browser_download_url", "https://example.test/Wisp-Setup-2.7.4.exe"),
                           ("browser_download_url", release()["assets"][0]["browser_download_url"] + "?redirect=1")]:
            item = release()
            item["assets"][0][key] = value
            cases.append(item)
        empty, duplicate = release(), release()
        empty["assets"] = []
        duplicate["assets"].append(duplicate["assets"][0].copy())
        cases.extend([empty, duplicate])
        result = self.evaluate(cases, r"""
            @(foreach ($item in $fixture) {
                try { $null = Get-UpgradeInstallerAsset $item '2.7.5'; $false } catch { $true }
            }) | ConvertTo-Json -Compress
        """)
        self.assertEqual(result, [True] * len(cases))

    def test_corrupted_and_truncated_downloads_fail_closed(self):
        (self.root / "corrupt.fixture").write_bytes(b"changed")
        (self.root / "short.fixture").write_bytes(b"fixtur")
        result = self.evaluate(release(), r"""
            $asset = Get-UpgradeInstallerAsset $fixture '2.7.5'
            @(foreach ($name in @('corrupt.fixture', 'short.fixture')) {
                try { Assert-UpgradeInstallerBytes (Join-Path $FixtureDirectory $name) $asset; $false }
                catch { $true }
            }) | ConvertTo-Json -Compress
        """)
        self.assertEqual(result, [True, True])


if __name__ == "__main__":
    unittest.main()
