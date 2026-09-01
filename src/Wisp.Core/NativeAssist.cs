namespace Wisp.Core;

public enum NativeGameplayVisibility
{
    Unknown,
    Visible,
    Hidden
}

public enum NativeAssistProviderStatus
{
    Unavailable,
    GameNotRunning,
    UnsupportedBuild,
    AccessDenied,
    InvalidSourceVector,
    InvalidProvider,
    PlayerNotUnique,
    TelemetryMismatch,
    ReadFailure,
    Ready
}

public sealed record NativeAssistSnapshot(
    bool Available,
    ulong Generation,
    int CarOrdinal,
    NativeAssistProviderStatus Status,
    bool IsABSAvailable,
    bool IsABSOn,
    bool IsTCRAvailable,
    bool IsTCROn,
    bool IsSTMAvailable,
    bool IsSTMOn,
    bool IsLCAvailable,
    bool IsLCOn,
    double ABSAngle,
    double TCRAngle,
    double STMAngle,
    double LCAngle,
    bool HeadlightStateAvailable = false,
    bool AreHeadlightsOn = false)
{
    public static NativeAssistSnapshot Unavailable(
        NativeAssistProviderStatus status = NativeAssistProviderStatus.Unavailable,
        ulong generation = 0,
        int carOrdinal = 0) =>
        new(
            false,
            generation,
            carOrdinal,
            status,
            false,
            false,
            false,
            false,
            false,
            false,
            false,
            false,
            0,
            0,
            0,
            0);
}

public readonly record struct NativeElectricGearState(
    bool Available,
    int Gear,
    int GearNext,
    int GearPrevious,
    int GearGaugeState,
    bool UseDriveFor1)
{
    public static NativeElectricGearState Unavailable => default;
}

public readonly record struct NativeDisplayedSpeedState(
    bool Available,
    int Hundreds,
    int Tens,
    int Ones,
    bool SpeedLessOrEqualOne,
    bool SpeedLessTen,
    bool SpeedLessHundred,
    SpeedUnit? Unit = null)
{
    public int Value => (Hundreds * 100) + (Tens * 10) + Ones;

    public bool IsUsable =>
        Available &&
        Hundreds is >= 0 and <= 9 &&
        Tens is >= 0 and <= 9 &&
        Ones is >= 0 and <= 9 &&
        Unit is SpeedUnit.MilesPerHour or SpeedUnit.KilometersPerHour;

    public static NativeDisplayedSpeedState Unavailable => default;
}

public sealed record NativeHudSnapshot(
    bool Available,
    ulong Generation,
    int CarOrdinal,
    NativeAssistProviderStatus Status,
    ExactRedlineResult ExactRedline,
    double TachometerMaximumRpm,
    NativeAssistSnapshot Assists,
    NativeGameplayVisibility GameplayVisibility = NativeGameplayVisibility.Unknown,
    long VisibilityObservedTimestamp = 0L,
    double NativeNeedleAngleDegrees = double.NaN,
    double NativeNeedleBlurAmount = double.NaN,
    double NativeRegenFillAmount = double.NaN,
    double NativePowerFillAmount = double.NaN,
    double NativeRegenPowerRatio = double.NaN,
    double NativeElectricMaximumSpeed = double.NaN,
    long NativeGaugeObservedTimestamp = 0L,
    NativeElectricGearState ElectricGearState = default,
    NativeDisplayedSpeedState DisplayedSpeedState = default)
{
    public bool HasNativeNeedleState =>
        double.IsFinite(NativeNeedleAngleDegrees) &&
        double.IsFinite(NativeNeedleBlurAmount);

    public bool HasNativeElectricGaugeState =>
        IsUnitRatio(NativeRegenFillAmount) &&
        IsUnitRatio(NativePowerFillAmount) &&
        IsUnitRatio(NativeRegenPowerRatio) &&
        double.IsFinite(NativeElectricMaximumSpeed) &&
        NativeElectricMaximumSpeed > 0;

    public bool HasAvailableCapabilities =>
        Available || Assists.Available || HasNativeNeedleState || HasNativeElectricGaugeState ||
        ElectricGearState.Available || DisplayedSpeedState.IsUsable ||
        (VisibilityObservedTimestamp > 0 &&
         GameplayVisibility is NativeGameplayVisibility.Visible or NativeGameplayVisibility.Hidden);

    public static NativeHudSnapshot Unavailable(
        NativeAssistProviderStatus status = NativeAssistProviderStatus.Unavailable,
        ulong generation = 0,
        int carOrdinal = 0) =>
        new(
            false,
            generation,
            carOrdinal,
            status,
            ExactRedlineResult.Unavailable(ToExactRedlineStatus(status)),
            0,
            NativeAssistSnapshot.Unavailable(status, generation, carOrdinal));

    private static bool IsUnitRatio(double value) =>
        double.IsFinite(value) && value is >= 0 and <= 1;

    public static ExactRedlineStatus ToExactRedlineStatus(NativeAssistProviderStatus status) => status switch
    {
        NativeAssistProviderStatus.GameNotRunning => ExactRedlineStatus.GameNotRunning,
        NativeAssistProviderStatus.UnsupportedBuild => ExactRedlineStatus.UnsupportedBuild,
        NativeAssistProviderStatus.AccessDenied => ExactRedlineStatus.AccessDenied,
        NativeAssistProviderStatus.InvalidSourceVector or NativeAssistProviderStatus.InvalidProvider =>
            ExactRedlineStatus.InvalidProvider,
        NativeAssistProviderStatus.PlayerNotUnique => ExactRedlineStatus.PlayerNotUnique,
        NativeAssistProviderStatus.TelemetryMismatch => ExactRedlineStatus.TelemetryMismatch,
        NativeAssistProviderStatus.ReadFailure => ExactRedlineStatus.ReadFailure,
        _ => ExactRedlineStatus.Unavailable
    };
}

public readonly record struct NativeAssistRawState(
    bool IsABSAvailable,
    bool IsTCRAvailable,
    bool IsSTMAvailable,
    bool IsLCAvailable,
    uint ABSState,
    float TCRPrimary,
    float TCRSecondary,
    float TCRTertiary,
    IReadOnlyList<float> TCRWheelValues,
    uint STMState,
    float LCPrimary,
    uint LCMode,
    float LCSecondary);

public static class NativeAssistStateCalculator
{
    public static NativeAssistSnapshot Calculate(
        NativeAssistRawState raw,
        float threshold,
        ulong generation,
        int carOrdinal)
    {
        ArgumentNullException.ThrowIfNull(raw.TCRWheelValues);
        if (raw.TCRWheelValues.Count != 4 || !float.IsFinite(threshold))
        {
            return NativeAssistSnapshot.Unavailable(
                NativeAssistProviderStatus.ReadFailure,
                generation,
                carOrdinal);
        }

        var lcFirst = IsUnorderedOrLessThan(threshold, raw.LCPrimary);
        var lcOn = raw.LCMode == 2 &&
                   (!lcFirst || IsUnorderedOrLessThan(threshold, raw.LCSecondary));
        var tcrOn = IsUnorderedOrLessThan(threshold, raw.TCRPrimary) ||
                    IsUnorderedOrLessThan(threshold, raw.TCRSecondary) ||
                    IsUnorderedOrLessThan(threshold, raw.TCRTertiary) ||
                    raw.TCRWheelValues.Any(value => IsUnorderedOrLessThan(threshold, value));
        var angles = NativeAssistAngles.Calculate(
            raw.IsABSAvailable,
            raw.IsTCRAvailable,
            raw.IsSTMAvailable,
            raw.IsLCAvailable);

        return new NativeAssistSnapshot(
            true,
            generation,
            carOrdinal,
            NativeAssistProviderStatus.Ready,
            raw.IsABSAvailable,
            raw.IsABSAvailable && raw.ABSState != 0,
            raw.IsTCRAvailable,
            raw.IsTCRAvailable && tcrOn,
            raw.IsSTMAvailable,
            raw.IsSTMAvailable && raw.STMState != 0,
            raw.IsLCAvailable,
            raw.IsLCAvailable && lcOn,
            angles.ABS,
            angles.TCR,
            angles.STM,
            angles.LC);
    }

    public static bool IsUnorderedOrLessThan(float left, float right) =>
        float.IsNaN(left) || float.IsNaN(right) || left < right;

    public static int MapWheelIndex(int requestedWheelId, int firstId, int secondId, int thirdId) =>
        firstId == requestedWheelId
            ? 0
            : secondId == requestedWheelId
                ? 1
                : thirdId == requestedWheelId
                    ? 2
                    : 3;
}

public readonly record struct NativeAssistAngleSet(double ABS, double TCR, double STM, double LC);

public static class NativeAssistAngles
{
    public static NativeAssistAngleSet Calculate(
        bool absAvailable,
        bool tcrAvailable,
        bool stmAvailable,
        bool lcAvailable)
    {
        var count = Convert.ToInt32(lcAvailable) +
                    Convert.ToInt32(absAvailable) +
                    Convert.ToInt32(tcrAvailable) +
                    Convert.ToInt32(stmAvailable);
        var angle = count switch
        {
            2 => 20d,
            3 => 40d,
            4 => 60d,
            _ => 0d
        };
        var abs = 0d;
        var tcr = 0d;
        var stm = 0d;
        var lc = 0d;

        if (absAvailable)
        {
            abs = angle;
            angle -= 40;
        }

        if (tcrAvailable)
        {
            tcr = angle;
            angle -= 40;
        }

        if (stmAvailable)
        {
            stm = angle;
            angle -= 40;
        }

        if (lcAvailable)
        {
            lc = angle;
        }

        return new NativeAssistAngleSet(abs, tcr, stm, lc);
    }
}
