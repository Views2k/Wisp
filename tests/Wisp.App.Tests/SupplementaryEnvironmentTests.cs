using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text.Json;
using Wisp.App.Supplementary;
using Xunit;

namespace Wisp.App.Tests;

public sealed class SupplementaryEnvironmentTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-10T16:00:00Z");
    private static SupplementaryEnvironment Sample() => new(1, Now, 26100, 16, "available", "none",
        [new(4318, 1234, "Example GPU (TM)", 8192, false, "32.0.15.1234")],
        [new(0, 3840, 2160, "enabled", 239760, 150, 10)], new("gpu", "mph", "native-analogue", true),
        new("not-measured", "not-measured", "not-measured", "available"), 32);

    [Fact]
    public void CompleteBoundedSnapshotRoundTripsAndOnlyHeartbeatAcceptsIt()
    {
        using var fixture = new SupplementaryFixture();
        var environment = Sample() with { CollectedAt = fixture.Now };
        var heartbeat = fixture.Event() with { Kind = "heartbeat", Environment = environment, ForegroundState = "game" };
        Assert.True(SupplementarySchema.Valid(heartbeat, fixture.Now));
        Assert.False(SupplementarySchema.Valid(heartbeat with { Kind = "feature" }, fixture.Now));
        Assert.False(SupplementarySchema.Valid(heartbeat with { ForegroundState = "private-window-title" }, fixture.Now));
        var json = JsonSerializer.Serialize(environment, SupplementarySchema.Json);
        Assert.True(json.Length < 8192);
        Assert.True(JsonSerializer.Deserialize<SupplementaryEnvironment>(json, SupplementarySchema.Json)!.IsValid(fixture.Now));
    }

    [Theory]
    [InlineData("C:\\private\\GPU")]
    [InlineData("owner@example.invalid")]
    [InlineData("<script>")]
    [InlineData("")]
    [InlineData("   ")]
    public void UnboundedOrPrivateModelTextIsRejected(string model) =>
        Assert.False((Sample() with { Adapters = [Sample().Adapters[0] with { Model = model }] }).IsValid(Now));

    [Fact]
    public void InvalidDimensionsReferencesDatesAndClaimsAreRejected()
    {
        Assert.False((Sample() with { Displays = [Sample().Displays[0] with { AdapterIndex = 1 }] }).IsValid(Now));
        Assert.False((Sample() with { Displays = [Sample().Displays[0] with { RefreshMilliHz = 1000001 }] }).IsValid(Now));
        Assert.False((Sample() with { CollectedAt = Now.AddMinutes(-11) }).IsValid(Now));
        Assert.False((Sample() with { Coverage = Sample().Coverage with { Vrr = "enabled" } }).IsValid(Now));
        Assert.False((Sample() with { Adapters = Enumerable.Repeat(Sample().Adapters[0], 5).ToImmutableArray() }).IsValid(Now));
    }

    [Fact]
    public void SettingsProjectionCapturesUsefulChoicesWithoutProfilesPathsOrHotkeys()
    {
        var preferences = new AppSettings
        {
            CpuRenderingEnabled = true,
            HudGpuPriority = true,
            Smoothing = 0.08,
            OverlayWidthScale = 1.5,
            LayoutMode = HudLayoutMode.Native,
            NativeGaugeMode = NativeGaugeMode.Analogue,
            LapMapEnabled = true,
            DriftGaugeEnabled = true,
            DebugLoggingEnabled = true,
            Clips = new()
            {
                Enabled = true,
                LosslessVideo = true,
                FrameRate = 30,
                ResolutionHeight = 2160,
                LengthSeconds = 120,
                StorageDirectory = "C:\\fixture-private\\clips",
                LegacyLibraryDirectory = "fixture-private"
            },
            ColorTheme = "fixture-private-theme"
        };
        var projected = SupplementarySettings.Capture(preferences);
        Assert.True(projected.IsValid);
        Assert.Equal(0.08, projected.Smoothing);
        Assert.Equal(2160, projected.ClipsResolutionHeight);
        Assert.True(projected.ClipsLossless);
        Assert.True(projected.LapMapEnabled);
        Assert.True(projected.DriftGaugeEnabled);
        Assert.True(projected.HudGpuPriority);
        Assert.DoesNotContain("fixture-private", JsonSerializer.Serialize(projected, SupplementarySchema.Json));
        preferences.Smoothing = double.NaN; preferences.Clips.FrameRate = 900;
        projected = SupplementarySettings.Capture(preferences);
        Assert.Null(projected.Smoothing); Assert.Null(projected.ClipsFrameRate);
        Assert.True(projected.IsValid);
        Assert.False((projected with { ClipsQuality = 1000 }).IsValid);
        Assert.False((projected with { OverlayOpacity = double.PositiveInfinity }).IsValid);
    }

    [Fact]
    public void NativeLayoutMatchesFixedBackgroundAbi()
    {
        Assert.Equal(292, Marshal.SizeOf<SupplementaryEnvironmentCollector.NativeAdapter>());
        Assert.Equal(28, Marshal.SizeOf<SupplementaryEnvironmentCollector.NativeDisplay>());
        Assert.Equal(1412, Marshal.SizeOf<SupplementaryEnvironmentCollector.NativeSnapshot>());
    }

    [Fact]
    public void NativeProjectionPreservesUnavailableFactsAndRejectsInvalidIndices()
    {
        var native = new SupplementaryEnvironmentCollector.NativeSnapshot
        {
            Size = 1412,
            Status = 2,
            Reason = 4,
            AdapterCount = 1,
            DisplayCount = 1,
            Adapters = new SupplementaryEnvironmentCollector.NativeAdapter[4],
            Displays = new SupplementaryEnvironmentCollector.NativeDisplay[8]
        };
        native.Adapters[0] = new() { VendorId = 4318, DeviceId = 1234, Model = "private@example.invalid" };
        native.Displays[0] = new() { Width = 1920, Height = 1080, AdapterIndex = 0 };
        var result = SupplementaryEnvironmentCollector.Project(native, Sample());
        Assert.Equal("partial", result.GraphicsStatus);
        Assert.Equal("Unrecognized graphics adapter", result.Adapters[0].Model);
        Assert.Null(result.Adapters[0].DriverVersion);
        Assert.Null(result.Displays[0].RefreshMilliHz);
        Assert.Null(result.Displays[0].ScalePercent);
        Assert.Equal("unknown", result.Displays[0].Hdr);
        native.Displays[0].AdapterIndex = 4;
        Assert.Equal("collector-failed", SupplementaryEnvironmentCollector.Project(native, Sample()).GraphicsReason);
    }

    [Fact]
    public void CacheRefreshesOnMonotonicDeadlineAndSettingsStayCurrent()
    {
        var clock = new ManualClock(); var calls = 0;
        var collector = new SupplementaryEnvironmentCollector((now, settings) => { calls++; return Sample() with { CollectedAt = now, Settings = settings }; }, clock);
        var settings = Sample().Settings;
        _ = collector.Snapshot(settings);
        clock.Seconds = 299;
        Assert.Equal("cpu", collector.Snapshot(settings with { RenderMode = "cpu" }).Settings.RenderMode);
        Assert.Equal(1, calls);
        clock.Seconds = 300;
        Assert.True(collector.Snapshot(settings).IsValid(clock.GetUtcNow()));
        Assert.Equal(2, calls);
    }

    [Fact]
    public void ActualBackgroundProbeReturnsOnlyValidContractWithoutWindowChanges()
    {
        var result = new SupplementaryEnvironmentCollector().Snapshot(SupplementarySettings.Capture(new AppSettings()));
        Assert.True(result.IsValid(DateTimeOffset.UtcNow));
        Assert.InRange(result.Adapters.Length, 0, 4);
        Assert.InRange(result.Displays.Length, 0, 8);
        TestContext.Current.TestOutputHelper!.WriteLine(JsonSerializer.Serialize(new
        {
            result.GraphicsStatus,
            result.GraphicsReason,
            adapters = result.Adapters.Length,
            displays = result.Displays.Length,
            driverVersions = result.Adapters.Count(a => a.DriverVersion is not null),
            refreshRates = result.Displays.Count(d => d.RefreshMilliHz is not null),
            scaleFactors = result.Displays.Count(d => d.ScalePercent is not null),
            hdrStates = result.Displays.Count(d => d.Hdr != "unknown")
        }));
        var destination = System.Environment.GetEnvironmentVariable("WISP_ENVIRONMENT_FIXTURE_OUTPUT");
        if (!string.IsNullOrEmpty(destination))
        {
            using var fixture = new SupplementaryFixture();
            var observed = DateTimeOffset.UtcNow;
            var value = fixture.Event() with { ObservedAt = observed, Kind = "heartbeat", Environment = result, ForegroundState = "unknown" };
            Assert.True(SupplementarySchema.Valid(value, observed));
            System.IO.File.WriteAllBytes(destination, JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, batchSequence = 1, events = new[] { value } }, SupplementarySchema.Json));
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        public long Seconds;
        public override long TimestampFrequency => 1;
        public override long GetTimestamp() => Seconds;
        public override DateTimeOffset GetUtcNow() => Now.AddSeconds(Seconds);
    }
}
