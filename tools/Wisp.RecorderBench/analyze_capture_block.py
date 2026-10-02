"""Offline analysis of one shared PresentMon 2.6 QPC session; never starts capture."""
import csv
import hashlib
import importlib.util
import io
import json
import re
import statistics
import sys
from pathlib import Path

try:
    specification = importlib.util.spec_from_file_location(
        "wisp_presentmon_calculations", Path(__file__).resolve().with_name("analyze_presentmon.py"))
    if specification is None or specification.loader is None:
        raise ImportError
    existing = importlib.util.module_from_spec(specification)
    specification.loader.exec_module(existing)
except (ImportError, OSError, SyntaxError, ValueError):
    print(json.dumps({"result": "rejected", "reason": "presentmon_calculations_unavailable"}))
    raise SystemExit(2) from None


MAXIMUM_QPC = (1 << 63) - 1
MANIFEST_KEYS = {
    "schema_version", "csv", "csv_sha256", "pid", "swapchain", "qpc_frequency",
    "minimum_intervals", "minimum_window_ms", "long_frame_thresholds_ms", "windows",
}
QPC_COLUMNS = existing.REQUIRED_COLUMNS - {"CPUStartTime"} | {"CPUStartQPC"}
OPTIONAL_COLUMNS = {
    "FrameType", "HybridPresent", "VidPnSourceId", "LayerIndex", "GPULatency", "GPUTime",
    "GPUBusy", "GPUWait", "VideoBusy", "AnimationError", "AnimationTime", "MsFlipDelay",
    "AllInputToPhotonLatency", "ClickToPhotonLatency", "InstrumentedLatency", "FrameId",
    "AppFrameId", "PCLFrameId", "PresentId", "EtwBufferFillPct", "EtwBuffersInUse",
    "EtwTotalBuffers", "EtwEventsLost", "EtwBuffersLost", "OverflowedPresents",
}
OTHER_CLOCK_COLUMNS = {
    "CPUStartTime", "CPUStartQPCTime", "CPUStartQPCTimeInMs", "CPUStartDateTime",
    "CPUStartTimeInMs", "CPUStartTimeInSeconds", "TimeInSeconds", "TimeInMs",
    "TimeInQPC", "TimeInDateTime", "QPCTime", "DisplayTimeAbs", "Dropped",
}
ORDERS = (
    ["baseline", "capture_only", "capture_only", "baseline"],
    ["capture_only", "baseline", "baseline", "capture_only"],
)


def tick(value, code):
    existing.require(type(value) is int and 0 < value <= MAXIMUM_QPC, code)
    return value


def validate_manifest(manifest):
    existing.require(isinstance(manifest, dict) and set(manifest) == MANIFEST_KEYS and
                     type(manifest.get("schema_version")) is int and manifest["schema_version"] == 2,
                     "unsupported_block_manifest")
    existing.require(isinstance(manifest["csv"], str) and bool(manifest["csv"]), "missing_csv_input")
    existing.require(isinstance(manifest["csv_sha256"], str) and
                     re.fullmatch(r"[0-9a-fA-F]{64}", manifest["csv_sha256"]) is not None,
                     "invalid_csv_hash")
    existing.integer(manifest["pid"], "invalid_pid_selection", 1)
    existing.address(manifest["swapchain"])
    tick(manifest["qpc_frequency"], "invalid_qpc_frequency")
    windows = manifest["windows"]
    existing.require(isinstance(windows, list) and len(windows) == 4, "invalid_block_window_count")
    width, previous_end = None, None
    for window in windows:
        existing.require(isinstance(window, dict) and set(window) == {"phase", "from_qpc", "to_qpc"},
                         "invalid_block_window")
        start = tick(window["from_qpc"], "invalid_qpc_window")
        end = tick(window["to_qpc"], "invalid_qpc_window")
        existing.require(end > start, "invalid_qpc_window")
        existing.require(previous_end is None or start >= previous_end, "overlapping_or_unordered_windows")
        existing.require(width is None or end - start == width, "unequal_qpc_window_widths")
        width, previous_end = end - start, end
    existing.require([window["phase"] for window in windows] in ORDERS, "invalid_block_phase_order")
    return existing.validate_limits(manifest)


def relative_csv(data, manifest):
    """Read the raw CSV once; retain integer QPC until a common origin is subtracted."""
    reader = csv.DictReader(io.StringIO(data.decode("utf-8-sig"), newline=""), strict=True)
    columns = reader.fieldnames or []
    existing.require(not (set(columns) & OTHER_CLOCK_COLUMNS), "mixed_or_unsupported_time_schema")
    existing.require(len(columns) == len(set(columns)) and QPC_COLUMNS <= set(columns) and
                     set(columns) <= QPC_COLUMNS | OPTIONAL_COLUMNS, "unsupported_qpc_v2_schema")
    selected_pid = existing.integer(manifest["pid"], "invalid_pid_selection", 1)
    selected_chain = existing.address(manifest["swapchain"])
    rows = []
    origin = manifest["windows"][0]["from_qpc"]
    identity = None
    previous_qpc = None
    for row in reader:
        existing.require(None not in row and None not in row.values(), "malformed_csv_row")
        raw = row["CPUStartQPC"]
        existing.require(re.fullmatch(r"[0-9]{1,19}", raw) is not None, "invalid_cpu_qpc")
        qpc = tick(int(raw), "invalid_cpu_qpc")
        pid = existing.integer(row["ProcessID"], "invalid_csv_process", 1)
        chain = existing.address(row["SwapChainAddress"])
        if (pid, chain) == (selected_pid, selected_chain):
            origin = min(origin, qpc)
            existing.require(previous_qpc is None or qpc > previous_qpc, "non_increasing_selected_cpu_qpc")
            previous_qpc = qpc
            current = tuple(row[key] for key in (
                "Application", "PresentRuntime", "PresentMode", "SyncInterval", "PresentFlags", "AllowsTearing"))
            current += (row.get("FrameType", "not_tracked"),)
            existing.require(identity is None or identity == current,
                             "shared_session_identity_or_presentation_changed")
            identity = current
        rows.append((row, qpc))
    existing.require(identity is not None, "selected_identity_missing")
    output = io.StringIO(newline="")
    output_columns = ["CPUStartTime" if key == "CPUStartQPC" else key for key in columns]
    writer = csv.DictWriter(output, fieldnames=output_columns)
    writer.writeheader()
    frequency = manifest["qpc_frequency"]
    for row, qpc in rows:
        del row["CPUStartQPC"]
        row["CPUStartTime"] = repr((qpc - origin) * 1000 / frequency)
        writer.writerow(row)
    return output.getvalue(), origin


def analyze_manifest(manifest, directory):
    limits = validate_manifest(manifest)
    path = directory / manifest["csv"]
    existing.require(0 < path.stat().st_size <= existing.MAXIMUM_CSV_BYTES, "csv_size_limit")
    with path.open("rb") as source:
        data = source.read(existing.MAXIMUM_CSV_BYTES + 1)
    existing.require(0 < len(data) <= existing.MAXIMUM_CSV_BYTES, "csv_size_limit")
    existing.require(hashlib.sha256(data).hexdigest() == manifest["csv_sha256"].lower(), "csv_hash_mismatch")
    text, origin = relative_csv(data, manifest)
    summaries = []
    for index, window in enumerate(manifest["windows"], 1):
        selection = {"pid": manifest["pid"], "swapchain": manifest["swapchain"],
                     "from_ms": (window["from_qpc"] - origin) * 1000 / manifest["qpc_frequency"],
                     "to_ms": (window["to_qpc"] - origin) * 1000 / manifest["qpc_frequency"]}
        summary = existing.summarize_csv(io.StringIO(text, newline=""), selection, limits)
        summary["schema"] = "presentmon_2_6_v2_qpc_derived_relative_milliseconds"
        summaries.append({"window_index": index, "phase": window["phase"], **summary})
    means = {phase: {metric: statistics.fmean(summary[metric] for summary in summaries if summary["phase"] == phase)
                     for metric in existing.METRICS} for phase in ("baseline", "capture_only")}
    return {
        "schema_version": 2, "result": "evidence_summary", "decision": "inconclusive",
        "experimental_block_count": 1, "window_count": 4, "independent_replication_established": False,
        "phase_order": "ABBA" if manifest["windows"][0]["phase"] == "baseline" else "BAAB",
        "reasons": ["one_shared_session_block_is_not_independent_replication",
                    "descriptive_summary_has_no_statistical_acceptance_rule"],
        "clock_provenance": "frequency_and_phase_boundaries_supplied_by_caller_not_verified_from_csv",
        "method": {
            "time_units": "QPC-derived relative milliseconds after subtracting one common integer origin",
            "original_media_times": "No QPC ticks or DisplayLatency/DisplayedTime values are rounded or shifted in the input file.",
            "calculations": "Unchanged summarize_csv complete-interval, contiguity, NA, percentile and duration-weighted calculations.",
            "comparison": "Equal-weight means of two windows per condition within one block; no frames or windows counted as independent repeats.",
            "limits": "No statistical sensitivity, performance acceptance, optical FPS, recorder quality or HUD latency is established.",
        },
        "window_summaries": summaries,
        "within_block_condition_means": means,
        "within_block_capture_minus_baseline": {metric: means["capture_only"][metric] - means["baseline"][metric]
                                                for metric in existing.METRICS},
    }


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        existing.require(key not in result, "duplicate_manifest_field")
        result[key] = value
    return result


def main(argv=None):
    try:
        parser = existing.SafeParser(description=__doc__)
        parser.add_argument("--manifest", required=True)
        args = parser.parse_args(argv)
        path = Path(args.manifest)
        existing.require(path.stat().st_size <= 1024 * 1024, "manifest_size_limit")
        with path.open(encoding="utf-8-sig") as source:
            manifest = json.load(source, object_pairs_hook=unique_object)
        result = analyze_manifest(manifest, path.parent)
    except existing.EvidenceError as error:
        result = {"result": "rejected", "reason": str(error)}
    except (OSError, UnicodeError, csv.Error, json.JSONDecodeError, ValueError, OverflowError, RecursionError):
        result = {"result": "rejected", "reason": "unreadable_or_malformed_input"}
    print(json.dumps(result, indent=2, allow_nan=False))
    return 2 if result["result"] == "rejected" else 0


if __name__ == "__main__":
    sys.exit(main())
