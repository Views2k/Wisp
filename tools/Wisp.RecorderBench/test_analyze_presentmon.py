import contextlib
import csv
import io
import json
import tempfile
import unittest
from pathlib import Path

import analyze_presentmon as analyzer


HEADERS = ["Application", "ProcessID", "SwapChainAddress", "PresentRuntime", "SyncInterval",
           "PresentFlags", "AllowsTearing", "PresentMode", "CPUStartTime", "FrameTime",
           "CPUBusy", "CPUWait", "DisplayLatency", "DisplayedTime"]
SELECTION = {"pid": 42, "swapchain": "0xabc", "from_ms": 0, "to_ms": 4000}
LIMITS = (100, 1000, [50, 100])


def rows(durations=None):
    time = 0
    result = []
    for duration in durations or [20] * 100:
        result.append(dict(zip(HEADERS, ["private-name.exe", "42", "0x0000000000000ABC", "DXGI", "0",
                                        "0", "1", "Hardware: Independent Flip", str(time), str(duration),
                                        "1", "1", "0", str(duration)])))
        time += duration
    return result


def csv_text(records, headers=HEADERS):
    output = io.StringIO(newline="")
    writer = csv.DictWriter(output, fieldnames=headers)
    writer.writeheader()
    writer.writerows(records)
    return output.getvalue()


def summarize(records, selection=SELECTION, limits=LIMITS):
    return analyzer.summarize_csv(io.StringIO(csv_text(records), newline=""), selection, limits)


def comparison(first, second, manifest=None):
    return analyzer.compare_summaries([
        {"phase": "baseline", "pair": 1, "summary": first},
        {"phase": "capture_only", "pair": 1, "summary": second},
    ], manifest or {})


class PresentMonTests(unittest.TestCase):
    def test_duration_weighted_fps_nearest_rank_and_worst_one_percent_mean(self):
        result = summarize(rows([10] * 198 + [80, 100]))
        self.assertAlmostEqual(result["average_displayed_fps"], 200000 / 2160)
        self.assertEqual(result["p99_displayed_ms"], 10)
        self.assertEqual(result["worst_one_percent_interval_count"], 2)
        self.assertEqual(result["worst_one_percent_mean_ms"], 90)
        self.assertAlmostEqual(result["worst_one_percent_mean_fps"], 1000 / 90)
        self.assertEqual(result["long_frames"], [
            {"strictly_greater_than_ms": 50, "count": 2},
            {"strictly_greater_than_ms": 100, "count": 0},
        ])
        self.assertEqual(result["observed_display_interval_ms"], {"from": 0, "to": 2160})

    def test_na_is_non_displayed_not_a_zero_duration(self):
        records = rows()
        dropped = dict(records[0], CPUStartTime="10", DisplayedTime="NA", DisplayLatency="NA")
        records.insert(1, dropped)
        result = summarize(records)
        self.assertEqual(result["counts"]["not_displayed_rows_in_cpu_window"], 1)
        self.assertEqual(result["counts"]["complete_displayed_intervals"], 100)
        self.assertEqual(result["average_displayed_fps"], 50)

    def test_exact_pid_and_swapchain_filtering_does_not_blend_other_metrics(self):
        records = rows()
        records.insert(1, dict(records[0], ProcessID="99", DisplayedTime="not-a-number"))
        records.insert(2, dict(records[0], SwapChainAddress="0xdef", DisplayedTime="not-a-number"))
        result = summarize(records)
        self.assertEqual(result["counts"]["other_identity_rows_ignored"], 2)
        self.assertEqual(result["counts"]["complete_displayed_intervals"], 100)
        text = json.dumps(result)
        for private in ("private-name.exe", "ProcessID", "SwapChainAddress", "0xabc", "0xdef"):
            self.assertNotIn(private, text)

    def test_boundary_intervals_are_excluded_not_shortened(self):
        result = summarize(rows([20] * 103), dict(SELECTION, from_ms=5, to_ms=2035))
        self.assertEqual(result["counts"]["boundary_intervals_excluded"], 2)
        self.assertEqual(result["counts"]["complete_displayed_intervals"], 100)
        self.assertEqual(result["observed_display_interval_ms"], {"from": 20, "to": 2020})
        self.assertEqual(result["displayed_duration_sum_ms"], 2000)

    def test_insufficient_samples_or_duration_are_rejected(self):
        for records, limits, expected in [
            (rows([20] * 99), LIMITS, "insufficient_displayed_intervals"),
            (rows([5] * 100), LIMITS, "insufficient_observed_display_time"),
            (rows(), (100, 5000, [50]), "insufficient_requested_window"),
        ]:
            with self.subTest(expected=expected), self.assertRaisesRegex(analyzer.EvidenceError, expected):
                summarize(records, limits=limits)

    def test_invalid_durations_and_mismatched_na_are_rejected(self):
        for value in ("0", "-1", "nan", "inf", "", "redacted-not-numeric"):
            records = rows()
            records[5]["DisplayedTime"] = value
            with self.subTest(value=value), self.assertRaisesRegex(analyzer.EvidenceError, "invalid_displayed_duration"):
                summarize(records)
        records = rows()
        records[5]["DisplayedTime"] = "NA"
        with self.assertRaisesRegex(analyzer.EvidenceError, "inconsistent_non_displayed_row"):
            summarize(records)

    def test_gaps_overlaps_and_duplicate_timestamps_are_rejected(self):
        for field, value, expected in [
            ("DisplayedTime", "19", "non_contiguous_display_intervals"),
            ("DisplayedTime", "21", "non_contiguous_display_intervals"),
            ("CPUStartTime", "80", "non_increasing_selected_cpu_time"),
        ]:
            records = rows()
            records[5][field] = value
            with self.subTest(value=value), self.assertRaisesRegex(analyzer.EvidenceError, expected):
                summarize(records)

    def test_mixed_presentation_or_application_identity_is_rejected(self):
        for field, value in [("PresentMode", "Composed: Flip"), ("Application", "other-private-name.exe")]:
            records = rows()
            records[10][field] = value
            with self.subTest(field=field), self.assertRaisesRegex(analyzer.EvidenceError, "mixed_selected_identity_or_presentation"):
                summarize(records)

    def test_v1_default_and_raw_qpc_schemas_are_not_guessed(self):
        for missing, replacement in [("DisplayedTime", "MsBetweenDisplayChange"), ("CPUStartTime", "CPUStartQPC")]:
            records = [{replacement if key == missing else key: value for key, value in row.items()} for row in rows()]
            headers = [replacement if key == missing else key for key in HEADERS]
            with self.subTest(replacement=replacement), self.assertRaisesRegex(analyzer.EvidenceError, "unsupported_v2_schema"):
                analyzer.summarize_csv(io.StringIO(csv_text(records, headers)), SELECTION, LIMITS)

    def test_selected_identity_must_exist(self):
        with self.assertRaisesRegex(analyzer.EvidenceError, "selected_identity_missing"):
            summarize(rows(), dict(SELECTION, pid=99))

    def test_frame_generation_types_are_not_silently_mixed(self):
        records = [dict(row, FrameType="Application") for row in rows()]
        records[4]["FrameType"] = "AMD AFMF"
        with self.assertRaisesRegex(analyzer.EvidenceError, "mixed_frame_types_not_supported"):
            analyzer.summarize_csv(io.StringIO(csv_text(records, HEADERS + ["FrameType"])), SELECTION, LIMITS)

    def test_comparison_reports_effect_direction_but_never_automatic_pass(self):
        baseline, active = summarize(rows()), summarize(rows([25] * 100))
        result = comparison(baseline, active)
        self.assertEqual(result["decision"], "inconclusive")
        self.assertEqual(result["paired_repeat_count"], 1)
        difference = result["paired_differences_capture_minus_baseline"][0]
        self.assertEqual(difference["average_displayed_fps"], -10)
        self.assertEqual(difference["p99_displayed_ms"], 5)
        self.assertIn("measurement_sensitivity_not_declared", result["reasons"])
        self.assertIn("milliseconds", result["method"]["time_units"])

    def test_declared_sensitivity_and_attestation_do_not_become_proof(self):
        summary = summarize(rows())
        manifest = {"declared_sensitivity": {key: .1 for key in analyzer.METRICS},
                    "actual_gameplay_confirmed": True, "conditions_matched_confirmed": True,
                    "trace_loss_free_confirmed": True}
        result = comparison(summary, summary, manifest)
        self.assertEqual(result["decision"], "inconclusive")
        self.assertEqual(result["confirmations_source"], "caller_attestation_not_verified_from_csv")
        self.assertEqual(result["reasons"], ["descriptive_summary_has_no_statistical_acceptance_rule"])

    def test_incompatible_presentation_or_window_lengths_are_rejected(self):
        first = summarize(rows())
        second = summarize([dict(row, PresentMode="Composed: Flip") for row in rows()])
        with self.assertRaisesRegex(analyzer.EvidenceError, "incompatible_presentation_conditions"):
            comparison(first, second)
        with self.assertRaisesRegex(analyzer.EvidenceError, "incompatible_requested_window_lengths"):
            comparison(first, summarize(rows(), dict(SELECTION, to_ms=5000)))

    def test_repeated_files_are_not_counted_as_independent_repeats(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "first.csv").write_text(csv_text(rows()), encoding="utf-8")
            (root / "copy.csv").write_text(csv_text(rows()), encoding="utf-8")
            manifest = {"schema_version": 1, "minimum_intervals": 100, "minimum_window_ms": 1000,
                        "long_frame_thresholds_ms": [50], "trials": [
                            dict(SELECTION, csv="first.csv", phase="baseline", pair=1),
                            dict(SELECTION, csv="copy.csv", phase="capture_only", pair=1)]}
            with self.assertRaisesRegex(analyzer.EvidenceError, "duplicate_trial_input"):
                analyzer.analyze_manifest(manifest, root)

    def test_io_and_argument_errors_never_echo_paths_or_values(self):
        for arguments in (["--manifest", "private-missing-path.json"], ["--unknown", "private-value"]):
            output = io.StringIO()
            with self.subTest(arguments=arguments), contextlib.redirect_stdout(output):
                self.assertEqual(analyzer.main(arguments), 2)
            result = json.loads(output.getvalue())
            self.assertEqual(result["result"], "rejected")
            self.assertNotIn("private", output.getvalue())

    def test_malformed_manifest_types_have_fixed_rejections(self):
        summary = summarize(rows())
        with self.assertRaisesRegex(analyzer.EvidenceError, "invalid_trial_phase"):
            analyzer.compare_summaries([
                {"phase": ["private-invalid-phase"], "pair": 1, "summary": summary},
                {"phase": "capture_only", "pair": 1, "summary": summary},
            ], {})
        with self.assertRaisesRegex(analyzer.EvidenceError, "unsupported_manifest"):
            analyzer.analyze_manifest({"schema_version": True}, Path("."))


if __name__ == "__main__":
    unittest.main()
