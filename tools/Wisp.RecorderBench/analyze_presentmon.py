"""Offline PresentMon 2.6 --v2_metrics evidence summary; never starts capture."""
import argparse
import csv
import hashlib
import io
import json
import math
import re
import statistics
import sys
from pathlib import Path


MAXIMUM_CSV_BYTES = 64 * 1024 * 1024
REQUIRED_COLUMNS = {
    "Application", "ProcessID", "SwapChainAddress", "PresentRuntime",
    "SyncInterval", "PresentFlags", "AllowsTearing", "PresentMode",
    "CPUStartTime", "FrameTime", "CPUBusy", "CPUWait", "DisplayLatency", "DisplayedTime",
}
MODES = {
    "Hardware: Legacy Flip", "Hardware: Legacy Copy to front buffer",
    "Hardware: Independent Flip", "Composed: Flip",
    "Hardware Composed: Independent Flip", "Composed: Copy with GPU GDI",
    "Composed: Copy with CPU GDI", "Other",
}
FRAME_TYPES = {"Application", "Intel XeSS-FG", "AMD AFMF", "Unknown"}
METRICS = ("average_displayed_fps", "p99_displayed_ms", "worst_one_percent_mean_fps")
# The v2 writer rounds CPU start, latency and duration to 0.0001 ms.
ROUNDING_TOLERANCE_MS = 0.001


class EvidenceError(Exception):
    """Messages are fixed codes, never raw input or operating-system errors."""


def require(condition, code):
    if not condition:
        raise EvidenceError(code)


def number(value, code, *, positive=False):
    require(not isinstance(value, bool), code)
    try:
        result = float(value)
    except (TypeError, ValueError, OverflowError):
        raise EvidenceError(code) from None
    require(math.isfinite(result) and (result > 0 if positive else result >= 0), code)
    return result


def integer(value, code, minimum=0):
    require(not isinstance(value, bool) and re.fullmatch(r"[0-9]+", str(value)) is not None, code)
    result = int(value)
    require(result >= minimum, code)
    return result


def address(value):
    require(isinstance(value, str) and re.fullmatch(r"0x[0-9a-fA-F]{1,16}", value) is not None,
            "invalid_swapchain_selection")
    result = int(value, 16)
    require(result != 0, "invalid_swapchain_selection")
    return result


def validate_limits(manifest):
    minimum = integer(manifest.get("minimum_intervals"), "invalid_minimum_intervals", 100)
    duration = number(manifest.get("minimum_window_ms"), "invalid_minimum_window", positive=True)
    thresholds = manifest.get("long_frame_thresholds_ms")
    require(isinstance(thresholds, list) and 0 < len(thresholds) <= 16, "invalid_long_frame_thresholds")
    thresholds = sorted(set(number(value, "invalid_long_frame_threshold", positive=True) for value in thresholds))
    return minimum, duration, thresholds


def summarize_csv(stream, selection, limits):
    """Analyze only complete displayed intervals inside an explicit capture-relative window."""
    pid = integer(selection.get("pid"), "invalid_pid_selection", 1)
    chain = address(selection.get("swapchain"))
    start = number(selection.get("from_ms"), "invalid_window")
    end = number(selection.get("to_ms"), "invalid_window")
    minimum, minimum_duration, thresholds = limits
    require(end > start and end - start >= minimum_duration, "insufficient_requested_window")
    reader = csv.DictReader(stream, strict=True)
    columns = reader.fieldnames or []
    require(len(columns) == len(set(columns)) and REQUIRED_COLUMNS <= set(columns), "unsupported_v2_schema")
    require(not ({"Dropped", "CPUStartQPC", "CPUStartQPCTime", "CPUStartDateTime", "TimeInSeconds"} & set(columns)),
            "mixed_or_unsupported_time_schema")
    applications, configurations, frame_types = set(), set(), set()
    displayed = []
    matched = cpu_rows = not_displayed = boundary = ignored = 0
    previous_cpu = None
    for row in reader:
        require(None not in row and None not in row.values(), "malformed_csv_row")
        row_pid = integer(row["ProcessID"], "invalid_csv_process", 1)
        row_chain = address(row["SwapChainAddress"])
        if (row_pid, row_chain) != (pid, chain):
            ignored += 1
            continue
        matched += 1
        cpu = number(row["CPUStartTime"], "invalid_cpu_time")
        require(previous_cpu is None or cpu > previous_cpu, "non_increasing_selected_cpu_time")
        previous_cpu = cpu
        in_cpu_window = start <= cpu < end
        cpu_rows += int(in_cpu_window)
        duration_text, latency_text = row["DisplayedTime"], row["DisplayLatency"]
        if duration_text == "NA":
            require(latency_text == "NA", "inconsistent_non_displayed_row")
            not_displayed += int(in_cpu_window)
            if not in_cpu_window:
                continue
        else:
            duration = number(duration_text, "invalid_displayed_duration", positive=True)
            latency = number(latency_text, "invalid_display_latency")
            first, last = cpu + latency, cpu + latency + duration
            overlaps = first < end and last > start
            if not in_cpu_window and not overlaps:
                continue
            if first >= start and last <= end:
                displayed.append((first, last, duration))
            elif overlaps:
                boundary += 1
        # Inspect the selected window, without emitting the Application text.
        applications.add(row["Application"])
        require(row["PresentRuntime"] in {"DXGI", "D3D9", "Other"}, "unsupported_present_runtime")
        require(row["PresentMode"] in MODES, "unsupported_present_mode")
        require(re.fullmatch(r"-?[0-9]+", row["SyncInterval"]) is not None, "invalid_sync_interval")
        sync = int(row["SyncInterval"])
        require(-1 <= sync <= 4, "invalid_sync_interval")
        flags = integer(row["PresentFlags"], "invalid_present_flags")
        tearing = integer(row["AllowsTearing"], "invalid_tearing_flag")
        require(tearing in (0, 1), "invalid_tearing_flag")
        configurations.add((row["PresentRuntime"], row["PresentMode"], sync, flags, tearing))
        if "FrameType" in row:
            require(row["FrameType"] in FRAME_TYPES, "unsupported_frame_type")
            frame_types.add(row["FrameType"])
    require(matched > 0, "selected_identity_missing")
    require(len(applications) == 1 and len(configurations) == 1, "mixed_selected_identity_or_presentation")
    require(len(frame_types) <= 1, "mixed_frame_types_not_supported")
    require(len(displayed) >= minimum, "insufficient_displayed_intervals")
    for previous, current in zip(displayed, displayed[1:]):
        require(abs(current[0] - previous[1]) <= ROUNDING_TOLERANCE_MS,
                "non_contiguous_display_intervals")
    durations = sorted(item[2] for item in displayed)
    count, total = len(durations), math.fsum(durations)
    require(total >= minimum_duration, "insufficient_observed_display_time")
    tail_count = math.ceil(count * .01)
    tail_mean = statistics.fmean(durations[-tail_count:])
    runtime, mode, sync, flags, tearing = next(iter(configurations))
    return {
        "scope": "one_explicit_pid_and_swapchain",
        "schema": "presentmon_2_6_v2_relative_milliseconds",
        "requested_window_ms": {"from": start, "to": end},
        "observed_display_interval_ms": {"from": displayed[0][0], "to": displayed[-1][1]},
        "counts": {"matched_rows_total": matched, "other_identity_rows_ignored": ignored,
                   "present_rows_in_cpu_window": cpu_rows, "not_displayed_rows_in_cpu_window": not_displayed,
                   "complete_displayed_intervals": count, "boundary_intervals_excluded": boundary},
        "presentation": {"runtime": runtime, "mode": mode, "sync_interval": sync,
                         "present_flags": flags, "allows_tearing": bool(tearing),
                         "frame_type": next(iter(frame_types)) if frame_types else "not_tracked"},
        "displayed_duration_sum_ms": total,
        "average_displayed_fps": 1000 * count / total,
        "p99_displayed_ms": durations[math.ceil(.99 * count) - 1],
        "worst_one_percent_interval_count": tail_count,
        "worst_one_percent_mean_ms": tail_mean,
        "worst_one_percent_mean_fps": 1000 / tail_mean,
        "maximum_displayed_ms": durations[-1],
        "long_frames": [{"strictly_greater_than_ms": threshold,
                         "count": sum(value > threshold for value in durations)} for threshold in thresholds],
    }


def compare_summaries(trials, manifest):
    require(isinstance(trials, list) and len(trials) >= 2, "missing_comparison_trials")
    groups = {"baseline": [], "capture_only": []}
    pairs = {}
    for trial in trials:
        phase = trial.get("phase")
        require(isinstance(phase, str) and phase in groups, "invalid_trial_phase")
        pair = integer(trial.get("pair"), "invalid_pair", 1)
        require(phase not in pairs.setdefault(pair, {}), "duplicate_pair_phase")
        pairs[pair][phase] = trial["summary"]
        groups[phase].append(trial["summary"])
    require(all(set(pair) == set(groups) for pair in pairs.values()), "unmatched_trial_pairs")
    summaries = [trial["summary"] for trial in trials]
    first = summaries[0]
    require(all(summary["schema"] == first["schema"] and summary["presentation"] == first["presentation"]
                for summary in summaries), "incompatible_presentation_conditions")
    widths = [summary["requested_window_ms"]["to"] - summary["requested_window_ms"]["from"] for summary in summaries]
    require(max(widths) - min(widths) <= ROUNDING_TOLERANCE_MS, "incompatible_requested_window_lengths")
    sensitivity = manifest.get("declared_sensitivity")
    if sensitivity is not None:
        require(isinstance(sensitivity, dict) and set(sensitivity) == set(METRICS), "invalid_declared_sensitivity")
        sensitivity = {key: number(value, "invalid_declared_sensitivity", positive=True) for key, value in sensitivity.items()}
    confirmations = {}
    for key in ("actual_gameplay_confirmed", "conditions_matched_confirmed", "trace_loss_free_confirmed"):
        value = manifest.get(key, False)
        require(isinstance(value, bool), "invalid_evidence_confirmation")
        confirmations[key] = value
    reasons = ["descriptive_summary_has_no_statistical_acceptance_rule"]
    reasons.extend(key.replace("_confirmed", "_not_confirmed") for key, value in confirmations.items() if not value)
    if sensitivity is None:
        reasons.append("measurement_sensitivity_not_declared")

    def across_runs(values):
        return {"mean": statistics.fmean(values), "minimum": min(values), "maximum": max(values),
                "sample_standard_deviation": statistics.stdev(values) if len(values) > 1 else None}

    return {
        "schema_version": 1, "result": "evidence_summary", "decision": "inconclusive", "reasons": reasons,
        "confirmations_source": "caller_attestation_not_verified_from_csv", "confirmations": confirmations,
        "declared_sensitivity": sensitivity, "paired_repeat_count": len(pairs),
        "method": {
            "intervals": "Positive DisplayedTime values for complete intervals inside the requested window; NA is counted separately, never zero.",
            "time_units": "milliseconds", "rate_units": "frames_per_second",
            "average_displayed_fps": "1000 * valid_interval_count / sum(DisplayedTime)",
            "p99_displayed_ms": "Nearest rank: sorted durations at ceil(0.99 * N) - 1.",
            "worst_one_percent_mean_fps": "1000 / mean of the ceil(0.01 * N) longest durations; not reciprocal p99.",
            "comparison": "Equal-weight per-run summaries and paired differences; frames are not pooled across trials.",
            "limits": "OS presentation evidence, not optical FPS, recorder output FPS, HUD latency or proof of no game regression.",
        },
        "trial_summaries": [{"phase": trial["phase"], "pair": trial["pair"], **trial["summary"]} for trial in trials],
        "across_trials": {phase: {metric: across_runs([summary[metric] for summary in values]) for metric in METRICS}
                          for phase, values in groups.items()},
        "paired_differences_capture_minus_baseline": [
            {"pair": pair, **{metric: values["capture_only"][metric] - values["baseline"][metric] for metric in METRICS}}
            for pair, values in sorted(pairs.items())],
    }


def analyze_manifest(manifest, directory):
    require(isinstance(manifest, dict) and type(manifest.get("schema_version")) is int
            and manifest["schema_version"] == 1, "unsupported_manifest")
    limits = validate_limits(manifest)
    specs = manifest.get("trials")
    require(isinstance(specs, list) and 2 <= len(specs) <= 100, "invalid_trial_count")
    trials, hashes = [], set()
    for spec in specs:
        require(isinstance(spec, dict), "invalid_trial")
        name = spec.get("csv")
        require(isinstance(name, str) and bool(name), "missing_csv_input")
        path = directory / name
        require(0 < path.stat().st_size <= MAXIMUM_CSV_BYTES, "csv_size_limit")
        with path.open("rb") as source:
            data = source.read(MAXIMUM_CSV_BYTES + 1)
        require(0 < len(data) <= MAXIMUM_CSV_BYTES, "csv_size_limit")
        digest = hashlib.sha256(data).digest()
        require(digest not in hashes, "duplicate_trial_input")
        hashes.add(digest)
        summary = summarize_csv(io.StringIO(data.decode("utf-8-sig"), newline=""), spec, limits)
        trials.append({"phase": spec.get("phase"), "pair": spec.get("pair"), "summary": summary})
    return compare_summaries(trials, manifest)


class SafeParser(argparse.ArgumentParser):
    def error(self, message):
        raise EvidenceError("invalid_command_line")


def main(argv=None):
    try:
        parser = SafeParser(description=__doc__)
        parser.add_argument("--manifest", required=True)
        args = parser.parse_args(argv)
        path = Path(args.manifest)
        require(path.stat().st_size <= 1024 * 1024, "manifest_size_limit")
        with path.open(encoding="utf-8-sig") as source:
            manifest = json.load(source)
        result = analyze_manifest(manifest, path.parent)
    except EvidenceError as error:
        result = {"result": "rejected", "reason": str(error)}
    except (OSError, UnicodeError, csv.Error, json.JSONDecodeError, ValueError, OverflowError, RecursionError):
        result = {"result": "rejected", "reason": "unreadable_or_malformed_input"}
    print(json.dumps(result, indent=2, allow_nan=False))
    return 2 if result["result"] == "rejected" else 0


if __name__ == "__main__":
    sys.exit(main())
