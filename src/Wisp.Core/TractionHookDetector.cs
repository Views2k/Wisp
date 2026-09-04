namespace Wisp.Core;

public sealed class TractionHookDetector
{
    private const double MinimumGroundSpeedMetersPerSecond = 5.0;
    private const double SlipEvidenceSpeedFraction = 0.15;
    private const double HookedSpeedFraction = 0.06;
    private const double SlipEvidenceRatio = 0.20;
    private const double HookedSlipRatio = 0.10;
    private const int RequiredConvergedSamples = 3;

    private int _carOrdinal;
    private int _convergedSamples;
    private bool _hasSlipEvidence;

    public bool Observe(VehicleState state, double? rollingRadiusMeters)
    {
        var radii = rollingRadiusMeters is { } radius
            ? RollingRadii.Uniform(radius)
            : (RollingRadii?)null;
        return ObserveWithRadii(state, radii);
    }

    public bool ObserveWithRadii(VehicleState state, RollingRadii? rollingRadii)
    {
        if (_carOrdinal != state.CarOrdinal)
        {
            Reset();
            _carOrdinal = state.CarOrdinal;
        }

        var groundSpeed = Math.Abs((double)state.GroundSpeedMetersPerSecond);
        if (rollingRadii is not { } radii || !radii.IsPlausible ||
            groundSpeed < MinimumGroundSpeedMetersPerSecond)
        {
            _convergedSamples = 0;
            _hasSlipEvidence = false;
            return false;
        }

        var wheelSpeed = DrivenWheelSpeed.MetersPerSecond(state, radii);
        var relativeSpeedError = Math.Abs(wheelSpeed - groundSpeed) / groundSpeed;
        var drivenSlipRatio = MaximumDrivenSlipRatio(state);

        if (relativeSpeedError >= SlipEvidenceSpeedFraction || drivenSlipRatio >= SlipEvidenceRatio)
        {
            _hasSlipEvidence = true;
            _convergedSamples = 0;
            return false;
        }

        if (!_hasSlipEvidence || relativeSpeedError > HookedSpeedFraction || drivenSlipRatio > HookedSlipRatio)
        {
            _convergedSamples = 0;
            return false;
        }

        _convergedSamples++;
        if (_convergedSamples < RequiredConvergedSamples)
        {
            return false;
        }

        _hasSlipEvidence = false;
        _convergedSamples = 0;
        return true;
    }

    public void Reset()
    {
        _carOrdinal = 0;
        _convergedSamples = 0;
        _hasSlipEvidence = false;
    }

    private static double MaximumDrivenSlipRatio(VehicleState state)
    {
        var slip = state.TireSlipRatio;
        return state.Drivetrain switch
        {
            DrivetrainType.FrontWheelDrive => Math.Max(Math.Abs((double)slip.FrontLeft), Math.Abs((double)slip.FrontRight)),
            DrivetrainType.RearWheelDrive => Math.Max(Math.Abs((double)slip.RearLeft), Math.Abs((double)slip.RearRight)),
            DrivetrainType.AllWheelDrive => slip.MaximumAbsolute(),
            _ => double.PositiveInfinity
        };
    }
}
