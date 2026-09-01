using Wisp.Core;
using Wisp.Telemetry;

namespace Wisp.App;

public sealed record SetupPreferences(
    int UdpPort,
    SpeedUnit SpeedUnit,
    SpeedSourceMode SpeedSource,
    HudLayoutMode LayoutMode,
    NativeGaugeMode NativeGaugeMode,
    GearDisplayMode GearDisplayMode,
    bool DataOutConfirmed,
    bool DisplayModeConfirmed,
    bool StockHudConfirmed)
{
    internal static SetupPreferences FromSettings(AppSettings settings) => new(
        settings.UdpPort, settings.SpeedUnit, settings.SpeedSource, settings.LayoutMode,
        settings.NativeGaugeMode, settings.GearDisplayMode, false, false, false);

    internal void Apply(AppSettings settings)
    {
        settings.UdpPort = UdpPort;
        settings.SpeedUnit = SpeedUnit;
        settings.SpeedSource = SpeedSource;
        settings.LayoutMode = LayoutMode;
        settings.NativeGaugeMode = NativeGaugeMode;
        settings.GearDisplayMode = GearDisplayMode;
    }
}

internal static class SetupCompletion
{
    public static void Save(
        AppSettings settings,
        SetupPreferences preferences,
        SetupTelemetryEvidence? evidence,
        Action<AppSettings> save,
        DateTimeOffset completedAtUtc)
    {
        TelemetryUdpReceiver.ValidatePort(preferences.UdpPort);
        if (!Enum.IsDefined(preferences.SpeedUnit) || !Enum.IsDefined(preferences.SpeedSource) ||
            !Enum.IsDefined(preferences.LayoutMode) || !Enum.IsDefined(preferences.NativeGaugeMode) ||
            !Enum.IsDefined(preferences.GearDisplayMode))
        {
            throw new InvalidOperationException("Choose a valid HUD style, speed source, unit, and gear display.");
        }

        if (evidence is null || evidence.Port != preferences.UdpPort)
        {
            throw new InvalidOperationException("Test the selected UDP port successfully before completing setup.");
        }

        var completion = new SetupCompletionRecord
        {
            Version = SetupCompletionRecord.CurrentVersion,
            CompletedAtUtc = completedAtUtc,
            ValidatedUdpPort = evidence.Port,
            ValidatedPackets = evidence.Packets,
            MovingPackets = evidence.MovingPackets,
            ValidatedElapsedMilliseconds = evidence.Elapsed.TotalMilliseconds,
            DataOutConfirmed = preferences.DataOutConfirmed,
            DisplayModeConfirmed = preferences.DisplayModeConfirmed,
            StockHudConfirmed = preferences.StockHudConfirmed
        };
        if (!completion.IsValid)
        {
            throw new InvalidOperationException("Finish the connection test and confirm the game display settings before continuing.");
        }

        var previousPreferences = SetupPreferences.FromSettings(settings);
        var previousCompletion = settings.SetupCompletion;
        var previousLegacyFlag = settings.HasCompletedSetup;
        try
        {
            preferences.Apply(settings);
            settings.SetupCompletion = completion;
            settings.HasCompletedSetup = true;
            // Unlike background preference saves, completion must reach disk
            // before startup is allowed to create the main UI or HUD windows.
            save(settings);
        }
        catch
        {
            previousPreferences.Apply(settings);
            settings.SetupCompletion = previousCompletion;
            settings.HasCompletedSetup = previousLegacyFlag;
            throw;
        }
    }
}
