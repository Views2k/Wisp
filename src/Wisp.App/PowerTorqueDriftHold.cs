using Wisp.Core;

namespace Wisp.App;

public readonly record struct PowerTorqueDriftInput(bool IsRaceOn, byte Accelerator,
    double GroundSpeedMetersPerSecond, double EngineRpm, TransmissionGear Gear, bool IsElectric = false);

// This is a display preference for short output interruptions, not a limiter detector.
internal sealed class PowerTorqueDriftHold
{
    internal const double MaximumCutMilliseconds = 450;
    internal const double MaximumSampleGapMilliseconds = 250;
    private double _referencePower;
    private double _referenceTorque;
    private double _cutMilliseconds;
    private bool _hasReference;

    internal bool PulseAllowed { get; private set; }

    internal bool Observe(double powerBhp, double torqueNm, PowerTorqueDriftInput? input,
        bool continuous, double elapsedMilliseconds)
    {
        if (input is not { } context || !context.IsRaceOn || context.Accelerator < 64 ||
            !double.IsFinite(context.GroundSpeedMetersPerSecond) || context.GroundSpeedMetersPerSecond < 2 ||
            !context.IsElectric && (!double.IsFinite(context.EngineRpm) || context.EngineRpm < 1_000) ||
            context.Gear < TransmissionGear.First ||
            powerBhp < 0 || torqueNm < 0)
        {
            Reset();
            return false;
        }
        if (!continuous || elapsedMilliseconds > MaximumSampleGapMilliseconds) Reset();

        var cutFraction = _cutMilliseconds > 0 ? .20 : .08;
        var cut = _hasReference && (powerBhp <= Math.Max(2, _referencePower * cutFraction) ||
            torqueNm <= Math.Max(2, _referenceTorque * cutFraction));
        if (cut)
        {
            _cutMilliseconds += elapsedMilliseconds;
            if (_cutMilliseconds <= MaximumCutMilliseconds)
            {
                PulseAllowed = true;
                return true;
            }
            Reset();
            return false;
        }

        // Only genuine positive output can arm another hold; retained display values
        // never reach this path. Continuous zero output always exhausts the budget.
        if (powerBhp >= 25 && torqueNm >= 25)
        {
            _referencePower = powerBhp;
            _referenceTorque = torqueNm;
            _hasReference = true;
            _cutMilliseconds = 0;
            PulseAllowed = true;
        }
        else Reset();
        return false;
    }

    internal void Reset()
    {
        _referencePower = 0;
        _referenceTorque = 0;
        _cutMilliseconds = 0;
        _hasReference = false;
        PulseAllowed = false;
    }
}
