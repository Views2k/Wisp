using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Wisp.App;

// Runs on the existing packaging reader, after recording stops. It never polls the
// game, joins live producer threads or retains raw packets, screenshots or paths.
internal sealed class ShiftCaptureSessionAudit
{
    private const int MaximumChannels = 128, MaximumCarGears = 128, MaximumProfiles = 64;
    private readonly long _frequency;
    private readonly Dictionary<string, Cadence> _channels = new(StringComparer.Ordinal);
    private readonly Dictionary<(int Car, int Gear), GearAudit> _gears = [];
    private readonly HashSet<string> _profiles = new(StringComparer.Ordinal);
    private readonly Measure _inputBracket = new(), _inputQuery = new(), _readbackCost = new(), _readbackGap = new();
    private Telemetry? _previousTelemetry;
    private long _records, _malformed, _parseFailures, _rawPackets, _equalGameTimes, _equalGameTimesChanged;
    private long _gameClockRegressions, _gameClockWraps, _successfulSubmissions, _failedSubmissions;
    private long _pixelReads, _qualifiedPixelReads, _pixelFailures, _lastPixelEnd;
    private long _buttonPresses, _invalidInputBrackets, _configurationRecords;
    private long _inactiveTelemetry;
    private long _completedPixelChecks, _passedPixelChecks;
    private bool? _pixelContextQualifiedAtStop, _pixelQualificationLost;
    private bool _truncated;

    internal ShiftCaptureSessionAudit(long qpcFrequency)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(qpcFrequency);
        _frequency = qpcFrequency;
    }

    internal void Observe(JsonElement entry)
    {
        _records++;
        if (!String(entry, "kind", out var kind) || !SafeLabel(kind) ||
            !Long(entry, "timestamp", out var qpc) || qpc <= 0 ||
            !Object(entry, "payload", out var payload))
        { _malformed++; return; }
        if (!_channels.TryGetValue(kind, out var channel))
        {
            if (_channels.Count == MaximumChannels) { _truncated = true; return; }
            _channels.Add(kind, channel = new Cadence());
        }
        channel.Observe(qpc, _frequency);
        switch (kind)
        {
            case "telemetry": ObserveTelemetry(payload, qpc); break;
            case "cue_evaluation": ObserveEvaluation(payload); break;
            case "cue_submission": ObserveSubmission(payload); break;
            case "controller_button": ObserveButton(payload); break;
            case "cue_pixels": ObservePixels(payload); break;
            case "canary_result":
                if (Bool(payload, "qualifies", out var qualifies))
                {
                    _completedPixelChecks++;
                    if (qualifies) _passedPixelChecks++;
                }
                break;
            case "capture_summary":
                // Older exports used pixelCanaryPassed for the final live latch.
                // Historical check outcomes come from canary_result, never that field.
                if (Bool(payload, "pixelContextQualifiedAtStop", out var current)) _pixelContextQualifiedAtStop = current;
                if (Bool(payload, "pixelQualificationLostDuringSession", out var lost)) _pixelQualificationLost = lost;
                break;
            case "native_configuration":
                _configurationRecords++;
                ObserveProfile(payload, "fingerprint");
                break;
        }
    }

    private void ObserveTelemetry(JsonElement payload, long qpc)
    {
        if (!Bool(payload, "parsed", out var parsed)) { _malformed++; return; }
        if (!parsed) { _parseFailures++; _previousTelemetry = null; return; }
        if (!Object(payload, "state", out var state) || !Long(state, "carOrdinal", out var car) ||
            car is < 0 or > int.MaxValue || !Long(state, "gear", out var gear) || gear is < -2 or > 10 ||
            !Long(state, "gameTimestampMilliseconds", out var gameTime) || gameTime is < 0 or > uint.MaxValue ||
            !Number(state, "engineRpm", out var rpm))
        { _malformed++; _previousTelemetry = null; return; }
        string? raw = null;
        if (String(payload, "rawBase64", out var rawValue) && rawValue.Length is > 0 and <= 2048)
        { _rawPackets++; raw = rawValue; }
        // FH6 sends valid race-off packets with no car during menus/transitions.
        // Keep the packet count but never join driving evidence across this gap.
        if (Bool(state, "isRaceOn", out var raceOn) && !raceOn)
        {
            _inactiveTelemetry++;
            _previousTelemetry = null;
            return;
        }
        if (car == 0) { _malformed++; _previousTelemetry = null; return; }
        var current = new Telemetry((int)car, (int)gear, (uint)gameTime, qpc, rpm, raw,
            Number(state, "torqueNm", out var torque) ? torque : null,
            Number(state, "powerWatts", out var power) ? power : null,
            Number(state, "accelerator", out var accelerator) ? accelerator : null,
            Number(state, "brake", out var brake) ? brake : null,
            Number(state, "groundSpeedMetersPerSecond", out var speed) ? speed : null,
            Bool(state, "isRaceOn", out var race) && race);
        var audit = Gear((int)car, (int)gear);
        if (audit is not null)
        {
            audit.TelemetrySamples++;
            audit.MaximumRecordedRpm = Math.Max(audit.MaximumRecordedRpm, rpm);
        }
        if (_previousTelemetry is { } before && before.Car == current.Car)
        {
            var advance = unchecked((int)(current.GameTime - before.GameTime));
            if (advance == 0)
            {
                _equalGameTimes++;
                if (raw is not null && before.Raw is not null && raw != before.Raw) _equalGameTimesChanged++;
            }
            else if (advance < 0) _gameClockRegressions++;
            else if (current.GameTime < before.GameTime) _gameClockWraps++;
            // A full-throttle engine-output interruption is not proof of a limiter:
            // traction control, clutch and other controls can cause the same signal.
            if (audit is not null && before.Gear == current.Gear && qpc > before.Qpc &&
                Milliseconds(qpc - before.Qpc) <= 100 && before.Race && current.Race &&
                before.Accelerator >= 250 && current.Accelerator >= 250 &&
                before.Brake <= 5 && current.Brake <= 5 && before.Speed >= 5 && current.Speed >= 5 &&
                before.Power > 1 && before.Torque > 1 && current.Power <= 0 && current.Torque <= 0)
                audit.FullThrottleOutputCutCandidates++;
        }
        _previousTelemetry = current;
    }

    private void ObserveEvaluation(JsonElement payload)
    {
        ObserveProfile(payload, "profile");
        if (String(payload, "decision", out var decision) && decision == "ParkedRecorderCanary") return;
        if (Object(payload, "cue", out var submittedCue) && IsCanaryCue(submittedCue)) return;
        var audit = PayloadGear(payload);
        if (audit is null) return;
        audit.Evaluations++;
        if (Number(payload, "engineRpm", out var rpm)) audit.MaximumEvaluatedRpm = Math.Max(audit.MaximumEvaluatedRpm, rpm);
        if (!Object(payload, "cue", out var cue)) { _malformed++; return; }
        if (Number(cue, "targetRpm", out var target) && target > 0)
        {
            audit.MinimumTargetRpm = Math.Min(audit.MinimumTargetRpm, target);
            audit.MaximumTargetRpm = Math.Max(audit.MaximumTargetRpm, target);
        }
        if (Bool(cue, "enabled", out var enabled) && enabled && Long(cue, "stage", out var stage) && stage == 3)
            audit.RedRequestedSamples++;
    }

    private void ObserveSubmission(JsonElement payload)
    {
        if (!Bool(payload, "submitted", out var submitted)) { _malformed++; return; }
        if (submitted) _successfulSubmissions++; else _failedSubmissions++;
        if (Bool(payload, "canary", out var canary) && canary ||
            Object(payload, "shiftCue", out var submittedCue) && IsCanaryCue(submittedCue)) return;
        var audit = PayloadGear(payload);
        if (audit is null || !submitted) return;
        audit.SuccessfulSubmissions++;
        if (Object(payload, "shiftCue", out var cue) && Bool(cue, "enabled", out var enabled) && enabled &&
            Long(cue, "stage", out var stage) && stage == 3 && Bool(cue, "flashOn", out var flash) && flash &&
            Bool(cue, "isVisible", out var visible) && visible)
            audit.RedOnSubmissions++;
    }

    private void ObserveButton(JsonElement payload)
    {
        if (!String(payload, "edge", out var edge) || edge != "pressed") return;
        _buttonPresses++;
        if (!Long(payload, "earliestObservedEdgeQpc", out var earliest) || earliest <= 0 ||
            !Long(payload, "latestObservedEdgeQpc", out var latest) || latest < earliest ||
            !Long(payload, "pollStartedQpc", out var start) || start < earliest ||
            !Long(payload, "pollCompletedQpc", out var end) || end < start || end != latest)
        { _invalidInputBrackets++; return; }
        _inputBracket.Add(Milliseconds(latest - earliest));
        _inputQuery.Add(Milliseconds(end - start));
    }

    private static bool IsCanaryCue(JsonElement cue) => Number(cue, "targetRpm", out var target) && target == 1;

    private void ObservePixels(JsonElement payload)
    {
        // Existing exports encode the enum as a number; allow named exports too.
        var observed = Long(payload, "status", out var status) ? status == 0 :
            String(payload, "status", out var statusName) && statusName == "Observed";
        if (!observed) { _pixelFailures++; return; }
        if (!Long(payload, "readStartedQpc", out var start) || start <= 0 ||
            !Long(payload, "readFinishedQpc", out var end) || end < start)
        { _malformed++; return; }
        _pixelReads++;
        if (Bool(payload, "qualified", out var qualified) && qualified) _qualifiedPixelReads++;
        _readbackCost.Add(Milliseconds(end - start));
        if (_lastPixelEnd > 0 && start >= _lastPixelEnd) _readbackGap.Add(Milliseconds(start - _lastPixelEnd));
        _lastPixelEnd = end;
    }

    private void ObserveProfile(JsonElement payload, string name)
    {
        if (!String(payload, name, out var profile) || profile.Length is 0 or > 128 ||
            profile.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_')) return;
        if (_profiles.Count < MaximumProfiles || _profiles.Contains(profile)) _profiles.Add(profile);
        else _truncated = true;
    }

    private GearAudit? PayloadGear(JsonElement payload)
    {
        if (!Long(payload, "carOrdinal", out var car) || car is <= 0 or > int.MaxValue ||
            !Long(payload, "gear", out var gear) || gear is < 1 or > 10) return null;
        return Gear((int)car, (int)gear);
    }

    private GearAudit? Gear(int car, int gear)
    {
        if (gear is < 1 or > 10) return null;
        if (_gears.TryGetValue((car, gear), out var existing)) return existing;
        if (_gears.Count == MaximumCarGears) { _truncated = true; return null; }
        var audit = new GearAudit(car, gear);
        _gears.Add((car, gear), audit);
        return audit;
    }

    internal ShiftCaptureSessionAuditSnapshot Snapshot() => new(
        1, _frequency, _records, _malformed, _truncated,
        _channels.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(pair => pair.Key, pair => pair.Value.Snapshot(), StringComparer.Ordinal),
        _gears.Values.OrderBy(gear => gear.Car).ThenBy(gear => gear.Gear).Select(gear => gear.Snapshot()).ToArray(),
        _profiles.Count, _configurationRecords, _rawPackets, _parseFailures,
        _equalGameTimes, _equalGameTimesChanged, _gameClockRegressions, _gameClockWraps,
        _buttonPresses, _invalidInputBrackets, _inputBracket.Snapshot(), _inputQuery.Snapshot(),
        _successfulSubmissions, _failedSubmissions, _pixelReads, _qualifiedPixelReads, _pixelFailures,
        _readbackCost.Snapshot(), _readbackGap.Snapshot(),
        [.. new[] { "telemetry", "native_configuration", "controller_button", "cue_evaluation", "cue_submission", "cue_pixels" }
            .Where(kind => !_channels.ContainsKey(kind))])
    {
        InactiveTelemetrySamples = _inactiveTelemetry,
        CompletedPixelChecks = _completedPixelChecks,
        PassedPixelChecks = _passedPixelChecks,
        PixelContextQualifiedAtStop = _pixelContextQualifiedAtStop,
        PixelQualificationLostDuringSession = _pixelQualificationLost
    };

    internal string ToPlainText()
    {
        var data = Snapshot();
        var text = new StringBuilder("Wisp shift capture: recorded-channel audit\n\n");
        text.AppendLine(FormattableString.Invariant($"Recorded events: {data.Records}; raw packets: {data.RawPackets}; parse failures: {data.ParseFailures}."));
        if (data.InactiveTelemetrySamples > 0)
            text.AppendLine(FormattableString.Invariant($"Valid race-off telemetry packets: {data.InactiveTelemetrySamples}; excluded from driving comparisons."));
        text.AppendLine(FormattableString.Invariant($"Recorded configurations: {data.ConfigurationRecords}; distinct model fingerprints: {data.ProfileCount}."));
        text.AppendLine(FormattableString.Invariant($"Equal game timestamps: {data.EqualGameTimestamps}; changed raw packets within those timestamps: {data.EqualGameTimestampsWithChangedRawPackets}."));
        text.AppendLine(FormattableString.Invariant($"Button presses: {data.ButtonPresses}; valid edge-bracket maximum: {data.InputEdgeBracketMilliseconds.Maximum?.ToString("F3", CultureInfo.InvariantCulture) ?? "unavailable"} ms."));
        text.AppendLine(FormattableString.Invariant($"Successful render submissions: {data.SuccessfulSubmissions}; qualified desktop readbacks: {data.QualifiedPixelReads}."));
        if (data.CompletedPixelChecks > 0)
            text.AppendLine(FormattableString.Invariant($"Parked pixel checks: {data.PassedPixelChecks} passed out of {data.CompletedPixelChecks}. A pass does not qualify later HUD contexts."));
        if (data.PixelContextQualifiedAtStop is { } qualified)
            text.AppendLine("Pixel context at stop: " + (qualified ? "qualified." : "unqualified."));
        if (data.PixelQualificationLostDuringSession is true)
            text.AppendLine("Pixel qualification was lost later in the session; earlier successful checks remain recorded.");
        text.AppendLine();
        text.AppendLine("Car / gear: recorded maximum RPM; target range; red requests; red-on submissions; full-throttle output-cut candidates");
        foreach (var gear in data.Gears)
            text.AppendLine(FormattableString.Invariant($"{gear.CarOrdinal} / {gear.Gear}: {gear.MaximumRecordedRpm:F2}; {gear.MinimumTargetRpm?.ToString("F2", CultureInfo.InvariantCulture) ?? "none"} - {gear.MaximumTargetRpm?.ToString("F2", CultureInfo.InvariantCulture) ?? "none"}; {gear.RedRequestedSamples}; {gear.RedOnSubmissions}; {gear.FullThrottleOutputCutCandidates}"));
        if (data.AbsentRecordedChannels.Length > 0) text.AppendLine("Absent recorded channels: " + string.Join(", ", data.AbsentRecordedChannels));
        if (data.SummaryTruncated) text.AppendLine("Summary bounds were reached; inspect the retained raw events.");
        if (data.MalformedAuditRecords > 0) text.AppendLine("Some events lacked fields expected by this audit; raw events remain authoritative.");
        text.AppendLine();
        text.AppendLine(data.ClockMeaning);
        text.AppendLine(data.GearGroupingMeaning);
        text.AppendLine(data.InterpretationLimits);
        return text.ToString();
    }

    private double Milliseconds(long ticks) => ticks * 1000d / _frequency;
    private static bool SafeLabel(string value) => value.Length is > 0 and <= 64 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
    private static bool Property(JsonElement item, string name, out JsonElement value)
    { value = default; return item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out value); }
    private static bool Object(JsonElement item, string name, out JsonElement value) =>
        Property(item, name, out value) && value.ValueKind == JsonValueKind.Object;
    private static bool Long(JsonElement item, string name, out long value)
    { value = 0; return Property(item, name, out var found) && found.ValueKind == JsonValueKind.Number && found.TryGetInt64(out value); }
    private static bool Number(JsonElement item, string name, out double value)
    { value = 0; return Property(item, name, out var found) && found.ValueKind == JsonValueKind.Number && found.TryGetDouble(out value) && double.IsFinite(value); }
    private static bool String(JsonElement item, string name, out string value)
    { value = ""; if (!Property(item, name, out var found) || found.ValueKind != JsonValueKind.String) return false; value = found.GetString()!; return true; }
    private static bool Bool(JsonElement item, string name, out bool value)
    { value = false; if (!Property(item, name, out var found) || found.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false; value = found.GetBoolean(); return true; }

    private sealed record Telemetry(int Car, int Gear, uint GameTime, long Qpc, double Rpm, string? Raw,
        double? Torque, double? Power, double? Accelerator, double? Brake, double? Speed, bool Race);
    private sealed class GearAudit(int car, int gear)
    {
        internal int Car = car, Gear = gear;
        internal long TelemetrySamples, Evaluations, RedRequestedSamples, SuccessfulSubmissions, RedOnSubmissions, FullThrottleOutputCutCandidates;
        internal double MaximumRecordedRpm, MaximumEvaluatedRpm, MinimumTargetRpm = double.PositiveInfinity, MaximumTargetRpm;
        internal ShiftCaptureGearAudit Snapshot() => new(Car, Gear, TelemetrySamples, Evaluations,
            MaximumRecordedRpm, MaximumEvaluatedRpm, double.IsFinite(MinimumTargetRpm) ? MinimumTargetRpm : null,
            MaximumTargetRpm > 0 ? MaximumTargetRpm : null, RedRequestedSamples, SuccessfulSubmissions,
            RedOnSubmissions, FullThrottleOutputCutCandidates);
    }
    private sealed class Cadence
    {
        private long _samples, _first, _last, _equal, _regressions, _gaps;
        private readonly Measure _interval = new();
        internal void Observe(long qpc, long frequency)
        {
            if (_samples++ == 0) _first = qpc;
            else if (qpc < _last) _regressions++;
            else if (qpc == _last) _equal++;
            else
            {
                var milliseconds = (qpc - _last) * 1000d / frequency;
                _interval.Add(milliseconds);
                if (milliseconds > 100) _gaps++;
            }
            _last = qpc;
        }
        internal ShiftCaptureChannelAudit Snapshot() => new(_samples, _first, _last, _equal, _regressions, _gaps, _interval.Snapshot());
    }
    private sealed class Measure
    {
        private long _count;
        private double _minimum = double.PositiveInfinity, _maximum, _mean;
        internal void Add(double value)
        {
            if (!double.IsFinite(value) || value < 0) return;
            _count++;
            _mean += (value - _mean) / _count;
            _minimum = Math.Min(_minimum, value);
            _maximum = Math.Max(_maximum, value);
        }
        internal ShiftCaptureAuditMeasure Snapshot() => new(_count, _count > 0 ? _minimum : null,
            _count > 0 ? _maximum : null, _count > 0 ? _mean : null);
    }
}

internal sealed record ShiftCaptureSessionAuditSnapshot(int SchemaVersion, long QpcFrequency, long Records,
    long MalformedAuditRecords, bool SummaryTruncated, IReadOnlyDictionary<string, ShiftCaptureChannelAudit> Channels,
    ShiftCaptureGearAudit[] Gears, int ProfileCount, long ConfigurationRecords, long RawPackets, long ParseFailures,
    long EqualGameTimestamps, long EqualGameTimestampsWithChangedRawPackets, long GameClockRegressions, long GameClockWraps,
    long ButtonPresses, long InvalidInputBrackets, ShiftCaptureAuditMeasure InputEdgeBracketMilliseconds,
    ShiftCaptureAuditMeasure InputQueryMilliseconds, long SuccessfulSubmissions, long FailedSubmissions,
    long PixelReads, long QualifiedPixelReads, long PixelFailures, ShiftCaptureAuditMeasure ReadbackMilliseconds,
    ShiftCaptureAuditMeasure ReadbackIdleGapMilliseconds, string[] AbsentRecordedChannels)
{
    public long InactiveTelemetrySamples { get; init; }
    public long CompletedPixelChecks { get; init; }
    public long PassedPixelChecks { get; init; }
    public bool? PixelContextQualifiedAtStop { get; init; }
    public bool? PixelQualificationLostDuringSession { get; init; }
    public string ClockMeaning => "Intervals use recorded local QPC, not game milliseconds. Repeated game timestamps can contain changed packets. Journal order is producer arrival order; cross-channel event timestamps can interleave.";
    public string GearGroupingMeaning => "Gear rows aggregate each car and gear across the session; they are not tune-specific. Use fingerprint-separated scenario coverage and raw events for model validation.";
    public string InterpretationLimits => "This audit describes recorded observations, not storage-integrity or scenario-coverage certification. Output cuts are candidates, not confirmed limiter events. Red submissions are not displayed frames; readbacks are not scanout. B/X brackets are not game command-consumption times. No universal optimal shift accuracy is established.";
}
internal sealed record ShiftCaptureChannelAudit(long Samples, long FirstQpc, long LastQpc, long EqualQpc,
    long QpcRegressions, long GapsOver100Milliseconds, ShiftCaptureAuditMeasure IntervalMilliseconds);
internal sealed record ShiftCaptureAuditMeasure(long Count, double? Minimum, double? Maximum, double? Mean);
internal sealed record ShiftCaptureGearAudit(int CarOrdinal, int Gear, long TelemetrySamples, long Evaluations,
    double MaximumRecordedRpm, double MaximumEvaluatedRpm, double? MinimumTargetRpm, double? MaximumTargetRpm,
    long RedRequestedSamples, long SuccessfulSubmissions, long RedOnSubmissions, long FullThrottleOutputCutCandidates);
