using System.Diagnostics;
using System.Text.Json.Nodes;
using Wisp.Core;
using Xunit;

namespace Wisp.App.Tests;

public sealed class NativeGaugeDirectResolverTests
{
    private const ulong Module = 0x140000000;
    private const ulong Wrapper = 0x200000000;
    private const ulong ContextControl = 0x210000000;
    private const ulong Context = ContextControl + 0x10;
    private const ulong Sentinel = 0x220000000;
    private const ulong Buckets = 0x230000000;
    private const ulong Boundary = 0x240000000;
    private const ulong Node = 0x250000000;
    private const ulong HudControl = 0x260000000;
    private const ulong Hud = HudControl + 0x10;
    private const ulong TypeVector = 0x270000000;
    private const ulong Instances = 0x280000000;
    private const ulong Outer = 0x290000000;
    private const ulong OuterControl = 0x298000000;
    private const ulong Child = 0x2A0000000;
    private const ulong ReplacementChild = 0x2A8000000;
    private const ulong SpeedUnitObject = 0x2A9000000;
    private const ulong Source = 0x2B0000000;
    private const ulong BucketCount = 8;
    private const ulong LiveTypeCount = 13;

    private static NativeGaugeLayout Layout => NativeHudBuildContract.BuiltIn.NativeGauge!;

    [Fact]
    public void CombustionGaugeResolvesThenUsesGuardedCache()
    {
        var memory = ValidMemory(mode: 1, electric: false);
        var resolver = new NativeGaugeDirectResolver();

        var captureStarted = Stopwatch.GetTimestamp();
        var first = resolver.Read(memory, Module, Source, false);
        var captureFinished = Stopwatch.GetTimestamp();
        Assert.Equal(NativeGaugeDirectState.Resolved, first.State);
        Assert.True(first.IsAvailable);
        Assert.Equal(1u, first.Mode);
        Assert.False(first.IsElectric);
        Assert.True(first.HasNeedlePair);
        Assert.True(first.HasHeadlightState);
        Assert.True(first.AreHeadlightsOn);
        Assert.Equal(273.25f, first.NeedleAngleDegrees);
        Assert.Equal(-0.25f, first.NeedleBlurAmount);
        Assert.Equal(9_500f, first.TachometerMaximum);
        Assert.True(float.IsNaN(first.PowerFillAmount));
        Assert.True(float.IsNaN(first.RegenFillAmount));
        Assert.True(float.IsNaN(first.RegenPowerRatio));
        Assert.True(float.IsNaN(first.ElectricMaximumSpeed));
        Assert.InRange(first.ObservedTimestamp, captureStarted, captureFinished);

        memory.ResetReadCounts();
        var cached = resolver.Read(memory, Module, Source, false);
        Assert.Equal(NativeGaugeDirectState.Cached, cached.State);
        Assert.Equal(first.Mode, cached.Mode);
        Assert.Equal(first.HasHeadlightState, cached.HasHeadlightState);
        Assert.Equal(first.AreHeadlightsOn, cached.AreHeadlightsOn);
        Assert.Equal(first.NeedleAngleDegrees, cached.NeedleAngleDegrees);
        Assert.Equal(first.NeedleBlurAmount, cached.NeedleBlurAmount);
        Assert.Equal(first.TachometerMaximum, cached.TachometerMaximum);
        Assert.True(memory.ReadCount > 0);
    }

    [Fact]
    public void ElectricAnalogUsesTheExactNativeAngleBlurAndPowerTriplet()
    {
        Assert.Equal(0x130UL, Layout.ChildRegenOffset);
        Assert.Equal(0x134UL, Layout.ChildPowerOffset);
        var memory = ValidMemory(mode: 3, electric: true);

        var result = new NativeGaugeDirectResolver().Read(memory, Module, Source, true);

        Assert.Equal(NativeGaugeDirectState.Resolved, result.State);
        Assert.Equal(3u, result.Mode);
        Assert.True(result.IsElectric);
        Assert.True(result.HasNeedlePair);
        Assert.True(result.HasHeadlightState);
        Assert.True(result.AreHeadlightsOn);
        Assert.Equal(301.5f, result.NeedleAngleDegrees);
        Assert.Equal(-0.25f, result.NeedleBlurAmount);
        Assert.Equal(0.77f, result.PowerFillAmount);
        Assert.Equal(0.19f, result.RegenFillAmount);
        Assert.Equal(0.42f, result.RegenPowerRatio);
        Assert.Equal(310f, result.ElectricMaximumSpeed);
        Assert.True(result.DisplayedSpeedState.Available);
        Assert.Equal(123, result.DisplayedSpeedState.Value);
        Assert.Equal(1, result.DisplayedSpeedState.Hundreds);
        Assert.Equal(2, result.DisplayedSpeedState.Tens);
        Assert.Equal(3, result.DisplayedSpeedState.Ones);
        Assert.False(result.DisplayedSpeedState.SpeedLessOrEqualOne);
        Assert.False(result.DisplayedSpeedState.SpeedLessTen);
        Assert.False(result.DisplayedSpeedState.SpeedLessHundred);
        Assert.Equal(SpeedUnit.MilesPerHour, result.DisplayedSpeedState.Unit);
        Assert.True(result.ElectricGearState.Available);
        Assert.Equal(1, result.ElectricGearState.Gear);
        Assert.Equal(2, result.ElectricGearState.GearNext);
        Assert.Equal(0, result.ElectricGearState.GearPrevious);
        Assert.Equal(-1, result.ElectricGearState.GearGaugeState);
        Assert.True(result.ElectricGearState.UseDriveFor1);
        Assert.True(float.IsNaN(result.TachometerMaximum));
    }

    [Fact]
    public void ElectricDigitalPublishesTheExactNativeDisplayedSpeedStateWithoutANeedle()
    {
        var result = new NativeGaugeDirectResolver().Read(
            ValidMemory(mode: 4, electric: true),
            Module,
            Source,
            true);

        Assert.Equal(NativeGaugeDirectState.Resolved, result.State);
        Assert.False(result.HasNeedlePair);
        Assert.True(result.DisplayedSpeedState.Available);
        Assert.Equal(123, result.DisplayedSpeedState.Value);
        Assert.Equal(SpeedUnit.MilesPerHour, result.DisplayedSpeedState.Unit);
    }

    [Theory]
    [InlineData(0x16U, SpeedUnit.MilesPerHour)]
    [InlineData(0x17U, SpeedUnit.KilometersPerHour)]
    public void ElectricDisplayedSpeedUsesTheExactNativeUnitEnum(
        uint nativeValue,
        SpeedUnit expectedUnit)
    {
        var memory = ValidMemory(mode: 3, electric: true);
        memory.SetUInt32(SpeedUnitObject + Layout.SpeedUnitEnumOffset, nativeValue);

        var result = new NativeGaugeDirectResolver().Read(memory, Module, Source, true);

        Assert.Equal(NativeGaugeDirectState.Resolved, result.State);
        Assert.True(result.DisplayedSpeedState.Available);
        Assert.True(result.DisplayedSpeedState.IsUsable);
        Assert.Equal(expectedUnit, result.DisplayedSpeedState.Unit);
    }

    [Theory]
    [InlineData(0x18U)]
    [InlineData(uint.MaxValue)]
    public void ElectricDisplayedSpeedIsUnavailableForANonDisplayUnit(uint nativeValue)
    {
        var memory = ValidMemory(mode: 3, electric: true);
        memory.SetUInt32(SpeedUnitObject + Layout.SpeedUnitEnumOffset, nativeValue);

        var result = new NativeGaugeDirectResolver().Read(memory, Module, Source, true);

        Assert.Equal(NativeGaugeDirectState.Resolved, result.State);
        Assert.False(result.DisplayedSpeedState.Available);
        Assert.False(result.DisplayedSpeedState.IsUsable);
        Assert.Null(result.DisplayedSpeedState.Unit);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ElectricGaugeRejectsOnlyAnUnreadableNativeUnitObject(bool invalidPointer)
    {
        var memory = ValidMemory(mode: 3, electric: true);
        if (invalidPointer)
        {
            memory.SetUInt64(Child + Layout.ChildSpeedUnitObjectOffset, 1);
        }
        else
        {
            memory.Remove(SpeedUnitObject + Layout.SpeedUnitEnumOffset, sizeof(uint));
        }

        var result = new NativeGaugeDirectResolver().Read(memory, Module, Source, true);

        Assert.Equal(NativeGaugeDirectState.Resolved, result.State);
        Assert.True(result.IsAvailable);
        Assert.Equal(0.77f, result.PowerFillAmount);
        Assert.Equal(0.19f, result.RegenFillAmount);
        Assert.True(result.ElectricGearState.Available);
        Assert.False(result.DisplayedSpeedState.Available);
    }

    [Theory]
    [InlineData("one")]
    [InlineData("ten")]
    [InlineData("hundred")]
    [InlineData("less-or-equal-one")]
    [InlineData("less-ten")]
    [InlineData("less-hundred")]
    public void ElectricGaugeRejectsOnlyTheInvalidNativeDisplayedSpeedState(string field)
    {
        var memory = ValidMemory(mode: 3, electric: true);
        switch (field)
        {
            case "one":
                memory.SetUInt32(Child + Layout.ChildSpeedDigitOneOffset, 10);
                break;
            case "ten":
                memory.SetUInt32(Child + Layout.ChildSpeedDigitTenOffset, 10);
                break;
            case "hundred":
                memory.SetUInt32(Child + Layout.ChildSpeedDigitHundredOffset, 10);
                break;
            case "less-or-equal-one":
                memory.SetByte(Child + Layout.ChildSpeedLessOrEqualOneOffset, 2);
                break;
            case "less-ten":
                memory.SetByte(Child + Layout.ChildSpeedLessTenOffset, 2);
                break;
            case "less-hundred":
                memory.SetByte(Child + Layout.ChildSpeedLessHundredOffset, 2);
                break;
        }

        var result = new NativeGaugeDirectResolver().Read(memory, Module, Source, true);

        Assert.Equal(NativeGaugeDirectState.Resolved, result.State);
        Assert.True(result.IsAvailable);
        Assert.Equal(0.77f, result.PowerFillAmount);
        Assert.Equal(0.19f, result.RegenFillAmount);
        Assert.True(result.ElectricGearState.Available);
        Assert.False(result.DisplayedSpeedState.Available);
    }

    [Fact]
    public void ChildlessElectricGaugeUsesTheNativeProviderPowerTarget()
    {
        var memory = ValidMemory(mode: 3, electric: true);
        ConfigureChildlessElectricGauge(
            memory,
            powerTarget: 0.68f,
            regenNumerator: 0.20f);

        var result = new NativeGaugeDirectResolver().Read(memory, Module, Source, true);

        Assert.Equal(NativeGaugeDirectState.Resolved, result.State);
        Assert.True(result.IsAvailable);
        Assert.True(result.IsElectric);
        Assert.False(result.HasNeedlePair);
        Assert.Equal(0.68f, result.PowerFillAmount);
        Assert.Equal(0f, result.RegenFillAmount);
        Assert.Equal(0.3f, result.RegenPowerRatio);
    }

    [Theory]
    [InlineData(-0.40f, 0.50f)]
    [InlineData(-1.00f, 0.55f)]
    public void ChildlessElectricGaugeConvertsTheNativeNegativeNumeratorToRegen(
        float regenNumerator,
        float expectedRegen)
    {
        var memory = ValidMemory(mode: 3, electric: true);
        ConfigureChildlessElectricGauge(
            memory,
            powerTarget: 0f,
            regenNumerator: regenNumerator);

        var result = new NativeGaugeDirectResolver().Read(memory, Module, Source, true);

        Assert.True(result.IsAvailable);
        Assert.Equal(0f, result.PowerFillAmount);
        Assert.Equal(expectedRegen, result.RegenFillAmount, 5);
        Assert.Equal(0.3f, result.RegenPowerRatio);
    }

    [Theory]
    [InlineData("sourceProvider")]
    [InlineData("providerVtable")]
    public void ChildlessElectricGaugeRequiresAValidatedProvider(string corruption)
    {
        var memory = ValidMemory(mode: 3, electric: true);
        ConfigureChildlessElectricGauge(
            memory,
            powerTarget: 0.68f,
            regenNumerator: -0.20f);
        switch (corruption)
        {
            case "sourceProvider":
                memory.SetUInt64(
                    Source + NativeHudBuildContract.BuiltIn.Fields.SourceProvider,
                    0);
                break;
            case "providerVtable":
                memory.SetUInt64(ProviderAddress, 0);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(corruption));
        }

        var result = new NativeGaugeDirectResolver().Read(memory, Module, Source, true);

        Assert.False(result.IsAvailable);
    }

    [Fact]
    public void CachedChildlessElectricGaugeReadsFreshNativeProviderValues()
    {
        var memory = ValidMemory(mode: 3, electric: true);
        ConfigureChildlessElectricGauge(
            memory,
            powerTarget: 0.68f,
            regenNumerator: -0.16f);
        var resolver = new NativeGaugeDirectResolver();
        Assert.Equal(
            NativeGaugeDirectState.Resolved,
            resolver.Read(memory, Module, Source, true).State);

        memory.SetSingle(ProviderAddress + Layout.ProviderRegenTargetOffset, 0.31f);
        memory.SetSingle(ProviderAddress + Layout.ProviderPowerNumeratorOffset, -0.40f);
        memory.ResetReadCounts();

        var result = resolver.Read(
            memory,
            Module,
            Source,
            expectedElectric: true,
            forceStructuralValidation: false);

        Assert.Equal(NativeGaugeDirectState.Cached, result.State);
        Assert.Equal(0.31f, result.PowerFillAmount);
        Assert.Equal(0.50f, result.RegenFillAmount, 5);
        Assert.Equal(0.3f, result.RegenPowerRatio);
        Assert.True(memory.ReadCount > 0);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(4u)]
    public void ElectricModesWithoutNativeNeedleIgnoreStaleNeedleFields(uint mode)
    {
        var memory = ValidMemory(mode, electric: true);
        memory.SetSingle(Child + Layout.ChildAngleOffset, float.NaN);
        memory.SetSingle(Child + Layout.ChildBlurOffset, float.NaN);
        memory.SetSingle(Child + Layout.ChildMaximumTachometerOffset, float.NaN);

        var result = new NativeGaugeDirectResolver().Read(memory, Module, Source, true);

        Assert.True(result.IsAvailable);
        Assert.Equal(mode, result.Mode);
        Assert.False(result.HasNeedlePair);
        Assert.True(float.IsNaN(result.NeedleAngleDegrees));
        Assert.True(float.IsNaN(result.NeedleBlurAmount));
        Assert.Equal(0.77f, result.PowerFillAmount);
        Assert.Equal(0.19f, result.RegenFillAmount);
        Assert.Equal(310f, result.ElectricMaximumSpeed);
        Assert.Equal(mode == 4, result.DisplayedSpeedState.Available);
    }

    [Theory]
    [InlineData((byte)0, false)]
    [InlineData((byte)1, true)]
    public void AuthoredHeadlightByteIsReturnedForEveryGaugeType(byte nativeValue, bool expected)
    {
        foreach (var (mode, electric) in new[] { (1u, false), (3u, true), (4u, true) })
        {
            var memory = ValidMemory(mode, electric);
            memory.SetByte(Child + Layout.ChildHeadlightsOnOffset, nativeValue);

            var result = new NativeGaugeDirectResolver().Read(memory, Module, Source, electric);

            Assert.True(result.IsAvailable);
            Assert.True(result.HasHeadlightState);
            Assert.Equal(expected, result.AreHeadlightsOn);
        }
    }

    [Theory]
    [InlineData((byte)2)]
    [InlineData(byte.MaxValue)]
    public void NonBooleanHeadlightBytesNeverClaimAState(byte nativeValue)
    {
        var memory = ValidMemory(mode: 1, electric: false);
        memory.SetByte(Child + Layout.ChildHeadlightsOnOffset, nativeValue);

        var result = new NativeGaugeDirectResolver().Read(memory, Module, Source, false);

        Assert.True(result.IsAvailable);
        Assert.False(result.HasHeadlightState);
        Assert.False(result.AreHeadlightsOn);
    }

    [Theory]
    [InlineData(false, 3u)]
    [InlineData(false, 4u)]
    [InlineData(true, 1u)]
    [InlineData(true, 2u)]
    [InlineData(false, 5u)]
    [InlineData(true, uint.MaxValue)]
    public void ImpossiblePowertrainModeCombinationsFailClosed(bool electric, uint mode)
    {
        var memory = ValidMemory(electric ? 3u : 1u, electric);
        memory.SetUInt32(Child + Layout.ChildModeOffset, mode);

        Assert.False(new NativeGaugeDirectResolver().Read(memory, Module, Source, electric).IsAvailable);
    }

    [Theory]
    [InlineData("angle", -0.01f)]
    [InlineData("angle", 720.01f)]
    [InlineData("angle", float.NaN)]
    [InlineData("blur", -0.66f)]
    [InlineData("blur", 0.66f)]
    [InlineData("maximum", 0f)]
    [InlineData("maximum", 100_001f)]
    public void CombustionFieldsRequireFiniteNativeRanges(string field, float value)
    {
        var memory = ValidMemory(mode: 0, electric: false);
        memory.SetSingle(Child + CombustionOffset(field), value);

        Assert.False(new NativeGaugeDirectResolver().Read(memory, Module, Source, false).IsAvailable);
    }

    [Theory]
    [InlineData("power", -0.01f)]
    [InlineData("power", 1.01f)]
    [InlineData("regen", float.PositiveInfinity)]
    [InlineData("regen", 1.01f)]
    [InlineData("ratio", -0.01f)]
    [InlineData("ratio", float.NaN)]
    [InlineData("maximum", 0f)]
    [InlineData("maximum", 100_001f)]
    [InlineData("angle", 149.99f)]
    [InlineData("angle", 390.01f)]
    public void ElectricFieldsRequireFiniteNativeRanges(string field, float value)
    {
        var memory = ValidMemory(mode: 3, electric: true);
        memory.SetSingle(Child + ElectricOffset(field), value);

        Assert.False(new NativeGaugeDirectResolver().Read(memory, Module, Source, true).IsAvailable);
    }

    [Fact]
    public void ExactExpectedSourceIdentityIsRequired()
    {
        var memory = ValidMemory(mode: 1, electric: false);

        Assert.False(new NativeGaugeDirectResolver().Read(memory, Module, Source + 8, false).IsAvailable);
    }

    [Theory]
    [InlineData("wrapperVtable")]
    [InlineData("contextVtable")]
    [InlineData("contextControlVtable")]
    [InlineData("hudVtable")]
    [InlineData("hudControlVtable")]
    [InlineData("subobjectPointer")]
    [InlineData("subobjectVtable")]
    [InlineData("slotZero")]
    [InlineData("outerControlVtable")]
    [InlineData("outerControlOwner")]
    [InlineData("outerPrimaryVtable")]
    [InlineData("outerSecondaryVtable")]
    [InlineData("hudBackReference")]
    [InlineData("hudControlBackReference")]
    [InlineData("childVtable")]
    public void EveryObjectIdentityAndOwnershipGuardIsRequired(string guard)
    {
        var memory = ValidMemory(mode: 1, electric: false);
        memory.SetUInt64(GuardAddress(guard), 0);

        Assert.False(new NativeGaugeDirectResolver().Read(memory, Module, Source, false).IsAvailable);
    }

    [Fact]
    public void RegistryTraversalRejectsCyclesAndUnboundedCollisionChains()
    {
        var memory = ValidMemory(mode: 1, electric: false);
        var bucket = Buckets + ((Layout.RegistryKeyHash & (BucketCount - 1)) * Layout.RegistryBucketStride);
        memory.SetUInt64(bucket + Layout.RegistryBucketBoundaryOffset, Boundary);
        memory.SetUInt64(Node + Layout.RegistryNodeHashOffset, Layout.RegistryKeyHash + BucketCount);
        memory.SetUInt64(Node + Layout.RegistryNodeNextOffset, Node);

        Assert.False(new NativeGaugeDirectResolver().Read(memory, Module, Source, false).IsAvailable);
        Assert.True(memory.ReadCount < 200);
    }

    [Fact]
    public void InclusiveLastNodeIsTestedButItsNextPointerIsNeverFollowed()
    {
        var memory = ValidMemory(mode: 1, electric: false);
        memory.SetUInt64(Node + Layout.RegistryNodeHashOffset, Layout.RegistryKeyHash + BucketCount);
        var followed = false;
        memory.BeforeRead = (address, _) =>
            followed |= address == Node + Layout.RegistryNodeNextOffset;

        Assert.False(new NativeGaugeDirectResolver().Read(memory, Module, Source, false).IsAvailable);
        Assert.False(followed);
    }

    [Theory]
    [InlineData("mask")]
    [InlineData("bucketEnd")]
    [InlineData("bucketCapacity")]
    [InlineData("nodeHash")]
    [InlineData("typeMissing")]
    [InlineData("typeDuplicate")]
    [InlineData("instanceCount")]
    public void StructuralMapAndVectorCorruptionFailsClosed(string corruption)
    {
        var memory = ValidMemory(mode: 1, electric: false);
        Corrupt(memory, corruption);

        Assert.False(new NativeGaugeDirectResolver().Read(memory, Module, Source, false).IsAvailable);
    }

    [Fact]
    public void HudVectorAcceptsAppendOnlyGrowthWithinPackMaximum()
    {
        var memory = ValidMemory(mode: 1, electric: false, liveTypeCount: 20);

        Assert.True(new NativeGaugeDirectResolver().Read(memory, Module, Source, false).IsAvailable);
    }

    [Fact]
    public void HudVectorAbovePackMaximumIsRejectedBeforeScanningEntries()
    {
        var memory = ValidMemory(mode: 1, electric: false);
        var vectorAddress = Hud + Layout.HudSubobjectOffset + Layout.HudTypeVectorOffset;
        memory.SetUInt64(
            vectorAddress + Layout.HudTypeVectorEndOffset,
            TypeVector + ((Layout.HudTypeVectorMaximumCount + 1) * Layout.HudTypeVectorEntryStride));

        Assert.False(new NativeGaugeDirectResolver().Read(memory, Module, Source, false).IsAvailable);
    }

    [Fact]
    public void VolatileGaugeChangeAfterAtomicReadDoesNotDiscardTheCapturedSample()
    {
        var memory = ValidMemory(mode: 1, electric: false);
        var trigger = Outer + Layout.OuterChildOffset;
        memory.BeforeRead = (address, occurrence) =>
        {
            if (address == trigger && occurrence == 2)
            {
                memory.SetSingle(Child + Layout.ChildAngleOffset, 300f);
                memory.SetSingle(Child + Layout.ChildBlurOffset, 0.35f);
            }
        };

        var result = new NativeGaugeDirectResolver().Read(memory, Module, Source, false);

        Assert.True(result.IsAvailable);
        Assert.Equal(273.25f, result.NeedleAngleDegrees);
        Assert.Equal(-0.25f, result.NeedleBlurAmount);
        Assert.Equal(1, memory.GaugeBlockReadCount);
    }

    [Fact]
    public void VolatileHeadlightChangeAfterAtomicReadDoesNotTearTheCapturedSample()
    {
        var memory = ValidMemory(mode: 1, electric: false);
        var trigger = Outer + Layout.OuterChildOffset;
        memory.BeforeRead = (address, occurrence) =>
        {
            if (address == trigger && occurrence == 2)
            {
                memory.SetByte(Child + Layout.ChildHeadlightsOnOffset, 0);
            }
        };

        var result = new NativeGaugeDirectResolver().Read(memory, Module, Source, false);

        Assert.True(result.IsAvailable);
        Assert.True(result.HasHeadlightState);
        Assert.True(result.AreHeadlightsOn);
        Assert.Equal(1, memory.GaugeBlockReadCount);
    }

    [Fact]
    public void ChangedChainBetweenFullPassesIsNeverReturned()
    {
        var memory = ValidMemory(mode: 1, electric: false);
        ConfigureChild(memory, ReplacementChild, mode: 1, electric: false);
        var watched = Outer + Layout.OuterChildOffset;
        memory.BeforeRead = (address, occurrence) =>
        {
            if (address == watched && occurrence == 2)
            {
                memory.SetUInt64(address, ReplacementChild);
            }
        };

        Assert.False(new NativeGaugeDirectResolver().Read(memory, Module, Source, false).IsAvailable);
    }

    [Fact]
    public void CacheInvalidatesAndFullyResolvesAStableReplacement()
    {
        var memory = ValidMemory(mode: 1, electric: false);
        var resolver = new NativeGaugeDirectResolver();
        Assert.Equal(NativeGaugeDirectState.Resolved, resolver.Read(memory, Module, Source, false).State);

        ConfigureChild(memory, ReplacementChild, mode: 2, electric: false);
        memory.SetSingle(ReplacementChild + Layout.ChildAngleOffset, 315f);
        memory.SetUInt64(Outer + Layout.OuterChildOffset, ReplacementChild);

        var replacement = resolver.Read(memory, Module, Source, false);
        Assert.Equal(NativeGaugeDirectState.Resolved, replacement.State);
        Assert.Equal(2u, replacement.Mode);
        Assert.Equal(315f, replacement.NeedleAngleDegrees);

        resolver.Reset();
        Assert.Equal(NativeGaugeDirectState.Resolved, resolver.Read(memory, Module, Source, false).State);
    }

    [Fact]
    public void CachedTraversalReadsFewerLocationsThanFullTypeSearch()
    {
        var memory = ValidMemory(mode: 1, electric: false, liveTypeCount: 40);
        var resolver = new NativeGaugeDirectResolver();
        Assert.True(resolver.Read(memory, Module, Source, false).IsAvailable);
        var fullReads = memory.ReadCount;

        memory.ResetReadCounts();
        Assert.Equal(NativeGaugeDirectState.Cached, resolver.Read(memory, Module, Source, false).State);
        Assert.True(memory.ReadCount < fullReads);
        Assert.Equal(1, memory.GaugeBlockReadCount);
        Assert.InRange(memory.BlockReadCount, 1, 30);
        Assert.InRange(memory.ReadCount, 1, 36);
    }

    [Fact]
    public void HotGaugeCaptureUsesOneAtomicBlockAndTwoChildIdentityReads()
    {
        var memory = ValidMemory(mode: 1, electric: false, liveTypeCount: 40);
        var resolver = new NativeGaugeDirectResolver();
        Assert.True(resolver.Read(memory, Module, Source, false).IsAvailable);

        memory.ResetReadCounts();
        var before = Stopwatch.GetTimestamp();
        var result = resolver.Read(
            memory,
            Module,
            Source,
            expectedElectric: false,
            forceStructuralValidation: false);
        var after = Stopwatch.GetTimestamp();

        Assert.Equal(NativeGaugeDirectState.Cached, result.State);
        Assert.Equal(3, memory.ReadCount);
        Assert.Equal(1, memory.BlockReadCount);
        Assert.Equal(1, memory.GaugeBlockReadCount);
        Assert.InRange(result.ObservedTimestamp, before, after);
    }

    [Fact]
    public void ForcedStructuralAuditRebindsAfterHotCaptureWindow()
    {
        var memory = ValidMemory(mode: 1, electric: false);
        var resolver = new NativeGaugeDirectResolver();
        Assert.True(resolver.Read(memory, Module, Source, false).IsAvailable);

        ConfigureChild(memory, ReplacementChild, mode: 2, electric: false);
        memory.SetSingle(ReplacementChild + Layout.ChildAngleOffset, 315f);
        memory.SetUInt64(Outer + Layout.OuterChildOffset, ReplacementChild);

        var hot = resolver.Read(
            memory,
            Module,
            Source,
            expectedElectric: false,
            forceStructuralValidation: false);
        Assert.Equal(273.25f, hot.NeedleAngleDegrees);

        var audited = resolver.Read(
            memory,
            Module,
            Source,
            expectedElectric: false,
            forceStructuralValidation: true);
        Assert.Equal(NativeGaugeDirectState.Resolved, audited.State);
        Assert.Equal(315f, audited.NeedleAngleDegrees);
    }

    [Fact]
    public void SlotZeroPrologueIsAnExactBuildGuard()
    {
        var memory = ValidMemory(mode: 1, electric: false);
        memory.SetByte(Module + Layout.HudSubobjectSlotZeroTargetRva, 0x90);

        Assert.False(new NativeGaugeDirectResolver().Read(memory, Module, Source, false).IsAvailable);
    }

    [Fact]
    public void LegacyPackDoesNotAttemptUnspecifiedGaugeReads()
    {
        using var stream = typeof(NativeHudBuildContract).Assembly
            .GetManifestResourceStream("Wisp.NativeCompatibility.BuiltIn.json")!;
        var document = JsonNode.Parse(stream)!.AsObject();
        document["schemaVersion"] = 2;
        document["readerVersion"] = 2;
        document.Remove("nativeGauge");
        var pack = NativeHudCompatibilityPack.Parse(
            System.Text.Encoding.UTF8.GetBytes(document.ToJsonString()));
        var memory = new Memory();

        Assert.False(new NativeGaugeDirectResolver(pack).Read(memory, Module, Source, false).IsAvailable);
        Assert.Equal(0, memory.ReadCount);
    }

    [Fact]
    public void ControlObjectOffsetComesFromTheCompatibilityPack()
    {
        using var stream = typeof(NativeHudBuildContract).Assembly
            .GetManifestResourceStream("Wisp.NativeCompatibility.BuiltIn.json")!;
        var document = JsonNode.Parse(stream)!.AsObject();
        document["nativeGauge"]!["sharedControlObjectOffset"] = 0x18UL;
        var pack = NativeHudCompatibilityPack.Parse(
            System.Text.Encoding.UTF8.GetBytes(document.ToJsonString()));
        var memory = ValidMemory(mode: 1, electric: false);

        Assert.False(new NativeGaugeDirectResolver(pack).Read(memory, Module, Source, false).IsAvailable);
    }

    [Fact]
    public void InlineContextAndHudObjectsAreAddressComparedNotDereferenced()
    {
        var memory = ValidMemory(mode: 1, electric: false);
        Assert.Equal(Context, ContextControl + Layout.SharedControlObjectOffset);
        Assert.Equal(Hud, HudControl + Layout.SharedControlObjectOffset);
        Assert.True(memory.TryReadUInt64(Context, out var contextFirstWord));
        Assert.True(memory.TryReadUInt64(Hud, out var hudFirstWord));
        Assert.Equal(Module + Layout.RegistryContextVtableRva, contextFirstWord);
        Assert.Equal(Module + Layout.HudVtableRva, hudFirstWord);
        Assert.NotEqual(Context, contextFirstWord);
        Assert.NotEqual(Hud, hudFirstWord);
        memory.ResetReadCounts();

        Assert.True(new NativeGaugeDirectResolver().Read(memory, Module, Source, false).IsAvailable);
    }

    [Fact]
    public void OuterControlRequiresAnIndirectObjectPointer()
    {
        var memory = ValidMemory(mode: 1, electric: false);
        Assert.NotEqual(Outer, OuterControl + Layout.SharedControlObjectOffset);
        Assert.True(
            memory.TryReadUInt64(
                OuterControl + Layout.SharedControlObjectOffset,
                out var controlledOuter));
        Assert.Equal(Outer, controlledOuter);
        memory.ResetReadCounts();

        Assert.True(new NativeGaugeDirectResolver().Read(memory, Module, Source, false).IsAvailable);
    }

    [Fact]
    public void HeadlightOffsetComesFromTheCompatibilityPack()
    {
        using var stream = typeof(NativeHudBuildContract).Assembly
            .GetManifestResourceStream("Wisp.NativeCompatibility.BuiltIn.json")!;
        var document = JsonNode.Parse(stream)!.AsObject();
        document["nativeGauge"]!["childHeadlightsOnOffset"] = 0x10DUL;
        var pack = NativeHudCompatibilityPack.Parse(
            System.Text.Encoding.UTF8.GetBytes(document.ToJsonString()));
        var memory = ValidMemory(mode: 1, electric: false);
        memory.SetByte(Child + 0x10D, 0);

        var result = new NativeGaugeDirectResolver(pack).Read(memory, Module, Source, false);

        Assert.True(result.IsAvailable);
        Assert.True(result.HasHeadlightState);
        Assert.False(result.AreHeadlightsOn);
    }

    [Fact]
    public void HudResolverPublishesExactCombustionAngleBlurAndMaximum()
    {
        var memory = ValidMemory(mode: 1, electric: false);
        ConfigureHudReader(memory, Source, carOrdinal: 314, electricSpeed: 0);

        var result = new NativeHudMemoryResolver().Resolve(
            memory, Module, 314, 4_000, 8_000, 1, isElectric: false);

        Assert.True(result.Available);
        Assert.True(result.HasNativeNeedleState);
        Assert.Equal(273.25, result.NativeNeedleAngleDegrees);
        Assert.Equal(-0.25, result.NativeNeedleBlurAmount);
        Assert.Equal(9_500, result.TachometerMaximumRpm);
        Assert.False(result.HasNativeElectricGaugeState);
    }

    [Theory]
    [InlineData((byte)0, false)]
    [InlineData((byte)1, true)]
    public void HudResolverPublishesTheExactChildHeadlightState(byte nativeValue, bool expected)
    {
        Assert.Equal(0x10CUL, Layout.ChildHeadlightsOnOffset);
        var memory = ValidMemory(mode: 1, electric: false);
        ConfigureHudReader(memory, Source, carOrdinal: 314, electricSpeed: 0);
        memory.SetByte(Child + 0x10C, nativeValue);

        var result = new NativeHudMemoryResolver().Resolve(
            memory, Module, 314, 4_000, 8_000, 1, isElectric: false);

        Assert.True(result.Assists.HeadlightStateAvailable);
        Assert.Equal(expected, result.Assists.AreHeadlightsOn);
    }

    [Fact]
    public void HudResolverPublishesExactElectricAnalogNeedleAndAtomicBarTriplet()
    {
        var memory = ValidMemory(mode: 3, electric: true);
        ConfigureHudReader(memory, Source, carOrdinal: 314, electricSpeed: 155);

        var result = new NativeHudMemoryResolver().Resolve(
            memory, Module, 314, 4_000, 8_000, 1, isElectric: true);

        Assert.True(result.HasNativeNeedleState);
        Assert.Equal(301.5, result.NativeNeedleAngleDegrees);
        Assert.Equal(-0.25, result.NativeNeedleBlurAmount);
        Assert.True(result.HasNativeElectricGaugeState);
        Assert.Equal(0.19, result.NativeRegenFillAmount, 5);
        Assert.Equal(0.77, result.NativePowerFillAmount, 5);
        Assert.Equal(0.42, result.NativeRegenPowerRatio, 5);
        Assert.Equal(310, result.NativeElectricMaximumSpeed);
        Assert.True(result.ElectricGearState.Available);
        Assert.Equal(1, result.ElectricGearState.Gear);
        Assert.Equal(2, result.ElectricGearState.GearNext);
        Assert.Equal(0, result.ElectricGearState.GearPrevious);
        Assert.Equal(-1, result.ElectricGearState.GearGaugeState);
        Assert.True(result.ElectricGearState.UseDriveFor1);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(4u)]
    public void HudResolverLeavesNeedleUnavailableForElectricModesWithoutASourceProvenPair(uint mode)
    {
        var memory = ValidMemory(mode, electric: true);
        ConfigureHudReader(memory, Source, carOrdinal: 314, electricSpeed: 155);

        var result = new NativeHudMemoryResolver().Resolve(
            memory, Module, 314, 4_000, 8_000, 1, isElectric: true);

        Assert.False(result.HasNativeNeedleState);
        Assert.True(double.IsNaN(result.NativeNeedleAngleDegrees));
        Assert.True(double.IsNaN(result.NativeNeedleBlurAmount));
        Assert.True(result.HasNativeElectricGaugeState);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(4u)]
    public void ProviderSpeedDoesNotAuthorizeANeedleWithoutTheNativePair(uint mode)
    {
        var memory = ValidMemory(mode, electric: true);
        ConfigureHudReader(memory, Source, carOrdinal: 314, electricSpeed: 155);
        var slot = Layout.RequiredProviderVtableSlots.First();
        memory.SetUInt64(
            Module + NativeHudBuildContract.BuiltIn.LeadVtableRva + slot.Key,
            Module + slot.Value + 8);

        var result = new NativeHudMemoryResolver().Resolve(
            memory, Module, 314, 4_000, 8_000, 1, isElectric: true);

        Assert.False(result.HasNativeNeedleState);
        Assert.True(double.IsNaN(result.NativeNeedleAngleDegrees));
        Assert.True(double.IsNaN(result.NativeNeedleBlurAmount));
        Assert.True(result.HasNativeElectricGaugeState);
        Assert.Equal(0.19, result.NativeRegenFillAmount, 5);
        Assert.Equal(0.77, result.NativePowerFillAmount, 5);
        Assert.Equal(0.42, result.NativeRegenPowerRatio, 5);
        Assert.Equal(310, result.NativeElectricMaximumSpeed);
    }

    [Fact]
    public void InvalidElectricBarFieldFailsTheNativeGaugeAtomically()
    {
        var memory = ValidMemory(mode: 3, electric: true);
        ConfigureHudReader(memory, Source, carOrdinal: 314, electricSpeed: 155);
        memory.SetSingle(Child + Layout.ChildPowerOffset, float.NaN);

        var result = new NativeHudMemoryResolver().Resolve(
            memory, Module, 314, 4_000, 8_000, 1, isElectric: true);

        Assert.True(result.Available);
        Assert.False(result.HasNativeNeedleState);
        Assert.False(result.HasNativeElectricGaugeState);
        Assert.True(double.IsNaN(result.NativeNeedleAngleDegrees));
        Assert.True(double.IsNaN(result.NativeRegenFillAmount));
        Assert.True(double.IsNaN(result.NativePowerFillAmount));
    }

    [Fact]
    public void ResolverDoesNotReuseGaugeStateAcrossPowertrainSourceOrResetChanges()
    {
        var memory = ValidMemory(mode: 1, electric: false);
        ConfigureHudReader(memory, Source, carOrdinal: 314, electricSpeed: 155);
        var resolver = new NativeHudMemoryResolver();
        var combustion = resolver.Resolve(
            memory, Module, 314, 4_000, 8_000, 1, isElectric: false);
        Assert.Equal(273.25, combustion.NativeNeedleAngleDegrees);

        ConfigureChild(memory, Child, mode: 3, electric: true);
        var electric = resolver.Resolve(
            memory, Module, 314, 4_000, 8_000, 2, isElectric: true);
        Assert.Equal(301.5, electric.NativeNeedleAngleDegrees);
        Assert.True(electric.HasNativeElectricGaugeState);

        const ulong replacementSource = 0x2B8000000;
        var sourceList = 0x2C0000000UL;
        memory.SetUInt64(sourceList, replacementSource);
        ConfigureHudSource(memory, replacementSource, ProviderAddress, 314);
        memory.SetUInt64(Outer + Layout.OuterSourceOffset, replacementSource);
        memory.SetSingle(Child + Layout.ChildAngleOffset, 315f);

        var switched = resolver.Resolve(
            memory, Module, 314, 4_000, 8_000, 3,
            forceSourceAudit: true,
            isElectric: true);
        Assert.Equal(315, switched.NativeNeedleAngleDegrees);

        resolver.Reset();
        memory.SetSingle(Child + Layout.ChildAngleOffset, 325f);
        var reset = resolver.Resolve(
            memory, Module, 314, 4_000, 8_000, 4, isElectric: true);
        Assert.Equal(325, reset.NativeNeedleAngleDegrees);
    }

    [Fact]
    public async Task ProcessServicePublishesReceiveTimestampAndResetsOnElectricChange()
    {
        var memory = ValidMemory(mode: 1, electric: false);
        ConfigureHudReader(memory, Source, carOrdinal: 314, electricSpeed: 155);
        await using var service = new NativeHudProcessService(new GaugeFactory(memory));

        service.UpdateTelemetry(TelemetryState(isElectric: false), nativeLayoutActive: true);
        var combustion = await WaitForSnapshotAsync(
            service,
            snapshot => snapshot.HasNativeNeedleState && snapshot.NativeGaugeObservedTimestamp > 0);
        Assert.Equal(273.25, combustion.NativeNeedleAngleDegrees);

        ConfigureChild(memory, Child, mode: 3, electric: true);
        service.UpdateTelemetry(
            TelemetryState(isElectric: true) with { GameTimestampMilliseconds = 2 },
            nativeLayoutActive: true);
        Assert.False(service.SnapshotFor(314).HasAvailableCapabilities);
        var electric = await WaitForSnapshotAsync(
            service,
            snapshot => snapshot.HasNativeElectricGaugeState && snapshot.NativeGaugeObservedTimestamp > 0);
        Assert.Equal(301.5, electric.NativeNeedleAngleDegrees);
    }

    [Fact]
    public async Task ProcessServicePublishesFreshGaugeWithoutAnotherTelemetryObject()
    {
        var memory = ValidMemory(mode: 1, electric: false);
        ConfigureHudReader(memory, Source, carOrdinal: 314, electricSpeed: 155);
        var factory = new GaugeFactory(memory);
        await using var service = new NativeHudProcessService(factory);
        var telemetry = TelemetryState(isElectric: false);

        service.UpdateTelemetry(telemetry, nativeLayoutActive: true);
        var first = await WaitForSnapshotAsync(
            service,
            snapshot => snapshot.HasNativeNeedleState && snapshot.NativeGaugeObservedTimestamp > 0);

        memory.SetSingle(Child + Layout.ChildAngleOffset, 312.5f);
        memory.SetSingle(Child + Layout.ChildBlurOffset, 0.35f);
        memory.ResetReadCounts();
        service.RequestNativeGaugeSample();
        var refreshed = await WaitForSnapshotAsync(
            service,
            snapshot => snapshot.Generation > first.Generation &&
                        snapshot.NativeGaugeObservedTimestamp > first.NativeGaugeObservedTimestamp &&
                        snapshot.NativeNeedleAngleDegrees == 312.5 &&
                        Math.Abs(snapshot.NativeNeedleBlurAmount - 0.35) < 0.00001);

        Assert.Equal(312.5, refreshed.NativeNeedleAngleDegrees);
        Assert.Equal(0.35, refreshed.NativeNeedleBlurAmount, 5);
        Assert.True(memory.GaugeBlockReadCount >= 1);
        Assert.True(memory.ReadCount >= 3);
        Assert.Equal(1, factory.OpenCount);
    }

    private const ulong ProviderAddress = 0x2D0000000;

    private static void ConfigureHudReader(
        Memory memory,
        ulong source,
        uint carOrdinal,
        float electricSpeed)
    {
        var pack = NativeHudBuildContract.BuiltIn;
        var sourceList = 0x2C0000000UL;
        memory.SetSingle(Module + pack.ThresholdRva, 0.1f);
        memory.SetUInt64(Module + pack.SourceVectorRva, sourceList);
        memory.SetUInt64(Module + pack.SourceVectorRva + 8, sourceList + 8);
        memory.SetUInt64(Module + pack.SourceVectorRva + 16, sourceList + 8);
        memory.SetUInt64(sourceList, source);
        ConfigureHudSource(memory, source, ProviderAddress, carOrdinal);

        var provider = ProviderAddress;
        var vtable = Module + pack.LeadVtableRva;
        memory.SetUInt64(provider, vtable);
        foreach (var slot in pack.RequiredVtableSlots.Concat(Layout.RequiredProviderVtableSlots))
        {
            memory.SetUInt64(vtable + slot.Key, Module + slot.Value);
        }

        memory.SetByte(provider + pack.Fields.LocalPlayerFlag, 1);
        memory.SetByte(provider + pack.Fields.LocalPlayerProviderFlag, 1);
        memory.SetSingle(provider + pack.Fields.ProviderRpm, 4_000 * 2 * MathF.PI / 60);
        memory.SetSingle(provider + pack.Fields.ProviderSimRedlineAngularVelocity, 6_500 * 2 * MathF.PI / 60);
        memory.SetSingle(provider + pack.Fields.ProviderTachometerMaximumAngularVelocity, 8_000 * 2 * MathF.PI / 60);
        memory.SetSingle(provider + Layout.ProviderElectricSpeedOffset, electricSpeed);
    }

    private static void ConfigureChildlessElectricGauge(
        Memory memory,
        float powerTarget,
        float regenNumerator,
        float rawDenominator = 100f,
        float firstLimit = 0.4f,
        float secondLimit = 0.6f)
    {
        var pack = NativeHudBuildContract.BuiltIn;
        memory.SetUInt64(Outer + Layout.OuterChildOffset, 0);
        ConfigureHudSource(memory, Source, ProviderAddress, carOrdinal: 314);
        memory.SetUInt64(ProviderAddress, Module + pack.LeadVtableRva);
        foreach (var slot in pack.RequiredVtableSlots.Concat(Layout.RequiredProviderVtableSlots))
        {
            memory.SetUInt64(
                Module + pack.LeadVtableRva + slot.Key,
                Module + slot.Value);
        }

        memory.SetSingle(ProviderAddress + Layout.ProviderRegenTargetOffset, powerTarget);
        memory.SetSingle(ProviderAddress + Layout.ProviderPowerNumeratorOffset, regenNumerator);
        memory.SetSingle(ProviderAddress + Layout.ProviderPowerDenominatorOffset, rawDenominator);
        memory.SetSingle(ProviderAddress + Layout.ProviderPowerLimitFirstOffset, firstLimit);
        memory.SetSingle(ProviderAddress + Layout.ProviderPowerLimitSecondOffset, secondLimit);
    }

    private static void ConfigureHudSource(
        Memory memory,
        ulong source,
        ulong provider,
        uint carOrdinal)
    {
        var fields = NativeHudBuildContract.BuiltIn.Fields;
        memory.SetUInt64(source + fields.SourceProvider, provider);
        memory.SetUInt32(source + fields.SourceCarOrdinal, carOrdinal);
    }

    private static Wisp.Core.VehicleState TelemetryState(bool isElectric) => new()
    {
        IsRaceOn = true,
        CarOrdinal = 314,
        EngineRpm = 4_000,
        EngineMaximumRpm = 8_000,
        GameTimestampMilliseconds = 1,
        NumCylinders = isElectric ? 0 : 4,
        ReceivedAtUtc = DateTimeOffset.UtcNow,
        Drivetrain = Wisp.Core.DrivetrainType.RearWheelDrive,
        GroundSpeedMetersPerSecond = 0,
        WheelRotationRadiansPerSecond = new Wisp.Core.WheelValues(0, 0, 0, 0),
        TireSlipRatio = new Wisp.Core.WheelValues(0, 0, 0, 0),
        TireSlipAngle = new Wisp.Core.WheelValues(0, 0, 0, 0),
        NormalizedSuspensionTravel = new Wisp.Core.WheelValues(0, 0, 0, 0),
        LateralAccelerationMetersPerSecondSquared = 0,
        LongitudinalAccelerationMetersPerSecondSquared = 0,
        Gear = Wisp.Core.TransmissionGear.First,
        Steering = 0,
        Accelerator = 0,
        Brake = 0
    };

    private static async Task<Wisp.Core.NativeHudSnapshot> WaitForSnapshotAsync(
        NativeHudProcessService service,
        Func<Wisp.Core.NativeHudSnapshot, bool> predicate)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var snapshot = service.SnapshotFor(314);
            if (predicate(snapshot))
            {
                return snapshot;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("The native gauge snapshot did not settle.");
    }

    private sealed class GaugeFactory(Memory memory) : INativeHudProcessMemoryFactory
    {
        private int _openCount;
        public int OpenCount => Volatile.Read(ref _openCount);

        public bool TryOpen(
            out INativeHudProcessMemory? opened,
            out Wisp.Core.NativeAssistProviderStatus status)
        {
            Interlocked.Increment(ref _openCount);
            opened = memory;
            status = Wisp.Core.NativeAssistProviderStatus.Ready;
            return true;
        }
    }

    private static ulong CombustionOffset(string field) => field switch
    {
        "angle" => Layout.ChildAngleOffset,
        "blur" => Layout.ChildBlurOffset,
        "maximum" => Layout.ChildMaximumTachometerOffset,
        _ => throw new ArgumentOutOfRangeException(nameof(field))
    };

    private static ulong ElectricOffset(string field) => field switch
    {
        "angle" => Layout.ChildAngleOffset,
        "power" => Layout.ChildPowerOffset,
        "regen" => Layout.ChildRegenOffset,
        "ratio" => Layout.ChildRatioOffset,
        "maximum" => Layout.ChildElectricMaximumSpeedOffset,
        _ => throw new ArgumentOutOfRangeException(nameof(field))
    };

    private static ulong GuardAddress(string guard) => guard switch
    {
        "wrapperVtable" => Wrapper,
        "contextVtable" => Context,
        "contextControlVtable" => ContextControl,
        "hudVtable" => Hud,
        "hudControlVtable" => HudControl,
        "subobjectPointer" => Hud + Layout.HudSubobjectPointerOffset,
        "subobjectVtable" => Hud + Layout.HudSubobjectOffset,
        "slotZero" => Module + Layout.HudSubobjectVtableRva,
        "outerControlVtable" => OuterControl,
        "outerControlOwner" => OuterControl + Layout.SharedControlObjectOffset,
        "outerPrimaryVtable" => Outer,
        "outerSecondaryVtable" => Outer + Layout.OuterSecondaryOffset,
        "hudBackReference" => Outer + Layout.OuterHudBackReferenceOffset,
        "hudControlBackReference" => Outer + Layout.OuterHudControlBackReferenceOffset,
        "childVtable" => Child,
        _ => throw new ArgumentOutOfRangeException(nameof(guard))
    };

    private static void Corrupt(Memory memory, string corruption)
    {
        var bucketBytes = BucketCount * Layout.RegistryBucketStride;
        var vectorAddress = Hud + Layout.HudSubobjectOffset + Layout.HudTypeVectorOffset;
        var targetIndex = 8UL;
        var target = TypeVector + (targetIndex * Layout.HudTypeVectorEntryStride);
        switch (corruption)
        {
            case "mask":
                memory.SetUInt64(Context + Layout.RegistryMaskOffset, BucketCount);
                break;
            case "bucketEnd":
                memory.SetUInt64(Context + Layout.RegistryBucketsEndOffset, Buckets + bucketBytes - 8);
                break;
            case "bucketCapacity":
                memory.SetUInt64(Context + Layout.RegistryBucketsCapacityOffset, Buckets + bucketBytes + 8);
                break;
            case "nodeHash":
                memory.SetUInt64(Node + Layout.RegistryNodeHashOffset, Layout.RegistryKeyHash + 1);
                memory.SetUInt64(Node + Layout.RegistryNodeNextOffset, Boundary);
                break;
            case "typeMissing":
                memory.SetUInt64(target + Layout.HudTypeTokenOffset, Module + 0x12340);
                break;
            case "typeDuplicate":
                memory.SetUInt64(TypeVector + Layout.HudTypeTokenOffset, Module + Layout.HudTypeTokenRva);
                break;
            case "instanceCount":
                memory.SetUInt64(
                    target + Layout.HudTypeInstancesEndOffset,
                    Instances + (2 * Layout.HudTypeInstanceStride));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(corruption));
        }
    }

    private static Memory ValidMemory(uint mode, bool electric, ulong liveTypeCount = LiveTypeCount)
    {
        var memory = new Memory();
        memory.SetUInt64(Module + Layout.RegistryGlobalRva, Wrapper);
        memory.SetUInt64(Wrapper, Module + Layout.RegistryWrapperVtableRva);
        memory.SetUInt64(Wrapper + Layout.RegistryContextOffset, Context);
        memory.SetUInt64(Wrapper + Layout.RegistryContextControlOffset, ContextControl);
        memory.SetUInt64(Context, Module + Layout.RegistryContextVtableRva);
        memory.SetUInt64(ContextControl, Module + Layout.RegistryContextControlVtableRva);
        memory.SetUInt64(Context + Layout.RegistrySentinelOffset, Sentinel);
        memory.SetUInt64(Context + Layout.RegistryCountOffset, 1);
        memory.SetUInt64(Context + Layout.RegistryBucketsOffset, Buckets);
        var bucketsEnd = Buckets + (BucketCount * Layout.RegistryBucketStride);
        memory.SetUInt64(Context + Layout.RegistryBucketsEndOffset, bucketsEnd);
        memory.SetUInt64(Context + Layout.RegistryBucketsCapacityOffset, bucketsEnd);
        memory.SetUInt64(Context + Layout.RegistryMaskOffset, BucketCount - 1);
        memory.SetUInt64(Context + Layout.RegistryBucketCountOffset, BucketCount);

        var bucket = Buckets + ((Layout.RegistryKeyHash & (BucketCount - 1)) * Layout.RegistryBucketStride);
        memory.SetUInt64(bucket + Layout.RegistryBucketBoundaryOffset, Node);
        memory.SetUInt64(bucket + Layout.RegistryBucketNodeOffset, Node);
        memory.SetUInt64(Node + Layout.RegistryNodeNextOffset, Sentinel);
        memory.SetUInt64(Node + Layout.RegistryNodeHashOffset, Layout.RegistryKeyHash);
        memory.SetUInt64(Node + Layout.RegistryNodeObjectOffset, Hud);
        memory.SetUInt64(Node + Layout.RegistryNodeControlOffset, HudControl);

        memory.SetUInt64(Hud, Module + Layout.HudVtableRva);
        memory.SetUInt64(HudControl, Module + Layout.HudControlVtableRva);
        var subobject = Hud + Layout.HudSubobjectOffset;
        memory.SetUInt64(Hud + Layout.HudSubobjectPointerOffset, subobject);
        memory.SetUInt64(subobject, Module + Layout.HudSubobjectVtableRva);
        memory.SetUInt64(
            Module + Layout.HudSubobjectVtableRva,
            Module + Layout.HudSubobjectSlotZeroTargetRva);
        var prologue = Convert.FromHexString(Layout.HudSubobjectSlotZeroPrologueHex);
        for (var index = 0; index < prologue.Length; index++)
        {
            memory.SetByte(Module + Layout.HudSubobjectSlotZeroTargetRva + (ulong)index, prologue[index]);
        }

        var vector = subobject + Layout.HudTypeVectorOffset;
        memory.SetUInt64(vector + Layout.HudTypeVectorBeginOffset, TypeVector);
        memory.SetUInt64(
            vector + Layout.HudTypeVectorEndOffset,
            TypeVector + (liveTypeCount * Layout.HudTypeVectorEntryStride));
        memory.SetUInt64(
            vector + Layout.HudTypeVectorCapacityOffset,
            TypeVector + (liveTypeCount * Layout.HudTypeVectorEntryStride));

        var targetIndex = Math.Min(8UL, liveTypeCount - 1);
        for (ulong index = 0; index < liveTypeCount; index++)
        {
            var entry = TypeVector + (index * Layout.HudTypeVectorEntryStride);
            var token = index == targetIndex
                ? Module + Layout.HudTypeTokenRva
                : Module + 0x100000 + (index * 8);
            memory.SetUInt64(entry + Layout.HudTypeTokenOffset, token);
        }

        var target = TypeVector + (targetIndex * Layout.HudTypeVectorEntryStride);
        memory.SetUInt64(target + Layout.HudTypeInstancesBeginOffset, Instances);
        memory.SetUInt64(
            target + Layout.HudTypeInstancesEndOffset,
            Instances + Layout.HudTypeInstanceStride);
        memory.SetUInt64(
            target + Layout.HudTypeInstancesCapacityOffset,
            Instances + Layout.HudTypeInstanceStride);
        memory.SetUInt64(Instances + Layout.HudTypeInstanceObjectOffset, Outer);
        memory.SetUInt64(Instances + Layout.HudTypeInstanceControlOffset, OuterControl);

        memory.SetUInt64(OuterControl, Module + Layout.OuterControlVtableRva);
        memory.SetUInt64(OuterControl + Layout.SharedControlObjectOffset, Outer);
        memory.SetUInt64(Outer, Module + Layout.OuterPrimaryVtableRva);
        memory.SetUInt64(Outer + Layout.OuterSecondaryOffset, Module + Layout.OuterSecondaryVtableRva);
        memory.SetUInt64(Outer + Layout.OuterHudBackReferenceOffset, Hud);
        memory.SetUInt64(Outer + Layout.OuterHudControlBackReferenceOffset, HudControl);
        memory.SetUInt64(Outer + Layout.OuterSourceOffset, Source);
        memory.SetUInt64(Outer + Layout.OuterChildOffset, Child);
        ConfigureChild(memory, Child, mode, electric);
        return memory;
    }

    private static void ConfigureChild(Memory memory, ulong child, uint mode, bool electric)
    {
        var childFields = new[]
        {
            Layout.ChildModeOffset,
            Layout.ChildAngleOffset,
            Layout.ChildBlurOffset,
            Layout.ChildSpeedDigitOneOffset,
            Layout.ChildSpeedDigitTenOffset,
            Layout.ChildSpeedDigitHundredOffset,
            Layout.ChildSpeedLessOrEqualOneOffset,
            Layout.ChildSpeedLessTenOffset,
            Layout.ChildSpeedLessHundredOffset,
            Layout.ChildSpeedUnitObjectOffset,
            Layout.ChildHeadlightsOnOffset,
            Layout.ChildPowerOffset,
            Layout.ChildRegenOffset,
            Layout.ChildRatioOffset,
            Layout.ChildGearOffset,
            Layout.ChildGearNextOffset,
            Layout.ChildGearPreviousOffset,
            Layout.ChildGearGaugeStateOffset,
            Layout.ChildUseDriveFor1Offset,
            Layout.ChildMaximumTachometerOffset,
            Layout.ChildElectricMaximumSpeedOffset
        };
        var blockStart = childFields.Min();
        var blockEnd = childFields.Max() + sizeof(uint);
        for (var offset = blockStart; offset < blockEnd; offset++)
        {
            memory.SetByte(child + offset, 0);
        }

        memory.SetUInt64(child, Module + Layout.ChildVtableRva);
        memory.SetUInt32(child + Layout.ChildModeOffset, mode);
        memory.SetSingle(child + Layout.ChildAngleOffset, electric ? 301.5f : 273.25f);
        memory.SetSingle(child + Layout.ChildBlurOffset, -0.25f);
        memory.SetUInt32(child + Layout.ChildSpeedDigitOneOffset, 3);
        memory.SetUInt32(child + Layout.ChildSpeedDigitTenOffset, 2);
        memory.SetUInt32(child + Layout.ChildSpeedDigitHundredOffset, 1);
        memory.SetByte(child + Layout.ChildSpeedLessOrEqualOneOffset, 0);
        memory.SetByte(child + Layout.ChildSpeedLessTenOffset, 0);
        memory.SetByte(child + Layout.ChildSpeedLessHundredOffset, 0);
        memory.SetUInt64(child + Layout.ChildSpeedUnitObjectOffset, SpeedUnitObject);
        memory.SetUInt32(SpeedUnitObject + Layout.SpeedUnitEnumOffset, Layout.SpeedUnitMphValue);
        memory.SetByte(child + Layout.ChildHeadlightsOnOffset, 1);
        memory.SetSingle(child + Layout.ChildPowerOffset, 0.77f);
        memory.SetSingle(child + Layout.ChildRegenOffset, 0.19f);
        memory.SetSingle(child + Layout.ChildRatioOffset, 0.42f);
        memory.SetUInt32(child + Layout.ChildGearOffset, 1);
        memory.SetUInt32(child + Layout.ChildGearNextOffset, 2);
        memory.SetUInt32(child + Layout.ChildGearPreviousOffset, 0);
        memory.SetUInt32(child + Layout.ChildGearGaugeStateOffset, uint.MaxValue);
        memory.SetByte(child + Layout.ChildUseDriveFor1Offset, 1);
        memory.SetSingle(child + Layout.ChildMaximumTachometerOffset, 9_500f);
        memory.SetSingle(child + Layout.ChildElectricMaximumSpeedOffset, 310f);
    }

    private sealed class Memory : INativeHudProcessMemory
    {
        private readonly Dictionary<ulong, byte> _bytes = [];
        private readonly Dictionary<ulong, int> _reads = [];

        public Action<ulong, int>? BeforeRead { get; set; }
        public ulong ModuleBase => Module;
        public int ReadCount { get; private set; }
        public int BlockReadCount { get; private set; }
        public int GaugeBlockReadCount { get; private set; }

        public void Remove(ulong address, int width)
        {
            for (var index = 0; index < width; index++)
            {
                _bytes.Remove(address + (ulong)index);
            }
        }

        public void ResetReadCounts()
        {
            ReadCount = 0;
            BlockReadCount = 0;
            GaugeBlockReadCount = 0;
            _reads.Clear();
        }

        public void SetByte(ulong address, byte value) => _bytes[address] = value;
        public void SetUInt32(ulong address, uint value) => Set(address, BitConverter.GetBytes(value));
        public void SetUInt64(ulong address, ulong value) => Set(address, BitConverter.GetBytes(value));
        public void SetSingle(ulong address, float value) => SetUInt32(address, BitConverter.SingleToUInt32Bits(value));

        public bool TryReadByte(ulong address, out byte value)
        {
            Observe(address);
            return _bytes.TryGetValue(address, out value);
        }

        public bool TryReadUInt32(ulong address, out uint value)
        {
            var success = Read(address, 4, out var bytes);
            value = success ? BitConverter.ToUInt32(bytes) : 0;
            return success;
        }

        public bool TryReadUInt64(ulong address, out ulong value)
        {
            var success = Read(address, 8, out var bytes);
            value = success ? BitConverter.ToUInt64(bytes) : 0;
            return success;
        }

        public bool TryReadSingle(ulong address, out float value)
        {
            var success = TryReadUInt32(address, out var bits);
            value = BitConverter.UInt32BitsToSingle(bits);
            return success;
        }

        public bool TryReadBytes(ulong address, Span<byte> destination)
        {
            Observe(address);
            BlockReadCount++;
            var gaugeStart = new[]
            {
                Layout.ChildModeOffset,
                Layout.ChildAngleOffset,
                Layout.ChildBlurOffset,
                Layout.ChildHeadlightsOnOffset,
                Layout.ChildPowerOffset,
                Layout.ChildRegenOffset,
                Layout.ChildRatioOffset,
                Layout.ChildMaximumTachometerOffset,
                Layout.ChildElectricMaximumSpeedOffset
            }.Min();
            if (address == Child + gaugeStart || address == ReplacementChild + gaugeStart)
            {
                GaugeBlockReadCount++;
            }
            for (var index = 0; index < destination.Length; index++)
            {
                _bytes.TryGetValue(address + (ulong)index, out destination[index]);
            }

            return true;
        }

        public void Dispose()
        {
        }

        private void Set(ulong address, byte[] bytes)
        {
            for (var index = 0; index < bytes.Length; index++)
            {
                _bytes[address + (ulong)index] = bytes[index];
            }
        }

        private bool Read(ulong address, int width, out byte[] bytes)
        {
            Observe(address);
            bytes = new byte[width];
            for (var index = 0; index < width; index++)
            {
                if (!_bytes.TryGetValue(address + (ulong)index, out bytes[index]))
                {
                    return false;
                }
            }

            return true;
        }

        private void Observe(ulong address)
        {
            ReadCount++;
            _reads.TryGetValue(address, out var count);
            _reads[address] = ++count;
            BeforeRead?.Invoke(address, count);
        }
    }
}
