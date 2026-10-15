using System.Collections.Immutable;
using System.Text.Json;
using Wisp.App.DebugLogging;
using Wisp.App.Supplementary;
using Wisp.Core;
using Xunit;

namespace Wisp.App.Tests;

public sealed class SupplementaryDiagnosticDetailTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void OverlappingCollectorSnapshotsRetainTwentyMinutesWithoutDuplicatingSamples()
    {
        var detail = new SupplementaryDiagnosticDetailBuffer();
        var settings = SupplementaryDiagnosticSettings.Capture(new AppSettings());
        for (var cycle = 0; cycle <= 20; cycle++)
        {
            var at = Now.AddMinutes(-20 + cycle);
            var samples = Enumerable.Range(0, 60).Select(n => Sample(at.AddSeconds(-118 + n * 2))).ToImmutableArray();
            detail.Observe(at, new(at, samples, [new(at, HealthEventCode.RecorderChanged, 0, Clips.ClipRecorderState.Buffering, null)]), [], settings: settings);
        }
        var result = detail.Snapshot(Now.AddMinutes(-20), Now);
        Assert.Equal(601, result.Health.Length);
        Assert.Equal(Now.AddMinutes(-20), result.Health[0].TimestampUtc);
        Assert.Equal(Now, result.Health[^1].TimestampUtc);
        Assert.Equal(21, result.Configurations.Length);
        Assert.Equal(21, result.Breadcrumbs.Length);
        Assert.Equal(0, result.Omitted);
        Assert.True(result.IsValid(Now.AddMinutes(-20), Now));
        Assert.Equal(601, result.Health.Select(s => s.TimestampUtc).Distinct().Count());

        var json = JsonSerializer.Serialize(new SupplementaryDiagnosticHistory(2, Now.AddMinutes(-20), Now, 0, null, [], result), SupplementarySchema.Json);
        Assert.DoesNotContain("NaN", json);
        // Optional cross-language contract fixture, produced from these real serialized records.
        if (Environment.GetEnvironmentVariable("WISP_SUPPORT_FIXTURE_OUTPUT") is { Length: > 0 } output)
            System.IO.File.WriteAllText(output, json);
        Assert.Empty(detail.Snapshot(Now.AddSeconds(1), Now.AddMinutes(20).AddSeconds(1)).Health);
        Assert.Equal(601, result.Health.Length);
    }

    [Fact]
    public void DetailKeepsFailureEvidenceAndSettingsWhileClearRemovesRetainedHistory()
    {
        var detail = new SupplementaryDiagnosticDetailBuffer();
        var component = new ComponentDiagnosticReport(Now, DiagnosticComponent.Playback, "{\"stage\":\"seeking\",\"hresult\":-1}");
        detail.Observe(Now, new(Now, [Sample(Now)], [], 5), [component], settings: SupplementaryDiagnosticSettings.Capture(new AppSettings { BoostMaximumPsi = 200 }));
        var captured = detail.Snapshot(Now.AddMinutes(-20), Now);
        Assert.Equal(200, Assert.Single(captured.Configurations).Settings.BoostMaximumPsi);
        Assert.Equal(component, Assert.Single(captured.Components));
        Assert.Equal(5, captured.DroppedBreadcrumbs);
        detail.Clear();
        var cleared = detail.Snapshot(Now.AddMinutes(-20), Now);
        Assert.Empty(cleared.Health); Assert.Empty(cleared.Components); Assert.Empty(cleared.Configurations);
        Assert.Single(captured.Health);
    }

    [Fact]
    public void BoundedDetailRejectsInvalidOrOutOfWindowEvidence()
    {
        var detail = new SupplementaryDiagnosticDetail([Sample(Now)], [], [], [], [], 0, 0);
        Assert.True(detail.IsValid(Now.AddMinutes(-20), Now));
        Assert.False((detail with { Health = [Sample(Now) with { CpuPercent = double.NaN }] }).IsValid(Now.AddMinutes(-20), Now));
        Assert.False((detail with { Health = [Sample(Now.AddSeconds(1))] }).IsValid(Now.AddMinutes(-20), Now));
        Assert.False((detail with { Health = Enumerable.Repeat(Sample(Now), 602).ToImmutableArray() }).IsValid(Now.AddMinutes(-20), Now));
    }

    private static HealthContextSample Sample(DateTimeOffset at) => new()
    {
        TimestampUtc = at,
        CollectionGapMs = 2000,
        IncomingDatagramsPerSecond = 60,
        ProcessedPacketsPerSecond = 60,
        AcceptedPackets = 72000,
        ReceivedDatagrams = 72000,
        ProcessedPackets = 72000,
        PacketAgeMs = 8,
        ListenerRunning = true,
        GameActive = true,
        Focus = DebugFocus.Game,
        CpuRendering = false,
        RecorderState = Clips.ClipRecorderState.Buffering,
        GameTimestampAdvancing = true,
        OverlayExpectedVisible = true,
        UiHeartbeatAgeMs = 5,
        DispatcherDelayMs = 1.5,
        CompositionCallbacksPerSecond = 144,
        CompositionCallbackAgeMs = 3,
        CompositionMaximumGapMs = 8,
        NativeExpected = true,
        NativeStatus = NativeAssistProviderStatus.Ready,
        NativeGameVersion = "1.0.0.0",
        GameVerification = NativeGameVerification.Steam,
        NativeGamePlatform = DiagnosticGamePlatform.Steam,
        NativeCompatibilityRevision = 1,
        NativeReadAttempts = 72000,
        NativeAgeMs = 2,
        CpuPercent = 0.4,
        WorkingSetBytes = 200000000,
        ManagedHeapBytes = 64000000,
        Gen2Collections = 3,
        Renderer = new(72000, 71998, 1, 1, 0, 0, 0, 144, 0.6, 4)
    };
}
