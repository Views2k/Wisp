# Recorder gameplay evidence summary

Offline Python standard-library analysis only. This tool does not start PresentMon, inspect processes, capture a game, change focus, or decide that recording is performance-neutral. Exit 0 means that an evidence summary was produced, **not** that the performance requirement passed.

The existing focus-diagnostics presentation extractor is a separate v1/QPC tool for renderer attribution. It is unchanged. This analyzer requires **PresentMon 2.6 `--v2_metrics`**, display tracking enabled, dropped frames retained, and the default capture-relative `CPUStartTime` in milliseconds. Do not use `--exclude_dropped`, `--no_track_display`, `--qpc_time`, `--qpc_time_ms` or `--date_time`. The pinned writer emits `DisplayedTime` in the v2 path; do not substitute default `MsBetweenDisplayChange` or v1 fields. See the [official console documentation](https://github.com/GameTechDev/PresentMon/blob/v2.6.0/README-ConsoleApplication.md) and [pinned CSV implementation](https://github.com/GameTechDev/PresentMon/blob/v2.6.0/PresentMon/CsvOutput.cpp#L554).

Provide already-collected, private CSV files and a private manifest. Each trial requires its own verified PID **and** swapchain. Other identities are counted but never combined. The analyzer cannot determine which swapchain is the actual game, whether the game was focused, or whether a process restarted; verify that provenance separately. Mixed presentation modes, mixed application names within the selected PID, non-increasing CPU timestamps and mixed tracked frame types are rejected. Frame generation that is not tracked remains unknown, not silently certified off.

Example manifest structure (identities below are illustrative; use actual verified values privately):

```json
{
  "schema_version": 1,
  "minimum_intervals": 1000,
  "minimum_window_ms": 19000,
  "long_frame_thresholds_ms": [33.3333, 50, 100],
  "actual_gameplay_confirmed": false,
  "conditions_matched_confirmed": false,
  "trace_loss_free_confirmed": false,
  "declared_sensitivity": null,
  "trials": [
    {"phase": "baseline", "pair": 1, "csv": "baseline.csv", "pid": 42, "swapchain": "0xabc", "from_ms": 5000, "to_ms": 25000},
    {"phase": "capture_only", "pair": 1, "csv": "capture.csv", "pid": 42, "swapchain": "0xabc", "from_ms": 5000, "to_ms": 25000}
  ]
}
```

Choose the minimum duration/count and long-frame thresholds before comparing captures. They are declared data sufficiency requirements, not regression acceptance thresholds. The parser requires at least 100 complete intervals so the worst-one-percent group is defined; that alone does not establish adequate statistical power. Add actual alternating A/B repeats as further matched pairs. Reusing identical CSV bytes is rejected. Do not invent repeats by reusing one capture or selecting correlated subwindows.

If sensitivity has been established independently, `declared_sensitivity` is an object with positive absolute values for `average_displayed_fps` (fps), `p99_displayed_ms` (ms), and `worst_one_percent_mean_fps` (fps). These are reported without converting them into an automatic pass. Scene, game settings, build, cap/VRR, frame generation, HDR, background workload, actual focus and trace-loss evidence must be checked outside this CSV parser. The three confirmation booleans remain explicitly labeled caller attestations.

Run from this folder using the prepared Python interpreter:

```[WINDOWS POWERSHELL]
& $pythonExecutable -I -B .\analyze_presentmon.py --manifest .\private-comparison.json
```

Focused tests (synthetic CSV fixtures validate calculations and refusals, not game performance):

```[WINDOWS POWERSHELL]
& $pythonExecutable -I -B -m unittest discover -s . -p test_analyze_presentmon.py
```

The JSON contains allowlisted scalars and method descriptions. It never includes process names, input/output paths, PIDs, swapchain addresses, absolute QPC values or arbitrary CSV text. Errors are fixed codes. A malformed/missing/incompatible input exits 2. CSVs and manifests remain private.

## Calculation contract

- Valid intervals are positive `DisplayedTime` values whose entire displayed interval (`CPUStartTime + DisplayLatency` through that time plus `DisplayedTime`) fits the requested window. Boundary intervals are excluded and counted, not artificially shortened.
- `NA` is counted as a non-displayed row within the CPU-time window; it is never a zero-duration frame. This is not a diagnosis of why the frame was not displayed. Blank, negative, zero or nonfinite displayed durations are rejected.
- Adjacent complete display intervals must meet within 0.001 ms, allowing the pinned writer's decimal rounding. Gaps/overlaps fail closed; the tool does not quietly concatenate separated intervals. No optical-display claim is made.
- Average displayed fps is `1000 × N / sum(duration_ms)`, not the arithmetic mean of per-frame reciprocal durations.
- P99 is the nearest-rank percentile: sorted duration at index `ceil(0.99 × N) − 1`.
- The reported 1% low is `1000 / mean(longest ceil(0.01 × N) durations)`. It is explicitly **not** `1000 / p99`.
- Long-frame counts use strict `duration_ms > declared_threshold_ms`. Full per-trial counts and observed intervals are included.
- Repeats are compared as equally weighted runs, with per-run means/ranges/sample standard deviations and paired capture-minus-baseline differences. Frames from different trials are not pooled as independent repeats.

The result is always `evidence_summary` / `inconclusive`. This narrow tool implements no statistical acceptance rule. Actual gameplay measurement, repeat comparability, sensitivity, HUD behavior and recorder quality require a separate evidence-based decision.

## One shared capture session: QPC block analysis

`analyze_capture_block.py` is a separate additive offline entry point. The original analyzer and its duplicate-file guard are unchanged. This entry point requires one already-collected PresentMon 2.6 `--v2_metrics --qpc_time` CSV: the pinned writer calls its raw integer clock column `CPUStartQPC`. `--qpc_time_ms`, default relative milliseconds, date clocks, v1 columns and mixed clocks are rejected. The required display fields remain `DisplayLatency` and `DisplayedTime` in milliseconds. Other columns must belong to the pinned v2 schema; no alternate clock is inferred.

A version 2 manifest contains exactly these keys. Values below are illustrative, not a usable game selection; supply the actual recorded CSV hash, verified identity, measured QPC frequency and phase boundaries privately.

```json
{
  "schema_version": 2,
  "csv": "private-session.csv",
  "csv_sha256": "<64 hexadecimal SHA-256 characters>",
  "pid": 42,
  "swapchain": "0xabc",
  "qpc_frequency": 10000000,
  "minimum_intervals": 1000,
  "minimum_window_ms": 19000,
  "long_frame_thresholds_ms": [33.3333, 50, 100],
  "windows": [
    {"phase": "baseline", "from_qpc": 100000000, "to_qpc": 300000000},
    {"phase": "capture_only", "from_qpc": 350000000, "to_qpc": 550000000},
    {"phase": "capture_only", "from_qpc": 600000000, "to_qpc": 800000000},
    {"phase": "baseline", "from_qpc": 850000000, "to_qpc": 1050000000}
  ]
}
```

Exactly four chronological, disjoint, equal-width windows are required, in ABBA or BAAB order (A = baseline, B = capture only). The frequency and absolute window ticks must be positive JSON integers, not floats or numeric strings. Overlap, repeated windows, per-window identity overrides, extra manifest keys and duplicate JSON keys are rejected. A matching CSV hash is required, with a 64 MiB input limit. The selected application, presentation configuration and tracked frame type must remain constant across the shared session. Other PID/swapchain rows never substitute for the selected identity.

The file is opened once. Raw integer ticks are parsed before any floating-point conversion. One common integer origin is subtracted from timestamps and window boundaries, then the differences are converted to relative milliseconds using the supplied frequency. The input file and original times are not rewritten, shifted or rounded. The resulting in-memory CSV is passed to the existing `summarize_csv` calculation for each window, preserving its complete-interval, boundary, contiguity, NA, percentile and duration-weighting rules. Output explicitly identifies QPC-derived relative time; it omits the raw origin, absolute QPC values, identities, input hash and paths.

The four correlated windows form **one experimental block**, not four independent repeats. Output includes each window, equally weighted condition means within that block and capture-minus-baseline differences. It remains `evidence_summary` / `inconclusive`; it declares no measured sensitivity, statistical acceptance, gameplay verification or absence of regression. The tool cannot verify the caller's QPC frequency, phase boundaries, process lifetime, game focus or trace loss from these rows alone.

```[WINDOWS POWERSHELL]
& $pythonExecutable -I -B .\analyze_capture_block.py --manifest .\private-block.json
& $pythonExecutable -I -B -m unittest discover -s . -p test_capture_block.py
```

The synthetic tests cover large absolute QPC values, both phase orders, one file read, hash pinning, identity/presentation changes, malformed clocks and manifests, interval rules and privacy. A real `-I -B` CLI subprocess test runs from a separate fixture directory. The block analyzer loads the known calculation sibling directly from its own source directory, without adding the working directory or any directory to `sys.path`. These tests never launch a diagnostic or game capture.
