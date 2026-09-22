using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;

namespace Wisp.App;

internal static class NativeShiftCaptureEvidence
{
    // Called only after the configured-curve reader's successful build/identity
    // checks. Auxiliary read errors are journal evidence, never cue invalidation.
    internal static void RecordIfArmed(IReadOnlyProcessMemory memory, ulong provider, NativeShiftPerformanceSnapshot snapshot)
    {
        var session = ShiftCaptureHub.Current;
        if (session is null || !session.Active) return;
        var evidence = ReadForCapture(memory, provider, snapshot);
        if (!ReferenceEquals(session, ShiftCaptureHub.Current) || !session.Active) return;
        if (evidence.Status == "stable")
            session.RecordProfile("aux:" + snapshot.Fingerprint + ":" + evidence.AuxiliaryFingerprint, evidence);
        else
            session.Record("native_auxiliary_failure", evidence);
    }

    internal static NativeShiftAuxiliaryEvidence ReadForCapture(
        IReadOnlyProcessMemory memory, ulong provider, NativeShiftPerformanceSnapshot snapshot)
    {
        var started = Stopwatch.GetTimestamp();
        if (!snapshot.Available || snapshot.CarOrdinal <= 0 || string.IsNullOrEmpty(snapshot.Fingerprint))
            return Failure("unavailable-snapshot", "configuration", 0);
        if (!Pointer(provider) || provider < 0x568 || provider > 0x00007FFFFFFF0000 - 0x4124)
            return Failure("invalid-provider", "provider", 0);
        // Exactly eight audited scalar fields and the selected-wheel ID/radius.
        // The selected wheel address is used only for reads/consistency, never output.
        Span<byte> first = stackalloc byte[40];
        Span<byte> second = stackalloc byte[40];
        ulong selectedWheel = 0;
        for (var pass = 1; pass <= 2; pass++)
        {
            var bytes = pass == 1 ? first : second;
            for (var index = 0; index < Fields.Items.Length; index++)
            {
                var field = Fields.Items[index];
                var address = field.Offset < 0 ? provider - (ulong)-field.Offset : provider + (ulong)field.Offset;
                if (!memory.TryReadBytes(address, bytes.Slice(index * 4, 4)))
                    return Failure("read-failed", field.Path, pass);
            }
            if (!memory.TryReadUInt64(provider + 0xBA0, out var wheel))
                return Failure("read-failed", "provider+0xBA0 (selected wheel reference)", pass);
            if (!Pointer(wheel) || wheel > 0x00007FFFFFFF0000 - 0x5B0)
                return Failure("invalid-wheel-reference", "provider+0xBA0 (selected wheel reference)", pass);
            if (pass == 1) selectedWheel = wheel;
            else if (wheel != selectedWheel) return Failure("unstable-wheel-reference", "provider+0xBA0 (selected wheel reference)", pass);
            if (!memory.TryReadBytes(wheel + 0x5A0, bytes.Slice(32, 4)))
                return Failure("read-failed", "selected-wheel+0x5A0", pass);
            if (!memory.TryReadBytes(wheel + 0x5AC, bytes.Slice(36, 4)))
                return Failure("read-failed", "selected-wheel+0x5AC", pass);
        }
        // Recheck the reference after the second radius read as in the established
        // known-wheel-radius operation, without retaining its pointer bytes.
        if (!memory.TryReadUInt64(provider + 0xBA0, out var finalWheel))
            return Failure("read-failed", "provider+0xBA0 (selected wheel reference)", 3);
        if (finalWheel != selectedWheel)
            return Failure("unstable-wheel-reference", "provider+0xBA0 (selected wheel reference)", 3);
        if (!memory.TryReadUInt32(selectedWheel + 0x5A0, out var finalWheelId))
            return Failure("read-failed", "selected-wheel+0x5A0", 3);
        if (finalWheelId != BinaryPrimitives.ReadUInt32LittleEndian(second.Slice(32, 4)))
            return Failure("unstable-wheel-id", "selected-wheel+0x5A0", 3);
        var rows = new NativeShiftAuxiliaryField[10];
        var finite = true;
        for (var index = 0; index < 8; index++)
        {
            var field = Fields.Items[index];
            rows[index] = Scalar(first, second, field.Path, field.Label, "inferred from native estimator 0x3197DD0; physical identity and units unverified", index * 4);
            finite &= rows[index].FirstFiniteValue.HasValue && rows[index].RepeatedFiniteValue.HasValue;
        }
        var firstId = BinaryPrimitives.ReadUInt32LittleEndian(first.Slice(32, 4));
        var secondId = BinaryPrimitives.ReadUInt32LittleEndian(second.Slice(32, 4));
        rows[8] = new("selected-wheel+0x5A0", "selected wheel ID", "known-wheel-radius operation: integer 0 through 3",
            "uint32", Convert.ToHexString(first.Slice(32, 4)), Convert.ToHexString(second.Slice(32, 4)), firstId, secondId);
        rows[9] = Scalar(first, second, "selected-wheel+0x5AC", "selected wheel radius", "known-wheel-radius operation; expected value greater than 0.1 and less than 2.0", 36);
        var wheelValid = firstId <= 3 && secondId <= 3 && rows[9].FirstFiniteValue is > .1 and < 2 &&
            rows[9].RepeatedFiniteValue is > .1 and < 2;
        var consistent = first.SequenceEqual(second);
        var status = !consistent ? "unstable-data" : !finite ? "nonfinite-scalar" : !wheelValid ? "invalid-wheel-data" : "stable";
        // Hash the documented fixed field order, including the relative offsets.
        // Car/tune association is carried separately by the caller's fingerprint.
        Span<byte> hashInput = stackalloc byte[80];
        for (var index = 0; index < 8; index++) BinaryPrimitives.WriteInt32LittleEndian(hashInput.Slice(index * 4, 4), Fields.Items[index].Offset);
        BinaryPrimitives.WriteInt32LittleEndian(hashInput.Slice(32, 4), 0x5A0);
        BinaryPrimitives.WriteInt32LittleEndian(hashInput.Slice(36, 4), 0x5AC);
        first.CopyTo(hashInput[40..]);
        var fingerprint = status == "stable" ? Convert.ToHexString(SHA256.HashData(hashInput)) : null;
        var finished = Stopwatch.GetTimestamp();
        return new(snapshot.CarOrdinal, snapshot.Fingerprint, snapshot.ObservedTimestamp, started, finished,
            status, null, null, consistent, fingerprint, rows,
            status == "stable" ? NativeShiftEstimatorEvidence.Derive(snapshot, rows) : null);

        NativeShiftAuxiliaryEvidence Failure(string status, string field, int pass) => new(snapshot.CarOrdinal,
            snapshot.Fingerprint, snapshot.ObservedTimestamp, started, Stopwatch.GetTimestamp(), status, field, pass, false, null, []);

    }

    private static NativeShiftAuxiliaryField Scalar(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second,
        string path, string label, string semantics, int offset)
    {
        var firstValue = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(first.Slice(offset, 4)));
        var repeatedValue = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(second.Slice(offset, 4)));
        return new(path, label, semantics, "float32", Convert.ToHexString(first.Slice(offset, 4)), Convert.ToHexString(second.Slice(offset, 4)),
            float.IsFinite(firstValue) ? firstValue : null, float.IsFinite(repeatedValue) ? repeatedValue : null);
    }

    private static bool Pointer(ulong value) => value >= 0x10000 && value <= 0x00007FFFFFFF0000 && (value & 7) == 0;

    // The nested type is initialized only after an armed capture reaches this
    // helper; the normal null-hub path allocates and reads nothing.
    private static class Fields
    {
        internal static readonly (int Offset, string Path, string Label)[] Items =
        [
            (-0x568, "provider-0x568", "mass-like estimator term"),
            (0x12C, "provider+0x12C", "transmission inertia-like estimator term"),
            (0x170, "provider+0x170", "driveline inertia-like estimator term"),
            (0x23C, "provider+0x23C", "engine and flywheel inertia-like estimator term"),
            (0x2BA0, "provider+0x2BA0", "first axle inertia-like estimator term"),
            (0x4120, "provider+0x4120", "second axle inertia-like estimator term"),
            (0x2ADC, "provider+0x2ADC", "first axle radius-like estimator term"),
            (0x405C, "provider+0x405C", "second axle radius-like estimator term")
        ];
    }
}

internal sealed record NativeShiftAuxiliaryField(string RelativePath, string InferredLabel, string Semantics,
    string Representation, string RawLittleEndianHex, string RepeatedRawLittleEndianHex,
    double? FirstFiniteValue, double? RepeatedFiniteValue);

internal sealed record NativeShiftAuxiliaryEvidence(int CarOrdinal, string ConfigurationFingerprint,
    long ConfigurationObservedQpc, long ReadStartedQpc, long ReadFinishedQpc, string Status,
    string? FailedRelativeField, int? FailedPass, bool DoubleReadConsistent, string? AuxiliaryFingerprint,
    NativeShiftAuxiliaryField[] Fields, NativeShiftEstimatorEvidence? EstimatorHypothesis = null)
{
    public string Kind => "native-auxiliary-inertia-radius";
    public long QpcFrequency => Stopwatch.Frequency;
    public double? ConfigurationAgeAtReadStartMilliseconds => ConfigurationObservedQpc > 0 && ReadStartedQpc >= ConfigurationObservedQpc
        ? (ReadStartedQpc - ConfigurationObservedQpc) * 1000d / Stopwatch.Frequency : null;
    public string Association => "Caller-validated configured-curve fingerprint and auxiliary read bracket; independent double-read consistency does not make configuration and auxiliary reads atomic or prove uninterrupted tune identity.";
    public string MeasuredSignals => "Configured fingerprint, bounded inertia/radius scalars, selected wheel ID/radius, independent repeated bytes and local monotonic read timing.";
    public string UnobservedSignals => "Game input consumption, clutch engagement, active shift-duration configuration, instantaneous wheel force, road load and physical display timing.";
    public string Limitation => "This auxiliary re-read is diagnostic, separate from the guarded inertia inputs included in the configured cue profile. Neither snapshot establishes instantaneous driving physics or optimal command timing.";
}

internal sealed record NativeShiftGearEstimatorFactor(int Gear, double Ratio, double Factor);

/// <summary>Auxiliary re-read for comparison with the separately guarded cue profile.</summary>
internal sealed record NativeShiftEstimatorEvidence(string Status, string ConfigurationFingerprint,
    double? FinalDrive, NativeShiftGearEstimatorFactor[] Gears)
{
    public string FormulaVersion => "fixed-ratio-inertia-factor-v1";
    public string SourceFunctionRva => "0x3197DD0";
    public string SourceInstructionSpanRva => "0x3197FED-0x31980BC";
    public string Formula => "B=2*Jf/rf^2+2*Jr/rr^2+(J170+J12c)*(d/r)^2; factor(g)=M/(M+B+Je*(g*d/r)^2)";
    public string Arithmetic => "Float64 reduction of decoded float32 instructions for positive speed and fixed gearing.";
    public bool AppliedToCueTargets => false;
    public string Limitation => "These independently re-read diagnostic factors are not the cue's authoritative inputs. The same-state estimator reduction does not establish live acceleration or optimal command timing; traction, interventions and shift transients remain separate.";

    internal static NativeShiftEstimatorEvidence Derive(NativeShiftPerformanceSnapshot snapshot, NativeShiftAuxiliaryField[] fields)
    {
        NativeShiftEstimatorEvidence Unavailable(string status) => new(status, snapshot.Fingerprint,
            double.IsFinite(snapshot.FinalDrive) ? snapshot.FinalDrive : null, []);
        if (!snapshot.Available || fields.Length != 10 || snapshot.ForwardRatios.Count is < 1 or > 16 ||
            !Positive(snapshot.FinalDrive) || snapshot.ForwardRatios.Any(ratio => !Positive(ratio)) ||
            snapshot.ForwardRatios.Zip(snapshot.ForwardRatios.Skip(1)).Any(pair => pair.First <= pair.Second))
            return Unavailable("unavailable-inputs");
        // Use named fields, not an assumed caller ordering, and reject incomplete or inconsistent values.
        double Read(string path)
        {
            var matches = fields.Where(field => field.RelativePath == path).ToArray();
            return matches.Length == 1 && matches[0].FirstFiniteValue is { } value &&
                matches[0].RepeatedFiniteValue == value && matches[0].RawLittleEndianHex == matches[0].RepeatedRawLittleEndianHex
                ? value : double.NaN;
        }
        var mass = Read("provider-0x568");
        var transmission = Read("provider+0x12C");
        var driveline = Read("provider+0x170");
        var engine = Read("provider+0x23C");
        var front = Read("provider+0x2BA0");
        var rear = Read("provider+0x4120");
        var frontRadius = Read("provider+0x2ADC");
        var rearRadius = Read("provider+0x405C");
        var selectedRadius = Read("selected-wheel+0x5AC");
        if (!Positive(mass) || !Positive(frontRadius) || !Positive(rearRadius) || !Positive(selectedRadius) ||
            !Nonnegative(driveline) || !Nonnegative(transmission) || !Nonnegative(engine) ||
            !Nonnegative(front) || !Nonnegative(rear)) return Unavailable("invalid-inertia-or-radius");
        var drivePerRadius = snapshot.FinalDrive / selectedRadius;
        var common = 2 * front / (frontRadius * frontRadius) + 2 * rear / (rearRadius * rearRadius) +
            (transmission + driveline) * drivePerRadius * drivePerRadius;
        if (!Nonnegative(common) || !Positive(drivePerRadius)) return Unavailable("nonfinite-derived-factor");
        var gears = new NativeShiftGearEstimatorFactor[snapshot.ForwardRatios.Count];
        for (var i = 0; i < gears.Length; i++)
        {
            var ratio = snapshot.ForwardRatios[i];
            var omegaPerSpeed = ratio * drivePerRadius;
            var denominator = mass + common + engine * omegaPerSpeed * omegaPerSpeed;
            var factor = mass / denominator;
            if (!Positive(denominator) || !Positive(factor) || factor > 1) return Unavailable("nonfinite-derived-factor");
            gears[i] = new(i + 1, ratio, factor);
        }
        return new("derived-estimator-hypothesis", snapshot.Fingerprint, snapshot.FinalDrive, gears);
    }

    private static bool Positive(double value) => double.IsFinite(value) && value > 0;
    private static bool Nonnegative(double value) => double.IsFinite(value) && value >= 0;
}
