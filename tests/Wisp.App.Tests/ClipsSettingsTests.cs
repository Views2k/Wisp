using System.IO;
using System.Text.Json;
using System.Windows.Input;
using Wisp.App.Clips;
using Xunit;

namespace Wisp.App.Tests;

public sealed class ClipsSettingsTests
{
    [Fact]
    public void DefaultsAreOptInAndMatchRequestedRecordingChoices()
    {
        var settings = new ClipsSettings();
        Assert.False(settings.Enabled);
        Assert.False(settings.ShowCaptureBorder);
        Assert.False(settings.CaptureSystemAudio);
        Assert.Equal(60, settings.LengthSeconds);
        Assert.Equal(1080, settings.ResolutionHeight);
        Assert.Equal(60, settings.FrameRate);
        Assert.Equal(75, settings.Quality);
        Assert.True(settings.RemindersEnabled);
        Assert.Equal("", settings.StorageDirectory);
        Assert.False(settings.ToggleShortcutEnabled);
        Assert.False(settings.SaveShortcutEnabled);
        Assert.Equal(new(OverlayHotkeyModifiers.Control | OverlayHotkeyModifiers.Alt, Key.F8), settings.ToggleShortcut);
        Assert.Equal(new(OverlayHotkeyModifiers.Control | OverlayHotkeyModifiers.Alt, Key.F9), settings.SaveShortcut);
        Assert.NotEqual(settings.ToggleShortcut, settings.SaveShortcut);
        Assert.All(new[] { Key.H, Key.R, Key.M }, existing =>
        {
            Assert.NotEqual(existing, settings.ToggleShortcutKey);
            Assert.NotEqual(existing, settings.SaveShortcutKey);
        });
        Assert.Equal(Enumerable.Range(1, 10).Select(value => value * 30), ClipsSettings.LengthChoices);
        Assert.Equal(new[] { 360, 480, 720, 1080, 1440, 2160 }, ClipsSettings.ResolutionChoices);
        Assert.Equal(new[] { 30, 60 }, ClipsSettings.FrameRateChoices);
    }

    [Fact]
    public void LegacyAudioIsGameOnlyAndSystemAudioRoundTrips()
    {
        var legacy = JsonSerializer.Deserialize<ClipsSettings>("{\"LengthSeconds\":60}")!;
        Assert.False(legacy.CaptureSystemAudio);
        Assert.False(JsonSerializer.Deserialize<ClipRecordingSpec>("{\"LengthSeconds\":60,\"ResolutionHeight\":1080,\"FrameRate\":60,\"Quality\":75}")!.CaptureSystemAudio);
        legacy.CaptureSystemAudio = true;
        Assert.True(JsonSerializer.Deserialize<ClipsSettings>(JsonSerializer.Serialize(legacy.Clone()))!.CaptureSystemAudio);
    }

    [Fact]
    public void InvalidExportLocationDoesNotDisablePrivateRecordingOrInventAFolder()
    {
        var settings = new ClipsSettings
        {
            Enabled = true,
            LengthSeconds = 61,
            ResolutionHeight = 900,
            FrameRate = 120,
            Quality = 101,
            StorageDirectory = "relative",
            ToggleShortcutEnabled = true,
            ToggleShortcutModifiers = OverlayHotkeyModifiers.None,
            ToggleShortcutKey = Key.None,
            SaveShortcutEnabled = true,
            SaveShortcutModifiers = OverlayHotkeyModifiers.Alt,
            SaveShortcutKey = Key.F4
        };
        settings.Normalize();
        Assert.True(settings.Enabled);
        Assert.Equal("", settings.StorageDirectory);
        Assert.Equal(60, settings.LengthSeconds);
        Assert.Equal(1080, settings.ResolutionHeight);
        Assert.Equal(60, settings.FrameRate);
        Assert.Equal(100, settings.Quality);
        Assert.False(settings.ToggleShortcutEnabled);
        Assert.False(settings.SaveShortcutEnabled);
        Assert.Equal(new(OverlayHotkeyModifiers.Control | OverlayHotkeyModifiers.Alt, Key.F8), settings.ToggleShortcut);
        Assert.Equal(new(OverlayHotkeyModifiers.Control | OverlayHotkeyModifiers.Alt, Key.F9), settings.SaveShortcut);
        settings.Quality = -1;
        settings.Normalize();
        Assert.Equal(10, settings.Quality);
    }

    [Fact]
    public void OlderSettingsDefaultToHiddenBorderAndRetainLegacyLibraryForImport()
    {
        var settings = JsonSerializer.Deserialize<ClipsSettings>("""{"Enabled":true,"StorageDirectory":"C:\\Clips"}""")!;
        settings.Normalize();
        Assert.False(settings.ShowCaptureBorder);
        Assert.True(settings.UsesPrivateLibrary);
        Assert.Equal(@"C:\Clips", settings.LegacyLibraryDirectory);
        settings.StorageDirectory = @"C:\Exports";
        settings.Normalize();
        Assert.Equal(@"C:\Clips", settings.LegacyLibraryDirectory);
        Assert.True(settings.Enabled);
    }

    [Fact]
    public void DuplicateEnabledShortcutsDoNotRegisterTheSameActionTwice()
    {
        var settings = new ClipsSettings
        {
            ToggleShortcutEnabled = true,
            SaveShortcutEnabled = true,
            SaveShortcutModifiers = OverlayHotkeyModifiers.Control | OverlayHotkeyModifiers.Alt,
            SaveShortcutKey = Key.F8
        };
        settings.Normalize();
        Assert.True(settings.ToggleShortcutEnabled);
        Assert.False(settings.SaveShortcutEnabled);
    }

    [Fact]
    public void ExistingValidShortcutsArePreservedWhenDefaultsChange()
    {
        var settings = new ClipsSettings
        {
            ToggleShortcutEnabled = true,
            ToggleShortcutModifiers = OverlayHotkeyModifiers.Control | OverlayHotkeyModifiers.Shift,
            ToggleShortcutKey = Key.C,
            SaveShortcutEnabled = true,
            SaveShortcutModifiers = OverlayHotkeyModifiers.Control | OverlayHotkeyModifiers.Shift,
            SaveShortcutKey = Key.S
        };
        var originalToggle = settings.ToggleShortcut;
        var originalSave = settings.SaveShortcut;
        settings.Normalize();
        Assert.True(settings.ToggleShortcutEnabled);
        Assert.True(settings.SaveShortcutEnabled);
        Assert.Equal(originalToggle, settings.ToggleShortcut);
        Assert.Equal(originalSave, settings.SaveShortcut);
    }

    [Fact]
    public void SettingsRoundTripAndCloneWithoutTouchingStorage()
    {
        var folder = Path.Combine(Path.GetTempPath(), "WispClipsSettings", Guid.NewGuid().ToString("N"));
        var settings = new ClipsSettings
        {
            Enabled = true,
            LengthSeconds = 300,
            ShowCaptureBorder = true,
            ResolutionHeight = 2160,
            FrameRate = 30,
            Quality = 42,
            RemindersEnabled = false,
            StorageDirectory = folder,
            ToggleShortcutEnabled = true,
            ToggleShortcutModifiers = OverlayHotkeyModifiers.Control | OverlayHotkeyModifiers.Alt,
            ToggleShortcutKey = Key.F8,
            SaveShortcutEnabled = true,
            SaveShortcutKey = Key.F9
        };
        settings.Normalize();
        var loaded = JsonSerializer.Deserialize<ClipsSettings>(JsonSerializer.Serialize(settings))!;
        loaded.Normalize();
        Assert.Equal(JsonSerializer.Serialize(settings), JsonSerializer.Serialize(loaded));
        Assert.False(Directory.Exists(folder));
        var clone = loaded.Clone();
        clone.LengthSeconds = 30;
        Assert.Equal(300, loaded.LengthSeconds);
    }

    [Theory]
    [InlineData("relative")]
    [InlineData(@"C:\clip:stream")]
    [InlineData(@"C:\NUL")]
    [InlineData(@"C:\COM1\clips")]
    [InlineData(@"C:\clips.\nested")]
    [InlineData(@"\\?\C:\clips")]
    [InlineData(@"\\.\pipe\clips")]
    [InlineData(@"\\example.invalid\share\clips")]
    public void UnsafeStorageSyntaxIsRejectedWithoutIo(string candidate)
    {
        Assert.False(ClipsSettings.TryNormalizeStorageDirectory(candidate, out var result));
        Assert.Empty(result);
    }

    [Fact]
    public void ExportPathSyntaxIsSeparateFromLocalRecordingStorage()
    {
        const string destination = @"\\example.invalid\share\clips";
        Assert.True(ClipsSettings.TryNormalizeDirectory(destination, out var normalized));
        Assert.Equal(destination, normalized);
        Assert.False(ClipsSettings.TryNormalizeStorageDirectory(destination, out _));
    }

    [Theory]
    [InlineData(259, true)]
    [InlineData(260, false)]
    public void RecordingGuardUsesTheFinalMediaPathWithoutClearingExistingSettings(int mediaPathLength, bool allowed)
    {
        var directory = @"C:\" + new string('a', mediaPathLength - 40);
        Assert.Equal(mediaPathLength, Path.Combine(directory, Guid.Empty.ToString("N") + ".mp4").Length);
        Assert.Equal(allowed, ClipsSettings.CanRecordToDirectory(directory));
        Assert.Equal(allowed, ClipsSettings.CanRecordToDirectory(directory + Path.DirectorySeparatorChar));
        var settings = new ClipsSettings { Enabled = true, StorageDirectory = directory };
        settings.Normalize();
        Assert.Equal(directory, settings.StorageDirectory);
        Assert.True(settings.Enabled);
    }

    [Fact]
    public void RecordingGuardHandlesDriveRootsAndCountsUtf16Characters()
    {
        Assert.True(ClipsSettings.CanRecordToDirectory(@"C:\"));
        var directory = @"C:\" + string.Concat(Enumerable.Repeat("\U0001F3CE", 110));
        Assert.Equal(260, Path.Combine(directory, Guid.Empty.ToString("N") + ".mp4").Length);
        Assert.False(ClipsSettings.CanRecordToDirectory(directory));
    }
}
