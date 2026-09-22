using System.Buffers.Binary;
using Xunit;

namespace Wisp.App.Tests;

public sealed class NativeCombustionRuntimeCurveTests
{
    [Fact]
    public void CapturedC7CoefficientsReproduceAllSupportedEquilibriumRowsWithoutFittedConstants()
    {
        var bytes = new byte[124];
        for (var i = 0; i < C7RuntimeEquilibriumFixture.ModifierBits.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4), C7RuntimeEquilibriumFixture.ModifierBits[i]);
        Assert.True(NativeCombustionRuntimeCurve.TryCreate(C7RuntimeEquilibriumFixture.Raw,
            C7RuntimeEquilibriumFixture.Step, C7RuntimeEquilibriumFixture.InverseStep,
            C7RuntimeEquilibriumFixture.Redline, C7RuntimeEquilibriumFixture.OperatingCeiling, bytes, out var curve));
        var inside = 0;
        var outside = 0;
        foreach (var row in C7RuntimeEquilibriumFixture.Rows)
        {
            var omega = (float)(row.Rpm * Math.PI / 30);
            Assert.True(curve!.TryEvaluate(omega, out var torque));
            if (omega > curve.OperatingCeiling)
            {
                // The earlier audit did not apply the native positive-output cut.
                Assert.Equal(0, torque);
                outside++;
                continue;
            }
            Assert.True(Math.Abs(torque * 100d - row.TorqueNm) < .001,
                $"C7 CSV row {row.CsvRow}: runtime {torque * 100d:R}, independent audit {row.TorqueNm:R}");
            inside++;
        }
        Assert.Equal(900, inside + outside);
        Assert.True(inside > 800);
        Assert.True(outside > 0);
        AssertSegmentsMatchFloatOracle(curve!);
    }

    [Fact]
    public void RawInterpolationUsesInverseCoordinateBeforeSubtractingTheIndex()
    {
        Assert.True(NativeCombustionRuntimeCurve.TryCreate([2, 9, -7], 10.4719696f, .0954930186f,
            10, 20, new byte[124], out var curve));
        const float omega = 11.1132002f;
        var x = omega * .0954930186f;
        var expected = 9 + (-7f - 9) * (x - 1);
        Assert.True(curve!.TryEvaluate(omega, out var actual));
        Assert.Equal(BitConverter.SingleToInt32Bits(expected), BitConverter.SingleToInt32Bits(actual));
    }

    [Fact]
    public void RawTableIsInterpolatedBeforeTheContinuousModifier()
    {
        Assert.True(Create([10, 20, 30, 40], Ae0(), out var curve));
        Assert.True(curve!.TryEvaluate(0, out var left));
        Assert.True(curve.TryEvaluate(50, out var middle));
        Assert.True(curve.TryEvaluate(100, out var right));
        Assert.Equal(21.5625f, middle);
        Assert.NotEqual((left + right) / 2, middle);
        AssertSegmentsMatchFloatOracle(curve);
    }

    [Fact]
    public void ModifierLowEndpointIsCappedAtOneBeforeNormalization()
    {
        var bytes = Ae0();
        Float(bytes, 22, 4); Float(bytes, 25, 2);
        Assert.True(Create([10, 10, 10, 10], bytes, out var curve));
        Assert.True(curve!.TryEvaluate(0, out var torque));
        Assert.Equal(10, torque);
        Assert.True(curve.TryEvaluate(100, out torque));
        Assert.Equal(15, torque);
        AssertSegmentsMatchFloatOracle(curve);
    }

    [Fact]
    public void NormalizationClampsBeforeAndAfterItsCoordinateRange()
    {
        var bytes = Ae0();
        Float(bytes, 25, -6);
        Assert.True(Create([10, 10, 10, 10], bytes, out var curve));
        Assert.True(curve!.TryEvaluate(50, out var low));
        Assert.Equal(5, low);
        Assert.True(curve.TryEvaluate(250, out var high));
        Assert.Equal(20, high);
        Assert.Contains(curve.Segments, segment => segment.MinimumOmega == 100);
        AssertSegmentsMatchFloatOracle(curve);
    }

    [Fact]
    public void DropoffScalesOnlyTheExcessAboveOneAndSplitsItsBranches()
    {
        var bytes = Ae0();
        Float(bytes, 22, 1); Float(bytes, 24, 0); Float(bytes, 25, 0);
        Float(bytes, 27, 100); Float(bytes, 28, 200); Float(bytes, 30, .5f);
        Assert.True(Create([10, 10, 10, 10], bytes, out var curve));
        Assert.True(curve!.TryEvaluate(150, out var torque));
        Assert.Equal(15.625f, torque);
        Assert.True(curve.TryEvaluate(250, out torque));
        Assert.Equal(15, torque);
        AssertSegmentsMatchFloatOracle(curve);
    }

    [Fact]
    public void DropoffDoesNotAffectScalesAtOrBelowOne()
    {
        var bytes = Ae0();
        Float(bytes, 23, .75f); Float(bytes, 29, 100); Float(bytes, 30, 100);
        Assert.True(Create([10, 10, 10, 10], bytes, out var curve));
        Assert.True(curve!.TryEvaluate(200, out var torque));
        Assert.Equal(7.5f, torque);
        AssertSegmentsMatchFloatOracle(curve);
    }

    [Fact]
    public void PolynomialSegmentsSplitTheScaleOneCrossingAndRetainCubicTerms()
    {
        var bytes = Ae0();
        Float(bytes, 24, 0); Float(bytes, 25, 0);
        Float(bytes, 27, 40); Float(bytes, 28, 160); Float(bytes, 29, .2f); Float(bytes, 30, .8f);
        Assert.True(Create([10, 20, 30, 40], bytes, out var curve));
        Assert.Contains(curve!.Segments, segment => Math.Abs(segment.MinimumOmega - 200d / 3) < 1e-12);
        Assert.Contains(curve.Segments, segment => segment.C3 != 0);
        AssertSegmentsMatchFloatOracle(curve);
    }

    [Fact]
    public void FullControlCoordinateAboveHighEndpointClampsThroughoutTheRange()
    {
        var bytes = Ae0();
        Float(bytes, 25, 12);
        Assert.True(Create([10, 10, 10, 10], bytes, out var curve));
        Assert.True(curve!.TryEvaluate(50, out var torque));
        Assert.Equal(20, torque);
        AssertSegmentsMatchFloatOracle(curve);
    }

    [Fact]
    public void FinalIntervalSignCrossingIsSplitEvenBeyondItsNominalLastKnot()
    {
        Assert.True(NativeCombustionRuntimeCurve.TryCreate([1, .0001f], 100, .010009f,
            50, 100, Ae0(), out var curve));
        Assert.True(curve!.Segments[^1].MinimumOmega > 1 / (double).010009f);
        Assert.True(curve.TryEvaluate(100, out var overrun));
        Assert.True(overrun < 0);
        AssertSegmentsMatchFloatOracle(curve);
    }

    [Fact]
    public void LimiterCutsPositiveOutputStrictlyAboveCeilingAndPreservesNegativeRawOverrun()
    {
        Assert.True(Create([10, 10, 10, -10], Ae0(), out var curve, ceiling: 250));
        Assert.True(curve!.TryEvaluate(200, out var torque));
        Assert.Equal(20, torque);
        Assert.True(curve.TryEvaluate(250, out torque));
        Assert.Equal(0, torque);
        Assert.True(curve.TryEvaluate(275, out torque));
        Assert.Equal(-5, torque);
        Assert.True(Create([10, 10, 10, 10], Ae0(), out curve, ceiling: 250));
        Assert.True(curve!.TryEvaluate(250, out torque));
        Assert.Equal(20, torque);
        Assert.True(curve.TryEvaluate(MathF.BitIncrement(250), out torque));
        Assert.Equal(0, torque);
        AssertSegmentsMatchFloatOracle(curve);
    }

    [Fact]
    public void UnsupportedWindowAndPowerBranchesNeverUseTheExportFormulaAsFallback()
    {
        foreach (var index in new[] { 0, 6 })
            foreach (var ae0 in new[] { false, true })
            {
                var bytes = ae0 ? Ae0() : new byte[124];
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(index * 4), 1);
                Assert.False(Create([10, 10, 10, 10], bytes, out var curve));
                Assert.Null(curve);
            }
    }

    [Theory]
    [InlineData(24, float.NaN)]
    [InlineData(25, float.NaN)]
    [InlineData(26, float.NaN)]
    [InlineData(24, float.PositiveInfinity)]
    [InlineData(25, float.NegativeInfinity)]
    [InlineData(26, float.PositiveInfinity)]
    [InlineData(26, 2)]
    [InlineData(26, 1)]
    [InlineData(28, 400)]
    public void InvalidConsumedCoordinatesAndDenominatorsAreRejected(int index, float value)
    {
        var bytes = Ae0();
        Float(bytes, index, value);
        Assert.False(Create([10, 10, 10, 10], bytes, out var curve));
        Assert.Null(curve);
    }

    [Fact]
    public void FiniteCoordinatesWithOverflowingNormalizationDenominatorAreRejected()
    {
        var bytes = Ae0();
        Float(bytes, 24, -float.MaxValue); Float(bytes, 26, float.MaxValue);
        Assert.False(Create([10, 10, 10, 10], bytes, out _));
    }

    [Fact]
    public void InvalidTableSpacingCeilingAndUnknownFlagsAreUnavailable()
    {
        Assert.False(NativeCombustionRuntimeCurve.TryCreate([10, 20], 100, .02f, 100, 100, new byte[124], out _));
        Assert.False(NativeCombustionRuntimeCurve.TryCreate([10, 20], 100, .01f, 100, 90, new byte[124], out _));
        Assert.False(NativeCombustionRuntimeCurve.TryCreate([10, float.NaN], 100, .01f, 100, 100, new byte[124], out _));
        Assert.False(NativeCombustionRuntimeCurve.TryCreate([10], 100, .01f, 100, 100, new byte[124], out _));
        foreach (var index in new[] { 0, 6, 20 })
        {
            var bytes = new byte[124];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(index * 4), 2);
            Assert.False(Create([10, 20, 30, 40], bytes, out _));
        }
    }

    [Fact]
    public void DisabledModifierCoordinatesAreNotReadAndTheRawInputIsCopied()
    {
        var bytes = Enumerable.Repeat((byte)255, 124).ToArray();
        foreach (var index in new[] { 0, 6, 20 }) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(index * 4), 0);
        float[] raw = [10, 20, 30, -10];
        Assert.True(Create(raw, bytes, out var curve));
        raw[1] = 1000;
        Assert.True(curve!.TryEvaluate(100, out var torque));
        Assert.Equal(20, torque);
        AssertSegmentsMatchFloatOracle(curve);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(-1)]
    [InlineData(301)]
    public void QueriesOutsideTheStoredCurveAreUnavailable(float omega)
    {
        Assert.True(Create([10, 20, 30, -10], new byte[124], out var curve));
        Assert.False(curve!.TryEvaluate(omega, out _));
    }

    private static bool Create(float[] raw, byte[] bytes, out NativeCombustionRuntimeCurve? curve, float ceiling = 300) =>
        NativeCombustionRuntimeCurve.TryCreate(raw, 100, .01f, 200, ceiling, bytes, out curve);

    private static byte[] Ae0()
    {
        var bytes = new byte[124];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20 * 4), 1);
        Float(bytes, 22, .5f); Float(bytes, 23, 2); Float(bytes, 24, 2);
        Float(bytes, 25, 6); Float(bytes, 26, 10);
        Float(bytes, 27, 400); Float(bytes, 28, 500); Float(bytes, 29, 1); Float(bytes, 30, 1);
        return bytes;
    }

    private static void Float(byte[] bytes, int index, float value) =>
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(index * 4), BitConverter.SingleToInt32Bits(value));

    private static void AssertSegmentsMatchFloatOracle(NativeCombustionRuntimeCurve curve)
    {
        Assert.NotEmpty(curve.Segments);
        Assert.Equal(0, curve.Segments[0].MinimumOmega);
        Assert.Equal((double)curve.MaximumOmega, curve.Segments[^1].MaximumOmega);
        for (var i = 0; i < curve.Segments.Count; i++)
        {
            var segment = curve.Segments[i];
            Assert.True(segment.MinimumOmega < segment.MaximumOmega);
            if (i > 0) Assert.Equal(curve.Segments[i - 1].MaximumOmega, segment.MinimumOmega);
            foreach (var fraction in new[] { .01, .25, .5, .75, .99 })
            {
                var omega = (float)(segment.MinimumOmega + (segment.MaximumOmega - segment.MinimumOmega) * fraction);
                // Float rounding can cross a very narrow exact-real interval.
                if (omega <= segment.MinimumOmega || omega >= segment.MaximumOmega) continue;
                Assert.True(curve.TryEvaluate(omega, out var scalar));
                Assert.InRange(Math.Abs(segment.Evaluate(omega) - scalar), 0, Math.Max(.0001, Math.Abs(scalar) * .000002));
            }
        }
    }
}
