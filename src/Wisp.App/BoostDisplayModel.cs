namespace Wisp.App;

public readonly record struct BoostDisplay(
    bool IsAvailable,
    double PressurePsi,
    double LearnedPeakPsi,
    double Fraction,
    double ScaleMaximumPsi)
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

    public BoostDisplay Calculate(int carOrdinal, bool isElectric, double pressurePsi)
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

        // FH6 reports vacuum on forced-induction cars before they make positive
        // boost. Accepting that first non-zero sample lets the gauge appear with
        // the speedometer while a zero-only naturally aspirated car stays gated.
        if (Math.Abs(pressurePsi) >= ActivityThresholdPsi)
        {
            _forcedInduction = true;
        }

        if (!_forcedInduction)
        {
            return BoostDisplay.Unavailable;
        }

        var displayedPressure = Math.Max(0, pressurePsi);
        _peakPsi = Math.Max(_peakPsi, displayedPressure);
        var denominator = Math.Max(_peakPsi, 5);
        return new BoostDisplay(
            true,
            displayedPressure,
            _peakPsi,
            Math.Clamp(displayedPressure / denominator, 0, 1),
            GaugeMaximumPsi);
    }

    public void Reset()
    {
        _carOrdinal = 0;
        _forcedInduction = false;
        _peakPsi = 0;
    }
}
