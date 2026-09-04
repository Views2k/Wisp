using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using Wisp.App;
using Xunit;

namespace Wisp.App.Tests;

public sealed class OverlayHotkeyTests
{
    [Fact]
    public void DefaultsToAValidButOptInShortcutWithoutChangingSettingsRevision()
    {
        var settings = new AppSettings();

        settings.MigrateSettings();

        Assert.Equal(9, settings.SettingsRevision);
        Assert.False(settings.OverlayHotkeyEnabled);
        Assert.Equal(OverlayHotkeyModifiers.Control | OverlayHotkeyModifiers.Shift,
            settings.OverlayHotkeyModifiers);
        Assert.Equal(Key.H, settings.OverlayHotkeyKey);
        Assert.Equal("Ctrl + Shift + H", OverlayHotkeyChord.Default.ToString());
    }

    [Fact]
    public void GlobalRegistrationUsesADedicatedMessageWindowAndNoRepeat()
    {
        var type = typeof(OverlayHotkeyService);
        Assert.Equal(typeof(HwndSource), type.GetField("_window",
            BindingFlags.Instance | BindingFlags.NonPublic)!.FieldType);
        Assert.Equal(0x4000u, type.GetField("NoRepeat",
            BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue());
        var register = type.GetMethod("RegisterHotKey", BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert.Equal("user32.dll", register.GetCustomAttribute<DllImportAttribute>()!.Value);
    }

    [Theory]
    [InlineData(OverlayHotkeyModifiers.None, Key.H)]
    [InlineData(OverlayHotkeyModifiers.Control, Key.LeftShift)]
    [InlineData(OverlayHotkeyModifiers.Alt, Key.Tab)]
    [InlineData(OverlayHotkeyModifiers.Alt, Key.F4)]
    [InlineData(OverlayHotkeyModifiers.Control | OverlayHotkeyModifiers.Alt, Key.Delete)]
    [InlineData(OverlayHotkeyModifiers.Windows, Key.L)]
    public void UnsafeOrReservedShortcutsAreRejected(OverlayHotkeyModifiers modifiers, Key key)
    {
        Assert.False(OverlayHotkeyChord.TryCreate(modifiers, key, out _, out var error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void InvalidPersistedShortcutFailsClosedToTheDisabledDefault()
    {
        var settings = new AppSettings
        {
            OverlayHotkeyEnabled = true,
            OverlayHotkeyModifiers = OverlayHotkeyModifiers.None,
            OverlayHotkeyKey = Key.A
        };

        settings.MigrateSettings();

        Assert.False(settings.OverlayHotkeyEnabled);
        Assert.Equal(OverlayHotkeyChord.Default.Modifiers, settings.OverlayHotkeyModifiers);
        Assert.Equal(OverlayHotkeyChord.Default.Key, settings.OverlayHotkeyKey);
    }

    [Fact]
    public void SettingsServiceRoundTripsTheOptInShortcut()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Wisp.App.Tests", Guid.NewGuid().ToString("N"));
        var service = new SettingsService(Path.Combine(directory, "settings.json"));
        try
        {
            service.Save(new AppSettings
            {
                OverlayHotkeyEnabled = true,
                OverlayHotkeyModifiers = OverlayHotkeyModifiers.Control | OverlayHotkeyModifiers.Alt,
                OverlayHotkeyKey = Key.K
            });

            var loaded = service.Load();

            Assert.True(loaded.OverlayHotkeyEnabled);
            Assert.Equal(OverlayHotkeyModifiers.Control | OverlayHotkeyModifiers.Alt,
                loaded.OverlayHotkeyModifiers);
            Assert.Equal(Key.K, loaded.OverlayHotkeyKey);
            Assert.Equal(9, loaded.SettingsRevision);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public void ManualStateCanOnlySuppressNormalVisibility(
        bool normalVisibility,
        bool manuallyHidden,
        bool expected)
    {
        Assert.Equal(expected,
            AppController.ApplyManualOverlaySuppression(normalVisibility, manuallyHidden));
    }

    [Fact]
    public async Task FailedReplacementKeepsThePreviousWorkingShortcut()
    {
        var settings = CompletedSettings();
        settings.OverlayHotkeyEnabled = true;
        var saved = new List<AppSettings>();
        await using var controller = new AppController(
            settings,
            value => saved.Add(value),
            new SuccessfulStartupRegistration());
        var requested = new OverlayHotkeyChord(OverlayHotkeyModifiers.Alt, Key.J);
        controller.SetOverlayHotkeyRegistration((enabled, chord) =>
            enabled && chord == requested
                ? new OverlayHotkeyRegistrationResult(false, "another app is already using it")
                : OverlayHotkeyRegistrationResult.Success);

        controller.ViewModel.OverlayHotkeyModifiers = requested.Modifiers;
        controller.ViewModel.OverlayHotkeyKey = requested.Key;
        controller.ApplyViewOptions();

        Assert.True(settings.OverlayHotkeyEnabled);
        Assert.Equal(OverlayHotkeyChord.Default.Modifiers, settings.OverlayHotkeyModifiers);
        Assert.Equal(OverlayHotkeyChord.Default.Key, settings.OverlayHotkeyKey);
        Assert.Equal("Ctrl + Shift + H", controller.ViewModel.OverlayHotkeyText);
        Assert.Contains("kept Ctrl + Shift + H", controller.ViewModel.StatusDetail,
            StringComparison.Ordinal);
    }

    private static AppSettings CompletedSettings()
    {
        var settings = new AppSettings { StartWithWindows = false };
        var now = DateTimeOffset.UtcNow;
        var preferences = SetupPreferences.FromSettings(settings) with
        {
            DataOutConfirmed = true,
            DisplayModeConfirmed = true,
            StockHudConfirmed = true
        };
        SetupCompletion.Save(
            settings,
            preferences,
            new SetupTelemetryEvidence(settings.UdpPort, 12, 12, TimeSpan.FromMilliseconds(550), now),
            _ => { },
            now);
        return settings;
    }

    private sealed class SuccessfulStartupRegistration : IStartupRegistrationService
    {
        public void Apply(bool startWithWindows, bool startWithForza)
        {
        }
    }
}
