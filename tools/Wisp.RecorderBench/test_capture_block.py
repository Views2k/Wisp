import contextlib
import csv
import hashlib
import io
import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest import mock

import analyze_capture_block as block

existing = block.existing


ORIGIN = (1 << 60) + 123
FREQUENCY = 10_000_000
WIDTH = 30_000_000
STRIDE = 35_000_001
HEADERS = ["Application", "ProcessID", "SwapChainAddress", "PresentRuntime", "SyncInterval",
           "PresentFlags", "AllowsTearing", "PresentMode", "CPUStartQPC", "FrameTime",
           "CPUBusy", "CPUWait", "DisplayLatency", "DisplayedTime"]


def fixture(order=None):
    order = order or block.ORDERS[0]
    windows, rows = [], []
    for index, phase in enumerate(order):
        start = ORIGIN + index * STRIDE
        windows.append({"phase": phase, "from_qpc": start, "to_qpc": start + WIDTH})
        duration = 20 if phase == "baseline" else 25
        for elapsed in range(0, 3000, duration):
            rows.append(dict(zip(HEADERS, [
                "private-game-name.exe", "42", "0x0000000000000ABC", "DXGI", "0", "0", "1",
                "Hardware: Independent Flip", str(start + elapsed * FREQUENCY // 1000),
                str(duration), "1", "1", "0", str(duration)])))
    manifest = {"schema_version": 2, "csv": "private-session.csv", "csv_sha256": "0" * 64,
                "pid": 42, "swapchain": "0xabc", "qpc_frequency": FREQUENCY,
                "minimum_intervals": 100, "minimum_window_ms": 1000,
                "long_frame_thresholds_ms": [20, 50], "windows": windows}
    return manifest, rows


def write_csv(root, manifest, rows, headers=HEADERS):
    output = io.StringIO(newline="")
    writer = csv.DictWriter(output, fieldnames=headers)
    writer.writeheader()
    writer.writerows(rows)
    data = output.getvalue().encode("utf-8")
    (root / manifest["csv"]).write_bytes(data)
    manifest["csv_sha256"] = hashlib.sha256(data).hexdigest()


def analyze(manifest, rows, headers=HEADERS):
    with tempfile.TemporaryDirectory() as directory:
        root = Path(directory)
        write_csv(root, manifest, rows, headers)
        return block.analyze_manifest(manifest, root)


class CaptureBlockTests(unittest.TestCase):
    def test_real_isolated_cli_loads_only_the_known_sibling_from_another_directory(self):
        manifest, rows = fixture()
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            write_csv(root, manifest, rows)
            manifest_path = root / "private-manifest.json"
            manifest_path.write_text(json.dumps(manifest), encoding="utf-8")
            (root / "analyze_presentmon.py").write_text('raise RuntimeError("wrong_import_directory")\n', encoding="utf-8")
            completed = subprocess.run(
                [sys.executable, "-I", "-B", str(Path(block.__file__).resolve()), "--manifest", str(manifest_path)],
                cwd=root, capture_output=True, text=True, timeout=30, check=False)
        self.assertEqual(completed.returncode, 0)
        self.assertEqual(completed.stderr, "")
        result = json.loads(completed.stdout)
        self.assertEqual(result["result"], "evidence_summary")
        self.assertEqual(result["experimental_block_count"], 1)
        self.assertEqual(result["decision"], "inconclusive")
        self.assertNotIn("private", completed.stdout)
        self.assertNotIn(str(ORIGIN), completed.stdout)

    def test_abba_uses_exact_integer_origin_and_existing_statistics_for_one_block(self):
        manifest, rows = fixture()
        with mock.patch.object(existing, "summarize_csv", wraps=existing.summarize_csv) as summarize:
            result = analyze(manifest, rows)
        self.assertEqual(summarize.call_count, 4)
        self.assertEqual(result["experimental_block_count"], 1)
        self.assertEqual(result["window_count"], 4)
        self.assertFalse(result["independent_replication_established"])
        self.assertEqual(result["decision"], "inconclusive")
        self.assertEqual(result["phase_order"], "ABBA")
        first, second = result["window_summaries"][:2]
        self.assertEqual(first["observed_display_interval_ms"], {"from": 0, "to": 3000})
        self.assertEqual(second["requested_window_ms"]["from"], 3500.0001)
        self.assertEqual(first["average_displayed_fps"], 50)
        self.assertEqual(second["average_displayed_fps"], 40)
        self.assertEqual(first["p99_displayed_ms"], 20)
        self.assertEqual(second["worst_one_percent_mean_fps"], 40)
        self.assertEqual(result["within_block_capture_minus_baseline"], {
            "average_displayed_fps": -10, "p99_displayed_ms": 5, "worst_one_percent_mean_fps": -10})
        self.assertIn("qpc_derived", first["schema"])
        self.assertNotIn("paired_repeat_count", result)

    def test_inverse_baab_is_one_block_with_same_effect_direction(self):
        manifest, rows = fixture(block.ORDERS[1])
        result = analyze(manifest, rows)
        self.assertEqual(result["phase_order"], "BAAB")
        self.assertEqual(result["experimental_block_count"], 1)
        self.assertEqual(result["within_block_capture_minus_baseline"]["average_displayed_fps"], -10)

    def test_csv_file_is_opened_once_and_hash_is_verified_before_summary(self):
        manifest, rows = fixture()
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            write_csv(root, manifest, rows)
            actual_open = Path.open
            opened = []

            def counting_open(path, *args, **kwargs):
                opened.append(path)
                return actual_open(path, *args, **kwargs)

            with mock.patch.object(Path, "open", counting_open):
                block.analyze_manifest(manifest, root)
            self.assertEqual(opened, [root / manifest["csv"]])
            manifest["csv_sha256"] = "0" * 64
            with mock.patch.object(existing, "summarize_csv") as summarize:
                with self.assertRaisesRegex(existing.EvidenceError, "csv_hash_mismatch"):
                    block.analyze_manifest(manifest, root)
                summarize.assert_not_called()

    def test_selected_name_and_presentation_changes_across_phases_are_rejected(self):
        for field, value in [("Application", "other-private-name.exe"), ("PresentMode", "Composed: Flip"),
                             ("SyncInterval", "1"), ("AllowsTearing", "0")]:
            manifest, rows = fixture()
            rows[160][field] = value
            with self.subTest(field=field), self.assertRaisesRegex(
                    existing.EvidenceError, "shared_session_identity_or_presentation_changed"):
                analyze(manifest, rows)

    def test_selected_frame_type_changes_across_phases_are_rejected(self):
        manifest, rows = fixture()
        rows = [dict(row, FrameType="Application") for row in rows]
        rows[160]["FrameType"] = "AMD AFMF"
        with self.assertRaisesRegex(existing.EvidenceError, "shared_session_identity_or_presentation_changed"):
            analyze(manifest, rows, HEADERS + ["FrameType"])

    def test_other_identities_are_ignored_without_exposing_them(self):
        manifest, rows = fixture()
        rows.append(dict(rows[0], ProcessID="99", SwapChainAddress="0xdef", CPUStartQPC="1", DisplayedTime="not-numeric"))
        result = analyze(manifest, rows)
        self.assertEqual(result["window_summaries"][0]["counts"]["other_identity_rows_ignored"], 1)
        self.assertEqual(result["window_summaries"][0]["observed_display_interval_ms"]["from"], 0)
        output = json.dumps(result)
        for private in ("private", "0xabc", "0xdef", str(ORIGIN), "ProcessID", "SwapChainAddress"):
            self.assertNotIn(private, output)

    def test_a_replacement_identity_cannot_supply_a_missing_phase(self):
        for field, value in (("ProcessID", "99"), ("SwapChainAddress", "0xdef")):
            manifest, rows = fixture()
            for row in rows[150:270]:
                row[field] = value
            with self.subTest(field=field), self.assertRaises(existing.EvidenceError):
                analyze(manifest, rows)

    def test_phase_order_count_overlap_repeat_and_width_are_rejected(self):
        for alter, reason in [
            (lambda w: w.pop(), "invalid_block_window_count"),
            (lambda w: w[0].update(phase="capture_only"), "invalid_block_phase_order"),
            (lambda w: w[1].update(from_qpc=w[0]["from_qpc"], to_qpc=w[0]["to_qpc"]), "overlapping_or_unordered_windows"),
            (lambda w: w[1].update(from_qpc=w[0]["to_qpc"] - 1), "overlapping_or_unordered_windows"),
            (lambda w: w[1].update(to_qpc=w[1]["to_qpc"] + 1), "unequal_qpc_window_widths"),
            (lambda w: w[0].update(pid=99), "invalid_block_window"),
        ]:
            manifest, _ = fixture()
            alter(manifest["windows"])
            with self.subTest(reason=reason), self.assertRaisesRegex(existing.EvidenceError, reason):
                block.validate_manifest(manifest)

    def test_frequency_and_absolute_windows_require_positive_actual_integers(self):
        for value in (0, -1, True, 10_000_000.0, "10000000", None, 1 << 63):
            manifest, _ = fixture()
            manifest["qpc_frequency"] = value
            with self.subTest(frequency=value), self.assertRaisesRegex(existing.EvidenceError, "invalid_qpc_frequency"):
                block.validate_manifest(manifest)
        for value in (0, -1, False, float(ORIGIN), str(ORIGIN), None, 1 << 63):
            manifest, _ = fixture()
            manifest["windows"][0]["from_qpc"] = value
            with self.subTest(start=value), self.assertRaisesRegex(existing.EvidenceError, "invalid_qpc_window"):
                block.validate_manifest(manifest)

    def test_manifest_missing_extra_and_wrong_version_fields_fail_closed(self):
        for field in block.MANIFEST_KEYS:
            manifest, _ = fixture()
            del manifest[field]
            with self.subTest(missing=field), self.assertRaisesRegex(existing.EvidenceError, "unsupported_block_manifest"):
                block.validate_manifest(manifest)
        for key, value in (("trials", []), ("declared_sensitivity", {}), ("schema_version", 1), ("schema_version", True)):
            manifest, _ = fixture()
            manifest[key] = value
            with self.subTest(key=key), self.assertRaisesRegex(existing.EvidenceError, "unsupported_block_manifest"):
                block.validate_manifest(manifest)

    def test_wrong_missing_duplicate_and_mixed_clock_columns_are_rejected(self):
        for clock in ("CPUStartTime", "CPUStartQPCTime", "CPUStartDateTime", "TimeInSeconds", "DisplayTimeAbs"):
            manifest, rows = fixture()
            rows = [dict(row, **{clock: "1"}) for row in rows]
            with self.subTest(clock=clock), self.assertRaisesRegex(existing.EvidenceError, "mixed_or_unsupported_time_schema"):
                analyze(manifest, rows, HEADERS + [clock])
        manifest, rows = fixture()
        with self.assertRaisesRegex(existing.EvidenceError, "unsupported_qpc_v2_schema"):
            analyze(manifest, rows, HEADERS + ["CPUStartQPC"])
        manifest, rows = fixture()
        rows = [{key: value for key, value in row.items() if key != "DisplayedTime"} for row in rows]
        with self.assertRaisesRegex(existing.EvidenceError, "unsupported_qpc_v2_schema"):
            analyze(manifest, rows, [key for key in HEADERS if key != "DisplayedTime"])

    def test_noninteger_nonpositive_or_nonincreasing_csv_qpc_is_rejected(self):
        for value in ("0", "-1", "nan", "1.0", "1e6", "+100", " 100", str(1 << 63)):
            manifest, rows = fixture()
            rows[10]["CPUStartQPC"] = value
            with self.subTest(value=value), self.assertRaisesRegex(existing.EvidenceError, "invalid_cpu_qpc"):
                analyze(manifest, rows)
        manifest, rows = fixture()
        rows[10]["CPUStartQPC"] = rows[9]["CPUStartQPC"]
        with self.assertRaisesRegex(existing.EvidenceError, "non_increasing_selected_cpu_qpc"):
            analyze(manifest, rows)

    def test_na_and_complete_interval_boundaries_reuse_existing_rules(self):
        manifest, rows = fixture()
        rows.insert(1, dict(rows[0], CPUStartQPC=str(ORIGIN + 100_000), DisplayedTime="NA", DisplayLatency="NA"))
        result = analyze(manifest, rows)
        first = result["window_summaries"][0]
        self.assertEqual(first["counts"]["not_displayed_rows_in_cpu_window"], 1)
        self.assertEqual(first["counts"]["complete_displayed_intervals"], 150)
        manifest, rows = fixture()
        for window in manifest["windows"]:
            window["from_qpc"] += 50_000
            window["to_qpc"] += 50_000
        first = analyze(manifest, rows)["window_summaries"][0]
        self.assertEqual(first["counts"]["complete_displayed_intervals"], 149)
        self.assertEqual(first["counts"]["boundary_intervals_excluded"], 1)
        self.assertEqual(first["observed_display_interval_ms"], {"from": 20, "to": 3000})

    def test_gap_invalid_duration_and_insufficient_observation_still_fail(self):
        for value, reason in (("19", "non_contiguous_display_intervals"), ("0", "invalid_displayed_duration")):
            manifest, rows = fixture()
            rows[10]["DisplayedTime"] = value
            with self.subTest(value=value), self.assertRaisesRegex(existing.EvidenceError, reason):
                analyze(manifest, rows)
        manifest, rows = fixture()
        manifest["minimum_intervals"] = 200
        with self.assertRaisesRegex(existing.EvidenceError, "insufficient_displayed_intervals"):
            analyze(manifest, rows)

    def test_oversized_csv_is_rejected_without_opening_it(self):
        manifest, _ = fixture()
        with mock.patch.object(Path, "stat", return_value=SimpleNamespace(st_size=existing.MAXIMUM_CSV_BYTES + 1)), \
                mock.patch.object(Path, "open") as opened:
            with self.assertRaisesRegex(existing.EvidenceError, "csv_size_limit"):
                block.analyze_manifest(manifest, Path("."))
            opened.assert_not_called()

    def test_errors_never_echo_private_paths_values_or_duplicate_keys(self):
        for arguments in (["--manifest", "private-missing-path.json"], ["--unknown", "private-value"]):
            output = io.StringIO()
            with self.subTest(arguments=arguments), contextlib.redirect_stdout(output):
                self.assertEqual(block.main(arguments), 2)
            self.assertNotIn("private", output.getvalue())
            self.assertEqual(json.loads(output.getvalue())["result"], "rejected")
        with self.assertRaisesRegex(existing.EvidenceError, "duplicate_manifest_field"):
            json.loads('{"pid":42,"pid":99}', object_pairs_hook=block.unique_object)


if __name__ == "__main__":
    unittest.main()
