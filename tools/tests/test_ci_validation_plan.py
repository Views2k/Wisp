"""Offline fixtures for strict published-release validation reuse; never call GitHub."""
import copy
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / ".github" / "scripts" / "Get-CiValidationPlan.ps1"


class CiValidationPlanTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory(prefix="wisp-ci-plan-", dir=os.environ.get("WISP_CI_PLAN_TEST_TEMP"))
        self.addCleanup(self.directory.cleanup)
        self.path = Path(self.directory.name)
        self.repo = self.path / "repo"
        self.repo.mkdir()
        self.shell = shutil.which("pwsh")
        self.assertIsNotNone(self.shell, "PowerShell 7 is required for CI-plan fixtures.")
        self.git("init", "--quiet")
        self.git("config", "user.name", "CI fixture")
        self.git("config", "user.email", "fixture@invalid")
        self.git("config", "core.filemode", "false")
        for name, content in {
            "README.md": "# Wisp\n",
            "CHANGELOG.md": "## 2.3.4 - 2026-09-21\n",
            "docs/releases/Wisp-2.3.4-release-notes.md": "# Wisp 2.3.4\n",
            "src/program.cs": "// unchanged executable input\n",
            "installer/build.ps1": "# unchanged packaging input\n",
            "THIRD-PARTY-NOTICES.md": "Packaged notice\n",
            ".github/workflows/ci.yml": "# trusted workflow input\n",
        }.items():
            self.write(name, content)
        self.commit()
        self.baseline = self.git("rev-parse", "HEAD")
        self.run_suffix = f"actions/workflows/ci.yml/runs?head_sha={self.baseline}&event=push&status=success&per_page=100"
        self.job_suffix = "actions/runs/123/jobs?filter=latest&per_page=100"
        self.api = {
            "releases/latest": {"tag_name": "v2.3.4", "draft": False, "prerelease": False,
                                "immutable": True, "published_at": "2026-09-21T00:00:00Z"},
            "git/ref/tags/v2.3.4": {"object": {"type": "commit", "sha": self.baseline}},
            self.run_suffix: {"workflow_runs": [{"id": 123, "head_sha": self.baseline,
                "head_branch": "v2.3.4", "path": ".github/workflows/ci.yml", "event": "push",
                "status": "completed", "conclusion": "success", "repository": {"full_name": "Views2k/Wisp"},
                "head_repository": {"full_name": "Views2k/Wisp"}}]},
            self.job_suffix: {"total_count": 3, "jobs": [
                self.job("Build and test", "Build verified installer bundle"),
                self.job("Build installer", "Test installer lifecycle"),
                self.job("Publish release", "Publish immutable release")
            ]},
        }

    @staticmethod
    def job(name, step):
        return {"name": name, "status": "completed", "conclusion": "success",
                "steps": [{"name": step, "status": "completed", "conclusion": "success"}]}

    def git(self, *args):
        return subprocess.run(["git", "-C", str(self.repo), *args], check=True, capture_output=True,
                              text=True).stdout.strip()

    def write(self, name, text):
        target = self.repo / name
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(text, encoding="utf-8")

    def commit(self):
        self.git("add", "--all")
        self.git("commit", "--quiet", "-m", "fixture")

    def docs_change(self):
        self.write("README.md", "# Wisp\nDocumentation update.\n")
        self.commit()

    def evaluate(self, api=None, event="pull_request", ref="refs/pull/1/merge"):
        fixture = self.path / "api.json"
        fixture.write_text(json.dumps(api if api is not None else self.api), encoding="utf-8")
        process = subprocess.run([self.shell, "-NoLogo", "-NoProfile", "-NonInteractive", "-File", str(SCRIPT),
            "-RepositoryRoot", str(self.repo), "-Repository", "Views2k/Wisp", "-EventName", event,
            "-Ref", ref, "-ApiFixturePath", str(fixture)], capture_output=True, text=True, timeout=25)
        self.assertEqual(process.returncode, 0, process.stderr)
        result = json.loads(process.stdout)
        # A fixture transport must NEVER authorize a real workflow to skip validation.
        self.assertEqual(result["mode"], "full")
        return result["fixturePlan"]

    def test_documentation_reuses_exact_fully_published_baseline(self):
        self.docs_change()
        result = self.evaluate()
        self.assertEqual(result["mode"], "docs-only")
        self.assertEqual(result["baselineCommit"], self.baseline)
        self.assertEqual(result["baselineRunId"], 123)
        self.assertEqual(result["changedDocumentation"], ["README.md"])
        self.assertFalse(result["producesInstaller"])
        self.assertEqual(len(result["inputDigest"]), 64)

    def test_main_push_can_use_the_same_proof(self):
        self.docs_change()
        self.assertEqual(self.evaluate(event="push", ref="refs/heads/main")["mode"], "docs-only")

    def test_tag_dispatch_and_nonmain_push_are_always_full(self):
        self.docs_change()
        for event, ref in [("push", "refs/tags/v2.3.4"), ("workflow_dispatch", "refs/heads/main"),
                           ("push", "refs/heads/feature")]:
            with self.subTest(event=event, ref=ref):
                self.assertEqual(self.evaluate(event=event, ref=ref)["mode"], "full")

    def test_code_packaged_markdown_and_workflow_changes_require_full(self):
        for name in ["src/program.cs", "THIRD-PARTY-NOTICES.md", ".github/workflows/ci.yml"]:
            with self.subTest(name=name):
                self.git("reset", "--hard", self.baseline)
                self.write(name, "Changed\n")
                self.commit()
                self.assertEqual(self.evaluate()["mode"], "full")

    def test_nested_documentation_addition_is_eligible(self):
        self.write("docs/guides/new/setup.md", "# Setup\n")
        self.commit()
        self.assertEqual(self.evaluate()["mode"], "docs-only")

    def test_rename_from_source_to_documentation_does_not_hide_input_change(self):
        self.git("mv", "src/program.cs", "docs/program.md")
        self.commit()
        self.assertEqual(self.evaluate()["mode"], "full")

    def test_executable_documentation_blob_is_not_eligible(self):
        self.git("update-index", "--chmod=+x", "README.md")
        self.git("commit", "--quiet", "-m", "fixture mode")
        self.assertEqual(self.evaluate()["mode"], "full")

    def test_release_must_be_immutable_published_stable(self):
        self.docs_change()
        for key, value in [("draft", True), ("prerelease", True), ("immutable", False),
                           ("published_at", None), ("tag_name", "v2.3.4-beta")]:
            with self.subTest(key=key):
                api = copy.deepcopy(self.api)
                api["releases/latest"][key] = value
                self.assertEqual(self.evaluate(api)["mode"], "full")

    def test_baseline_must_be_same_repository_tag_push(self):
        self.docs_change()
        for field, value in [("head_branch", "main"), ("head_sha", "0" * 40),
                             ("event", "pull_request"), ("head_repository", {"full_name": "fork/Wisp"})]:
            with self.subTest(field=field):
                api = copy.deepcopy(self.api)
                api[self.run_suffix]["workflow_runs"][0][field] = value
                self.assertEqual(self.evaluate(api)["mode"], "full")

    def test_skipped_or_missing_full_steps_cannot_inherit_success(self):
        self.docs_change()
        for index in range(3):
            with self.subTest(job=index):
                api = copy.deepcopy(self.api)
                api[self.job_suffix]["jobs"][index]["steps"][0]["conclusion"] = "skipped"
                self.assertEqual(self.evaluate(api)["mode"], "full")
        api = copy.deepcopy(self.api)
        api[self.job_suffix]["jobs"][0]["steps"][0]["name"] = "Documentation evidence reuse"
        self.assertEqual(self.evaluate(api)["mode"], "full")

    def test_missing_api_evidence_falls_back_to_full(self):
        self.docs_change()
        self.assertEqual(self.evaluate({})["mode"], "full")

    def test_dirty_checkout_cannot_reuse_baseline(self):
        self.docs_change()
        self.write("src/program.cs", "Uncommitted input\n")
        self.assertEqual(self.evaluate()["mode"], "full")

    def test_annotated_release_tag_resolves_to_the_verified_commit(self):
        self.docs_change()
        api = copy.deepcopy(self.api)
        tag_object = "1" * 40
        api["git/ref/tags/v2.3.4"]["object"] = {"type": "tag", "sha": tag_object}
        api[f"git/tags/{tag_object}"] = {"object": {"type": "commit", "sha": self.baseline}}
        self.assertEqual(self.evaluate(api)["mode"], "docs-only")

    def test_stable_correction_tag_and_uppercase_forms_are_supported(self):
        self.docs_change()
        for tag in ["v2.3.4-stable", "V2.3.4-STABLE"]:
            with self.subTest(tag=tag):
                api = copy.deepcopy(self.api)
                api["releases/latest"]["tag_name"] = tag
                api[f"git/ref/tags/{tag}"] = api.pop("git/ref/tags/v2.3.4")
                api[self.run_suffix]["workflow_runs"][0]["head_branch"] = tag
                self.assertEqual(self.evaluate(api)["mode"], "docs-only")

    def test_unrelated_history_cannot_reuse_identical_executable_tree(self):
        self.git("checkout", "--orphan", "unrelated")
        self.docs_change()
        self.assertEqual(self.evaluate()["mode"], "full")


if __name__ == "__main__":
    unittest.main()
