using System.Buffers.Binary;
using System.Text.Json.Nodes;
using Wisp.Core;
using Xunit;

namespace Wisp.App.Tests;

public sealed class NativeShiftPerformanceReaderTests
{
    private const ulong Module = 0x140000000;
    private const ulong Source = 0x300000000;
    private const ulong Provider = 0x400000000;
    private const ulong SelectedWheel = 0x500000000;
    private const int Car = 3289;
    private static NativeHudCompatibilityPack Pack => NativeHudBuildContract.BuiltIn;

    [Fact]
    public void CalibrationConfigurationDoesNotRequireAReconstructedTorqueCurve()
    {
        var memory = ValidMemory();
        memory.Single(Provider + 0x654, 99);
        memory.UInt32(Provider + 0xA90, 1);
        var reader = new NativeShiftPerformanceReader(() => 100, 1000) { ConfigurationOnly = true };
        var result = Resolve(reader, memory);
        Assert.True(result.Available);
        Assert.Null(result.RuntimeCurve);
        Assert.Equal(new double[] { 4, 2, 1 }, result.ForwardRatios);
        Assert.NotEmpty(result.GearAccelerationFactors);
    }

    [Fact]
    public void CalibrationConfigurationStillRejectsChangingGearing()
    {
        var memory = ValidMemory();
        memory.BeforeBlockRead = (address, count) =>
        {
            if (address == Provider + 0xB68 && count == 2) memory.Single(address, 4);
        };
        var reader = new NativeShiftPerformanceReader(() => 100, 1000) { ConfigurationOnly = true };
        Assert.Equal(NativeShiftPerformanceStatus.UnstableData, Resolve(reader, memory).Status);
    }

    [Fact]
    public void RejectedCurveCapturesExistingBoundedRangesWithoutChangingReadSequence()
    {
        var ordinaryMemory = ValidMemory();
        ordinaryMemory.Single(Provider + 0x654, 99);
        var expected = Resolve(new(() => 100, 1000), ordinaryMemory);
        var captured = new List<NativeShiftRejectedEvidence>();
        var armedMemory = ValidMemory();
        armedMemory.Single(Provider + 0x654, 99);
        var actual = Resolve(new(() => 100, 1000, (_, evidence) => captured.Add(evidence)), armedMemory);
        Assert.Equal(expected.Status, actual.Status);
        Assert.Equal(NativeShiftPerformanceStatus.CurveMismatch, actual.Status);
        Assert.Equal(ordinaryMemory.BlockAddresses, armedMemory.BlockAddresses);
        var evidence = Assert.Single(captured);
        Assert.Equal("native-rejected-source", evidence.Kind);
        Assert.False(evidence.ValidatedConfiguration);
        Assert.Equal("consistent-double-read", evidence.ReadState);
        Assert.True(evidence.DoubleReadConsistent);
        Assert.True(evidence.FinalIdentityMatched);
        Assert.Equal(Car, evidence.CarOrdinal);
        Assert.Equal(Pack.ExecutableSha256, evidence.ExecutableSha256);
        Assert.Equal(9, evidence.Ranges.Length);
        Assert.Contains(evidence.Ranges, range => range.RelativeOffset == 0x25C && range.BytesHex.Length == 101 * 8);
        Assert.Contains(evidence.Ranges, range => range.RelativeOffset == 0xA90 && range.BytesHex.Length == 124 * 2);
        Assert.NotNull(evidence.Inertia);
        Assert.False(evidence.Inertia.ProvesLiveAcceleration);
    }

    [Fact]
    public void EarlyModifierRejectionCannotPretendPartialDataWasDoubleRead()
    {
        var captured = new List<NativeShiftRejectedEvidence>();
        var memory = ValidMemory();
        memory.UInt32(Provider + 0xA90, 2);
        var result = Resolve(new(() => 100, 1000, (_, evidence) => captured.Add(evidence)), memory);
        Assert.Equal(NativeShiftPerformanceStatus.UnsupportedModifiers, result.Status);
        var evidence = Assert.Single(captured);
        Assert.Equal("not-double-read", evidence.ReadState);
        Assert.Null(evidence.DoubleReadConsistent);
        Assert.Null(evidence.FinalIdentityMatched);
        Assert.Equal(7, evidence.Ranges.Length);
        Assert.DoesNotContain(evidence.Ranges, range => range.RelativeOffset == 0x25C);
        Assert.Equal("02000000", evidence.Ranges.Single(range => range.RelativeOffset == 0xA90).BytesHex[..8]);
    }

    [Fact]
    public void FailedRangeDoesNotExportZeroFilledOrPartialBytesAsObservedData()
    {
        var captured = new List<NativeShiftRejectedEvidence>();
        var memory = ValidMemory();
        memory.FailAddress = Provider + 0x234;
        Assert.Equal(NativeShiftPerformanceStatus.ReadFailure,
            Resolve(new(() => 100, 1000, (_, evidence) => captured.Add(evidence)), memory).Status);
        var evidence = Assert.Single(captured);
        Assert.Equal("incomplete-initial-read", evidence.ReadState);
        Assert.Equal(0x234ul, evidence.FailedRelativeOffset);
        Assert.Equal(0x20ul, Assert.Single(evidence.Ranges).RelativeOffset);
    }

    [Fact]
    public void InconsistentRepeatPreservesBothObservedVersionsOfThatRange()
    {
        var captured = new List<NativeShiftRejectedEvidence>();
        var memory = ValidMemory();
        memory.BeforeBlockRead = (address, count) =>
        {
            if (address == Provider + 0xB68 && count == 2) memory.Single(address, 4);
        };
        Assert.Equal(NativeShiftPerformanceStatus.UnstableData,
            Resolve(new(() => 100, 1000, (_, evidence) => captured.Add(evidence)), memory).Status);
        var evidence = Assert.Single(captured);
        Assert.Equal("unstable-double-read", evidence.ReadState);
        Assert.False(evidence.DoubleReadConsistent);
        Assert.Null(evidence.FinalIdentityMatched);
        Assert.Equal(0xB68ul, evidence.FailedRelativeOffset);
        Assert.NotEqual(evidence.Ranges.Single(range => range.RelativeOffset == 0xB68).BytesHex, evidence.RepeatedBytesHex);
    }

    [Fact]
    public void RepeatedRejectionsKeepBackoffAndProduceTimestampIndependentDeduplicationKeys()
    {
        long now = 100;
        var captured = new List<(string Key, NativeShiftRejectedEvidence Evidence)>();
        var reader = new NativeShiftPerformanceReader(() => now, 1000,
            (key, evidence) => captured.Add((key, evidence)));
        var memory = ValidMemory();
        memory.UInt32(Provider + 0xA90, 2);
        Resolve(reader, memory);
        now = 599;
        Resolve(reader, memory);
        Assert.Single(captured);
        now = 600;
        Resolve(reader, memory);
        Assert.Equal(2, captured.Count);
        Assert.Equal(captured[0].Key, captured[1].Key);
        Assert.StartsWith("rejected:", captured[0].Key);
        memory.UInt32(Provider + 0xA90, 3);
        now = 1100;
        Resolve(reader, memory);
        Assert.NotEqual(captured[1].Key, captured[2].Key);
    }

    [Fact]
    public void ValidConfigurationEmitsNoRejectedEvidence()
    {
        var captured = new List<NativeShiftRejectedEvidence>();
        Assert.True(Resolve(new(() => 100, 1000, (_, evidence) => captured.Add(evidence)), ValidMemory()).Available);
        Assert.Empty(captured);
    }

    [Fact]
    public void RawSourceGridIsReturnedInNmWithoutInventingLimiterOrFitting()
    {
        var memory = ValidMemory();
        var result = Resolve(new(() => 100, 1000), memory);

        Assert.True(result.Available);
        Assert.Equal(NativeShiftPerformanceStatus.Ready, result.Status);
        Assert.Equal(Car, result.CarOrdinal);
        Assert.Equal(100, result.ObservedTimestamp);
        Assert.Equal(100, result.StepRpm, 3);
        Assert.Equal(9500, result.ExactRedlineRpm, 2);
        Assert.Equal(101, result.TorqueNm.Count);
        Assert.Equal(600, result.TorqueNm[0], 4);
        Assert.Equal(800, result.TorqueNm[100], 4);
        Assert.Equal(new double[] { 4, 2, 1 }, result.ForwardRatios);
        Assert.Equal(3.5, result.FinalDrive);
        Assert.Equal(9750, result.ConfiguredOperatingCeilingRpm, 2);
        Assert.Equal(96, result.OfficialExportCount);
        Assert.False(result.HasModifiedExtension);
        Assert.Equal(64, result.Fingerprint.Length);
        Assert.Equal(3, result.GearAccelerationFactors.Count);
        Assert.True(result.GearAccelerationFactors[0] < result.GearAccelerationFactors[1]);
        Assert.True(result.GearAccelerationFactors[1] < result.GearAccelerationFactors[2]);
    }

    [Fact]
    public void SnapshotsCannotBeMutatedOrChangedByLaterTuneReads()
    {
        long time = 10;
        var reader = new NativeShiftPerformanceReader(() => time, 1000);
        var memory = ValidMemory();
        var first = Resolve(reader, memory);
        Assert.Throws<NotSupportedException>(() => ((IList<double>)first.TorqueNm)[0] = 1);
        Assert.Throws<NotSupportedException>(() => ((IList<double>)first.GearAccelerationFactors)[0] = 1);
        memory.Single(Provider + 0x25C, 9);
        SetConfiguredPeaks(memory);
        time += 500;
        var second = Resolve(reader, memory);
        Assert.Equal(600, first.TorqueNm[0]);
        Assert.Equal(900, second.TorqueNm[0]);
        Assert.NotEqual(first.Fingerprint, second.Fingerprint);
    }

    [Fact]
    public void SameCarFinalDriveAndCurveAreRefreshedAtFiveHundredMilliseconds()
    {
        long time = 0;
        var reader = new NativeShiftPerformanceReader(() => time, 1000);
        var memory = ValidMemory();
        var first = Resolve(reader, memory);
        var reads = memory.BlockReads;
        memory.Single(Provider + 0xB68, 3.7f);
        time = 499;
        Assert.Same(first, Resolve(reader, memory));
        Assert.Equal(reads, memory.BlockReads);
        time = 500;
        var second = Resolve(reader, memory);
        Assert.Equal(3.7, second.FinalDrive, 5);
        Assert.NotEqual(first.Fingerprint, second.Fingerprint);
        Assert.Equal(500, second.ObservedTimestamp);
    }

    [Fact]
    public void IdentityIsCheckedBeforeServingCachedData()
    {
        var reader = new NativeShiftPerformanceReader(() => 100, 1000);
        var memory = ValidMemory();
        Assert.True(Resolve(reader, memory).Available);
        memory.UInt32(Source + 0x740C, Car + 1);
        var changed = Resolve(reader, memory);
        Assert.Equal(NativeShiftPerformanceStatus.IdentityMismatch, changed.Status);
        Assert.Empty(changed.TorqueNm);
        memory.UInt32(Source + 0x740C, Car);
        memory.Single(Provider + 0xB68, 4);
        Assert.Equal(4, Resolve(reader, memory).FinalDrive);
    }

    [Fact]
    public void NewProcessMemoryAttachmentCannotReuseOldProviderCache()
    {
        var reader = new NativeShiftPerformanceReader(() => 100, 1000);
        Assert.True(Resolve(reader, ValidMemory()).Available);
        var replacement = ValidMemory();
        replacement.Single(Provider + 0xB68, 4);
        Assert.Equal(4, Resolve(reader, replacement).FinalDrive);
        Assert.True(replacement.BlockReads > 0);
    }

    [Fact]
    public void ReadFailureDiscardsLastGoodCurveAndBacksOffForFiveHundredMilliseconds()
    {
        long time = 100;
        var reader = new NativeShiftPerformanceReader(() => time, 1000);
        var memory = ValidMemory();
        Assert.True(Resolve(reader, memory).Available);
        time = 600;
        memory.FailAddress = Provider + 0x25C;
        var failure = Resolve(reader, memory);
        Assert.Equal(NativeShiftPerformanceStatus.ReadFailure, failure.Status);
        Assert.Empty(failure.TorqueNm);
        var failedReads = memory.BlockReads;
        memory.FailAddress = null;
        memory.Single(Provider + 0x25C, 9);
        SetConfiguredPeaks(memory);
        time = 1099;
        Assert.Same(failure, Resolve(reader, memory));
        Assert.Equal(failedReads, memory.BlockReads);
        Assert.Empty(Resolve(reader, memory).TorqueNm);
        time = 1100;
        Assert.Equal(900, Resolve(reader, memory).TorqueNm[0]);
    }

    [Fact]
    public void UnsupportedMetadataDoesNotRetryAtNativeSampleFrequency()
    {
        long time = 100;
        var reader = new NativeShiftPerformanceReader(() => time, 1000);
        var memory = ValidMemory();
        memory.Byte(Provider + 0xAA8, 1);
        var failure = Resolve(reader, memory);
        Assert.Equal(NativeShiftPerformanceStatus.UnsupportedModifiers, failure.Status);
        var reads = memory.BlockReads;
        for (var i = 0; i < 25; i++)
        {
            time += 16;
            Assert.Same(failure, Resolve(reader, memory));
        }
        Assert.Equal(reads, memory.BlockReads);
        memory.Byte(Provider + 0xAA8, 0);
        time = 600;
        Assert.True(Resolve(reader, memory).Available);
    }

    [Fact]
    public void NewIdentityAndExplicitResetBypassNegativeCacheImmediately()
    {
        var reader = new NativeShiftPerformanceReader(() => 100, 1000);
        var firstMemory = ValidMemory();
        firstMemory.Byte(Provider + 0xAA8, 1);
        Assert.False(Resolve(reader, firstMemory).Available);
        var secondMemory = ValidMemory();
        Assert.True(Resolve(reader, secondMemory).Available);
        reader.Reset();
        secondMemory.Byte(Provider + 0xAE0, 1);
        Assert.False(Resolve(reader, secondMemory).Available);
        secondMemory.Byte(Provider + 0xAE0, 0);
        reader.Reset();
        Assert.True(Resolve(reader, secondMemory).Available);
    }

    [Fact]
    public void IdentityFailureDuringBackoffClearsItBeforeRestoredIdentityCanBeReused()
    {
        var reader = new NativeShiftPerformanceReader(() => 100, 1000);
        var memory = ValidMemory();
        memory.Byte(Provider + 0xA90, 1);
        Assert.Equal(NativeShiftPerformanceStatus.UnsupportedModifiers, Resolve(reader, memory).Status);
        memory.UInt32(Source + 0x740C, Car + 1);
        Assert.Equal(NativeShiftPerformanceStatus.IdentityMismatch, Resolve(reader, memory).Status);
        memory.UInt32(Source + 0x740C, Car);
        memory.Byte(Provider + 0xA90, 0);
        Assert.True(Resolve(reader, memory).Available);
    }

    [Theory]
    [InlineData(0x20, (int)NativeShiftPerformanceStatus.UnsupportedTransmission)]
    [InlineData(0x21, (int)NativeShiftPerformanceStatus.UnsupportedTransmission)]
    [InlineData(0xA90, (int)NativeShiftPerformanceStatus.UnsupportedModifiers)]
    [InlineData(0xAA8, (int)NativeShiftPerformanceStatus.UnsupportedModifiers)]
    [InlineData(0xAE0, (int)NativeShiftPerformanceStatus.UnsupportedModifiers)]
    public void UnvalidatedModifierAndTransmissionBranchesFailClosed(int offset, int status)
    {
        var memory = ValidMemory();
        memory.Byte(Provider + (ulong)offset, 1);
        var result = Resolve(new(() => 100, 1000), memory);
        Assert.Equal((NativeShiftPerformanceStatus)status, result.Status);
        Assert.Empty(result.TorqueNm);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    public void OnlyDecodedCombustionSelectorFiveIsAccepted(uint selector)
    {
        var memory = ValidMemory();
        memory.UInt32(Provider + 0x234, selector);
        Assert.Equal(NativeShiftPerformanceStatus.UnsupportedPowertrain, Resolve(new(() => 100, 1000), memory).Status);
    }

    [Theory]
    [InlineData(0x258, 1)]
    [InlineData(0x258, 247)]
    [InlineData(0x258, uint.MaxValue)]
    [InlineData(0x24, 2)]
    [InlineData(0x24, 17)]
    public void CountBoundsPreventUnboundedOrOverlappingReads(int offset, uint value)
    {
        var memory = ValidMemory();
        memory.UInt32(Provider + (ulong)offset, value);
        Assert.Equal(NativeShiftPerformanceStatus.InvalidData, Resolve(new(() => 100, 1000), memory).Status);
        Assert.DoesNotContain(Provider + 0x25C, memory.BlockAddresses);
    }

    [Theory]
    [InlineData(0x254, float.NaN)]
    [InlineData(0x250, 0)]
    [InlineData(0x248, float.PositiveInfinity)]
    [InlineData(0x25C, float.NegativeInfinity)]
    [InlineData(0x25C, float.NaN)]
    [InlineData(0xB68, 0)]
    [InlineData(0x3C, -1)]
    public void InvalidNumericMetadataFailsClosed(int offset, float value)
    {
        var memory = ValidMemory();
        memory.Single(Provider + (ulong)offset, value);
        Assert.Equal(NativeShiftPerformanceStatus.InvalidData, Resolve(new(() => 100, 1000), memory).Status);
    }

    [Fact]
    public void NegativeTerminalSampleIsPreservedWithoutTreatingItAsScoringDomain()
    {
        var memory = ValidMemory();
        memory.Single(Provider + 0x25C + 100 * 4, -8.538f);
        var result = Resolve(new(() => 100, 1000), memory);
        Assert.True(result.Available);
        Assert.Equal(-853.8, result.TorqueNm[100], 3);
        Assert.Equal(9500, result.ExactRedlineRpm, 2);
    }

    [Fact]
    public void TornStaticCurveAndFinalDriveReadsAreRejected()
    {
        foreach (var offset in new ulong[] { 0x25C, 0xB68 })
        {
            var memory = ValidMemory();
            memory.BeforeBlockRead = (address, count) =>
            {
                if (address == Provider + offset && count == 2) memory.Single(address, 7);
            };
            Assert.Equal(NativeShiftPerformanceStatus.UnstableData, Resolve(new(() => 100, 1000), memory).Status);
        }
    }

    [Fact]
    public void ProviderSwapDuringReadInvalidatesTheEntireSnapshot()
    {
        var memory = ValidMemory();
        memory.BeforeBlockRead = (address, count) =>
        {
            if (address == Provider + 0xB68 && count == 2) memory.UInt64(Source + 0x7740, Provider + 0x10000);
        };
        Assert.Equal(NativeShiftPerformanceStatus.IdentityMismatch, Resolve(new(() => 100, 1000), memory).Status);
    }

    [Fact]
    public void UnsupportedFingerprintAndStoreNeverReadMemory()
    {
        using var stream = typeof(NativeHudBuildContract).Assembly.GetManifestResourceStream("Wisp.NativeCompatibility.BuiltIn.json")!;
        var node = JsonNode.Parse(stream)!.AsObject();
        node["executableSha256"] = new string('0', 64);
        var changed = NativeHudCompatibilityPack.Parse(System.Text.Encoding.UTF8.GetBytes(node.ToJsonString()));
        foreach (var pack in new[] { changed, NativeHudBuildContract.StoreBuiltIn })
        {
            var memory = ValidMemory();
            var result = new NativeShiftPerformanceReader().Resolve(memory, Module, Source, Provider, Car, pack);
            Assert.Equal(NativeShiftPerformanceStatus.UnsupportedBuild, result.Status);
            Assert.Equal(0, memory.Reads);
        }
    }

    [Fact]
    public void ChangedVtableContractIsRejectedEvenWithMatchingExecutableHash()
    {
        var memory = ValidMemory();
        var slot = Pack.RequiredVtableSlots.First();
        memory.UInt64(Module + Pack.LeadVtableRva + slot.Key, Module + slot.Value + 1);
        Assert.Equal(NativeShiftPerformanceStatus.IdentityMismatch, Resolve(new(() => 100, 1000), memory).Status);
    }

    [Fact]
    public void SlowReadCannotBePublishedAsFreshMetadata()
    {
        var timestamps = new Queue<long>([100, 600]);
        Assert.Equal(NativeShiftPerformanceStatus.UnstableData,
            Resolve(new(() => timestamps.Dequeue(), 1000), ValidMemory()).Status);
    }

    [Theory]
    [InlineData("turbo-2974", true)]
    [InlineData("turbo-3289", true)]
    [InlineData("na-3289", false)]
    [InlineData("na-1335", false)]
    public void ArchivedCurrentConfiguredCurvesMatchEveryFloatAndNativePeakWithoutCalibration(string fixture, bool modified)
    {
        var node = Fixture(fixture);
        var memory = FixtureMemory(node);
        var result = Resolve(new(() => 100, 1000), memory);
        Assert.True(result.Available, result.Status.ToString());
        Assert.Equal(modified, result.HasModifiers);
        Assert.Equal(modified, result.HasModifiedExtension);
        Assert.Equal(node["expected_export_count"]!.GetValue<int>(), result.OfficialExportCount);
        var expected = node["expected_full_modified"]!.AsArray();
        Assert.Equal(expected.Count, result.TorqueNm.Count);
        for (var i = 0; i < expected.Count; i++)
            Assert.Equal(BitConverter.SingleToUInt32Bits(expected[i]!.GetValue<float>()),
                BitConverter.SingleToUInt32Bits((float)(result.TorqueNm[i] / 100)));
        var exportedTorque = result.TorqueNm.Take(result.OfficialExportCount).Select(value => (float)(value / 100)).ToArray();
        var step = node["step"]!.GetValue<float>();
        Assert.Equal(node["configured_peak_torque"]!.GetValue<float>(), exportedTorque.Max());
        Assert.Equal(node["configured_peak_power"]!.GetValue<float>(),
            exportedTorque.Select((value, i) => value * (i * step)).Max());
        Assert.Equal(node["configured_peak_torque"]!.GetValue<float>() * 100d, result.ConfiguredPeakTorqueNm);
        Assert.Equal(node["configured_peak_power"]!.GetValue<float>() * 100d, result.ConfiguredPeakPowerWatts);

        // These are archived model crossovers, not a racing-timing assertion.
        // Using the separately named candidate demonstrates the source curve is
        // retained beyond redline; the resolver chooses its own operating policy.
        var profile = new AccelerationShiftProfile(result.TorqueNm.Select((value, i) =>
            new AccelerationShiftSample(i * result.StepRpm, value)), result.ForwardRatios);
        var crossovers = node["expected_crossovers"]!.AsArray();
        for (var i = 0; i < crossovers.Count; i++)
        {
            var solved = AccelerationShiftSolver.Solve(profile, i + 1, 2000, result.ConfiguredOperatingCeilingRpm);
            if (crossovers[i] is null) Assert.False(solved.HasEstimatedTarget);
            else Assert.InRange(Math.Abs(solved.EstimatedTargetRpm!.Value - crossovers[i]!.GetValue<double>()), 0, .01);
        }
        Assert.Equal(result.ForwardRatios.Count, result.NativeAdjustedUpperRpm.Count);
        Assert.Throws<NotSupportedException>(() => ((IList<double>)result.NativeAdjustedUpperRpm)[0] = 1);
        Assert.Throws<NotSupportedException>(() => ((IList<double>)result.NativeBaselineUpperRpm)[0] = 1);

        // Exercise the actual resolver policy, not only a solver assembled by
        // the test. Test1 incorrectly truncated all of these cars at redline.
        var resolved = NativeHudMemoryResolver.CreateShiftPerformance(result);
        Assert.NotNull(resolved.Metadata);
        Assert.Equal(modified ? "configured-export" : "runtime-full-control-equilibrium", resolved.Metadata.TorqueModel);
        Assert.Equal(result.ConfiguredOperatingCeilingRpm, resolved.Metadata.ConfiguredOperatingCeilingRpm);
        Assert.Equal(result.ConfiguredOperatingCeilingRpm, resolved.Profile!.VerifiedOperatingCeilingRpm);
        for (var i = 0; i < crossovers.Count; i++)
        {
            var solved = resolved.Gears[i];
            if (crossovers[i] is null)
            {
                Assert.Equal(AccelerationShiftStatus.VerifiedLimitBound, solved.Status);
                Assert.Equal(result.ConfiguredOperatingCeilingRpm, solved.EstimatedTargetRpm);
            }
            else Assert.InRange(Math.Abs(solved.EstimatedTargetRpm!.Value - crossovers[i]!.GetValue<double>()), 0, .01);
        }
    }

    [Theory]
    [InlineData(0x654)]
    [InlineData(0x664)]
    public void ConfiguredPeakMismatchWithdrawsCurveInsteadOfFittingIt(int offset)
    {
        var memory = FixtureMemory(Fixture("turbo-2974"));
        memory.Single(Provider + (ulong)offset, 1);
        var result = Resolve(new(() => 100, 1000), memory);
        Assert.Equal(NativeShiftPerformanceStatus.CurveMismatch, result.Status);
        Assert.Empty(result.TorqueNm);
    }

    [Theory]
    [InlineData(0xAB8, float.NaN)]
    [InlineData(0xAC4, -1)]
    [InlineData(0xAD8, float.PositiveInfinity)]
    public void MalformedActiveBoostCoefficientsFailClosed(int offset, float value)
    {
        var memory = FixtureMemory(Fixture("turbo-2974"));
        memory.Single(Provider + (ulong)offset, value);
        Assert.Equal(NativeShiftPerformanceStatus.UnsupportedModifiers, Resolve(new(() => 100, 1000), memory).Status);
    }

    [Theory]
    [InlineData(0x648)]
    [InlineData(0x654)]
    [InlineData(0x664)]
    public void NewLimitAndPeakMetadataMustBeStableAcrossBothReads(int offset)
    {
        var memory = ValidMemory();
        memory.BeforeBlockRead = (address, count) =>
        {
            if (address == Provider + (ulong)offset && count == 2) memory.Single(address, 7);
        };
        Assert.Equal(NativeShiftPerformanceStatus.UnstableData, Resolve(new(() => 100, 1000), memory).Status);
    }

    [Fact]
    public void InertiaFactorsMatchTheUnreducedNativeEstimatorAtSeveralSpeeds()
    {
        var memory = ValidMemory();
        var result = Resolve(new(() => 100, 1000), memory);
        Assert.True(result.Available);
        foreach (var speed in new double[] { 1, 10, 50 })
        {
            for (var i = 0; i < result.ForwardRatios.Count; i++)
            {
                var ratio = result.ForwardRatios[i];
                var omega = speed * ratio * result.FinalDrive / .5;
                var reflected = 2 * .125 * Math.Pow(speed / (.5 * omega), 2) +
                    2 * .25 * Math.Pow(speed / (.5 * omega), 2) + (.125 + .0625) / (ratio * ratio) + .03125;
                var expected = 20 * speed * speed / (20 * speed * speed + reflected * omega * omega);
                Assert.Equal(expected, result.GearAccelerationFactors[i], 12);
            }
        }
    }

    [Fact]
    public void SameCarInertiaAndSelectedRadiusRefreshTheModelWithoutARecalibrationRun()
    {
        long now = 100;
        var memory = ValidMemory();
        var reader = new NativeShiftPerformanceReader(() => now, 1000);
        var original = Resolve(reader, memory);
        memory.Single(Provider - 0x568, 21);
        now += 500;
        var changedMass = Resolve(reader, memory);
        Assert.NotEqual(original.Fingerprint, changedMass.Fingerprint);
        Assert.True(changedMass.GearAccelerationFactors[0] > original.GearAccelerationFactors[0]);
        memory.Single(SelectedWheel + 0x5AC, .625f);
        now += 500;
        var changedRadius = Resolve(reader, memory);
        Assert.NotEqual(changedMass.Fingerprint, changedRadius.Fingerprint);
        Assert.True(changedRadius.GearAccelerationFactors[0] > changedMass.GearAccelerationFactors[0]);
    }

    [Theory]
    [InlineData(-0x568, 0)]
    [InlineData(-0x568, float.NaN)]
    [InlineData(0x2BA0, -1)]
    [InlineData(0x4120, float.PositiveInfinity)]
    [InlineData(0x2ADC, 0)]
    [InlineData(0x405C, -1)]
    [InlineData(0x12C, -1)]
    [InlineData(0x170, -1)]
    [InlineData(0x23C, -1)]
    public void InvalidRequiredInertiaCannotFallBackToUnitFactors(int offset, float value)
    {
        var memory = ValidMemory();
        memory.Single((ulong)((long)Provider + offset), value);
        var result = Resolve(new(() => 100, 1000), memory);
        Assert.Equal(NativeShiftPerformanceStatus.InvalidData, result.Status);
        Assert.Empty(result.GearAccelerationFactors);
        Assert.Empty(result.TorqueNm);
    }

    [Theory]
    [InlineData(-0x568)]
    [InlineData(0x2BA0)]
    [InlineData(0x4120)]
    [InlineData(0x2ADC)]
    [InlineData(0x405C)]
    [InlineData(0x12C)]
    [InlineData(0x170)]
    [InlineData(0x23C)]
    [InlineData(0xBA0)]
    public void MissingInertiaOrSelectedWheelReadFailsClosed(int offset)
    {
        var memory = ValidMemory();
        memory.FailAddress = (ulong)((long)Provider + offset);
        Assert.Equal(NativeShiftPerformanceStatus.ReadFailure, Resolve(new(() => 100, 1000), memory).Status);
    }

    [Theory]
    [InlineData(-0x568)]
    [InlineData(0x2BA0)]
    [InlineData(0x4120)]
    [InlineData(0x2ADC)]
    [InlineData(0x405C)]
    [InlineData(0x12C)]
    [InlineData(0x170)]
    [InlineData(0x23C)]
    public void InertiaMustBeStableAcrossTheConfigurationRead(int offset)
    {
        var memory = ValidMemory();
        var field = (ulong)((long)Provider + offset);
        memory.BeforeBlockRead = (address, count) =>
        {
            if (address == field && count == 2) memory.Single(address, 7);
        };
        Assert.Equal(NativeShiftPerformanceStatus.UnstableData, Resolve(new(() => 100, 1000), memory).Status);
    }

    [Fact]
    public void SelectedWheelIdentityAndRadiusMustRemainStable()
    {
        foreach (var changePointer in new[] { true, false })
        {
            var memory = ValidMemory();
            memory.Single(SelectedWheel + 0x10000 + 0x5AC, .5f);
            memory.BeforeBlockRead = (address, count) =>
            {
                if (address != Provider + 0x20 || count != 2) return;
                if (changePointer) memory.UInt64(Provider + 0xBA0, SelectedWheel + 0x10000);
                else memory.Single(SelectedWheel + 0x5AC, .625f);
            };
            Assert.Equal(NativeShiftPerformanceStatus.UnstableData, Resolve(new(() => 100, 1000), memory).Status);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void InvalidSelectedRadiusCannotPublishAProfile(float value)
    {
        var memory = ValidMemory();
        memory.Single(SelectedWheel + 0x5AC, value);
        Assert.Equal(NativeShiftPerformanceStatus.InvalidData, Resolve(new(() => 100, 1000), memory).Status);
    }

    [Fact]
    public void RpmAndAngularWindowModifiersAreAdditiveAndKeepTheStoredTailSeparate()
    {
        var modifiers = new byte[124];
        void Float(int offset, float value) => BinaryPrimitives.WriteInt32LittleEndian(
            modifiers.AsSpan(offset, 4), BitConverter.SingleToInt32Bits(value));
        BinaryPrimitives.WriteUInt32LittleEndian(modifiers.AsSpan(0, 4), 1);
        Float(4, 100); Float(8, 500); Float(12, 1.5f);
        BinaryPrimitives.WriteUInt32LittleEndian(modifiers.AsSpan(0x50, 4), 1);
        Float(0x58, 1); Float(0x5C, 2);
        Float(0x6C, 1000); Float(0x70, 1500); Float(0x74, 1); Float(0x78, .75f);
        Assert.True(NativeCombustionCurve.TryCreate(Enumerable.Repeat(10f, 7).ToArray(),
            100, .01f, 500, modifiers, out var curve));
        Assert.Equal(6, curve!.ExportCount);
        Assert.Equal(7, curve.Samples.Length);
        Assert.Equal(19, curve.Samples[2], 4); // 10 * (1.5 + 1 + 1.4 - 2), not 10 * 1.5 * 1.4.
        Assert.Equal(20, curve.Samples[6], 4); // Above-redline RPM factor saturates at its configured endpoint.
        Assert.True(curve.HasModifiers);
    }

    [Fact]
    public void RuntimeRpmCurveKeepsIndependentExportPeakCheckAndReachesResolver()
    {
        var memory = ValidMemory();
        ConfigureRuntimeRpmModifier(memory);
        var result = Resolve(new(() => 100, 1000), memory);
        Assert.True(result.Available, result.Status.ToString());
        Assert.NotNull(result.RuntimeCurve);
        Assert.Equal(600, result.TorqueNm[0]); // Configured export remains intact.
        Assert.True(result.RuntimeCurve.TryEvaluate(0, out var runtime));
        Assert.Equal(8.25f, runtime); // Runtime normalization starts above its low endpoint.
        var resolved = NativeHudMemoryResolver.CreateShiftPerformance(result);
        Assert.Equal("runtime-full-control-equilibrium", resolved.Metadata!.TorqueModel);
        Assert.Equal(825, resolved.Profile!.Samples[0].Torque);
        memory.Single(Provider + 0x654, (float)(result.ConfiguredPeakTorqueNm / 100 + 1));
        Assert.Equal(NativeShiftPerformanceStatus.CurveMismatch, Resolve(new(() => 100, 1000), memory).Status);
    }

    [Fact]
    public void InvalidNewlyConsumedRpmCoordinatesCannotFallBackToExporter()
    {
        var memory = ValidMemory();
        ConfigureRuntimeRpmModifier(memory);
        memory.Single(Provider + 0xAF8, 2); // Zero-width runtime normalization, unused by export.
        Assert.Equal(NativeShiftPerformanceStatus.UnsupportedModifiers, Resolve(new(() => 100, 1000), memory).Status);
    }

    private static void ConfigureRuntimeRpmModifier(Memory memory)
    {
        memory.UInt32(Provider + 0xAE0, 1);
        foreach (var (offset, value) in new (ulong, float)[]
        {
            (0xAE8, 1), (0xAEC, 2), (0xAF0, 2), (0xAF4, 5), (0xAF8, 10),
            (0xAFC, 2000), (0xB00, 3000), (0xB04, 1), (0xB08, 1)
        }) memory.Single(Provider + offset, value);
        var raw = Enumerable.Range(0, 101).Select(i => 6 + i / 50f).ToArray();
        memory.TryReadSingle(Provider + 0x248, out var redline);
        memory.TryReadSingle(Provider + 0x250, out var inverse);
        memory.TryReadSingle(Provider + 0x254, out var step);
        var modifiers = new byte[124];
        Assert.True(memory.TryReadBytes(Provider + 0xA90, modifiers));
        Assert.True(NativeCombustionCurve.TryCreate(raw, step, inverse, redline, modifiers, out var exported));
        memory.Single(Provider + 0x654, exported!.ExportPeakTorque);
        memory.Single(Provider + 0x664, exported.ExportPeakPower);
    }

    [Theory]
    [InlineData(0xA90)]
    [InlineData(0xAA8)]
    [InlineData(0xAE0)]
    public void UnreviewedModifierFlagValuesDoNotSelectAnUnknownBranch(int offset)
    {
        var memory = ValidMemory();
        memory.UInt32(Provider + (ulong)offset, 2);
        Assert.Equal(NativeShiftPerformanceStatus.UnsupportedModifiers, Resolve(new(() => 100, 1000), memory).Status);
    }

    private static NativeShiftPerformanceSnapshot Resolve(NativeShiftPerformanceReader reader, Memory memory) =>
        reader.Resolve(memory, Module, Source, Provider, Car, Pack);

    private static Memory ValidMemory()
    {
        var memory = new Memory();
        memory.Zero(Provider, 0xB6C);
        memory.UInt64(Source + 0x7740, Provider);
        memory.UInt32(Source + 0x740C, Car);
        memory.UInt64(Provider, Module + Pack.LeadVtableRva);
        foreach (var slot in Pack.RequiredVtableSlots) memory.UInt64(Module + Pack.LeadVtableRva + slot.Key, Module + slot.Value);
        memory.UInt32(Provider + 0x234, 5);
        memory.UInt32(Provider + 0x258, 101);
        memory.Single(Provider + 0x248, (float)(9500 * Math.PI / 30));
        memory.Single(Provider + 0x24C, (float)(10000 * Math.PI / 30));
        memory.Single(Provider + 0x250, (float)(30 / (100 * Math.PI)));
        memory.Single(Provider + 0x254, (float)(100 * Math.PI / 30));
        for (var i = 0; i < 101; i++) memory.Single(Provider + 0x25C + (ulong)i * 4, 6 + i / 50f);
        memory.UInt32(Provider + 0x24, 4);
        foreach (var (ratio, index) in new float[] { -3, 4, 2, 1 }.Select((ratio, index) => (ratio, index)))
        {
            memory.Single(Provider + 0x28 + (ulong)index * 20, ratio);
            memory.Single(Provider + 0x34 + (ulong)index * 20, (float)(9625 * Math.PI / 30));
            memory.Single(Provider + 0x38 + (ulong)index * 20, (float)(9625 * Math.PI / 30));
        }
        memory.Single(Provider + 0xB68, 3.5f);
        memory.Single(Provider - 0x568, 20);
        memory.Single(Provider + 0x2BA0, .125f);
        memory.Single(Provider + 0x4120, .25f);
        memory.Single(Provider + 0x2ADC, .5f);
        memory.Single(Provider + 0x405C, .5f);
        memory.Single(Provider + 0x12C, .125f);
        memory.Single(Provider + 0x170, .0625f);
        memory.Single(Provider + 0x23C, .03125f);
        memory.UInt64(Provider + 0xBA0, SelectedWheel);
        memory.Single(SelectedWheel + 0x5AC, .5f);
        memory.Single(Provider + 0x648, (float)(9750 * Math.PI / 30));
        SetConfiguredPeaks(memory);
        return memory;
    }

    private static void SetConfiguredPeaks(Memory memory)
    {
        memory.TryReadSingle(Provider + 0x248, out var redline);
        memory.TryReadSingle(Provider + 0x250, out var inverse);
        memory.TryReadSingle(Provider + 0x254, out var step);
        memory.TryReadUInt32(Provider + 0x258, out var stored);
        var count = Math.Min((int)stored, (int)(redline * inverse + .5f) + 1);
        var peakTorque = 0f;
        var peakPower = 0f;
        for (var i = 0; i < count; i++)
        {
            memory.TryReadSingle(Provider + 0x25C + (ulong)i * 4, out var torque);
            peakTorque = Math.Max(peakTorque, torque);
            peakPower = Math.Max(peakPower, torque * (i * step));
        }
        memory.Single(Provider + 0x654, peakTorque);
        memory.Single(Provider + 0x664, peakPower);
        memory.Reads = 0;
    }

    private static JsonObject Fixture(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "src", "Wisp.App", "Wisp.App.csproj")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return JsonNode.Parse(File.ReadAllText(Path.Combine(directory.FullName,
            "tests", "Wisp.App.Tests", "Fixtures", "ShiftCue", name + ".json")))!.AsObject();
    }

    private static Memory FixtureMemory(JsonObject node)
    {
        var memory = ValidMemory();
        // These archived fixtures contain torque-curve data only. A zero engine
        // inertia gives all gears the same factor and preserves that independent
        // curve regression; real inertia is covered by the equation tests above.
        memory.Single(Provider + 0x23C, 0);
        foreach (var (name, offset) in new[] { ("redline", 0x248), ("maximum", 0x24C),
            ("inverse_step", 0x250), ("step", 0x254), ("configured_peak_torque", 0x654),
            ("configured_peak_power", 0x664), ("ceiling_candidate", 0x648), ("final_drive", 0xB68) })
            memory.Single(Provider + (ulong)offset, node[name]!.GetValue<float>());
        var raw = node["raw"]!.AsArray();
        memory.UInt32(Provider + 0x258, (uint)raw.Count);
        for (var i = 0; i < raw.Count; i++) memory.Single(Provider + 0x25C + (ulong)i * 4, raw[i]!.GetValue<float>());
        var modifiers = node["modifier_bits"]!.AsArray();
        for (var i = 0; i < modifiers.Count; i++) memory.UInt32(Provider + 0xA90 + (ulong)i * 4, modifiers[i]!.GetValue<uint>());
        var gears = node["gears"]!.AsArray();
        memory.UInt32(Provider + 0x24, (uint)gears.Count);
        for (var i = 0; i < gears.Count; i++)
            for (var j = 0; j < 5; j++)
                memory.Single(Provider + 0x28 + (ulong)i * 20 + (ulong)j * 4, gears[i]![j]!.GetValue<float>());
        return memory;
    }

    private sealed class Memory : IReadOnlyProcessMemory
    {
        private readonly Dictionary<ulong, byte> _bytes = [];
        private readonly Dictionary<ulong, int> _blockCounts = [];
        internal ulong? FailAddress;
        internal Action<ulong, int>? BeforeBlockRead;
        internal int Reads, BlockReads;
        internal List<ulong> BlockAddresses { get; } = [];
        internal void Zero(ulong address, int length) { for (var i = 0; i < length; i++) _bytes[address + (ulong)i] = 0; }
        internal void Byte(ulong address, byte value) => _bytes[address] = value;
        internal void UInt32(ulong address, uint value) { Span<byte> bytes = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(bytes, value); Write(address, bytes); }
        internal void UInt64(ulong address, ulong value) { Span<byte> bytes = stackalloc byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(bytes, value); Write(address, bytes); }
        internal void Single(ulong address, float value) => UInt32(address, BitConverter.SingleToUInt32Bits(value));
        private void Write(ulong address, ReadOnlySpan<byte> bytes) { for (var i = 0; i < bytes.Length; i++) _bytes[address + (ulong)i] = bytes[i]; }
        private bool Read(ulong address, Span<byte> bytes)
        {
            Reads++;
            if (FailAddress == address) return false;
            for (var i = 0; i < bytes.Length; i++) if (!_bytes.TryGetValue(address + (ulong)i, out bytes[i])) return false;
            return true;
        }
        public bool TryReadBytes(ulong address, Span<byte> destination)
        {
            BlockReads++;
            BlockAddresses.Add(address);
            var count = _blockCounts.GetValueOrDefault(address) + 1;
            _blockCounts[address] = count;
            BeforeBlockRead?.Invoke(address, count);
            return Read(address, destination);
        }
        public bool TryReadByte(ulong address, out byte value) { Reads++; return _bytes.TryGetValue(address, out value); }
        public bool TryReadUInt32(ulong address, out uint value) { Span<byte> bytes = stackalloc byte[4]; var ok = Read(address, bytes); value = ok ? BinaryPrimitives.ReadUInt32LittleEndian(bytes) : 0; return ok; }
        public bool TryReadUInt64(ulong address, out ulong value) { Span<byte> bytes = stackalloc byte[8]; var ok = Read(address, bytes); value = ok ? BinaryPrimitives.ReadUInt64LittleEndian(bytes) : 0; return ok; }
        public bool TryReadSingle(ulong address, out float value) { var ok = TryReadUInt32(address, out var bits); value = BitConverter.UInt32BitsToSingle(bits); return ok; }
    }
}
