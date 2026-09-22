using System.Buffers.Binary;

namespace Wisp.App;

// Translation of the reviewed Steam 6.440.853.0 combustion exporter (31BF2A0).
// Keep float intermediates and its previous-generated-point power recurrence.
// Samples after ExportCount extend that formula over stored raw knots; they are
// not points returned by the game's redline-limited export call.
internal sealed record NativeCombustionCurve(float[] Samples, int ExportCount,
    float ExportPeakTorque, float ExportPeakPower, bool HasModifiers)
{
    internal static bool TryCreate(float[] raw, float step, float inverseStep, float redline,
        ReadOnlySpan<byte> modifiers, out NativeCombustionCurve? result)
    {
        result = null;
        if (raw.Length is < 2 or > 246 || modifiers.Length != 124 ||
            !Finite(step, .0001f, 1000) || !Finite(inverseStep, .00001f, 100) ||
            !Finite(redline, 1, 10_000)) return false;
        var m = new float[31];
        for (var i = 0; i < m.Length; i++)
            m[i] = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(modifiers.Slice(i * 4, 4)));
        var window = BinaryPrimitives.ReadUInt32LittleEndian(modifiers);
        var power = BinaryPrimitives.ReadUInt32LittleEndian(modifiers.Slice(0x18, 4));
        var rpm = BinaryPrimitives.ReadUInt32LittleEndian(modifiers.Slice(0x50, 4));
        if (window > 1 || power > 1 || rpm > 1) return false;
        // Validate only coefficients actually consumed by enabled branches.
        if (window != 0 && (!Finite(m[1], 0, 10_000) || !Finite(m[2], m[1], 10_000) ||
            m[2] <= m[1] || !Finite(m[3], 0, 100))) return false;
        if (power != 0 && (!Finite(m[10], 0, 100) || !Finite(m[11], 0, 100) ||
            !Finite(m[12], 0, 100_000_000) || !Finite(m[13], m[12], 100_000_000) ||
            m[13] <= m[12] || !DropoffValid(m[16], m[17], m[18], m[19]))) return false;
        if (rpm != 0 && (!Finite(m[22], 0, 100) || !Finite(m[23], 0, 100) ||
            !DropoffValid(m[27], m[28], m[29], m[30]))) return false;

        var count = Math.Min(raw.Length, Math.Min(246, (int)(inverseStep * redline + .5f) + 1));
        if (count < 2) return false;
        var values = new float[raw.Length];
        var previousPower = 0f;
        var peakTorque = float.NegativeInfinity;
        var peakPower = float.NegativeInfinity;
        for (var i = 0; i < raw.Length; i++)
        {
            if (!Finite(raw[i], -1000, 1000)) return false;
            var omega = i * step;
            var powerScale = 1f;
            var rpmScale = 1f;
            var windowScale = 1f;
            if (power != 0)
            {
                powerScale = m[10];
                if (previousPower > m[12])
                    powerScale = previousPower >= m[13] ? m[11] :
                        m[10] + ((previousPower - m[12]) / (m[13] - m[12])) * (m[11] - m[10]);
                if (powerScale > 1)
                    powerScale = Dropoff(powerScale, omega, m[16], m[17], m[18], m[19]);
            }
            if (rpm != 0)
            {
                rpmScale = m[22];
                if (omega > 0)
                    rpmScale = omega >= redline ? m[23] : m[22] + (omega / redline) * (m[23] - m[22]);
                if (rpmScale > 1)
                    rpmScale = Dropoff(rpmScale, omega, m[27], m[28], m[29], m[30]);
            }
            if (window != 0)
            {
                var distance = Math.Min(omega - m[1], m[2] - omega);
                windowScale = distance <= 0 ? 1 : distance >= 41.88787841796875f ? m[3] :
                    1 + (distance * .023873254656791687f) * (m[3] - 1);
            }
            var factor = ((windowScale + powerScale) + (rpmScale - 1)) - 1;
            var value = factor * raw[i];
            previousPower = value * omega;
            if (!Finite(value, -1000, 1000) || !float.IsFinite(previousPower)) return false;
            values[i] = value;
            if (i < count)
            {
                peakTorque = Math.Max(peakTorque, value);
                peakPower = Math.Max(peakPower, previousPower);
            }
        }
        if (!(peakTorque > 0) || !(peakPower > 0)) return false;
        result = new(values, count, peakTorque, peakPower, window != 0 || power != 0 || rpm != 0);
        return true;
    }

    // Configured peak fields provide an independent consistency check. A tiny
    // relative/ULP allowance covers rounding, not a fitted torque scale.
    internal bool MatchesConfiguredPeaks(float torque, float power) =>
        Close(ExportPeakTorque, torque) && Close(ExportPeakPower, power);

    private static bool Close(float expected, float actual) => actual > 0 && float.IsFinite(actual) &&
        Math.Abs(expected - actual) <= Math.Max(1e-5f, Math.Abs(actual) * 2e-6f);

    private static bool DropoffValid(float start, float end, float low, float high) =>
        Finite(start, 0, 10_000) && Finite(end, start, 10_000) && end > start &&
        Finite(low, 0, 100) && Finite(high, 0, 100);

    private static float Dropoff(float scale, float omega, float start, float end, float low, float high)
    {
        var excess = scale - 1;
        var a = 1 + excess * low;
        var b = 1 + excess * high;
        return omega <= start ? a : omega >= end ? b : a + ((omega - start) / (end - start)) * (b - a);
    }

    private static bool Finite(float value, float minimum, float maximum) =>
        float.IsFinite(value) && value >= minimum && value <= maximum;
}
