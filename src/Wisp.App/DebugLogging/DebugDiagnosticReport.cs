using System.Globalization;
using System.Text;

namespace Wisp.App.DebugLogging;

internal static class DebugDiagnosticReport
{
    private const int MaximumIncidents = 100;
    internal const int MaximumSummaryBytes = 64 * 1024;
    private const int MaximumSessions = 32;

    internal static string Build(IReadOnlyList<DebugHealthSample> samples, long droppedRecords = 0)
    {
        var text = new StringBuilder("Wisp diagnostic summary\n");
        text.AppendLine("Times are UTC. Durations use a monotonic clock within each collection session.");
        text.AppendLine("Measurements and focus transitions are sampled, normally once per second. Listed times are observation times; brief transitions may be missed and exact onset is uncertain.");
        text.AppendLine("Findings narrow the affected component; they do not establish a Windows or GPU driver root cause.");
        text.AppendLine("Game FPS and GPU presentation latency are not measured. Composition callbacks are Wisp callbacks, not displayed game frames.");
        text.AppendLine($"Health samples retained: {samples.Count}");
        var validSamples = samples.Where(sample => Nonnegative(sample.ElapsedMilliseconds) &&
            Nonnegative(sample.CollectionGapMilliseconds)).ToArray();
        if (validSamples.Length < 2)
        {
            var incompleteDropped = Math.Max(droppedRecords, samples.Select(sample => sample.DroppedRecords).DefaultIfEmpty(0).Max());
            var incompleteFailures = samples.Select(sample => sample.CollectorFailures).DefaultIfEmpty(0).Max();
            text.AppendLine($"Coverage: {incompleteDropped} dropped records; {incompleteFailures} collector failures.");
            text.AppendLine("Insufficient evidence: record the issue with local debug logging enabled for several seconds, then export again.");
            return text.ToString();
        }

        var incidents = 0;
        var omitted = 0;
        var transitions = 0;
        var collectorGaps = 0;
        var normalSamples = 0;
        var allSessions = validSamples.GroupBy(sample => sample.SessionId).ToArray();
        var sessions = allSessions.TakeLast(MaximumSessions);
        if (allSessions.Length > MaximumSessions)
        {
            text.AppendLine($"Summary limited to the latest {MaximumSessions} retained sessions; older measurements remain in health.ndjson.");
        }
        var unknownMeasurements = validSamples.Count(sample => !Nonnegative(sample.PacketAgeMilliseconds) ||
            !Nonnegative(sample.UiHeartbeatAgeMilliseconds) || !Nonnegative(sample.DispatcherDelayMilliseconds));
        text.AppendLine($"Incomplete freshness measurements: {unknownMeasurements} samples. Unknown or invalid timing is not evidence of a healthy component.");
        if (validSamples.Length < samples.Count)
        {
            text.AppendLine($"{samples.Count - validSamples.Length} samples had invalid session timing and were excluded from classification.");
        }
        foreach (var session in sessions)
        {
            var ordered = session.OrderBy(sample => sample.ElapsedMilliseconds).ToArray();
            text.AppendLine($"\nCollection session: {ordered[0].TimestampUtc:O} to {ordered[^1].TimestampUtc:O}");
            var rejectedIntervals = new HashSet<DebugHealthSample>();
            var nativeFailureIntervals = new HashSet<DebugHealthSample>();
            var arrivalStoppedIntervals = new HashSet<DebugHealthSample>();
            DebugHealthSample? lastDrivingArrival = null;
            foreach (var current in ordered)
            {
                if (Active(current) && FreshIncoming(current) && current.UiContextFresh && current.Focus == DebugFocus.Game)
                {
                    lastDrivingArrival = current;
                }
                else if (lastDrivingArrival is { } baseline && current.RaceOn && current.ListenerRunning && !current.ListenerError &&
                         current.Focus == DebugFocus.Game && current.UiContextFresh && UiResponsive(current) &&
                         current.IncomingHz == 0 && current.ReceivedDatagrams == baseline.ReceivedDatagrams &&
                         current.PacketAgeMilliseconds is > 2000 && Nonnegative(current.PacketAgeMilliseconds) &&
                         current.ElapsedMilliseconds - baseline.ElapsedMilliseconds <= 10_000)
                {
                    arrivalStoppedIntervals.Add(current);
                }
            }
            for (var index = 1; index < ordered.Length; index++)
            {
                var current = ordered[index];
                var previous = ordered[index - 1];
                var rejectedDelta = current.RejectedPackets - previous.RejectedPackets;
                var acceptedDelta = current.AcceptedPackets - previous.AcceptedPackets;
                if (Nonnegative(current.IncomingHz) && current.IncomingHz > 0 && rejectedDelta > 0 && acceptedDelta >= 0 && rejectedDelta >= acceptedDelta)
                {
                    rejectedIntervals.Add(current);
                }
                if (current.NativeReadFailures > previous.NativeReadFailures)
                {
                    nativeFailureIntervals.Add(current);
                }
            }
            var rules = Rules(rejectedIntervals, nativeFailureIntervals, arrivalStoppedIntervals);
            foreach (var rule in rules)
            {
                DebugHealthSample? start = null;
                DebugHealthSample? previous = null;
                var count = 0;
                foreach (var sample in ordered)
                {
                    // Missing collection intervals cannot demonstrate a continuous fault.
                    var continuous = previous is null ||
                        (sample.ElapsedMilliseconds > previous.ElapsedMilliseconds &&
                         sample.ElapsedMilliseconds - previous.ElapsedMilliseconds <= 2000 &&
                         sample.CollectionGapMilliseconds <= 2000);
                    if (!rule.Matches(sample) || !continuous)
                    {
                        Finish();
                        start = null;
                        count = 0;
                    }
                    if (rule.Matches(sample))
                    {
                        start ??= sample;
                        count++;
                    }
                    previous = sample;
                }
                Finish();

                void Finish()
                {
                    if (start is null || previous is null || count < 2 ||
                        previous.ElapsedMilliseconds - start.ElapsedMilliseconds < 500)
                    {
                        return;
                    }
                    if (incidents >= MaximumIncidents)
                    {
                        omitted++;
                        return;
                    }
                    incidents++;
                    text.AppendLine($"\n{rule.Name}");
                    if (rule.ObservationOnly)
                    {
                        text.AppendLine("Classification: observed interruption; not a confirmed fault.");
                    }
                    text.AppendLine($"When: {start.TimestampUtc:O} to {previous.TimestampUtc:O}; " +
                        $"elapsed {Number(start.ElapsedMilliseconds)}–{Number(previous.ElapsedMilliseconds)} ms; {count} consecutive samples.");
                    text.AppendLine($"Evidence (first sample): {Evidence(start)}");
                    text.AppendLine($"Evidence (last sample): {Evidence(previous)}");
                    text.AppendLine($"Likely affected component: {rule.Component}");
                    text.AppendLine($"Uncertainty: {rule.Uncertainty}");
                    text.AppendLine($"Next useful step: {rule.NextStep}");
                }
            }

            DebugHealthSample? last = null;
            foreach (var sample in ordered)
            {
                if (!sample.RaceOn || !sample.GameTimestampAdvancing || !sample.OverlayExpectedVisible)
                {
                    normalSamples++;
                }
                if (sample.CollectionGapMilliseconds > 2000 ||
                    (last is not null && sample.ElapsedMilliseconds - last.ElapsedMilliseconds > 2000))
                {
                    collectorGaps++;
                    if (collectorGaps <= 10)
                    {
                        text.AppendLine($"\nCollector coverage gap at {sample.TimestampUtc:O}, elapsed {Number(sample.ElapsedMilliseconds)} ms: " +
                            $"collection interval {Number(sample.CollectionGapMilliseconds)} ms. Events inside this gap cannot be classified reliably. " +
                            "Next useful step: repeat a short capture and compare process CPU, collection failures, and system sleep/resume timing.");
                    }
                }
                if (last is not null && sample.Focus != last.Focus)
                {
                    transitions++;
                    if (transitions <= 30)
                    {
                        text.AppendLine($"Focus transition at {sample.TimestampUtc:O}, elapsed {Number(sample.ElapsedMilliseconds)} ms: {last.Focus} -> {sample.Focus}.");
                    }
                }
                last = sample;
            }
        }

        var dropped = Math.Max(droppedRecords, samples.Max(sample => sample.DroppedRecords));
        var failures = samples.Max(sample => sample.CollectorFailures);
        text.AppendLine($"\nCoverage: {collectorGaps} collector gaps; {dropped} dropped records; {failures} collector failures; {transitions} focus transitions.");
        text.AppendLine($"Context (summarized sessions): {normalSamples} samples were outside advancing gameplay or had no expected visible overlay. Hidden overlays, menus, and ordinary telemetry disconnection alone are not rendering faults.");
        if (dropped > 0 || failures > 0)
        {
            text.AppendLine("Evidence is incomplete because collection or storage lost records. Absence of a finding does not rule out a fault. Next useful step: repeat a shorter capture and inspect local storage availability and Wisp resource usage.");
        }
        if (incidents == 0)
        {
            text.AppendLine("No sustained classified fault detected in the retained samples. This does not prove smooth rendering. Next useful step: capture the symptom while driving, note its UTC time and focus state, then compare it with these measurements.");
        }
        if (omitted > 0)
        {
            text.AppendLine($"{omitted} additional incident windows omitted from this bounded summary; full retained measurements are in health.ndjson.");
        }
        text.AppendLine("Retention is bounded; this export may contain only the recent part of a session. Packet counters can reset between sessions. Drained datagrams are intentional newest-packet coalescing, not packet loss; a lower processed rate alone is not a fault. Raw measurements remain in health.ndjson.");
        return Bound(text.ToString());
    }

    private static bool Active(DebugHealthSample sample) => sample.RaceOn && sample.GameTimestampAdvancing;
    private static bool FreshIncoming(DebugHealthSample sample) => sample.ListenerRunning &&
        Nonnegative(sample.IncomingHz) && sample.IncomingHz > 0 &&
        Nonnegative(sample.PacketAgeMilliseconds) && sample.PacketAgeMilliseconds is <= 500;
    private static bool UiDelayed(DebugHealthSample sample) =>
        (sample.DispatcherProbePending && Nonnegative(sample.DispatcherDelayMilliseconds) && sample.DispatcherDelayMilliseconds is >= 500) ||
        (Active(sample) && sample.ProcessedHz == 0 && Nonnegative(sample.UiHeartbeatAgeMilliseconds) && sample.UiHeartbeatAgeMilliseconds is >= 2000);
    private static bool UiResponsive(DebugHealthSample sample) => sample.UiContextFresh &&
        Nonnegative(sample.UiHeartbeatAgeMilliseconds) && sample.UiHeartbeatAgeMilliseconds is <= 2000 &&
        Nonnegative(sample.DispatcherDelayMilliseconds) && sample.DispatcherDelayMilliseconds is < 500 && !UiDelayed(sample);
    private static bool Nonnegative(double? value) => value is { } number && double.IsFinite(number) && number >= 0;

    private static Rule[] Rules(HashSet<DebugHealthSample> rejectedIntervals, HashSet<DebugHealthSample> nativeFailureIntervals,
        HashSet<DebugHealthSample> arrivalStoppedIntervals) =>
    [
        new("UI processing delay", "Wisp UI dispatcher / telemetry consumption",
            sample => FreshIncoming(sample) && UiDelayed(sample),
            "Incoming data stayed fresh while the UI heartbeat or dispatcher probe was delayed. The blocking operation is not identified; this does not establish a GPU fault.",
            "Reproduce with the same page and focus state. Compare dispatcher delay, incoming versus processed rates, and resource samples during this interval."),
        new("Telemetry reception interruption", "Telemetry receiver / upstream Data Out connection",
            sample => sample.ListenerError,
            "The local listener reported an error. Its exact underlying cause is not captured; remote network and game FPS are not measured.",
            "Check the listener state and whether Data Out is enabled, then capture an uninterrupted driving interval. Compare received, accepted, and processed counters."),
        new("Telemetry arrival stopped (cause uncertain)", "Upstream sender or telemetry reception; insufficient evidence to separate them",
            arrivalStoppedIntervals.Contains,
            "Fresh driving packets were previously observed, then received counters stopped while the UI remained responsive. Menus, cutscenes, a normal disconnect, and an upstream interruption can produce this pattern. This is not proof of a receiver fault.",
            "Confirm whether driving continued during this interval. If so, compare the game's Data Out configuration and listener state; repeat an uninterrupted driving capture.", true),
        new("Telemetry rejected packets", "Telemetry parsing / incoming packet format",
            rejectedIntervals.Contains,
            "Rejected packet counters increased at least as fast as accepted counters in consecutive intervals. Unsupported or malformed input is possible; rejected payloads are not retained.",
            "Verify the Data Out destination and supported packet format. Compare received and rejected counter changes during a fresh capture."),
        new("Native HUD data unavailable or stale", "Native HUD data provider",
            sample => Active(sample) && sample.UiContextFresh && sample.NativeExpected && FreshIncoming(sample) &&
                UiResponsive(sample) && (!sample.NativeAvailable ||
                    (Nonnegative(sample.NativeAgeMilliseconds) && sample.NativeAgeMilliseconds is > 2000) ||
                    (Nonnegative(sample.NativeVisibilityAgeMilliseconds) && sample.NativeVisibilityAgeMilliseconds is > 2000) ||
                    sample.NativeStatus is "ReadFailure" or "InvalidSourceVector" or "InvalidProvider" || nativeFailureIntervals.Contains(sample)),
            "Telemetry remains fresh but required native data is unavailable or old. A compatibility failure, process access restriction, or provider delay needs further evidence.",
            "Compare native status and data ages with game focus transitions. Export another capture after checking the compatibility status in Diagnostics."),
        new("Composition callback gap", "Wisp composition / presentation scheduling",
            sample => Active(sample) && sample.UiContextFresh && sample.OverlayExpectedVisible && FreshIncoming(sample) &&
                UiResponsive(sample) && ((Nonnegative(sample.CompositionAgeMilliseconds) && sample.CompositionAgeMilliseconds is >= 250) ||
                    (Nonnegative(sample.CompositionMaximumGapMilliseconds) && sample.CompositionMaximumGapMilliseconds >= 250)),
            "Wisp composition callbacks were delayed while the UI heartbeat and incoming telemetry stayed responsive. Callbacks do not measure GPU presentation or prove a driver problem.",
            "Reproduce while the overlay is visible. Correlate focus transitions and resource pressure; if callbacks alone do not explain the symptom, collect a focused Windows presentation trace."),
        new("Resource pressure correlation", "Wisp process resource usage",
            sample => (Nonnegative(sample.WispCpuPercent) && sample.WispCpuPercent is >= 80 and <= 100) || sample.WorkingSetBytes >= 2L * 1024 * 1024 * 1024,
            "High Wisp CPU or working set is a correlation, not proof of system-wide resource pressure or the cause of another incident. Other processes and GPU utilization are not collected.",
            "Compare this interval with UI and composition findings. If repeatable, capture a short Wisp CPU or allocation profile; avoid changing drivers based on this report alone.")
    ];

    private static string Evidence(DebugHealthSample sample) =>
        $"incoming {Number(sample.IncomingHz)} Hz; processed {Number(sample.ProcessedHz)} Hz; packet age {Number(sample.PacketAgeMilliseconds)} ms; " +
        $"received/drained/accepted/rejected/processed {sample.ReceivedDatagrams}/{sample.DrainedDatagrams}/{sample.AcceptedPackets}/{sample.RejectedPackets}/{sample.ProcessedPackets}; " +
        $"UI heartbeat age {Number(sample.UiHeartbeatAgeMilliseconds)} ms; dispatcher delay {Number(sample.DispatcherDelayMilliseconds)} ms; " +
        $"composition age/max gap {Number(sample.CompositionAgeMilliseconds)}/{Number(sample.CompositionMaximumGapMilliseconds)} ms; " +
        $"native available {sample.NativeAvailable}, read failures {sample.NativeReadFailures}, age {Number(sample.NativeAgeMilliseconds)} ms, visibility age {Number(sample.NativeVisibilityAgeMilliseconds)} ms; " +
        $"Wisp CPU {Number(sample.WispCpuPercent)}%, working set {sample.WorkingSetBytes / (1024 * 1024)} MiB, managed heap {sample.ManagedHeapBytes / (1024 * 1024)} MiB, Gen2 collections {sample.Gen2Collections}; focus {sample.Focus}.";

    private static string Number(double? value) => value is { } number && double.IsFinite(number)
        ? number.ToString("0.0", CultureInfo.InvariantCulture) : "unknown";

    private static string Bound(string value)
    {
        if (Encoding.UTF8.GetByteCount(value) <= MaximumSummaryBytes)
        {
            return value;
        }
        const string suffix = "\nSummary output limit reached. Measurements remain in health.ndjson; use a shorter capture for complete incident details.\n";
        var budget = MaximumSummaryBytes - Encoding.UTF8.GetByteCount(suffix);
        var low = 0;
        var high = value.Length;
        while (low < high)
        {
            var midpoint = (low + high + 1) / 2;
            if (Encoding.UTF8.GetByteCount(value.AsSpan(0, midpoint)) <= budget)
            {
                low = midpoint;
            }
            else
            {
                high = midpoint - 1;
            }
        }
        if (low > 0 && char.IsHighSurrogate(value[low - 1]))
        {
            low--;
        }
        return value[..low] + suffix;
    }

    private sealed record Rule(string Name, string Component, Func<DebugHealthSample, bool> Matches,
        string Uncertainty, string NextStep, bool ObservationOnly = false);
}
