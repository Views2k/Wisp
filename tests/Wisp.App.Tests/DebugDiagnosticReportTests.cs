using Wisp.App.DebugLogging;
using Xunit;

namespace Wisp.App.Tests;

public sealed class DebugDiagnosticReportTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 4, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SustainedDispatcherDelayWithFreshUdpIsClassifiedAsUiProcessingDelay()
    {
        var report = Build(
            Healthy(1) with
            {
                UiHeartbeatAgeMilliseconds = 820,
                DispatcherDelayMilliseconds = 760,
                DispatcherProbePending = true,
                CompositionHz = 0,
                CompositionAgeMilliseconds = 810,
                CompositionMaximumGapMilliseconds = 790
            },
            Healthy(2) with
            {
                UiHeartbeatAgeMilliseconds = 1_820,
                DispatcherDelayMilliseconds = 1_760,
                DispatcherProbePending = true,
                CompositionHz = 0,
                CompositionAgeMilliseconds = 1_810,
                CompositionMaximumGapMilliseconds = 0
            },
            Healthy(3));

        AssertUsefulFinding(report, "UI processing delay");
        Assert.DoesNotContain("Telemetry reception interruption", report, StringComparison.Ordinal);
        Assert.DoesNotContain("Composition callback gap", report, StringComparison.Ordinal);
    }

    [Fact]
    public void ActualStoppedArrivalsAfterDrivingNarrowThePathWithoutCallingNormalDisconnectAFault()
    {
        var samples = new List<DebugHealthSample> { Healthy(1) };
        for (var second = 2; second <= 4; second++)
        {
            samples.Add(Healthy(second) with
            {
                IncomingHz = 0,
                ProcessedHz = 0,
                ReceivedDatagrams = 60,
                AcceptedPackets = 60,
                ProcessedPackets = 60,
                PacketAgeMilliseconds = (second - 1) * 1_000 + 5,
                GameTimestampAdvancing = false
            });
        }
        var report = DebugDiagnosticReport.Build(samples);

        AssertUsefulFinding(report, "Telemetry arrival stopped (cause uncertain)");
        Assert.Contains("not a confirmed fault", report, StringComparison.Ordinal);
        Assert.Contains("normal disconnect", report, StringComparison.Ordinal);
        Assert.DoesNotContain("Telemetry reception interruption", report, StringComparison.Ordinal);
        Assert.DoesNotContain("UI processing delay", report, StringComparison.Ordinal);
    }

    [Fact]
    public void ActualListenerErrorsAreSeparatedFromMissingUpstreamData()
    {
        var report = Build(Healthy(1) with { ListenerError = true }, Healthy(2) with { ListenerError = true });
        AssertUsefulFinding(report, "Telemetry reception interruption");
        Assert.Contains("local listener reported an error", report, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectedPacketGrowthIsReportedWithEvidence()
    {
        var report = Build(
            Healthy(0),
            Healthy(1) with { AcceptedPackets = 0, RejectedPackets = 3 },
            Healthy(2) with { AcceptedPackets = 0, RejectedPackets = 7 },
            Healthy(3));

        AssertUsefulFinding(report, "Telemetry rejected packets");
    }

    [Fact]
    public void HealthyUiWithSustainedCompositionGapIsClassifiedSeparately()
    {
        var report = Build(
            Healthy(1) with
            {
                CompositionHz = 0,
                CompositionAgeMilliseconds = 720,
                CompositionMaximumGapMilliseconds = 710
            },
            Healthy(2) with
            {
                CompositionHz = 0,
                CompositionAgeMilliseconds = 1_690,
                CompositionMaximumGapMilliseconds = 980
            },
            Healthy(3));

        AssertUsefulFinding(report, "Composition callback gap");
        Assert.DoesNotContain("UI processing delay", report, StringComparison.Ordinal);
    }

    [Fact]
    public void ExpectedNativeHudFailureAndStalenessAreReportedWithoutGuessingACause()
    {
        var report = Build(
            Healthy(0),
            Healthy(1) with
            {
                NativeAvailable = false,
                NativeStatus = "Failed",
                NativeAgeMilliseconds = 2_820,
                NativeVisibilityAgeMilliseconds = 2_810
            },
            Healthy(2) with
            {
                NativeAvailable = false,
                NativeStatus = "Failed",
                NativeAgeMilliseconds = 3_810,
                NativeVisibilityAgeMilliseconds = 3_790
            },
            Healthy(3));

        AssertUsefulFinding(report, "Native HUD data unavailable or stale");
        Assert.DoesNotContain("driver caused", report, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResourcePressureIsReportedAsCorrelationNotDriverBlame()
    {
        var report = Build(
            Healthy(0),
            Healthy(1) with
            {
                UiHeartbeatAgeMilliseconds = 850,
                DispatcherDelayMilliseconds = 800,
                DispatcherProbePending = true,
                WispCpuPercent = 96,
                WorkingSetBytes = 1_500_000_000,
                ManagedHeapBytes = 900_000_000,
                Gen2Collections = 5
            },
            Healthy(2) with
            {
                UiHeartbeatAgeMilliseconds = 920,
                DispatcherDelayMilliseconds = 870,
                DispatcherProbePending = true,
                WispCpuPercent = 98,
                WorkingSetBytes = 1_650_000_000,
                ManagedHeapBytes = 1_000_000_000,
                Gen2Collections = 8
            },
            Healthy(3));

        AssertUsefulFinding(report, "Resource pressure correlation");
        Assert.Contains("correlation", report, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("driver caused", report, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DroppedRecordsAndCollectorFailuresCreateACoverageWarning()
    {
        var report = DebugDiagnosticReport.Build(
            [Healthy(0), Healthy(1) with { CollectionGapMilliseconds = 3_000, CollectorFailures = 2 }, Healthy(2)],
            droppedRecords: 4);

        Assert.Contains("Collector coverage gap", report, StringComparison.Ordinal);
        Assert.Contains("cannot be classified reliably", report, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Next useful step", report, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("menu")]
    [InlineData("hidden")]
    [InlineData("disconnected")]
    [InlineData("background")]
    [InlineData("stale_context")]
    public void SustainedExpectedInactiveStatesDoNotProduceFaultFindings(string context)
    {
        var samples = Enumerable.Range(1, 3).Select(second =>
        {
            var sample = Healthy(second) with
            {
                OverlayExpectedVisible = false,
                NativeExpected = false,
                UiHeartbeatAgeMilliseconds = 700,
                CompositionHz = 0,
                CompositionAgeMilliseconds = second * 1000,
                NativeAvailable = false,
                NativeAgeMilliseconds = second * 1000 + 3000
            };
            return context switch
            {
                "menu" => sample with { RaceOn = false, GameTimestampAdvancing = false },
                "hidden" => sample,
                "background" => sample with { Focus = DebugFocus.Other },
                "stale_context" => sample with { UiContextFresh = false, OverlayExpectedVisible = true, NativeExpected = true },
                "disconnected" => sample with
                {
                    RaceOn = false,
                    GameTimestampAdvancing = false,
                    IncomingHz = 0,
                    ProcessedHz = 0,
                    ReceivedDatagrams = 0,
                    AcceptedPackets = 0,
                    ProcessedPackets = 0,
                    PacketAgeMilliseconds = null
                },
                _ => throw new InvalidOperationException()
            };
        }).ToArray();

        var report = Build(samples);
        AssertNoFaultHeadings(report);
    }

    [Fact]
    public void NormalIdleHeartbeatCadenceIsNotMistakenForUiDelay()
    {
        var report = Build(Enumerable.Range(1, 3).Select(second => Healthy(second) with
        {
            RaceOn = false,
            GameTimestampAdvancing = false,
            UiHeartbeatAgeMilliseconds = 780,
            DispatcherProbePending = false,
            DispatcherDelayMilliseconds = 6,
            OverlayExpectedVisible = false,
            ProcessedHz = 0
        }).ToArray());
        AssertNoFaultHeadings(report);
    }

    [Fact]
    public void RateDifferenceFromNewestPacketCoalescingDoesNotImplyLossOrUiFailure()
    {
        var report = Build(Enumerable.Range(1, 3).Select(second => Healthy(second) with
        {
            ReceivedDatagrams = second * 600,
            AcceptedPackets = second * 60,
            DrainedDatagrams = second * 540,
            ProcessedPackets = second * 30,
            IncomingHz = 600,
            ProcessedHz = 30
        }).ToArray());
        AssertNoFaultHeadings(report);
        Assert.Contains("intentional newest-packet coalescing", report, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownUiTimingCannotSupportACompositionOrNativeAttribution()
    {
        var report = Build(Enumerable.Range(1, 3).Select(second => Healthy(second) with
        {
            UiHeartbeatAgeMilliseconds = null,
            DispatcherDelayMilliseconds = double.NaN,
            CompositionAgeMilliseconds = second * 1000,
            NativeAvailable = false,
            NativeAgeMilliseconds = second * 1000 + 3000
        }).ToArray());
        AssertNoFaultHeadings(report);
        Assert.Contains("Unknown or invalid timing", report, StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidIncomingTimingCannotEstablishFreshTelemetryDuringAUiStall()
    {
        var report = Build(Enumerable.Range(1, 3).Select(second => Healthy(second) with
        {
            IncomingHz = double.PositiveInfinity,
            PacketAgeMilliseconds = double.NaN,
            DispatcherProbePending = true,
            DispatcherDelayMilliseconds = second * 1000,
            UiHeartbeatAgeMilliseconds = second * 1000
        }).ToArray());
        AssertNoFaultHeadings(report);
    }

    [Fact]
    public void SustainedMissingUiContextCanStillBeDetectedByThePendingProbe()
    {
        var report = Build(Enumerable.Range(1, 3).Select(second => Healthy(second) with
        {
            UiContextFresh = false,
            UiHeartbeatAgeMilliseconds = null,
            DispatcherProbePending = true,
            DispatcherDelayMilliseconds = second * 1000,
            ProcessedPackets = 0,
            ProcessedHz = 0
        }).ToArray());
        AssertUsefulFinding(report, "UI processing delay");
    }

    [Fact]
    public void UnknownSessionTimingIsInsufficientEvidence()
    {
        var report = Build(Healthy(1) with { ElapsedMilliseconds = double.NaN },
            Healthy(2) with { ElapsedMilliseconds = double.PositiveInfinity });
        AssertNoFaultHeadings(report);
        Assert.Contains("Insufficient evidence", report, StringComparison.Ordinal);
    }

    [Fact]
    public void SummarySizeAndSessionCountStayBounded()
    {
        var samples = Enumerable.Range(1, 200).SelectMany(session => Enumerable.Range(1, 3).Select(second =>
            Healthy(second) with
            {
                SessionId = $"session-{session}",
                DispatcherProbePending = true,
                DispatcherDelayMilliseconds = second * 1000,
                UiHeartbeatAgeMilliseconds = second * 1000,
                WispCpuPercent = 95
            })).ToArray();
        var report = DebugDiagnosticReport.Build(samples);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(report) <= DebugDiagnosticReport.MaximumSummaryBytes);
        Assert.Contains("latest 32 retained sessions", report, StringComparison.Ordinal);
        Assert.Contains("sampled, normally once per second", report, StringComparison.Ordinal);
    }

    [Fact]
    public void OneIsolatedBadSampleIsInsufficientEvidence()
    {
        var report = Build(
            Healthy(1) with
            {
                UiHeartbeatAgeMilliseconds = 900,
                DispatcherDelayMilliseconds = 850,
                DispatcherProbePending = true,
                CompositionHz = 0,
                CompositionAgeMilliseconds = 850,
                CompositionMaximumGapMilliseconds = 820,
                NativeAvailable = false,
                NativeStatus = "Failed",
                NativeAgeMilliseconds = 850
            });

        AssertNoFaultHeadings(report);
        Assert.Contains("insufficient", report, StringComparison.OrdinalIgnoreCase);
    }

    private static string Build(params DebugHealthSample[] samples) =>
        DebugDiagnosticReport.Build(samples);

    private static DebugHealthSample Healthy(int second) => new()
    {
        TimestampUtc = Start.AddSeconds(second),
        SessionId = "test-session",
        ElapsedMilliseconds = second * 1_000,
        CollectionGapMilliseconds = second == 0 ? 0 : 1_000,
        ReceivedDatagrams = 60L * second,
        DrainedDatagrams = 0,
        AcceptedPackets = 60L * second,
        RejectedPackets = 0,
        ProcessedPackets = 60L * second,
        IncomingHz = 60,
        ProcessedHz = 60,
        PacketAgeMilliseconds = 5,
        ListenerRunning = true,
        ListenerError = false,
        RaceOn = true,
        GameTimestampAdvancing = true,
        Focus = DebugFocus.Game,
        UiContextFresh = true,
        OverlayExpectedVisible = true,
        NativeExpected = true,
        UiHeartbeatAgeMilliseconds = 8,
        DispatcherDelayMilliseconds = 4,
        DispatcherProbePending = false,
        CompositionHz = 60,
        CompositionAgeMilliseconds = 8,
        CompositionMaximumGapMilliseconds = 18,
        NativeAvailable = true,
        NativeStatus = "Ready",
        NativeAgeMilliseconds = 8,
        NativeVisibilityAgeMilliseconds = 8,
        GameplayVisibility = "Visible",
        WispCpuPercent = 4,
        WorkingSetBytes = 180_000_000,
        ManagedHeapBytes = 45_000_000,
        Gen2Collections = 0,
        DroppedRecords = 0,
        CollectorFailures = 0
    };

    private static void AssertUsefulFinding(string report, string heading)
    {
        Assert.Contains(heading, report, StringComparison.Ordinal);
        Assert.Contains("Evidence", report, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("When:", report, StringComparison.Ordinal);
        Assert.Contains("Uncertainty", report, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Next useful step", report, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertNoFaultHeadings(string report)
    {
        foreach (var heading in new[]
                 {
                     "UI processing delay",
                     "Telemetry reception interruption",
                     "Telemetry rejected packets",
                     "Native HUD data unavailable or stale",
                     "Composition callback gap",
                     "Resource pressure correlation"
                 })
        {
            Assert.DoesNotContain(heading, report, StringComparison.Ordinal);
        }
    }
}
