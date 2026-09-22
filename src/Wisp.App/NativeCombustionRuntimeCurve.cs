using System.Buffers.Binary;

namespace Wisp.App;

// Real-arithmetic polynomial on an open interval between source branch changes.
// This supports continuous root isolation, not a claim of float32 bit equality.
internal readonly record struct NativeCombustionRuntimeSegment(double MinimumOmega, double MaximumOmega,
    double C0, double C1, double C2, double C3)
{
    internal double Evaluate(double omega) => ((C3 * omega + C2) * omega + C1) * omega + C0;
}

// Constant-speed, full-control equilibrium of the reviewed Steam combustion
// path (31B26F0 / 3196590). This is distinct from the configured graph exporter.
// Output is in raw native torque units, before control, damage and clutch scales;
// it does not reconstruct the transient modifier state following an upshift.
internal sealed class NativeCombustionRuntimeCurve
{
    private readonly float[] _raw, _modifiers;
    private readonly float _inverseStep, _redline, _operatingCeiling;
    private readonly bool _ae0;

    private NativeCombustionRuntimeCurve(float[] raw, float step, float inverseStep,
        float redline, float operatingCeiling, float[] modifiers, bool ae0)
    {
        _raw = (float[])raw.Clone();
        _inverseStep = inverseStep;
        _redline = redline;
        _operatingCeiling = operatingCeiling;
        _modifiers = modifiers;
        _ae0 = ae0;
        SourceStepOmega = step;
        MaximumOmega = (raw.Length - 1) * step;
        Segments = Array.AsReadOnly(CreateSegments());
    }

    internal float MaximumOmega { get; }
    internal float SourceStepOmega { get; }
    internal float OperatingCeiling => _operatingCeiling;
    internal IReadOnlyList<NativeCombustionRuntimeSegment> Segments { get; }

    internal static bool TryCreate(float[] raw, float step, float inverseStep, float redline, float operatingCeiling,
        ReadOnlySpan<byte> modifiers, out NativeCombustionRuntimeCurve? result)
    {
        result = null;
        if (raw.Length is < 2 or > 246 || modifiers.Length != 124 ||
            !Finite(step, .0001f, 1000) || !Finite(inverseStep, .00001f, 100) ||
            Math.Abs(step * inverseStep - 1) > .001f || !Finite(redline, 1, 10_000) ||
            redline > (raw.Length - 1) * step || !Finite(operatingCeiling, redline, (raw.Length - 1) * step) ||
            raw.Any(value => !Finite(value, -1000, 1000))) return false;
        var window = BinaryPrimitives.ReadUInt32LittleEndian(modifiers);
        var power = BinaryPrimitives.ReadUInt32LittleEndian(modifiers.Slice(0x18, 4));
        var ae0 = BinaryPrimitives.ReadUInt32LittleEndian(modifiers.Slice(0x50, 4));
        // Runtime window state and power feedback cannot be substituted with the
        // exporter's trapezoid or previous-generated-knot power recurrence.
        if (window != 0 || power != 0 || ae0 > 1) return false;
        var m = new float[31];
        if (ae0 != 0)
        {
            for (var i = 22; i < m.Length; i++)
                m[i] = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(modifiers.Slice(i * 4, 4)));
            if (!Finite(m[22], 0, 100) || !Finite(m[23], 0, 100) ||
                !float.IsFinite(m[24]) || !float.IsFinite(m[25]) || !float.IsFinite(m[26]) ||
                !float.IsFinite(m[26] - m[24]) || m[26] <= m[24] ||
                !float.IsFinite(m[26] - m[25]) || !float.IsFinite(m[25] - m[24]) ||
                !Finite(m[27], 0, 10_000) || !Finite(m[28], 0, 10_000) || m[28] <= m[27] ||
                !Finite(m[29], 0, 100) || !Finite(m[30], 0, 100)) return false;
        }
        var curve = new NativeCombustionRuntimeCurve(raw, step, inverseStep, redline, operatingCeiling, m, ae0 != 0);
        if (curve.Segments.Any(segment => !double.IsFinite(segment.MinimumOmega) || !double.IsFinite(segment.MaximumOmega) ||
            !double.IsFinite(segment.C0) || !double.IsFinite(segment.C1) || !double.IsFinite(segment.C2) || !double.IsFinite(segment.C3))) return false;
        result = curve;
        return true;
    }

    internal bool TryEvaluate(float omega, out float torque)
    {
        torque = 0;
        if (!float.IsFinite(omega) || omega < 0 || omega > MaximumOmega) return false;
        // 3199AD0: multiply by inverse spacing before subtracting the index.
        var position = omega * _inverseStep;
        var index = Math.Min((int)position, _raw.Length - 2);
        var fraction = position - index;
        var raw = _raw[index] + fraction * (_raw[index + 1] - _raw[index]);
        // The native leaf compares stored current speed against this threshold.
        // At constant-speed equilibrium, stored and queried speed are the same.
        if (omega > _operatingCeiling) raw = Math.Min(raw, 0);
        // 3196590 branches around positive-output modifiers for raw <= 0.
        // Keep stored overrun samples; the caller owns its positive-output domain.
        if (!_ae0 || raw <= 0)
        {
            torque = raw;
            return float.IsFinite(torque);
        }
        var m = _modifiers;
        var rpmTarget = Interpolate(omega, 0, _redline, m[25], m[26]);
        // Preserve the updater's subtract/add order even at full control u=1.
        var equilibrium = m[24] + (rpmTarget - m[24]);
        var scale = Interpolate(equilibrium, m[24], m[26], Math.Min(m[22], 1), m[23]);
        if (scale > 1)
        {
            var excess = scale - 1;
            scale = Interpolate(omega, m[27], m[28], 1 + excess * m[29], 1 + excess * m[30]);
        }
        var factor = ((1f + 1f) + (scale - 1f)) - 1f;
        var value = factor * raw;
        if (!float.IsFinite(equilibrium) || !float.IsFinite(scale) || scale < 0 ||
            !float.IsFinite(value) || value < 0) return false;
        torque = value;
        return true;
    }

    private static float Interpolate(float x, float start, float end, float low, float high) =>
        x <= start ? low : x >= end ? high : low + ((x - start) / (end - start)) * (high - low);

    private NativeCombustionRuntimeSegment[] CreateSegments()
    {
        var points = new List<double>(_raw.Length + 8) { 0, MaximumOmega };
        void Add(double value)
        {
            if (double.IsFinite(value) && value > 0 && value < MaximumOmega) points.Add(value);
        }
        for (var i = 1; i < _raw.Length - 1; i++) Add(i / (double)_inverseStep);
        for (var i = 0; i < _raw.Length - 1; i++)
        {
            var delta = (double)_raw[i + 1] - _raw[i];
            if (delta == 0) continue;
            var fraction = -_raw[i] / delta;
            var crossing = (i + fraction) / _inverseStep;
            var end = i == _raw.Length - 2 ? MaximumOmega : (i + 1) / (double)_inverseStep;
            // The final leaf interval may extend beyond fraction 1 because the
            // stored step and inverse are separate rounded values.
            if (crossing > i / (double)_inverseStep && crossing < end) Add(crossing);
        }
        Add(_operatingCeiling);
        if (_ae0)
        {
            var m = _modifiers;
            Add(_redline);
            Add(m[27]);
            Add(m[28]);
            void AddCoordinateCrossing(double coordinate)
            {
                var delta = (double)m[26] - m[25];
                if (delta == 0) return;
                var omega = (coordinate - m[25]) / delta * _redline;
                if (omega > 0 && omega < _redline) Add(omega);
            }
            AddCoordinateCrossing(m[24]);
            var low = Math.Min(m[22], 1f);
            if (m[23] > 1 && low < 1)
                AddCoordinateCrossing(m[24] + (1d - low) / (m[23] - (double)low) * (m[26] - (double)m[24]));
        }
        var bounds = points.Distinct().Order().ToArray();
        var segments = new NativeCombustionRuntimeSegment[bounds.Length - 1];
        for (var j = 0; j < segments.Length; j++)
        {
            var middle = bounds[j] + (bounds[j + 1] - bounds[j]) / 2;
            var index = Math.Min((int)(middle * _inverseStep), _raw.Length - 2);
            var delta = (double)_raw[index + 1] - _raw[index];
            var r0 = _raw[index] - index * delta;
            var r1 = delta * _inverseStep;
            var raw = r0 + r1 * middle;
            var (q0, q1, q2) = raw > 0 && middle <= _operatingCeiling ? EquilibriumPolynomial(middle) : (1d, 0d, 0d);
            if (middle > _operatingCeiling && raw > 0) q0 = q1 = q2 = 0;
            segments[j] = new(bounds[j], bounds[j + 1], r0 * q0, r0 * q1 + r1 * q0,
                r0 * q2 + r1 * q1, r1 * q2);
        }
        return segments;
    }

    private (double C0, double C1, double C2) EquilibriumPolynomial(double omega)
    {
        if (!_ae0) return (1, 0, 0);
        var m = _modifiers;
        var p0 = omega >= _redline ? m[26] : (double)m[25];
        var p1 = omega >= _redline ? 0 : (m[26] - (double)m[25]) / _redline;
        var p = p0 + p1 * omega;
        var low = Math.Min(m[22], 1f);
        double s0, s1;
        if (p <= m[24]) { s0 = low; s1 = 0; }
        else if (p >= m[26]) { s0 = m[23]; s1 = 0; }
        else
        {
            var slope = (m[23] - (double)low) / (m[26] - (double)m[24]);
            s0 = low + (p0 - m[24]) * slope;
            s1 = p1 * slope;
        }
        if (s0 + s1 * omega <= 1) return (s0, s1, 0);
        double d0, d1;
        if (omega <= m[27]) { d0 = m[29]; d1 = 0; }
        else if (omega >= m[28]) { d0 = m[30]; d1 = 0; }
        else
        {
            d1 = (m[30] - (double)m[29]) / (m[28] - (double)m[27]);
            d0 = m[29] - m[27] * d1;
        }
        return (1 + (s0 - 1) * d0, (s0 - 1) * d1 + s1 * d0, s1 * d1);
    }

    private static bool Finite(float value, float minimum, float maximum) =>
        float.IsFinite(value) && value >= minimum && value <= maximum;
}
