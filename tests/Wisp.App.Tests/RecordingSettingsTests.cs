using System.Text.Json;
using System.Windows.Input;
using Wisp.Core.Runs;
using Xunit;

namespace Wisp.App.Tests;

public sealed class RecordingSettingsTests
{
    [Fact]
    public void ExistingSettingsKeepTheirLayoutAndReceiveAnOptInShortcut()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>(
            "{\"SettingsRevision\":9,\"Smoothing\":0.4,\"OverlayOpacity\":0.8}")!;
        settings.MigrateSettings();
        Assert.Equal(9, settings.SettingsRevision);
        Assert.Equal(0.4, settings.Smoothing);
        Assert.Equal(0.8, settings.OverlayOpacity);
        Assert.False(settings.RecordingShortcutEnabled);
        Assert.Equal(Key.R, settings.RecordingShortcutKey);
        Assert.Equal(RunPurpose.General, settings.RunPurpose);
        Assert.Equal(0, settings.RecordingCountdownSeconds);
        Assert.Equal(0, settings.RecordingStopAfterSeconds);
        Assert.False(settings.MarkerShortcutEnabled);
        Assert.Equal(Key.M, settings.MarkerShortcutKey);
    }

    [Fact]
    public void InvalidPurposeAndShortcutNormalizeWithoutChangingOverlayShortcut()
    {
        var settings = new AppSettings
        {
            RunPurpose = (RunPurpose)999,
            RecordingShortcutEnabled = true,
            RecordingShortcutModifiers = OverlayHotkeyModifiers.None,
            RecordingShortcutKey = Key.None,
            RecordingCountdownSeconds = -1,
            RecordingStopAfterSeconds = 601,
            MarkerShortcutEnabled = true,
            MarkerShortcutModifiers = OverlayHotkeyModifiers.None,
            MarkerShortcutKey = Key.None,
            OverlayHotkeyEnabled = true
        };
        settings.MigrateSettings();
        Assert.Equal(RunPurpose.General, settings.RunPurpose);
        Assert.False(settings.RecordingShortcutEnabled);
        Assert.Equal(OverlayHotkeyModifiers.Control | OverlayHotkeyModifiers.Shift, settings.RecordingShortcutModifiers);
        Assert.Equal(Key.R, settings.RecordingShortcutKey);
        Assert.Equal(0, settings.RecordingCountdownSeconds);
        Assert.Equal(0, settings.RecordingStopAfterSeconds);
        Assert.False(settings.MarkerShortcutEnabled);
        Assert.Equal(Key.M, settings.MarkerShortcutKey);
        Assert.True(settings.OverlayHotkeyEnabled);
        Assert.Equal(Key.H, settings.OverlayHotkeyKey);
    }

    [Fact]
    public void RecordingPreferencesRoundTripBesideExistingPreferences()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Wisp-recording-settings", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SettingsService(Path.Combine(directory, "settings.json"));
            store.Save(new AppSettings
            {
                RunPurpose = RunPurpose.Drifting,
                RecordingShortcutEnabled = true,
                RecordingShortcutModifiers = OverlayHotkeyModifiers.Control | OverlayHotkeyModifiers.Alt,
                RecordingShortcutKey = Key.F8,
                RecordingCountdownSeconds = 5,
                RecordingStopAfterSeconds = 120,
                MarkerShortcutEnabled = true,
                MarkerShortcutModifiers = OverlayHotkeyModifiers.Control | OverlayHotkeyModifiers.Alt,
                MarkerShortcutKey = Key.F9,
                OverlayOpacity = 0.65
            });
            var settings = store.Load();
            Assert.Equal(RunPurpose.Drifting, settings.RunPurpose);
            Assert.True(settings.RecordingShortcutEnabled);
            Assert.Equal(Key.F8, settings.RecordingShortcutKey);
            Assert.Equal(OverlayHotkeyModifiers.Control | OverlayHotkeyModifiers.Alt, settings.RecordingShortcutModifiers);
            Assert.Equal(0.65, settings.OverlayOpacity);
            Assert.Equal(5, settings.RecordingCountdownSeconds);
            Assert.Equal(120, settings.RecordingStopAfterSeconds);
            Assert.True(settings.MarkerShortcutEnabled);
            Assert.Equal(Key.F9, settings.MarkerShortcutKey);
            Assert.Equal(OverlayHotkeyModifiers.Control | OverlayHotkeyModifiers.Alt, settings.MarkerShortcutModifiers);
            Assert.Equal(9, settings.SettingsRevision);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
