namespace Wisp.App;

public readonly record struct BoostDisplay(
    bool IsAvailable,
    double PressurePsi,
    double LearnedPeakPsi,
    double Fraction,
    double ScaleMaximumPsi,
    double ScaleMinimumPsi = 0)
{
    public static BoostDisplay Unavailable => new(false, 0, 0, 0, 70);
}

public sealed class BoostDisplayModel
{
    private const double ActivityThresholdPsi = 0.5;
    private const double GaugeMaximumPsi = 70;
    private int _carOrdinal;
    private bool _forcedInduction;
    private double _peakPsi;

    internal bool HasDetectedBoost => _forcedInduction;

    public BoostDisplay Calculate(int carOrdinal, bool isElectric, double pressurePsi, bool showVacuum = false)
    {
        if (carOrdinal != _carOrdinal)
        {
            _carOrdinal = carOrdinal;
            _forcedInduction = false;
            _peakPsi = 0;
        }

        if (isElectric)
        {
            _forcedInduction = false;
            _peakPsi = 0;
            return BoostDisplay.Unavailable;
        }

        if (carOrdinal <= 0 || !double.IsFinite(pressurePsi))
        {
            return BoostDisplay.Unavailable;
        }

        // Vacuum alone does not identify forced induction. Require positive
        // boost before retaining vacuum readings for this car/session.
        if (pressurePsi >= ActivityThresholdPsi)
        {
            _forcedInduction = true;
        }

        if (!_forcedInduction)
        {
            return new BoostDisplay(true, 0, 0, 0, GaugeMaximumPsi,
                BoostPressureUnits.AnalogMinimum(BoostPressureUnit.Psi, showVacuum));
        }

        var displayedPressure = showVacuum ? pressurePsi : Math.Max(0, pressurePsi);
        _peakPsi = Math.Max(_peakPsi, Math.Max(0, pressurePsi));
        var denominator = Math.Max(_peakPsi, 5);
        return new BoostDisplay(
            true,
            displayedPressure,
            _peakPsi,
            Math.Clamp(displayedPressure / denominator, 0, 1),
            GaugeMaximumPsi,
            BoostPressureUnits.AnalogMinimum(BoostPressureUnit.Psi, showVacuum));
    }

    public void Reset()
    {
        _carOrdinal = 0;
        _forcedInduction = false;
        _peakPsi = 0;
    }
}
