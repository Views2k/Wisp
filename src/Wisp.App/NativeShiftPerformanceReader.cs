using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Wisp.App;

internal enum NativeShiftPerformanceStatus
{
    Ready, UnsupportedBuild, UnsupportedPowertrain, UnsupportedModifiers,
    UnsupportedTransmission, InvalidData, ReadFailure, UnstableData, IdentityMismatch, CurveMismatch
}

internal sealed class NativeShiftPerformanceSnapshot
{
    internal bool Available => Status == NativeShiftPerformanceStatus.Ready;
    internal NativeShiftPerformanceStatus Status { get; }
    internal int CarOrdinal { get; }
    internal long ObservedTimestamp { get; }
    internal string Fingerprint { get; }
    internal double StepRpm { get; }
    internal double ExactRedlineRpm { get; }
    internal IReadOnlyList<double> TorqueNm { get; }
    internal IReadOnlyList<double> ForwardRatios { get; }
    internal IReadOnlyList<double> GearAccelerationFactors { get; }
    internal double FinalDrive { get; }
    internal double ConfiguredOperatingCeilingRpm { get; }
    internal IReadOnlyList<double> NativeBaselineUpperRpm { get; }
    internal IReadOnlyList<double> NativeAdjustedUpperRpm { get; }
    internal double ConfiguredPeakTorqueNm { get; }
    internal double ConfiguredPeakPowerWatts { get; }
    internal int OfficialExportCount { get; }
    internal double OfficialExportSourceMaximumRpm => Math.Max(0, OfficialExportCount - 1) * StepRpm;
    internal bool HasModifiers { get; }
    internal NativeCombustionRuntimeCurve? RuntimeCurve { get; }
    internal bool HasModifiedExtension => HasModifiers && TorqueNm.Count > OfficialExportCount;

    internal NativeShiftPerformanceSnapshot(NativeShiftPerformanceStatus status, int carOrdinal,
        long observedTimestamp = 0, string fingerprint = "", double stepRpm = 0,
        double exactRedlineRpm = 0, double[]? torqueNm = null, double[]? forwardRatios = null,
        double finalDrive = 0, double nativeCeilingCandidateRpm = 0,
        double[]? nativeBaselineUpperRpm = null, double[]? nativeAdjustedUpperRpm = null,
        double configuredPeakTorqueNm = 0, double configuredPeakPowerWatts = 0,
        int officialExportCount = 0, bool hasModifiers = false, double[]? gearAccelerationFactors = null,
        NativeCombustionRuntimeCurve? runtimeCurve = null)
    {
        Status = status;
        CarOrdinal = carOrdinal;
        ObservedTimestamp = observedTimestamp;
        Fingerprint = fingerprint;
        StepRpm = stepRpm;
        ExactRedlineRpm = exactRedlineRpm;
        TorqueNm = Array.AsReadOnly((double[])(torqueNm?.Clone() ?? Array.Empty<double>()));
        ForwardRatios = Array.AsReadOnly((double[])(forwardRatios?.Clone() ?? Array.Empty<double>()));
        GearAccelerationFactors = Array.AsReadOnly((double[])(gearAccelerationFactors?.Clone() ?? Array.Empty<double>()));
        FinalDrive = finalDrive;
        ConfiguredOperatingCeilingRpm = nativeCeilingCandidateRpm;
        NativeBaselineUpperRpm = Array.AsReadOnly((double[])(nativeBaselineUpperRpm?.Clone() ?? Array.Empty<double>()));
        NativeAdjustedUpperRpm = Array.AsReadOnly((double[])(nativeAdjustedUpperRpm?.Clone() ?? Array.Empty<double>()));
        ConfiguredPeakTorqueNm = configuredPeakTorqueNm;
        ConfiguredPeakPowerWatts = configuredPeakPowerWatts;
        OfficialExportCount = officialExportCount;
        HasModifiers = hasModifiers;
        RuntimeCurve = runtimeCurve;
    }
}

// Research-only configured combustion metadata. A matching curve does not establish optimal
// shift timing. The existing native worker owns this reader and its process handle.
internal sealed class NativeShiftPerformanceReader
{
    // Calibration needs coherent configuration, not a reconstructed torque curve.
    // Keep the research reader's stricter default for its independent audits.
    internal bool ConfigurationOnly { get; set; }
    private const string SupportedSha256 = "FEC4A63CDEAD26F6528564F0E33C3D7FD02CD887337ED6E1AEACA79EB2843FCD";
    // 246 is the decoded export caller's cap, not a claim about allocated raw storage.
    private const int MaximumSamples = 246;
    private const ulong LeadVtableRva = 0x6C31680;
    private const ulong SourceProviderOffset = 0x7740;
    private const ulong SourceCarOffset = 0x740C;
    private const double RadiansToRpm = 30 / Math.PI;
    private readonly Func<long> _timestamp;
    private readonly long _cacheTicks;
    private IReadOnlyProcessMemory? _memory;
    private ulong _module, _source, _provider;
    private long _attemptTimestamp;
    private NativeShiftPerformanceSnapshot? _cached;
    private readonly Action<string, NativeShiftRejectedEvidence>? _testRejectedCapture;
    private RejectedCapture? _rejectedCapture;

    internal NativeShiftPerformanceReader(Func<long>? timestamp = null, long? frequency = null,
        Action<string, NativeShiftRejectedEvidence>? rejectedCapture = null)
    {
        var ticksPerSecond = frequency ?? Stopwatch.Frequency;
        if (ticksPerSecond < 2) throw new ArgumentOutOfRangeException(nameof(frequency));
        _timestamp = timestamp ?? Stopwatch.GetTimestamp;
        _cacheTicks = ticksPerSecond / 2;
        _testRejectedCapture = rejectedCapture;
    }

    internal void Reset()
    {
        _cached = null;
        _memory = null;
        _module = _source = _provider = 0;
        _attemptTimestamp = 0;
        _rejectedCapture = null;
    }

    internal NativeShiftPerformanceSnapshot Resolve(IReadOnlyProcessMemory memory,
        ulong moduleBase, ulong source, ulong provider, int carOrdinal, NativeHudCompatibilityPack pack)
    {
        if (!Supported(pack)) return Invalidate(NativeShiftPerformanceStatus.UnsupportedBuild, carOrdinal);
        if (!IdentityMatches(memory, moduleBase, source, provider, carOrdinal, pack))
            return Invalidate(NativeShiftPerformanceStatus.IdentityMismatch, carOrdinal);
        var now = _timestamp();
        if (_cached is { } cached && ReferenceEquals(memory, _memory) &&
            moduleBase == _module && source == _source && provider == _provider &&
            carOrdinal == cached.CarOrdinal && now >= cached.ObservedTimestamp &&
            now - cached.ObservedTimestamp < _cacheTicks)
            return cached;

        Reset();
        _memory = memory;
        _module = moduleBase;
        _source = source;
        _provider = provider;
        _attemptTimestamp = now;
        _rejectedCapture = BeginRejectedCapture(pack, carOrdinal, now);
        // All ranges are static engine/tune metadata. Never include current RPM,
        // torque, boost or other changing simulation values in a consistency check.
        var transmission = new byte[8];
        var engine = new byte[40];
        var modifiers = new byte[124];
        var finalDrive = new byte[4];
        var ceiling = new byte[4];
        var peakTorque = new byte[4];
        var peakPower = new byte[4];
        if (!ReadInitialRange(memory, provider, 0x20, transmission) ||
            !ReadInitialRange(memory, provider, 0x234, engine) ||
            !ReadInitialRange(memory, provider, 0xA90, modifiers) ||
            !ReadInitialRange(memory, provider, 0xB68, finalDrive) ||
            !ReadInitialRange(memory, provider, 0x648, ceiling) ||
            !ReadInitialRange(memory, provider, 0x654, peakTorque) ||
            !ReadInitialRange(memory, provider, 0x664, peakPower))
            return Fail(NativeShiftPerformanceStatus.ReadFailure, carOrdinal);
        if (U32(engine, 0) != 5)
            return Fail(NativeShiftPerformanceStatus.UnsupportedPowertrain, carOrdinal);
        if (transmission[0] != 0 || transmission[1] != 0)
            return Fail(NativeShiftPerformanceStatus.UnsupportedTransmission, carOrdinal);
        if (U32(modifiers, 0) > 1 || U32(modifiers, 0x18) > 1 || U32(modifiers, 0x50) > 1)
            return Fail(NativeShiftPerformanceStatus.UnsupportedModifiers, carOrdinal);
        var count = U32(engine, 36);
        var gearCount = U32(transmission, 4);
        if (count < 2 || count > MaximumSamples || gearCount < 3 || gearCount > 16)
            return Fail(NativeShiftPerformanceStatus.InvalidData, carOrdinal);
        var curve = new byte[count * 4];
        var gears = new byte[gearCount * 20];
        if (!ReadInitialRange(memory, provider, 0x25C, curve) || !ReadInitialRange(memory, provider, 0x28, gears))
            return Fail(NativeShiftPerformanceStatus.ReadFailure, carOrdinal);
        var inertiaStatus = NativeShiftInertiaConfiguration.Read(memory, provider, out var inertia);
        if (inertiaStatus != NativeShiftPerformanceStatus.Ready)
        {
            if (_rejectedCapture is { } missingInertia) missingInertia.ReadState = "invalid-or-incomplete-inertia-read";
            return Fail(inertiaStatus, carOrdinal);
        }
        if (_rejectedCapture is { } inertiaCapture) inertiaCapture.Inertia = inertia!.Evidence;
        (ulong Offset, byte[] Bytes)[] ranges =
        [
            (0x20, transmission), (0x234, engine), (0x25C, curve),
            (0x28, gears), (0xA90, modifiers), (0xB68, finalDrive),
            (0x648, ceiling), (0x654, peakTorque), (0x664, peakPower)
        ];
        foreach (var range in ranges)
        {
            var repeated = new byte[range.Bytes.Length];
            if (!memory.TryReadBytes(provider + range.Offset, repeated))
            {
                if (_rejectedCapture is { } incomplete)
                {
                    incomplete.ReadState = "incomplete-repeat-read";
                    incomplete.FailedRelativeOffset = range.Offset;
                }
                return Fail(NativeShiftPerformanceStatus.ReadFailure, carOrdinal);
            }
            if (!range.Bytes.AsSpan().SequenceEqual(repeated))
            {
                if (_rejectedCapture is { } unstable)
                {
                    unstable.ReadState = "unstable-double-read";
                    unstable.DoubleReadConsistent = false;
                    unstable.FailedRelativeOffset = range.Offset;
                    unstable.RepeatedBytesHex = Convert.ToHexString(repeated);
                }
                return Fail(NativeShiftPerformanceStatus.UnstableData, carOrdinal);
            }
        }
        inertiaStatus = inertia!.VerifyUnchanged(memory, provider);
        if (inertiaStatus != NativeShiftPerformanceStatus.Ready)
        {
            if (_rejectedCapture is { } changedInertia)
            {
                changedInertia.ReadState = "incomplete-or-unstable-inertia-repeat";
                changedInertia.DoubleReadConsistent = inertiaStatus == NativeShiftPerformanceStatus.UnstableData ? false : null;
            }
            return Fail(inertiaStatus, carOrdinal);
        }
        if (_rejectedCapture is { } consistent)
        {
            consistent.ReadState = "consistent-double-read";
            consistent.DoubleReadConsistent = true;
        }
        if (!IdentityMatches(memory, moduleBase, source, provider, carOrdinal, pack))
        {
            if (_rejectedCapture is { } mismatch) mismatch.FinalIdentityMatched = false;
            return Invalidate(NativeShiftPerformanceStatus.IdentityMismatch, carOrdinal);
        }
        if (_rejectedCapture is { } matching) matching.FinalIdentityMatched = true;
        var finished = _timestamp();
        if (finished < now || finished - now >= _cacheTicks)
            return Fail(NativeShiftPerformanceStatus.UnstableData, carOrdinal);

        var step = Single(engine, 32);
        var inverse = Single(engine, 28);
        var stepRpm = step * RadiansToRpm;
        var redline = Single(engine, 20) * RadiansToRpm;
        var fd = Single(finalDrive, 0);
        var ceilingRpm = Single(ceiling, 0) * RadiansToRpm;
        if (!Between(stepRpm, 1, 5_000) || !Between(redline, 500, 30_000) ||
            !Between(inverse, 0.00001, 100) || Math.Abs(step * inverse - 1) > 0.001 ||
            !Between(fd, 0.01, 20) || redline > (count - 1) * stepRpm + 0.1 ||
            (count - 1) * stepRpm > 100_000 || !Between(ceilingRpm, redline, 30_000) ||
            ceilingRpm > Single(engine, 24) * RadiansToRpm ||
            ceilingRpm > (count - 1) * stepRpm)
            return Fail(NativeShiftPerformanceStatus.InvalidData, carOrdinal);
        var raw = new float[count];
        for (var i = 0; i < raw.Length; i++)
        {
            raw[i] = Single(curve, i * 4);
            // Stored curves include negative overrun/terminal samples. Preserve
            // those values; the solver must bound its positive-output domain.
            if (!Between(raw[i], -1000, 1000))
                return Fail(NativeShiftPerformanceStatus.InvalidData, carOrdinal);
        }
        var hasReconstructedCurve = NativeCombustionCurve.TryCreate(raw, step, inverse,
            Single(engine, 20), modifiers, out var reconstructed);
        if (!ConfigurationOnly && !hasReconstructedCurve)
            return Fail(NativeShiftPerformanceStatus.UnsupportedModifiers, carOrdinal);
        if (!ConfigurationOnly && !reconstructed!.MatchesConfiguredPeaks(Single(peakTorque, 0), Single(peakPower, 0)))
            return Fail(NativeShiftPerformanceStatus.CurveMismatch, carOrdinal);
        // The configured exporter remains an independent identity/peak check.
        // Only use a runtime equilibrium law for modifier branches whose full
        // evaluation order has been recovered; other branches retain the
        // explicitly labelled configured-export research model.
        NativeCombustionRuntimeCurve.TryCreate(raw, step, inverse, Single(engine, 20),
            Single(ceiling, 0), modifiers, out var runtimeCurve);
        if (!ConfigurationOnly && U32(modifiers, 0) == 0 && U32(modifiers, 0x18) == 0 && runtimeCurve is null)
            return Fail(NativeShiftPerformanceStatus.UnsupportedModifiers, carOrdinal);
        if (!ConfigurationOnly && runtimeCurve is not null)
            for (var i = 0; i < raw.Length; i++)
                if (!runtimeCurve.TryEvaluate(i * step, out _))
                    return Fail(NativeShiftPerformanceStatus.UnsupportedModifiers, carOrdinal);
        var torque = ConfigurationOnly ? raw.Select(value => value * 100d).ToArray()
            : reconstructed!.Samples.Select(value => value * 100d).ToArray();
        var ratios = new double[gearCount - 1];
        var baseline = new double[ratios.Length];
        var adjusted = new double[ratios.Length];
        for (var i = 0; i < ratios.Length; i++)
        {
            ratios[i] = Single(gears, (i + 1) * 20);
            if (!Between(ratios[i], 0.01, 20) || i > 0 && ratios[i] >= ratios[i - 1])
                return Fail(NativeShiftPerformanceStatus.InvalidData, carOrdinal);
            baseline[i] = Single(gears, (i + 1) * 20 + 12) * RadiansToRpm;
            adjusted[i] = Single(gears, (i + 1) * 20 + 16) * RadiansToRpm;
            if (!Between(baseline[i], 500, 30_000) || !Between(adjusted[i], 500, 30_000))
                return Fail(NativeShiftPerformanceStatus.InvalidData, carOrdinal);
        }
        if (!inertia.TryCalculate(ratios, fd, out var accelerationFactors))
            return Fail(NativeShiftPerformanceStatus.InvalidData, carOrdinal);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> identity = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(identity, carOrdinal);
        hash.AppendData(identity);
        foreach (var range in ranges) hash.AppendData(range.Bytes);
        inertia.AppendFingerprint(hash);
        var snapshot = new NativeShiftPerformanceSnapshot(NativeShiftPerformanceStatus.Ready,
            carOrdinal, now, Convert.ToHexString(hash.GetHashAndReset()), stepRpm,
            redline, torque, ratios, fd, ceilingRpm, baseline, adjusted,
            Single(peakTorque, 0) * 100d, Single(peakPower, 0) * 100d,
            ConfigurationOnly ? 0 : reconstructed!.ExportCount,
            U32(modifiers, 0) != 0 || U32(modifiers, 0x18) != 0 || U32(modifiers, 0x50) != 0,
            accelerationFactors, ConfigurationOnly ? null : runtimeCurve);
        ShiftCaptureHub.Current?.RecordProfile("raw:" + snapshot.Fingerprint, new
        {
            kind = "native-configured-source",
            carOrdinal,
            snapshot.Fingerprint,
            observedTimestamp = now,
            pack.GameVersion,
            pack.ExecutableSha256,
            inertia = inertia.Evidence,
            snapshot.GearAccelerationFactors,
            ranges = ranges.Select(range => new { offset = range.Offset, bytesHex = Convert.ToHexString(range.Bytes) }).ToArray()
        });
        NativeShiftCaptureEvidence.RecordIfArmed(memory, provider, snapshot);
        _rejectedCapture = null;
        return _cached = snapshot;
    }

    private NativeShiftPerformanceSnapshot Fail(NativeShiftPerformanceStatus status, int carOrdinal)
    {
        RecordRejected(status);
        // Back off unsupported/failed metadata on this still-validated identity.
        // The negative entry contains no old curve, and identity is checked before
        // every cache hit, so an attachment/car change never inherits the backoff.
        return _cached = new(status, carOrdinal, _attemptTimestamp);
    }

    private NativeShiftPerformanceSnapshot Invalidate(NativeShiftPerformanceStatus status, int carOrdinal)
    {
        RecordRejected(status);
        Reset();
        return new(status, carOrdinal);
    }

    private bool ReadInitialRange(IReadOnlyProcessMemory memory, ulong provider, ulong offset, byte[] destination)
    {
        var success = memory.TryReadBytes(provider + offset, destination);
        if (_rejectedCapture is { } capture)
        {
            if (success) capture.Ranges.Add((offset, destination));
            else
            {
                capture.ReadState = "incomplete-initial-read";
                capture.FailedRelativeOffset = offset;
            }
        }
        return success;
    }

    private RejectedCapture? BeginRejectedCapture(NativeHudCompatibilityPack pack, int car, long started)
    {
        if (_testRejectedCapture is not null) return new(pack, car, started, _testRejectedCapture);
        var session = ShiftCaptureHub.Current;
        if (session is null || !session.Active) return null;
        return new(pack, car, started, (key, evidence) =>
        {
            if (ReferenceEquals(session, ShiftCaptureHub.Current) && session.Active)
                session.RecordProfile(key, evidence);
        });
    }

    private void RecordRejected(NativeShiftPerformanceStatus status)
    {
        var capture = _rejectedCapture;
        _rejectedCapture = null;
        if (capture is null) return;
        var ranges = capture.Ranges.Select(range => new NativeShiftRejectedRange(range.Offset,
            Convert.ToHexString(range.Bytes))).ToArray();
        // Timestamp-free keys deduplicate identical failures for the armed session.
        // The existing failed-read cache still limits configuration attempts to 2 Hz.
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes($"{capture.Car}|{status}|{capture.Pack.GameVersion}|{capture.Pack.ExecutableSha256}|{capture.ReadState}|{capture.FinalIdentityMatched}|{capture.FailedRelativeOffset}|{capture.RepeatedBytesHex}"));
        if (capture.Inertia is { } inertia) hash.AppendData(Convert.FromHexString(inertia.ScalarBytesHex));
        Span<byte> header = stackalloc byte[12];
        foreach (var range in capture.Ranges)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(header, range.Offset);
            BinaryPrimitives.WriteInt32LittleEndian(header[8..], range.Bytes.Length);
            hash.AppendData(header);
            hash.AppendData(range.Bytes);
        }
        capture.Sink("rejected:" + Convert.ToHexString(hash.GetHashAndReset()), new(capture.Car,
            status.ToString(), capture.Pack.GameVersion, capture.Pack.ExecutableSha256, capture.Started,
            _timestamp(), capture.ReadState, capture.DoubleReadConsistent, capture.FinalIdentityMatched,
            capture.FailedRelativeOffset, capture.RepeatedBytesHex, ranges, capture.Inertia));
    }

    private sealed class RejectedCapture(NativeHudCompatibilityPack pack, int car, long started,
        Action<string, NativeShiftRejectedEvidence> sink)
    {
        internal NativeHudCompatibilityPack Pack { get; } = pack;
        internal int Car { get; } = car;
        internal long Started { get; } = started;
        internal Action<string, NativeShiftRejectedEvidence> Sink { get; } = sink;
        internal List<(ulong Offset, byte[] Bytes)> Ranges { get; } = [];
        internal string ReadState { get; set; } = "not-double-read";
        internal bool? DoubleReadConsistent { get; set; }
        internal bool? FinalIdentityMatched { get; set; }
        internal ulong? FailedRelativeOffset { get; set; }
        internal string? RepeatedBytesHex { get; set; }
        internal NativeShiftInertiaEvidence? Inertia { get; set; }
    }

    internal static bool Supported(NativeHudCompatibilityPack pack) =>
        pack.GameVersion == "6.440.853.0" && pack.ExecutableLength == 184_055_768 &&
        pack.ExecutableSha256.Equals(SupportedSha256, StringComparison.OrdinalIgnoreCase) &&
        pack.ImageSize == 188_497_920 && pack.LeadVtableRva == LeadVtableRva &&
        pack.Fields.SourceProvider == SourceProviderOffset && pack.Fields.SourceCarOrdinal == SourceCarOffset &&
        pack.RequiredVtableSlots.Count == NativeHudBuildContract.BuiltIn.RequiredVtableSlots.Count &&
        pack.RequiredVtableSlots.All(slot => NativeHudBuildContract.BuiltIn.RequiredVtableSlots.TryGetValue(slot.Key, out var target) && target == slot.Value);

    internal static bool IdentityMatches(IReadOnlyProcessMemory memory, ulong module,
        ulong source, ulong provider, int car, NativeHudCompatibilityPack pack)
    {
        if (car <= 0 || !Address(module) || !Address(source) || !Address(provider) ||
            !memory.TryReadUInt64(source + SourceProviderOffset, out var observedProvider) || observedProvider != provider ||
            !memory.TryReadUInt32(source + SourceCarOffset, out var observedCar) || observedCar != (uint)car ||
            !memory.TryReadUInt64(provider, out var table) || table != module + LeadVtableRva)
            return false;
        return pack.RequiredVtableSlots.All(slot =>
            memory.TryReadUInt64(table + slot.Key, out var target) && target == module + slot.Value);
    }

    private static bool Address(ulong value) => value >= 0x10000 && value < 0x7FFF_FFFE_0000;
    private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
    private static float Single(byte[] bytes, int offset) => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, 4)));
    private static bool Between(double value, double minimum, double maximum) => double.IsFinite(value) && value >= minimum && value <= maximum;
}

internal sealed record NativeShiftRejectedRange(ulong RelativeOffset, string BytesHex);
internal sealed record NativeShiftRejectedEvidence(int CarOrdinal, string RejectionStatus, string GameVersion,
    string ExecutableSha256, long ReadStartedQpc, long ReadFinishedQpc, string ReadState,
    bool? DoubleReadConsistent, bool? FinalIdentityMatched, ulong? FailedRelativeOffset,
    string? RepeatedBytesHex, NativeShiftRejectedRange[] Ranges, NativeShiftInertiaEvidence? Inertia = null)
{
    public string Kind => "native-rejected-source";
    public bool ValidatedConfiguration => false;
    public string Meaning => "Only already-read bounded configuration fields; partial or rejected evidence is never an accepted model.";
}
