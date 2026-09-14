using System.Text.Json;
using Xunit;

namespace Wisp.App.Tests;

public sealed class FeatureTourPersistenceTests
{
    [Fact]
    public void UpdatingSettingsDoesNotInventACompletedTourOrChangeTheExistingAppearance()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("""
            {"SettingsRevision":9,"OverlayOpacity":0.7,"CustomAccentColor":"#FFA2DDF5","CpuRenderingEnabled":true}
            """)!;
        settings.MigrateSettings();
        Assert.Null(settings.CompletedFeatureTourId);
        Assert.Equal(0.7, settings.OverlayOpacity);
        Assert.Equal("#FFA2DDF5", settings.CustomAccentColor);
        Assert.True(settings.CpuRenderingEnabled);
    }

    [Fact]
    public void SuccessfulReceiptSurvivesARealSettingsReloadAndPreservesOtherPreferences()
    {
        var directory = NewDirectory();
        try
        {
            var service = new SettingsService(Path.Combine(directory, "settings.json"));
            var settings = Settings();
            using var fixture = new ControllerFixture(settings, service.Save, directory);
            Assert.True(fixture.Controller.TryCompleteFeatureTour());

            var restored = service.Load();
            Assert.Equal(FeatureTourSession.CurrentTourId, restored.CompletedFeatureTourId);
            Assert.False(restored.RequiresSetup);
            Assert.Equal(0.63, restored.OverlayOpacity);
            Assert.Equal("#FFA2DDF5", restored.CustomAccentColor);
            Assert.True(restored.CpuRenderingEnabled);
            Assert.False(restored.DriftGaugeEnabled);
            Assert.False(restored.BackgroundParticlesEnabled);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void FailedReceiptRollsBackThenRetryPersistsOnlyTheTourReceipt()
    {
        var directory = NewDirectory();
        try
        {
            var settings = Settings();
            settings.CompletedFeatureTourId = "previous-feature-tour";
            var fail = true;
            var writes = 0;
            AppSettings? saved = null;
            using var fixture = new ControllerFixture(settings, value =>
            {
                writes++;
                if (fail) throw new IOException("Synthetic unavailable storage.");
                saved = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(value));
            }, directory);
            var before = JsonSerializer.Serialize(settings);
            Assert.False(fixture.Controller.TryCompleteFeatureTour());
            Assert.Equal("previous-feature-tour", settings.CompletedFeatureTourId);
            Assert.Equal(before, JsonSerializer.Serialize(settings));
            Assert.False(fixture.Controller.Runs.IsRecording);

            fail = false;
            Assert.True(fixture.Controller.TryCompleteFeatureTour());
            Assert.Equal(2, writes);
            Assert.Equal(FeatureTourSession.CurrentTourId, saved!.CompletedFeatureTourId);
            settings.CompletedFeatureTourId = "previous-feature-tour";
            Assert.Equal(before, JsonSerializer.Serialize(settings));
            Assert.False(fixture.Controller.Runs.IsRecording);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static string NewDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Wisp.App.Tests", "feature-tour-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static AppSettings Settings() => new()
    {
        StartWithWindows = false,
        StartWithForza = false,
        AutomaticApplicationUpdateChecks = false,
        DebugLoggingEnabled = false,
        OverlayOpacity = 0.63,
        CustomAccentColor = "#FFA2DDF5",
        CpuRenderingEnabled = true,
        DriftGaugeEnabled = false,
        BackgroundParticlesEnabled = false,
        SetupCompletion = new SetupCompletionRecord
        {
            Version = SetupCompletionRecord.CurrentVersion,
            CompletedAtUtc = DateTimeOffset.UtcNow,
            ValidatedUdpPort = 5500,
            ValidatedPackets = SetupCompletionRecord.MinimumPackets,
            MovingPackets = SetupCompletionRecord.MinimumMovingPackets,
            ValidatedElapsedMilliseconds = SetupCompletionRecord.MinimumElapsedMilliseconds,
            DataOutConfirmed = true,
            DisplayModeConfirmed = true,
            StockHudConfirmed = true
        }
    };

    private sealed class ControllerFixture : IDisposable
    {
        public AppController Controller { get; }
        public ControllerFixture(AppSettings settings, Action<AppSettings> save, string directory) =>
            Controller = new AppController(settings, save, new NoStartupRegistration(), runsDirectory: Path.Combine(directory, "Runs"));
        public void Dispose() => Controller.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private sealed class NoStartupRegistration : IStartupRegistrationService
    {
        public void Apply(bool startWithWindows, bool startWithForza) { }
    }
}
