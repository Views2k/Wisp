using Wisp.Core;

namespace Wisp.App;

internal static class NativeElectricGearModel
{
    public static string? CurrentToken(
        NativeElectricGearState state,
        NativeGaugeMode mode,
        TransmissionGear telemetryGear = TransmissionGear.Unknown)
    {
        if (!state.Available)
        {
            return TelemetryToken(telemetryGear, mode);
        }

        return mode == NativeGaugeMode.Digital
            ? DigitalToken(state.Gear, state.UseDriveFor1)
            : ElectricToken(state.Gear, state.UseDriveFor1);
    }

    public static string? AdjacentToken(NativeElectricGearState state, bool next)
    {
        if (!state.Available)
        {
            return null;
        }

        return ElectricToken(
            next ? state.GearNext : state.GearPrevious,
            state.UseDriveFor1);
    }

    public static bool IsMultiGear(NativeElectricGearState state) =>
        state.Available && state.GearGaugeState is >= 0 and <= 4;

    public static string? GaugeAsset(NativeElectricGearState state, bool digital)
    {
        if (!IsMultiGear(state))
        {
            return null;
        }

        if (!digital)
        {
            return $"GearGauge{state.GearGaugeState}.png";
        }

        return state.GearGaugeState == 4
            ? "HUD_EV_Digital_Bar_max.png"
            : $"HUD_EV_Digital_Bar_{state.GearGaugeState}bar.png";
    }

    private static string? ElectricToken(int gear, bool useDriveFor1) => gear switch
    {
        0 => "Reverse",
        1 when useDriveFor1 => "Drive",
        >= 1 and <= 4 => gear.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => null
    };

    private static string? DigitalToken(int gear, bool useDriveFor1) => gear switch
    {
        0 => "R",
        1 when useDriveFor1 => "Drive",
        >= 1 and <= 10 => gear.ToString(System.Globalization.CultureInfo.InvariantCulture),
        11 => "N",
        _ => null
    };

    private static string? TelemetryToken(TransmissionGear gear, NativeGaugeMode mode) =>
        mode == NativeGaugeMode.Digital
            ? gear switch
            {
                TransmissionGear.Reverse => "R",
                TransmissionGear.Neutral => "N",
                >= TransmissionGear.First and <= TransmissionGear.Tenth => "Drive",
                _ => null
            }
            : gear switch
            {
                TransmissionGear.Reverse => "Reverse",
                >= TransmissionGear.First and <= TransmissionGear.Tenth => "Drive",
                _ => null
            };
}
