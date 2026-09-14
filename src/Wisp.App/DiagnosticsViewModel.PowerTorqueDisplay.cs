using Wisp.Core;

namespace Wisp.App;

public sealed partial class DiagnosticsViewModel
{
    private readonly PowerTorqueDisplayModel _powerTorqueDisplayModel = new();
    private PowerTorqueDisplay _powerTorqueDisplay = PowerTorqueDisplay.Unavailable;
    private int _powerTorqueCarOrdinal;

    public PowerTorqueDisplay PowerTorqueDisplay
    {
        get => _powerTorqueDisplay;
        private set
        {
            if (Set(ref _powerTorqueDisplay, value))
            {
                OnPropertyChanged(nameof(PreviewPowerTorqueDisplay));
                OnPropertyChanged(nameof(CanSetPowerTorqueScales));
            }
        }
    }

    public PowerTorqueDisplay PreviewPowerTorqueDisplay => PowerTorqueDisplay.Available
        ? PowerTorqueDisplay
        : new PowerTorqueDisplay(true, 427, 507, 612, 690);

    public double PreviewPowerGaugeMaximum => PowerGaugeMaximum;
    public double PreviewTorqueGaugeMaximum => TorqueGaugeMaximum;
    public string PowerTorqueRangeCaption => _powerTorqueCarOrdinal <= 0
        ? "Default range · no car detected yet"
        : HasLiveTelemetry ? "Range for the current car" : "Range for the last detected car";
    public bool CanSetPowerTorqueScales => HasLiveTelemetry && PowerTorqueDisplay.Available && _powerTorqueCarOrdinal > 0 &&
        PowerTorqueDisplay.PeakPowerBhp > 0 && PowerTorqueDisplay.PeakTorqueNm > 0;

    private void UpdatePowerTorqueDisplay(VehicleState state)
    {
        if (state.CarOrdinal > 0 && state.CarOrdinal != _powerTorqueCarOrdinal)
        {
            _powerTorqueCarOrdinal = state.CarOrdinal;
            RestorePowerTorqueRange();
            OnPropertyChanged(nameof(PowerTorqueRangeCaption));
        }
        PowerTorqueDisplay = _powerTorqueDisplayModel.Observe(
            state.CarOrdinal, state.GameTimestampMilliseconds, state.PowerWatts, state.TorqueNm);
    }

    private void ClearPowerTorqueDisplay()
    {
        _powerTorqueDisplayModel.ResetCurrent();
        PowerTorqueDisplay = _powerTorqueDisplayModel.Current;
    }

    private void ResetPowerTorquePeaks()
    {
        _powerTorqueDisplayModel.ResetPeaks();
        PowerTorqueDisplay = _powerTorqueDisplayModel.Current;
    }
}
