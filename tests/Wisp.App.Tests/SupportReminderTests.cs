using System.Text.Json;
using Xunit;

namespace Wisp.App.Tests;

public sealed class SupportReminderTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FirstUseIsDue()
    {
        Assert.True(SupportReminderPolicy.IsDue(null, Now));
    }

    [Theory]
    [InlineData(86399, false)]
    [InlineData(86400, true)]
    [InlineData(86401, true)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    public void ReceiptRequiresTwentyFourElapsedHours(int elapsedSeconds, bool expected)
    {
        Assert.Equal(expected, SupportReminderPolicy.IsDue(Now.AddSeconds(-elapsedSeconds), Now));
    }

    [Fact]
    public void CrossingMidnightDoesNotResetTheCooldown()
    {
        var lastShown = new DateTimeOffset(2026, 10, 5, 23, 59, 0, TimeSpan.Zero);
        Assert.False(SupportReminderPolicy.IsDue(lastShown, lastShown.AddMinutes(2)));
    }

    [Fact]
    public void DifferentOffsetsCompareElapsedTimeAndRejectClockRollback()
    {
        var lastShown = Now.ToOffset(TimeSpan.FromHours(-5));
        Assert.False(SupportReminderPolicy.IsDue(lastShown, Now.AddHours(24).AddSeconds(-1).ToOffset(TimeSpan.FromHours(2))));
        Assert.True(SupportReminderPolicy.IsDue(lastShown, Now.AddHours(24).ToOffset(TimeSpan.FromHours(2))));
        Assert.False(SupportReminderPolicy.IsDue(lastShown, Now.AddDays(-1).ToOffset(TimeSpan.FromHours(2))));
    }

    [Fact]
    public void DailyTimerWaitsForTheRemainingIntervalAndDoesNotCatchUpAfterDismissal()
    {
        Assert.Equal(TimeSpan.Zero, SupportReminderPolicy.DelayUntilDue(null, Now));
        Assert.Equal(TimeSpan.FromHours(1), SupportReminderPolicy.DelayUntilDue(Now.AddHours(-23), Now));
        Assert.Equal(TimeSpan.Zero, SupportReminderPolicy.DelayUntilDue(Now.AddHours(-24), Now));
        Assert.Equal(TimeSpan.FromHours(24), SupportReminderPolicy.DelayUntilDue(Now.AddHours(-48), Now, Now));
        Assert.Equal(TimeSpan.FromSeconds(1), SupportReminderPolicy.DelayUntilDue(Now.AddHours(-48),
            Now.AddHours(24).AddSeconds(-1), Now));
        Assert.Equal(TimeSpan.Zero, SupportReminderPolicy.DelayUntilDue(Now.AddHours(-48), Now.AddHours(24), Now));
    }

    [Fact]
    public void ClockRollbackKeepsTheDailyTimerBoundedWithoutMakingTheReminderDue()
    {
        var futureReceipt = Now.AddDays(90);
        Assert.False(SupportReminderPolicy.IsDue(futureReceipt, Now));
        Assert.Equal(TimeSpan.FromHours(24), SupportReminderPolicy.DelayUntilDue(futureReceipt, Now));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(23)]
    public void EveryManualOpeningBypassesTheReceiptAndRestartsTheDailyCooldown(int priorReceiptHours)
    {
        var directory = NewDirectory();
        try
        {
            var settings = Settings();
            settings.LastSupportReminderShownUtc = Now.AddHours(-priorReceiptHours);
            var writes = 0;
            using var fixture = new ControllerFixture(settings, _ => writes++, directory);
            Assert.True(fixture.Controller.TryRecordSupportReminderShown(Now, manualOpening: true));
            var reopenedAt = Now.AddMinutes(5);
            Assert.True(fixture.Controller.TryRecordSupportReminderShown(reopenedAt, manualOpening: true));
            Assert.Equal(reopenedAt, settings.LastSupportReminderShownUtc);
            Assert.False(fixture.Controller.TryRecordSupportReminderShown(reopenedAt.AddHours(24).AddSeconds(-1)));
            Assert.Equal(2, writes);
            Assert.True(fixture.Controller.TryRecordSupportReminderShown(reopenedAt.AddHours(24)));
            Assert.Equal(3, writes);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void OlderSettingsHaveNoReceiptAndPreserveExistingPreferences()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("""
            {"SettingsRevision":9,"OverlayOpacity":0.7,"CustomAccentColor":"#FFA2DDF5","CpuRenderingEnabled":true}
            """)!;
        settings.MigrateSettings();
        Assert.Null(settings.LastSupportReminderShownUtc);
        Assert.Equal(0.7, settings.OverlayOpacity);
        Assert.Equal("#FFA2DDF5", settings.CustomAccentColor);
        Assert.True(settings.CpuRenderingEnabled);
    }

    [Fact]
    public void ReceiptSurvivesSettingsReloadInUtcAndPreservesOtherPreferences()
    {
        var directory = NewDirectory();
        try
        {
            var service = new SettingsService(Path.Combine(directory, "settings.json"));
            var settings = Settings();
            var writes = 0;
            using var fixture = new ControllerFixture(settings, value => { writes++; service.Save(value); }, directory);
            var shownAt = Now.ToOffset(TimeSpan.FromHours(-5));
            Assert.True(fixture.Controller.TryRecordSupportReminderShown(shownAt));

            var restored = service.Load();
            Assert.Equal(Now, restored.LastSupportReminderShownUtc);
            Assert.Equal(TimeSpan.Zero, restored.LastSupportReminderShownUtc!.Value.Offset);
            Assert.False(restored.RequiresSetup);
            Assert.Equal(0.63, restored.OverlayOpacity);
            Assert.Equal("#FFA2DDF5", restored.CustomAccentColor);
            Assert.True(restored.CpuRenderingEnabled);
            Assert.False(restored.DriftGaugeEnabled);
            Assert.False(restored.BackgroundParticlesEnabled);
            Assert.Equal("previous-feature-tour", restored.CompletedFeatureTourId);
            Assert.Equal("previous-release", restored.DismissedWhatsNewId);
            Assert.False(SupportReminderPolicy.IsDue(restored.LastSupportReminderShownUtc, Now.AddHours(23)));
            Assert.False(fixture.Controller.TryRecordSupportReminderShown(Now.AddHours(23)));
            Assert.Equal(Now, settings.LastSupportReminderShownUtc);
            Assert.Equal(1, writes);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedSaveRestoresPriorReceiptAndRetryPersistsOnlyTheNewReceipt(bool manualOpening)
    {
        var directory = NewDirectory();
        try
        {
            var settings = Settings();
            var previous = Now.AddHours(-25);
            settings.LastSupportReminderShownUtc = previous;
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
            Assert.False(fixture.Controller.TryRecordSupportReminderShown(Now, manualOpening));
            Assert.Equal(before, JsonSerializer.Serialize(settings));
            Assert.Equal(previous, settings.LastSupportReminderShownUtc);

            fail = false;
            Assert.True(fixture.Controller.TryRecordSupportReminderShown(Now, manualOpening));
            Assert.Equal(2, writes);
            Assert.Equal(Now, saved!.LastSupportReminderShownUtc);
            settings.LastSupportReminderShownUtc = previous;
            Assert.Equal(before, JsonSerializer.Serialize(settings));
            Assert.False(fixture.Controller.Runs.IsRecording);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IncompleteSetupDoesNotRecordOrSaveAReceipt(bool manualOpening)
    {
        var directory = NewDirectory();
        try
        {
            var settings = Settings();
            settings.SetupCompletion = null;
            var writes = 0;
            using var fixture = new ControllerFixture(settings, _ => writes++, directory);
            Assert.True(settings.RequiresSetup);
            Assert.False(fixture.Controller.TryRecordSupportReminderShown(Now, manualOpening));
            Assert.Null(settings.LastSupportReminderShownUtc);
            Assert.Equal(0, writes);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static string NewDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Wisp.App.Tests", "support-reminder-" + Guid.NewGuid().ToString("N"));
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
        CompletedFeatureTourId = "previous-feature-tour",
        DismissedWhatsNewId = "previous-release",
        SetupCompletion = new SetupCompletionRecord
        {
            Version = SetupCompletionRecord.CurrentVersion,
            CompletedAtUtc = Now,
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
            Controller = new AppController(settings, save, new NoStartupRegistration(),
                runsDirectory: Path.Combine(directory, "Runs"),
                shiftCalibrationDirectory: Path.Combine(directory, "ShiftCalibration"),
                clipLibraryDirectory: Path.Combine(directory, "Clips"),
                tuneLibraryDirectory: Path.Combine(directory, "Tunes"));
        public void Dispose() => Controller.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private sealed class NoStartupRegistration : IStartupRegistrationService
    {
        public void Apply(bool startWithWindows, bool startWithForza) { }
    }
}
