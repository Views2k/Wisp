using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Wisp.App;

// Same-state correction recovered from the ordinary fixed-ratio estimator. This
// does not model contact limits, intervention, clutch transfer or shift recovery.
internal sealed class NativeShiftInertiaConfiguration
{
    private static readonly int[] Offsets = [-0x568, 0x2BA0, 0x4120, 0x2ADC, 0x405C, 0x12C, 0x170, 0x23C];
    private readonly byte[] _bytes = new byte[36];
    private ulong _selectedWheel;

    internal static NativeShiftPerformanceStatus Read(IReadOnlyProcessMemory memory, ulong provider,
        out NativeShiftInertiaConfiguration? configuration)
    {
        var candidate = new NativeShiftInertiaConfiguration();
        configuration = null;
        if (!candidate.ReadValues(memory, provider)) return NativeShiftPerformanceStatus.ReadFailure;
        for (var i = 0; i < 9; i++)
        {
            var value = candidate.Value(i);
            var positiveRequired = i is 0 or 3 or 4 or 8;
            if (!double.IsFinite(value) || value < 0 || positiveRequired && value == 0)
                return NativeShiftPerformanceStatus.InvalidData;
        }
        configuration = candidate;
        return NativeShiftPerformanceStatus.Ready;
    }

    internal NativeShiftPerformanceStatus VerifyUnchanged(IReadOnlyProcessMemory memory, ulong provider)
    {
        var repeated = new NativeShiftInertiaConfiguration();
        if (!repeated.ReadValues(memory, provider)) return NativeShiftPerformanceStatus.ReadFailure;
        return _selectedWheel == repeated._selectedWheel && _bytes.AsSpan().SequenceEqual(repeated._bytes)
            ? NativeShiftPerformanceStatus.Ready : NativeShiftPerformanceStatus.UnstableData;
    }

    internal bool TryCalculate(IReadOnlyList<double> ratios, double finalDrive, out double[] factors)
    {
        factors = [];
        if (!double.IsFinite(finalDrive) || finalDrive <= 0 || ratios.Count == 0) return false;
        var mass = Value(0);
        var axleRatio = finalDrive / Value(8);
        var common = mass + 2 * Value(1) / Math.Pow(Value(3), 2) +
            2 * Value(2) / Math.Pow(Value(4), 2) + (Value(5) + Value(6)) * axleRatio * axleRatio;
        var calculated = new double[ratios.Count];
        for (var i = 0; i < calculated.Length; i++)
        {
            if (!double.IsFinite(ratios[i]) || ratios[i] <= 0) return false;
            var engineRatio = ratios[i] * axleRatio;
            calculated[i] = mass / (common + Value(7) * engineRatio * engineRatio);
            if (!double.IsFinite(calculated[i]) || calculated[i] <= 0 || calculated[i] > 1) return false;
        }
        factors = calculated;
        return true;
    }

    internal void AppendFingerprint(IncrementalHash hash) => hash.AppendData(_bytes);

    internal NativeShiftInertiaEvidence Evidence => new(Value(0), Value(1), Value(2), Value(3), Value(4),
        Value(5), Value(6), Value(7), Value(8), Convert.ToHexString(_bytes));

    private double Value(int index) => BitConverter.Int32BitsToSingle(
        BinaryPrimitives.ReadInt32LittleEndian(_bytes.AsSpan(index * 4, 4)));

    private bool ReadValues(IReadOnlyProcessMemory memory, ulong provider)
    {
        if (!memory.TryReadUInt64(provider + 0xBA0, out _selectedWheel) ||
            _selectedWheel < 0x10000 || _selectedWheel >= 0x7FFF_FFFE_0000)
            return false;
        for (var i = 0; i < Offsets.Length; i++)
        {
            var address = (ulong)((long)provider + Offsets[i]);
            if (!memory.TryReadBytes(address, _bytes.AsSpan(i * 4, 4))) return false;
        }
        return memory.TryReadBytes(_selectedWheel + 0x5AC, _bytes.AsSpan(32, 4)) &&
            memory.TryReadUInt64(provider + 0xBA0, out var selectedAfter) && selectedAfter == _selectedWheel;
    }
}

internal sealed record NativeShiftInertiaEvidence(double MassScalar, double FrontRotatingInertia,
    double RearRotatingInertia, double FrontNominalRadius, double RearNominalRadius,
    double TransmissionInertia, double DrivelineInertia, double EngineAndFlywheelInertia,
    double SelectedDrivenRadius, string ScalarBytesHex)
{
    public string Model => "configured-same-state-inertia-correction";
    public bool ProvesLiveAcceleration => false;
}
