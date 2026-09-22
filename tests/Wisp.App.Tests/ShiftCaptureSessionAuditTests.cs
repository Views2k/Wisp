using System.Text.Json;
using Xunit;

namespace Wisp.App.Tests;

public sealed class ShiftCaptureSessionAuditTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void EmptyAuditReportsAbsentChannelsWithoutInventingTimingOrAccuracy()
    {
        var audit = new ShiftCaptureSessionAudit(1000);
        var result = audit.Snapshot();
        Assert.Equal(0, result.Records);
        Assert.Null(result.InputEdgeBracketMilliseconds.Maximum);
        Assert.Equal(6, result.AbsentRecordedChannels.Length);
        Assert.Contains("No universal optimal shift accuracy", audit.ToPlainText(), StringComparison.Ordinal);
        Assert.Contains("not tune-specific", result.GearGroupingMeaning, StringComparison.Ordinal);
    }

    [Fact]
    public void ChangedRawPacketsAtEqualGameTimeAreCountedAndNotDiscarded()
    {
        var audit = new ShiftCaptureSessionAudit(1000);
        Telemetry(audit, 100, 40, 6000, "AA==");
        Telemetry(audit, 108, 40, 6040, "AQ==");
        Telemetry(audit, 116, 40, 6040, "AQ==");
        var result = audit.Snapshot();
        Assert.Equal(3, result.RawPackets);
        Assert.Equal(2, result.EqualGameTimestamps);
        Assert.Equal(1, result.EqualGameTimestampsWithChangedRawPackets);
        Assert.Equal(3, Assert.Single(result.Gears).TelemetrySamples);
        Assert.Equal(6040, Assert.Single(result.Gears).MaximumRecordedRpm);
    }

    [Fact]
    public void RecordedTest9RaceOffPacketsRemainCountedWithoutJoiningDrivingEvidence()
    {
        var audit = new ShiftCaptureSessionAudit(1000);
        Telemetry(audit, 100, 40, 6000);
        // Test9 recorded 1,217 valid packets with this race/car/gear combination.
        Feed(audit, "telemetry", new
        {
            parsed = true,
            rawBase64 = "AA==",
            state = new { carOrdinal = 0, gear = -1, gameTimestampMilliseconds = 40, engineRpm = 0, isRaceOn = false }
        }, 108);
        Telemetry(audit, 116, 40, 6100, "AQ==");
        var result = audit.Snapshot();
        Assert.Equal(3, result.RawPackets);
        Assert.Equal(1, result.InactiveTelemetrySamples);
        Assert.Equal(0, result.MalformedAuditRecords);
        Assert.Equal(0, result.EqualGameTimestamps);
        Assert.Equal(2, Assert.Single(result.Gears).TelemetrySamples);
        Assert.Contains("Valid race-off telemetry packets: 1", audit.ToPlainText(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(null)]
    public void MissingCarIsNotAcceptedWithoutExplicitRaceOffEvidence(bool? raceOn)
    {
        var audit = new ShiftCaptureSessionAudit(1000);
        Feed(audit, "telemetry", new
        {
            parsed = true,
            state = new { carOrdinal = 0, gear = -1, gameTimestampMilliseconds = 40, engineRpm = 0, isRaceOn = raceOn }
        });
        Assert.Equal(1, audit.Snapshot().MalformedAuditRecords);
        Assert.Equal(0, audit.Snapshot().InactiveTelemetrySamples);
        Assert.Empty(audit.Snapshot().Gears);
    }

    [Fact]
    public void HistoricalCanaryResultOverridesLegacyFinalLatchWithoutQualifyingLaterContexts()
    {
        var audit = new ShiftCaptureSessionAudit(1000);
        Feed(audit, "canary_result", new { qualifies = true });
        Feed(audit, "capture_summary", new { pixelCanaryPassed = false });
        var result = audit.Snapshot();
        Assert.Equal(1, result.PassedPixelChecks);
        Assert.Equal(1, result.CompletedPixelChecks);
        Assert.Null(result.PixelContextQualifiedAtStop);
        Assert.Contains("Parked pixel checks: 1 passed out of 1", audit.ToPlainText(), StringComparison.Ordinal);
    }

    [Fact]
    public void NewSummaryKeepsHistoricalSuccessAndLaterQualificationGapDistinct()
    {
        var audit = new ShiftCaptureSessionAudit(1000);
        Feed(audit, "canary_result", new { qualifies = true });
        Feed(audit, "canary_result", new { qualifies = false });
        Feed(audit, "capture_summary", new
        {
            pixelCanaryPassed = true,
            pixelContextQualifiedAtStop = false,
            pixelQualificationLostDuringSession = true
        });
        var result = audit.Snapshot();
        Assert.Equal(1, result.PassedPixelChecks);
        Assert.Equal(2, result.CompletedPixelChecks);
        Assert.False(result.PixelContextQualifiedAtStop);
        Assert.True(result.PixelQualificationLostDuringSession);
        Assert.Contains("Pixel context at stop: unqualified", audit.ToPlainText(), StringComparison.Ordinal);
        Assert.Contains("earlier successful checks remain recorded", audit.ToPlainText(), StringComparison.Ordinal);
    }

    [Fact]
    public void GameClockWrapAndRegressionAreDifferentFromLocalQpcProgress()
    {
        var audit = new ShiftCaptureSessionAudit(1000);
        Telemetry(audit, 100, uint.MaxValue - 3, 6000);
        Telemetry(audit, 108, 4, 6010);
        Telemetry(audit, 116, 3, 6020);
        var result = audit.Snapshot();
        Assert.Equal(1, result.GameClockWraps);
        Assert.Equal(1, result.GameClockRegressions);
        Assert.Equal(0, result.Channels["telemetry"].QpcRegressions);
    }

    [Fact]
    public void CrossChannelArrivalOrderDoesNotCreateClockFailure()
    {
        var audit = new ShiftCaptureSessionAudit(1000);
        Feed(audit, "one", new { }, 120);
        Feed(audit, "two", new { }, 100);
        Feed(audit, "one", new { }, 130);
        Feed(audit, "one", new { }, 130);
        Feed(audit, "one", new { }, 129);
        var result = audit.Snapshot();
        Assert.Equal(1, result.Channels["one"].EqualQpc);
        Assert.Equal(1, result.Channels["one"].QpcRegressions);
        Assert.Equal(0, result.Channels["two"].QpcRegressions);
        Assert.Equal(10, result.Channels["one"].IntervalMilliseconds.Maximum);
    }

    [Fact]
    public void GapSummaryUsesFrequencyAndDoesNotTreatEventCountsAsFrameRate()
    {
        var audit = new ShiftCaptureSessionAudit(10_000);
        Feed(audit, "cue_evaluation", new { }, 1000);
        Feed(audit, "cue_evaluation", new { }, 1080);
        Feed(audit, "cue_evaluation", new { }, 3080);
        var result = audit.Snapshot().Channels["cue_evaluation"];
        Assert.Equal(3, result.Samples);
        Assert.Equal(8, result.IntervalMilliseconds.Minimum);
        Assert.Equal(200, result.IntervalMilliseconds.Maximum);
        Assert.Equal(104, result.IntervalMilliseconds.Mean);
        Assert.Equal(1, result.GapsOver100Milliseconds);
    }

    [Fact]
    public void RecordedTest4SecondGearCutRemainsVisibleWithoutFabricatedRed()
    {
        // Sanitized Test4 packet values from archive aaeec45fffd503228645bd4cfd33d9caa0851b5e51abaffdb27a1a951efb7031.
        // QPC zero is translated; the recorded 10.5374 ms interval is unchanged.
        var audit = new ShiftCaptureSessionAudit(10_000_000);
        Feed(audit, "telemetry", new
        {
            parsed = true,
            state = new
            {
                carOrdinal = 3289,
                gear = 2,
                gameTimestampMilliseconds = 447605703,
                engineRpm = 9746.728,
                torqueNm = 640.1619,
                powerWatts = 653154.4,
                accelerator = 255,
                brake = 0,
                groundSpeedMetersPerSecond = 42.351635,
                isRaceOn = true
            }
        }, 1_000_000);
        Feed(audit, "cue_evaluation", new
        {
            carOrdinal = 3289,
            gear = 2,
            engineRpm = 9746.728,
            cue = new { enabled = true, stage = 2, flashOn = true, targetRpm = 9749.995009796077 }
        }, 1_010_000);
        Feed(audit, "telemetry", new
        {
            parsed = true,
            state = new
            {
                carOrdinal = 3289,
                gear = 2,
                gameTimestampMilliseconds = 447605718,
                engineRpm = 9615.129,
                torqueNm = -532.59937,
                powerWatts = -538365.1,
                accelerator = 255,
                brake = 0,
                groundSpeedMetersPerSecond = 42.401794,
                isRaceOn = true
            }
        }, 1_105_374);
        var gear = Assert.Single(audit.Snapshot().Gears);
        Assert.Equal(9746.728, gear.MaximumRecordedRpm);
        Assert.Equal(9749.995009796077, gear.MinimumTargetRpm);
        Assert.Equal(1, gear.FullThrottleOutputCutCandidates);
        Assert.Equal(0, gear.RedRequestedSamples);
        Assert.Equal(0, gear.RedOnSubmissions);
    }

    [Fact]
    public void RedRequestsAndSuccessfulRedOnSubmissionsRemainSeparate()
    {
        var audit = new ShiftCaptureSessionAudit(1000);
        Feed(audit, "cue_evaluation", new
        {
            carOrdinal = 3289,
            gear = 4,
            cue = new { enabled = true, stage = 3, flashOn = false, targetRpm = 9575.804 }
        });
        Feed(audit, "cue_evaluation", new
        {
            carOrdinal = 3289,
            gear = 4,
            decision = "ParkedRecorderCanary",
            cue = new { enabled = true, stage = 3, flashOn = true, targetRpm = 1 }
        });
        void Submission(bool submitted, bool canary, bool flashOn) => Feed(audit, "cue_submission", new
        {
            carOrdinal = 3289,
            gear = 4,
            submitted,
            canary,
            shiftCue = new { enabled = true, stage = 3, flashOn, isVisible = flashOn }
        });
        Submission(false, false, true);
        Submission(true, false, false);
        Submission(true, true, true);
        Submission(true, false, true);
        Feed(audit, "cue_submission", new
        {
            carOrdinal = 3289,
            gear = 4,
            submitted = true,
            canary = false,
            shiftCue = new { enabled = true, stage = 3, flashOn = true, isVisible = false }
        });
        var result = audit.Snapshot();
        var gear = Assert.Single(result.Gears);
        Assert.Equal(1, gear.RedRequestedSamples);
        Assert.Equal(3, gear.SuccessfulSubmissions);
        Assert.Equal(1, gear.RedOnSubmissions);
        Assert.Equal(4, result.SuccessfulSubmissions);
        Assert.Equal(1, result.FailedSubmissions);
        Assert.Contains("not displayed frames", result.InterpretationLimits, StringComparison.Ordinal);
    }

    [Fact]
    public void QueuedCanaryRemainsExcludedAfterSessionCanaryFlagHasFinished()
    {
        var audit = new ShiftCaptureSessionAudit(1000);
        Feed(audit, "cue_evaluation", new
        {
            carOrdinal = 3289,
            gear = 1,
            decision = "RecorderCheckEnded",
            cue = new { enabled = true, stage = 3, flashOn = true, targetRpm = 1, isVisible = true }
        });
        Feed(audit, "cue_submission", new
        {
            carOrdinal = 3289,
            gear = 1,
            submitted = true,
            canary = false,
            shiftCue = new { enabled = true, stage = 3, flashOn = true, targetRpm = 1, isVisible = true }
        });
        var result = audit.Snapshot();
        Assert.Empty(result.Gears);
        Assert.Equal(1, result.SuccessfulSubmissions);
        Assert.Equal(1, result.Channels["cue_evaluation"].Samples);
    }

    [Fact]
    public void InputEvidenceIncludesBracketUncertaintyAndRejectsInvertedBounds()
    {
        var audit = new ShiftCaptureSessionAudit(1000);
        Feed(audit, "controller_button", new
        {
            edge = "pressed",
            earliestObservedEdgeQpc = 100,
            latestObservedEdgeQpc = 120,
            pollStartedQpc = 118,
            pollCompletedQpc = 120
        });
        Feed(audit, "controller_button", new { edge = "released" });
        Feed(audit, "controller_button", new
        {
            edge = "pressed",
            earliestObservedEdgeQpc = 200,
            latestObservedEdgeQpc = 190,
            pollStartedQpc = 188,
            pollCompletedQpc = 190
        });
        var result = audit.Snapshot();
        Assert.Equal(2, result.ButtonPresses);
        Assert.Equal(1, result.InvalidInputBrackets);
        Assert.Equal(20, result.InputEdgeBracketMilliseconds.Maximum);
        Assert.Equal(2, result.InputQueryMilliseconds.Maximum);
    }

    [Fact]
    public void DesktopSamplingCostAndIdleGapAreNotDeclaredVisibleCueLatency()
    {
        var audit = new ShiftCaptureSessionAudit(1000);
        Feed(audit, "cue_pixels", new { status = 0, qualified = false, readStartedQpc = 100, readFinishedQpc = 108 });
        Feed(audit, "cue_pixels", new { status = "Observed", qualified = true, readStartedQpc = 268, readFinishedQpc = 280 });
        Feed(audit, "cue_pixels", new { status = 5, qualified = false });
        var result = audit.Snapshot();
        Assert.Equal(2, result.PixelReads);
        Assert.Equal(1, result.QualifiedPixelReads);
        Assert.Equal(1, result.PixelFailures);
        Assert.Equal(12, result.ReadbackMilliseconds.Maximum);
        Assert.Equal(160, result.ReadbackIdleGapMilliseconds.Maximum);
        Assert.Contains("not scanout", result.InterpretationLimits, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfigurationCardinalityCannotGrowUnboundedOrLeakFreeText()
    {
        var audit = new ShiftCaptureSessionAudit(1000);
        for (var index = 0; index < 1000; index++)
            Feed(audit, "native_configuration", new { fingerprint = "profile" + index });
        Feed(audit, "native_configuration", new { fingerprint = "private/path/with spaces" });
        var result = audit.Snapshot();
        Assert.Equal(64, result.ProfileCount);
        Assert.True(result.SummaryTruncated);
        Assert.Equal(1001, result.ConfigurationRecords);
        Assert.DoesNotContain("private/path", audit.ToPlainText(), StringComparison.Ordinal);
    }

    [Fact]
    public void ChannelAndGearCapsRetainTotalsAndExplicitlyReportTruncation()
    {
        var channels = new ShiftCaptureSessionAudit(1000);
        for (var index = 0; index < 1000; index++) Feed(channels, "event" + index, new { });
        var summary = channels.Snapshot();
        Assert.Equal(1000, summary.Records);
        Assert.Equal(128, summary.Channels.Count);
        Assert.True(summary.SummaryTruncated);
        var gears = new ShiftCaptureSessionAudit(1000);
        for (var index = 1; index <= 1000; index++) Feed(gears, "cue_evaluation", new { carOrdinal = index, gear = 2, cue = new { } });
        Assert.Equal(128, gears.Snapshot().Gears.Length);
        Assert.True(gears.Snapshot().SummaryTruncated);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"kind\":\"telemetry\",\"timestamp\":0,\"payload\":{}}")]
    [InlineData("{\"kind\":\"not a label\",\"timestamp\":1,\"payload\":{}}")]
    [InlineData("{\"kind\":\"telemetry\",\"timestamp\":1,\"payload\":{\"parsed\":true,\"state\":{\"engineRpm\":\"NaN\"}}}")]
    public void UnexpectedEventFieldsDoNotThrowOrInventEvidence(string json)
    {
        var audit = new ShiftCaptureSessionAudit(1000);
        using var document = JsonDocument.Parse(json);
        audit.Observe(document.RootElement);
        Assert.Equal(1, audit.Snapshot().MalformedAuditRecords);
        Assert.Empty(audit.Snapshot().Gears);
    }

    [Fact]
    public void SnapshotSerializesWithoutNonFiniteNumbersAndDoesNotRetainDocumentLifetime()
    {
        var audit = new ShiftCaptureSessionAudit(1000);
        Telemetry(audit, 100, 1, 6000);
        var json = JsonSerializer.Serialize(audit.Snapshot(), JsonOptions);
        Assert.DoesNotContain("Infinity", json, StringComparison.Ordinal);
        Assert.DoesNotContain("rawBase64", json, StringComparison.Ordinal);
        Assert.Contains("6000", audit.ToPlainText(), StringComparison.Ordinal);
    }

    private static void Telemetry(ShiftCaptureSessionAudit audit, long timestamp, uint gameTimestamp, double rpm, string raw = "AA==") =>
        Feed(audit, "telemetry", new
        {
            parsed = true,
            rawBase64 = raw,
            state = new { carOrdinal = 3289, gear = 2, gameTimestampMilliseconds = gameTimestamp, engineRpm = rpm }
        }, timestamp);

    private static void Feed(ShiftCaptureSessionAudit audit, string kind, object payload, long timestamp = 1)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new { kind, timestamp, payload }, JsonOptions));
        audit.Observe(document.RootElement);
    }
}
