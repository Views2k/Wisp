using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Wisp.Core;
using Wisp.Telemetry;
using Wisp.App.NativeRendering;
using Wisp.App.ShiftCapture;

namespace Wisp.App;

// Private, explicitly armed diagnostics. No network, injected input or game writes.
internal sealed class ShiftCaptureSession
{
    private readonly TelemetryUdpReceiver _receiver;
    private readonly RunDatagramCapture _datagrams;
    private readonly ShiftCaptureJournal _journal;
    private readonly ShiftCaptureInputMonitor _input;
    private readonly ShiftCaptureBindingCheck _bindings = new(Stopwatch.Frequency);
    private readonly ShiftCaptureCoverage _coverage = new(Stopwatch.Frequency);
    private readonly ShiftCaptureExperimentCoverage _experiments = new(Stopwatch.Frequency);
    private readonly ShiftCaptureCanaryStartGate _canaryStartGate = new(Stopwatch.Frequency);
    private readonly ShiftCaptureCanaryPhases _canaryPhases = new(Stopwatch.Frequency);
    private readonly ShiftCapturePixelCheckHistory _pixelCheckHistory = new();
    private ShiftCaptureExperimentSnapshot? _experimentStatus;
    private long _experimentStatusAt;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _packets, _observations;
    private readonly object _gate = new();
    private readonly HashSet<string> _profiles = [];
    private (int Car, string Status, string Fingerprint, bool Associated)? _nativeState;
    private long _nativeStateRecorded;
    private bool _profileLimitReached;
    private string[] _missingScenarios = [];
    private Task<string>? _completion;
    private Target? _target;
    private long _canaryStarted, _lastReceipt, _lastEvaluation, _lastSubmission, _targetGeneration;
    private int _canaryRequested, _canaryFinished, _upEdges, _downEdges, _presentationQualified;
    private int _canaryAttempt;
    private readonly List<(int Phase, int Matches)> _canaryPixels = [];
    private string _status = "Armed. Return to Forza and stay parked for the recorder check.";
    private string? _canaryStartFailure;
    private (int Phase, int Reads)? _canaryProgress;
    private string? _currentProfile;
    private ShiftCuePerformance? _currentPerformance;
    private int _foreground;
    private long _lastPixels;
    private int _captureFault, _pixelFault, _pixelsStopped, _cueEnabled, _everEnabled, _canaryCar;
    private long _packetsWritten;
    private readonly uint _red;

    internal ShiftCaptureSession(TelemetryUdpReceiver receiver, AppSettings settings, string directory)
    {
        _receiver = receiver;
        _red = DiagnosticsViewModel.ResolveShiftCueColor(settings, 3);
        _datagrams = receiver.BeginRunCapture();
        try
        {
            _journal = new ShiftCaptureJournal(directory, new
            {
                schema = "shift-capture-v2",
                protocol = "matched-speed-ABBA-boost-repeat-parked-tune-roundtrip-v1",
                protocolMeaning = "Requested development protocol, not independently verified driver compliance. No new limiter test required; prior recording covers sampled-threshold failure.",
                version = ApplicationVersionInfo.MachineVersion,
                diagnosticBuild = ApplicationVersionInfo.DiagnosticBuildId,
                assemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(ShiftCaptureSession).Assembly.Location))),
                coreAssemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(AccelerationShiftProfile).Assembly.Location))),
                telemetryAssemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(Fh6PacketParser).Assembly.Location))),
                rendererSha256 = File.Exists(Path.Combine(AppContext.BaseDirectory, "Wisp.NativeRenderer.dll"))
                    ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Wisp.NativeRenderer.dll")))) : null,
                platform = "Windows-x64",
                input = "XInput B upshift; X downshift",
                timing = "Stopwatch QPC. Input edges are polling brackets. Pixel observations are desktop readbacks, not scanout or photons.",
                nativeMode = settings.NativeGaugeMode.ToString(),
                settings.CpuRenderingEnabled,
                gearDisplayMode = settings.GearDisplayMode.ToString(),
                settings.OverlayOpacity,
                settings.AccelerationShiftCueEnabled,
                settings.ShiftCueGreenColor,
                settings.ShiftCueYellowColor,
                settings.ShiftCueRedColor
            }, maxBytes: 1024L * 1024 * 1024);
        }
        catch { receiver.EndRunCapture(_datagrams, "Diagnostic start failed"); throw; }
        _input = new ShiftCaptureInputMonitor((kind, payload, stamp) =>
        {
            Record(kind, payload, stamp);
            if (kind == "controller_selection_changed")
            {
                _bindings.Reset();
                _coverage.InvalidateContext();
                _experiments.InvalidateContext();
            }
            if (payload is ShiftCaptureButtonEvent edge && edge.Pressed)
            {
                var upBefore = _bindings.UpVerified;
                var downBefore = _bindings.DownVerified;
                _bindings.ObserveButton(edge, stamp);
                if (upBefore != _bindings.UpVerified || downBefore != _bindings.DownVerified)
                    Record("controller_binding_result", new
                    {
                        upshiftVerified = _bindings.UpVerified,
                        downshiftVerified = _bindings.DownVerified,
                        source = "button-history-replay"
                    }, stamp);
                _coverage.ObserveButton(edge, stamp);
                if (edge.Button == "B") Interlocked.Increment(ref _upEdges);
                if (edge.Button == "X") Interlocked.Increment(ref _downEdges);
            }
        }, IsForzaForeground);
        _packets = Task.Run(ReadPacketsAsync);
        _observations = Task.Run(ObserveAsync);
    }

    internal bool Active => _journal.IsAccepting && !_stop.IsCancellationRequested;
    internal bool HasIncompleteEvidence => _journal.Error is not null || _missingScenarios.Length != 0;
    internal string SavedSummary => _journal.Error is not null
        ? "ZIP saved and verified. Some recording channels were incomplete; the included report lists them."
        : _missingScenarios.Length != 0
            ? "ZIP saved and verified. Raw evidence is intact; some driving checks are still missing. See the included report."
            : "ZIP saved and verified. Planned evidence collected for analysis; shift accuracy is not certified.";
    internal string Status
    {
        get
        {
            if (_journal.Error is { } error) return "Recorder needs attention: " + error + ". Stop and save the evidence.";
            if (Volatile.Read(ref _captureFault) != 0 || _stop.IsCancellationRequested)
                return "Recorder stopped. Stop and save the evidence before starting another session.";
            if (!_input.IsDeviceAvailable) return _input.Status + ". Stay parked.";
            if (Volatile.Read(ref _canaryFinished) == 0) return Volatile.Read(ref _status);
            if (Volatile.Read(ref _canaryStartFailure) is { } canaryFailure) return canaryFailure;
            if (Volatile.Read(ref _cueEnabled) == 0) return "Enable the performance shift cue before the driving check.";
            if (!_bindings.UpVerified || !_bindings.DownVerified)
            {
                if (_bindings.UpVerified) return "B upshift verified. Stay parked in second; tap X to return to first.";
                if (_bindings.DownVerified) return "X downshift verified. Stay parked in first; tap B and wait for second.";
                return $"B presses: {Volatile.Read(ref _upEdges)}; X presses: {Volatile.Read(ref _downEdges)}. Park in first; tap B, wait for second, then X to return to first.";
            }
            lock (_gate)
            {
                var profile = Volatile.Read(ref _currentProfile);
                if (profile is null || !_profiles.Contains("raw:" + profile) ||
                    !_profiles.Any(p => p.StartsWith("aux:" + profile + ":", StringComparison.Ordinal)))
                    return "Waiting for this car's verified configuration and auxiliary capture. Stay parked.";
            }
            var coverage = _coverage.Snapshot();
            var now = Stopwatch.GetTimestamp();
            if (_experimentStatus is null || Stopwatch.GetElapsedTime(_experimentStatusAt, now).TotalSeconds >= 2)
            {
                _experimentStatus = _experiments.Snapshot();
                _experimentStatusAt = now;
            }
            var next = coverage.ActionableMissingEvidence.FirstOrDefault() ?? _experimentStatus.ActionableMissingEvidence.FirstOrDefault() ??
                "Recorded comparison candidates collected; their validity and shift accuracy still require analysis.";
            var pixelCheck = PixelCheckSnapshot();
            var visualStatus = pixelCheck.Incomplete
                ? pixelCheck.CanaryPassed
                    ? "Parked pixel check passed; later pixel coverage is incomplete. Other channels continue. "
                    : "Pixel timing is incomplete; other channels continue. "
                : "Parked pixel and B/X checks passed. ";
            var counts = $"{Interlocked.Read(ref _packetsWritten):N0} packets · {coverage.RecordedMovingUpshifts} moving upshifts · " +
                $"{coverage.BracketedEngineOutputTimings} output timing brackets · ";
            if (Volatile.Read(ref _foreground) == 0) return counts + visualStatus + next;
            if (Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastReceipt)).TotalSeconds > 1 ||
                Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastEvaluation)).TotalSeconds > 1 ||
                Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastSubmission)).TotalSeconds > 1)
                return "Waiting for fresh telemetry, cue evaluations and rendered HUD. Stay parked.";
            return counts + visualStatus + next;
        }
    }

    internal void Record(string kind, object payload, long? stamp = null) => _journal.TryRecord(kind, payload, stamp);

    internal void RecordProfile(string key, object payload)
    {
        lock (_gate)
        {
            if (!_journal.IsAccepting || _stop.IsCancellationRequested) return;
            if (_profiles.Count >= 128 && !_profiles.Contains(key))
            {
                if (!_profileLimitReached)
                {
                    _profileLimitReached = true;
                    _journal.MarkIncomplete("native-configuration-capacity");
                }
                return;
            }
            if (!_profiles.Add(key)) return;
            Record("native_configuration", payload);
        }
    }

    internal void RecordNativeAssociation(int car, string status, string? fingerprint, long observed, bool associated)
    {
        if (!Active) return;
        var now = Stopwatch.GetTimestamp();
        var current = (car, status, fingerprint ?? "", associated);
        lock (_gate)
        {
            if (_nativeState == current && now >= _nativeStateRecorded && now - _nativeStateRecorded < Stopwatch.Frequency) return;
            var changed = _nativeState != current;
            _nativeState = current;
            _nativeStateRecorded = now;
            Record("native_association", new { carOrdinal = car, status, fingerprint, observedTimestamp = observed, associated, changed }, now);
        }
    }

    internal void RecordEvaluation(object payload, long timestamp, bool cueEnabled, ShiftCuePerformance? profile)
    {
        Volatile.Write(ref _currentPerformance, profile);
        Volatile.Write(ref _currentProfile, profile?.Fingerprint);
        Volatile.Write(ref _cueEnabled, cueEnabled ? 1 : 0);
        if (cueEnabled) Volatile.Write(ref _everEnabled, 1);
        Interlocked.Exchange(ref _lastEvaluation, timestamp);
        Record("cue_evaluation", payload, timestamp);
    }

    internal void RequestCanary()
    {
        lock (_gate)
        {
            if (_pixelsStopped != 0)
            {
                Record("canary_retry_declined", new { reason = "Session-pixel-cost-or-reader-stop", telemetryContinues = true });
                return;
            }
            if (_canaryStarted != 0 && _canaryFinished == 0)
                AbortCanaryLocked("Manual-retry", Stopwatch.GetTimestamp());
            Interlocked.Increment(ref _canaryAttempt);
            Interlocked.Exchange(ref _canaryRequested, 1);
            Interlocked.Exchange(ref _canaryFinished, 0);
            InvalidatePixelQualificationLocked("Canary-retry", Stopwatch.GetTimestamp());
            Interlocked.Exchange(ref _pixelFault, 0);
            Interlocked.Exchange(ref _canaryStarted, 0);
            _canaryCar = 0;
            _canaryPixels.Clear();
            _canaryStartGate.Reset();
            _canaryPhases.Reset();
            _canaryProgress = null;
            Volatile.Write(ref _canaryStartFailure, null);
            Volatile.Write(ref _status, "Return to Forza and stay parked in first gear. The two-flash pixel check takes about 10–40 seconds, depending on readback speed.");
        }
        Record("canary_requested", new { parkedOnly = true, redArgb = _red });
    }

    internal ShiftCueVisualState? Canary(VehicleState state, NativeHudSnapshot native)
    {
        if (Volatile.Read(ref _canaryRequested) == 0 || Volatile.Read(ref _canaryFinished) != 0) return null;
        var eligible = state.IsRaceOn && float.IsFinite(state.GroundSpeedMetersPerSecond) &&
            MathF.Abs(state.GroundSpeedMetersPerSecond) <= .5 && state.NumCylinders > 0 && state.Accelerator <= 5 &&
            !state.IsElectric && state.Gear >= TransmissionGear.First && native.Available && native.CarOrdinal == state.CarOrdinal &&
            (_canaryCar == 0 || _canaryCar == state.CarOrdinal) && native.VisibilityObservedTimestamp > 0 &&
            Stopwatch.GetElapsedTime(native.VisibilityObservedTimestamp).TotalSeconds <= .25 &&
            native.GameplayVisibility == NativeGameplayVisibility.Visible && Volatile.Read(ref _foreground) != 0;
        lock (_gate)
        {
            if (_canaryRequested == 0 || _canaryFinished != 0) return null;
            if (_pixelsStopped != 0 || _stop.IsCancellationRequested)
            {
                AbortCanaryLocked("Recorder-stopped", Stopwatch.GetTimestamp());
                return null;
            }
            if (_canaryStarted != 0 && !eligible)
            {
                AbortCanaryLocked("Driving-or-visibility-state-changed", Stopwatch.GetTimestamp());
                return null;
            }
            if (_canaryStarted == 0)
            {
                var readiness = _canaryStartGate.Observe(Stopwatch.GetTimestamp(), _target?.Generation ?? 0, eligible);
                if (readiness == ShiftCaptureCanaryReadiness.TimedOut)
                {
                    Interlocked.Exchange(ref _canaryFinished, 1);
                    Interlocked.Exchange(ref _pixelFault, 1);
                    Volatile.Write(ref _canaryStartFailure, "Pixel check stopped because the HUD did not stay stable. Other recording channels continue; retry the pixel check while parked.");
                    Record("canary_result", new { qualifies = false, reason = "Target-did-not-settle", telemetryContinues = true });
                    return null;
                }
                if (readiness != ShiftCaptureCanaryReadiness.Ready)
                {
                    if (eligible) Volatile.Write(ref _status, "Stay parked while the HUD settles for the pixel check.");
                    return null;
                }
                _canaryCar = state.CarOrdinal;
                var started = Stopwatch.GetTimestamp();
                _canaryPhases.Start(started, _canaryAttempt, _target!.Generation);
                Interlocked.Exchange(ref _canaryStarted, started);
                _canaryProgress = (0, 0);
                Volatile.Write(ref _status, "Stay parked. Pixel check: phase 1 of 5.");
            }
            return _canaryPhases.Current is not null
                ? new ShiftCueVisualState(true, 3, _red, _canaryPhases.FlashOn, 1) : null;
        }
    }

    internal void SetTarget(nint window, ShiftCaptureRect rectangle, double opacity)
    {
        var effectiveAlpha = (uint)Math.Round((_red >> 24) * Math.Clamp(opacity, 0, 1));
        var next = new Target(window, rectangle, (_red & 0x00FFFFFF) | effectiveAlpha << 24);
        lock (_gate)
        {
            if (_target is { } current && Equals(current with { Generation = 0 }, next)) return;
            next = next with { Generation = ++_targetGeneration };
            Volatile.Write(ref _target, next);
            InvalidatePixelQualificationLocked("Pixel-target-changed", Stopwatch.GetTimestamp());
            if (_canaryStarted != 0 && _canaryFinished == 0)
                AbortCanaryLocked("Pixel-region-changed", Stopwatch.GetTimestamp());
        }
        Record("pixel_target", new { rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height, opacity, next.ExpectedArgb, next.Generation });
    }

    private void AbortCanaryLocked(string reason, long now)
    {
        _canaryPhases.Cancel(now);
        Interlocked.Exchange(ref _canaryFinished, 1);
        InvalidatePixelQualificationLocked(reason, now);
        Interlocked.Exchange(ref _pixelFault, 1);
        Record("canary_aborted", new { reason, attempt = _canaryAttempt, targetGeneration = _target?.Generation, phaseWindows = _canaryPhases.Windows });
    }

    private ShiftCaptureCanaryPhaseToken? AdvanceCanary(Target target, int attempt, ShiftCapturePresentationProbe probe)
    {
        lock (_gate)
        {
            if (_canaryStarted == 0 || _canaryFinished != 0 || _canaryAttempt != attempt || !Equals(_target, target)) return null;
            _canaryPhases.Advance(Stopwatch.GetTimestamp());
            if (_canaryPhases.Status is ShiftCaptureCanaryPhaseStatus.Completed or ShiftCaptureCanaryPhaseStatus.TimedOut)
            {
                var timedOut = _canaryPhases.Status == ShiftCaptureCanaryPhaseStatus.TimedOut;
                var phases = _canaryPixels.ToArray();
                var qualifies = !timedOut && probe.SetQualified(CanaryPasses(phases));
                _pixelCheckHistory.RecordCanary(qualifies);
                Interlocked.Exchange(ref _presentationQualified, qualifies ? 1 : 0);
                Interlocked.Exchange(ref _pixelFault, qualifies ? 0 : 1);
                Interlocked.Exchange(ref _canaryFinished, 1);
                if (timedOut)
                    Volatile.Write(ref _canaryStartFailure, "Pixel check stopped because a phase did not collect enough readbacks in time. Other recording channels continue; see the saved report.");
                Record("canary_result", new
                {
                    qualifies,
                    attempt,
                    targetGeneration = target.Generation,
                    reason = timedOut ? "Phase-readback-timeout" : qualifies ? "Qualified" : "Pixel-contrast-or-sample-check-failed",
                    minimumPhaseSeconds = ShiftCaptureCanaryPhases.MinimumPhaseSeconds,
                    maximumPhaseSeconds = ShiftCaptureCanaryPhases.MaximumPhaseSeconds,
                    phaseWindows = _canaryPhases.Windows,
                    qpcFrequency = _canaryPhases.QpcFrequency,
                    phases = phases.Select(p => new { p.Phase, p.Matches }).ToArray(),
                    meaning = "Desktop readback distinguishes this actual HUD ring off/on/off/on/off; not physical scanout. Phase windows are requested states, not display times."
                });
                return null;
            }
            if (_canaryPhases.Current is { } current && _canaryProgress != (current.Phase, _canaryPhases.AcceptedReads))
            {
                _canaryProgress = (current.Phase, _canaryPhases.AcceptedReads);
                Volatile.Write(ref _status, $"Stay parked. Pixel check: phase {current.Phase + 1} of 5 · {_canaryPhases.AcceptedReads} readbacks.");
            }
            return _canaryPhases.Current;
        }
    }

    private void InvalidatePixelQualificationLocked(string reason, long now)
    {
        if (_pixelCheckHistory.RecordInvalidation(_presentationQualified != 0, reason, now))
            Record("pixel_qualification_invalidated", new { reason, invalidatedQpc = now, targetGeneration = _target?.Generation });
        Interlocked.Exchange(ref _presentationQualified, 0);
    }

    private ShiftCapturePixelCheckSummary PixelCheckSnapshot()
    {
        lock (_gate) return _pixelCheckHistory.Snapshot(_presentationQualified != 0, _pixelFault != 0);
    }

    internal void RecordSubmission(nint window, NativeGaugeFrame frame, long renderSequence, long queued, long started, bool submitted)
    {
        if (Volatile.Read(ref _target)?.Window != window) return;
        var now = Stopwatch.GetTimestamp();
        if (submitted) Interlocked.Exchange(ref _lastSubmission, now);
        Record("cue_submission", new
        {
            renderSequence,
            queued,
            started,
            completed = now,
            submitted,
            frame.ReceivedTimestamp,
            frame.CarOrdinal,
            frame.GameTimestampMilliseconds,
            gear = (int)frame.Gear,
            frame.ShiftCue,
            // The private canary carries this impossible driving target through
            // the render queue; submission time may follow canary completion.
            canary = frame.ShiftCue.TargetRpm == 1
        }, now);
    }

    private async Task ReadPacketsAsync()
    {
        var parser = new Fh6PacketParser();
        try
        {
            await foreach (var packet in _datagrams.ReadAllAsync())
            {
                var parsed = parser.TryParse(packet.Bytes.Span, DateTimeOffset.UtcNow, out var state, out var error, packet.Timestamp);
                var upBefore = _bindings.UpVerified;
                var downBefore = _bindings.DownVerified;
                var processedAt = Stopwatch.GetTimestamp();
                var profile = Volatile.Read(ref _currentPerformance);
                var associated = false;
                if (parsed && state is not null)
                {
                    _bindings.ObserveState(state, packet.Timestamp);
                    var now = processedAt;
                    associated = Volatile.Read(ref _foreground) != 0 && profile?.Profile is { IsValid: true } &&
                        profile.Status == "Research" && profile.CarOrdinal == state.CarOrdinal &&
                        profile.ObservedTimestamp > 0 && now >= profile.ObservedTimestamp &&
                        now - profile.ObservedTimestamp <= Stopwatch.Frequency &&
                        now >= packet.Timestamp && now - packet.Timestamp <= Stopwatch.Frequency * .15;
                    _coverage.ObserveTelemetry(state, packet.Timestamp, associated ? profile!.Fingerprint : null);
                    _experiments.Observe(state, packet.Timestamp, associated ? profile!.Fingerprint : null);
                }
                else
                {
                    _bindings.ResetPending();
                    _coverage.InvalidateContext();
                    _experiments.InvalidateContext();
                }
                if (upBefore != _bindings.UpVerified || downBefore != _bindings.DownVerified)
                    Record("controller_binding_result", new
                    {
                        upshiftVerified = _bindings.UpVerified,
                        downshiftVerified = _bindings.DownVerified,
                        state?.GameTimestampMilliseconds,
                        state?.Gear,
                        state?.CarOrdinal
                    }, packet.Timestamp);
                Interlocked.Exchange(ref _lastReceipt, packet.Timestamp);
                Record("telemetry", new
                {
                    packet.Sequence,
                    receivedTimestamp = packet.Timestamp,
                    parsed,
                    packetLength = packet.Bytes.Length,
                    rawBase64 = Convert.ToBase64String(packet.Bytes.Span),
                    error = error.ToString(),
                    state,
                    additional = ShiftCapturePacketEvidence.Read(packet.Bytes.Span),
                    association = new
                    {
                        associated,
                        profile = profile?.Fingerprint,
                        nativeStatus = profile?.Status,
                        nativeObservedTimestamp = profile?.ObservedTimestamp,
                        processedAtQpc = processedAt,
                        nativeObservationMinusPacketMilliseconds = profile is null ? (double?)null :
                            (profile.ObservedTimestamp - packet.Timestamp) * 1000d / Stopwatch.Frequency,
                        packetQueueAgeMilliseconds = (processedAt - packet.Timestamp) * 1000d / Stopwatch.Frequency,
                        meaning = "Processing-time candidate association; separately sampled configuration is not an atomic state for this packet.",
                        gameForeground = Volatile.Read(ref _foreground) != 0
                    }
                }, packet.Timestamp);
                Interlocked.Increment(ref _packetsWritten);
            }
            if (!_stop.IsCancellationRequested)
            {
                Interlocked.Exchange(ref _captureFault, 1);
                Record("capture_error", new { component = "telemetry", error = "Stream-ended-before-session-stop" });
                _stop.Cancel();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            Record("capture_error", new { component = "telemetry", error = ex.GetType().Name });
            Interlocked.Exchange(ref _captureFault, 1);
            _stop.Cancel();
        }
    }

    private async Task ObserveAsync()
    {
        ShiftCapturePresentationProbe? probe = null;
        Target? prior = null;
        var priorAttempt = -1;
        long probeContext = 0;
        var priorFocus = false;
        try
        {
            while (!_stop.IsCancellationRequested && _journal.IsAccepting)
            {
                var foreground = IsForzaForeground();
                Volatile.Write(ref _foreground, foreground ? 1 : 0);
                if (foreground != priorFocus)
                {
                    Record("foreground", new { forza = foreground });
                    _coverage.InvalidateContext();
                    _experiments.InvalidateContext();
                    if (!foreground)
                    {
                        _bindings.ResetPending();
                        lock (_gate)
                            if (_canaryStarted != 0 && _canaryFinished == 0)
                                AbortCanaryLocked("Driving-or-visibility-state-changed", Stopwatch.GetTimestamp());
                    }
                    priorFocus = foreground;
                }
                var target = Volatile.Read(ref _target);
                var attempt = Volatile.Read(ref _canaryAttempt);
                if (target is not null && Volatile.Read(ref _pixelsStopped) == 0 &&
                    (!Equals(target, prior) || attempt != priorAttempt))
                {
                    if (probe is null) probe = new ShiftCapturePresentationProbe(target.Window);
                    else if (prior?.Window != target.Window) probe.ChangeWindow(target.Window);
                    probe.SetQualified(false);
                    probeContext++;
                    prior = target;
                    priorAttempt = attempt;
                }
                if (foreground && target is not null && probe is not null && Volatile.Read(ref _pixelsStopped) == 0)
                {
                    var sampledPhase = AdvanceCanary(target, attempt, probe);
                    ShiftCapturePresentationSample? sample = null;
                    try { sample = probe.TrySample(target.Rectangle, target.ExpectedArgb, probeContext); }
                    catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
                    {
                        lock (_gate)
                        {
                            Interlocked.Exchange(ref _pixelFault, 1);
                            Interlocked.Exchange(ref _pixelsStopped, 1);
                            InvalidatePixelQualificationLocked("Pixel-observer-" + error.GetType().Name, Stopwatch.GetTimestamp());
                            Interlocked.Exchange(ref _canaryFinished, 1);
                            _canaryPhases.Cancel(Stopwatch.GetTimestamp());
                        }
                        Record("capture_error", new
                        {
                            component = "pixel-observer",
                            error = error.GetType().Name,
                            telemetryContinues = true
                        });
                        probe.Dispose();
                        probe = null;
                    }
                    if (sample is not null)
                    {
                        Record("cue_pixels", sample);
                        if (sample.Status == ShiftCapturePresentationStatus.Observed)
                            Interlocked.Exchange(ref _lastPixels, sample.ReadFinishedQpc);
                        else
                        {
                            lock (_gate)
                            {
                                // Context failures belong to the sampled attempt;
                                // the accumulated cost stop applies to the session.
                                var costStopped = sample.Status == ShiftCapturePresentationStatus.CostLimitExceeded;
                                if (costStopped || _canaryAttempt == attempt && Equals(_target, target))
                                {
                                    InvalidatePixelQualificationLocked(sample.Status.ToString(), sample.ReadFinishedQpc);
                                    Interlocked.Exchange(ref _pixelFault, 1);
                                    if (costStopped || sample.Status == ShiftCapturePresentationStatus.CaptureExcluded)
                                    {
                                        if (costStopped) Interlocked.Exchange(ref _pixelsStopped, 1);
                                        Interlocked.Exchange(ref _canaryFinished, 1);
                                        _canaryPhases.Cancel(Stopwatch.GetTimestamp());
                                        Record("canary_result", new { qualifies = false, reason = sample.Status.ToString(), telemetryContinues = true, phaseWindows = _canaryPhases.Windows });
                                    }
                                }
                            }
                        }
                        if (sample.Status == ShiftCapturePresentationStatus.Observed && sampledPhase is { } phase)
                            lock (_gate)
                                if (_canaryFinished == 0 && _canaryAttempt == attempt && Equals(_target, target) &&
                                    _canaryPhases.TryObserve(phase, sample.ReadStartedQpc, sample.ReadFinishedQpc, Stopwatch.GetTimestamp()))
                                    _canaryPixels.Add((phase.Phase, sample.Pixels.CompatiblePixelCount));
                    }
                }
                await Task.Delay(16, _stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            lock (_gate)
            {
                Interlocked.Exchange(ref _pixelFault, 1);
                InvalidatePixelQualificationLocked("Pixel-observer-" + ex.GetType().Name, Stopwatch.GetTimestamp());
                Interlocked.Exchange(ref _canaryFinished, 1);
                _canaryPhases.Cancel(Stopwatch.GetTimestamp());
            }
            Record("capture_error", new { component = "pixel-observer", error = ex.GetType().Name });
        }
        finally
        {
            Volatile.Write(ref _foreground, 0);
            lock (_gate)
            {
                Interlocked.Exchange(ref _canaryFinished, 1);
                _canaryPhases.Cancel(Stopwatch.GetTimestamp());
            }
            probe?.Dispose();
            // A duration/size/storage stop must stop every producer, not just pixel readback.
            if (!_stop.IsCancellationRequested) _stop.Cancel();
            _receiver.EndRunCapture(_datagrams, "Shift capture observer stopped");
            await _input.StopAsync().ConfigureAwait(false);
        }
    }

    internal static bool CanaryPasses(IEnumerable<(int Phase, int Matches)> samples)
    {
        var groups = samples.GroupBy(x => x.Phase).ToDictionary(x => x.Key, x => x.Select(y => y.Matches).Order().ToArray());
        if (Enumerable.Range(0, 5).Any(p => !groups.TryGetValue(p, out var values) || values.Length < 5)) return false;
        double Median(int phase) => groups[phase][groups[phase].Length / 2];
        var on = Math.Min(Median(1), Median(3));
        var off = Math.Max(Median(0), Math.Max(Median(2), Median(4)));
        return on >= 12 && off <= on * .25;
    }

    internal Task<string> StopAsync(string reason = "user-stop")
    {
        lock (_gate) return _completion ??= CompleteAsync(reason);
    }

    private async Task<string> CompleteAsync(string reason)
    {
        ShiftCaptureHub.Detach(this);
        _stop.Cancel();
        _receiver.EndRunCapture(_datagrams, "Shift capture stopped");
        await _input.StopAsync().ConfigureAwait(false);
        await Task.WhenAll(_packets, _observations).ConfigureAwait(false);
        if (_datagrams.DroppedDatagrams != 0) _journal.MarkIncomplete("telemetry-drops");
        if (_packetsWritten == 0 || _lastEvaluation == 0 || _lastSubmission == 0) _journal.MarkIncomplete("missing-telemetry-or-cue-stream");
        if (!_bindings.UpVerified || !_bindings.DownVerified) _journal.MarkIncomplete("unverified-controller-bindings");
        var pixelCheck = PixelCheckSnapshot();
        if (pixelCheck.Incomplete) _journal.MarkIncomplete("pixel-check-incomplete");
        if (_captureFault != 0) _journal.MarkIncomplete("capture-worker-failed");
        if (_everEnabled == 0) _journal.MarkIncomplete("cue-never-enabled");
        var coverage = _coverage.Snapshot();
        Record("capture_coverage", coverage);
        _journal.SetScenarioCoverage(coverage);
        var experiments = _experiments.Snapshot();
        Record("experiment_coverage", experiments);
        _journal.SetExperimentCoverage(experiments);
        _missingScenarios = [.. coverage.ActionableMissingEvidence, .. experiments.ActionableMissingEvidence];
        if (_missingScenarios.Length != 0) _journal.MarkScenarioIncomplete("missing-driving-evidence");
        lock (_gate)
        {
            if (!_profiles.Any(p => p.StartsWith("raw:", StringComparison.Ordinal))) _journal.MarkIncomplete("missing-native-configuration");
            if (_profiles.Where(p => p.StartsWith("raw:", StringComparison.Ordinal)).Any(raw =>
                    !_profiles.Any(aux => aux.StartsWith("aux:" + raw[4..] + ":", StringComparison.Ordinal))))
                _journal.MarkIncomplete("missing-auxiliary-configuration");
        }
        Record("capture_summary", new
        {
            observedDatagrams = _datagrams.ObservedDatagrams,
            droppedDatagrams = _datagrams.DroppedDatagrams,
            writtenPackets = _packetsWritten,
            upshiftEdges = _upEdges,
            downshiftEdges = _downEdges,
            pixelCanaryPassed = pixelCheck.CanaryPassed,
            pixelContextQualifiedAtStop = pixelCheck.CurrentContextQualified,
            pixelObservationFault = pixelCheck.ObserverFault,
            pixelQualificationLostDuringSession = pixelCheck.QualificationLostDuringSession,
            pixelFirstQualificationLossReason = pixelCheck.FirstLossReason,
            pixelFirstQualificationLossQpc = pixelCheck.FirstLossQpc,
            pixelCanaryMeaning = "Historical successful parked check; does not qualify later contexts or erase observation gaps.",
            upshiftVerified = _bindings.UpVerified,
            downshiftVerified = _bindings.DownVerified,
            lastTelemetry = _lastReceipt,
            lastEvaluation = _lastEvaluation,
            lastSubmission = _lastSubmission,
            sourceHasNoCommandConsumptionOrPhotonTime = true
        });
        Record("controller_polling_final", _input.PollingSnapshot);
        _input.Dispose();
        _stop.Dispose();
        return await _journal.StopAndPackageAsync(reason).ConfigureAwait(false);
    }

    internal static bool IsForzaForeground()
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out var id);
        if (id == 0) return false;
        try { using var process = Process.GetProcessById((int)id); return process.ProcessName.Equals("ForzaHorizon6", StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    private sealed record Target(nint Window, ShiftCaptureRect Rectangle, uint ExpectedArgb, long Generation = 0);
}

// Reporting history only. The session/probe retain ownership of live qualification.
internal sealed class ShiftCapturePixelCheckHistory
{
    private bool _canaryPassed;
    private string? _firstLossReason;
    private long? _firstLossQpc;

    internal void RecordCanary(bool qualifies) => _canaryPassed |= qualifies;

    internal bool RecordInvalidation(bool currentlyQualified, string reason, long timestamp)
    {
        if (!_canaryPassed || !currentlyQualified) return false;
        if (_firstLossReason is null)
        {
            _firstLossReason = reason;
            _firstLossQpc = timestamp;
        }
        return true;
    }

    internal ShiftCapturePixelCheckSummary Snapshot(bool currentlyQualified, bool observerFault) =>
        new(_canaryPassed, currentlyQualified, observerFault, _firstLossReason is not null, _firstLossReason, _firstLossQpc);
}

internal sealed record ShiftCapturePixelCheckSummary(bool CanaryPassed, bool CurrentContextQualified, bool ObserverFault,
    bool QualificationLostDuringSession, string? FirstLossReason, long? FirstLossQpc)
{
    internal bool Incomplete => !CanaryPassed || !CurrentContextQualified || ObserverFault || QualificationLostDuringSession;
}

internal static class ShiftCaptureHub
{
    private static ShiftCaptureSession? _current;
    internal static ShiftCaptureSession? Current => Volatile.Read(ref _current);
    internal static void Attach(ShiftCaptureSession session)
    {
        if (Interlocked.CompareExchange(ref _current, session, null) is not null)
            throw new InvalidOperationException("A diagnostic session is already active.");
    }
    internal static void Detach(ShiftCaptureSession session) => Interlocked.CompareExchange(ref _current, null, session);

    internal static void ObserveLayout(NativeAnalogSpeedometer control, AnalogHudLayout layout)
    {
        if (Current is not { } session || !control.IsVisible || PresentationSource.FromVisual(control) is not HwndSource source) return;
        var window = Window.GetWindow(control);
        if (window is not OverlayWindow) return;
        var box = layout.Gear(false);
        // Only the upper ring arc: exclude the gear digit, which may share the cue color.
        var a = control.PointToScreen(new Point(box.X + box.Width * .4, box.Y + box.Height * .10));
        var b = control.PointToScreen(new Point(box.X + box.Width * .6, box.Y + box.Height * .20));
        double opacity = 1;
        for (DependencyObject? current = control; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is UIElement element) opacity *= element.Opacity;
            if (ReferenceEquals(current, window)) break;
        }
        session.SetTarget(source.Handle, new ShiftCaptureRect((int)Math.Floor(a.X), (int)Math.Floor(a.Y),
            (int)Math.Ceiling(b.X - a.X), (int)Math.Ceiling(b.Y - a.Y)), opacity);
    }
}
