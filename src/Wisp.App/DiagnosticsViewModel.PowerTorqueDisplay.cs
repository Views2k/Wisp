using System.Diagnostics;
using Wisp.Core;

namespace Wisp.App;

public sealed partial class DiagnosticsViewModel
{
    private readonly PowerTorqueDisplayModel _powerTorqueDisplayModel = new();
    private readonly PowerTorqueNeedlePlayback _powerTorqueNeedlePlayback = new();
    private PowerTorqueDisplay _powerTorqueDisplay = PowerTorqueDisplay.Unavailable;
    private int _powerTorqueCarOrdinal;
    internal NativePowerTorqueInput NativePowerTorqueInput { get; private set; }

    private void PublishNativePowerTorque(PowerTorqueDisplay display, int car, uint gameTime,
        long received, bool reset = false)
    {
        NativePowerTorqueInput = new(display, car, gameTime, received, Stopwatch.GetTimestamp(),
            NativePowerTorqueInput.Revision + (reset ? 1 : 0));
        OnPropertyChanged(nameof(NativePowerTorqueInput));
    }

    public PowerTorqueDisplay PowerTorqueDisplay
    {
        get => _powerTorqueDisplay;
        private set
        {
            var previous = _powerTorqueDisplay;
            if (Set(ref _powerTorqueDisplay, value))
            {
                OnPropertyChanged(nameof(PreviewPowerTorqueDisplay));
                if (previous.Available != value.Available || previous.PeakPowerBhp != value.PeakPowerBhp ||
                    previous.PeakTorqueNm != value.PeakTorqueNm)
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
        _powerTorqueDisplayModel.SmoothingMilliseconds = PowerTorqueSmoothingMilliseconds;
        _powerTorqueDisplayModel.ShowNegative = PowerTorqueShowNegative;
        _powerTorqueDisplayModel.DriftModeEnabled = PowerTorqueDriftModeEnabled;
        if (state.CarOrdinal > 0 && state.CarOrdinal != _powerTorqueCarOrdinal)
        {
            _powerTorqueCarOrdinal = state.CarOrdinal;
            RestorePowerTorqueRange();
            OnPropertyChanged(nameof(PowerTorqueRangeCaption));
        }
        var display = _powerTorqueDisplayModel.Observe(
            state.CarOrdinal, state.GameTimestampMilliseconds, state.PowerWatts, state.TorqueNm, state.ReceivedTimestamp,
            driftInput: new PowerTorqueDriftInput(state.IsRaceOn, state.Accelerator,
                state.GroundSpeedMetersPerSecond, state.EngineRpm, state.Gear, state.IsElectric));
        PublishNativePowerTorque(display, state.CarOrdinal, state.GameTimestampMilliseconds, state.ReceivedTimestamp ?? 0);
        PowerTorqueDisplay = _powerTorqueNeedlePlayback.Observe(display, state.CarOrdinal,
            state.GameTimestampMilliseconds, Stopwatch.GetTimestamp(), state.ReceivedTimestamp);
    }

    internal void AdvancePowerTorqueNeedles(long timestamp)
    {
        if (HasLiveTelemetry && (PowerGaugeEnabled || TorqueGaugeEnabled) && _powerTorqueNeedlePlayback.HasSamples)
            PowerTorqueDisplay = _powerTorqueNeedlePlayback.Sample(timestamp);
    }

    private void ClearPowerTorqueDisplay()
    {
        _powerTorqueDisplayModel.ResetCurrent();
        PublishNativePowerTorque(_powerTorqueDisplayModel.Current, _powerTorqueCarOrdinal, 0, 0, reset: true);
        _powerTorqueNeedlePlayback.Reset();
        PowerTorqueDisplay = _powerTorqueDisplayModel.Current;
    }

    private void ResetPowerTorquePeaks()
    {
        _powerTorqueDisplayModel.ResetPeaks();
        PublishNativePowerTorque(_powerTorqueDisplayModel.Current, NativePowerTorqueInput.CarOrdinal,
            NativePowerTorqueInput.GameTimestampMilliseconds, NativePowerTorqueInput.ReceivedTimestamp);
        _powerTorqueNeedlePlayback.UpdatePeaks(_powerTorqueDisplayModel.Current);
        PowerTorqueDisplay = PowerTorqueDisplay with
        {
            PeakPowerBhp = _powerTorqueDisplayModel.Current.PeakPowerBhp,
            PeakTorqueNm = _powerTorqueDisplayModel.Current.PeakTorqueNm
        };
    }

    internal void RefreshPowerTorqueDisplayOptions()
    {
        var changed = _powerTorqueDisplayModel.SmoothingMilliseconds != PowerTorqueSmoothingMilliseconds ||
            _powerTorqueDisplayModel.ShowNegative != PowerTorqueShowNegative ||
            _powerTorqueDisplayModel.DriftModeEnabled != PowerTorqueDriftModeEnabled;
        _powerTorqueDisplayModel.SmoothingMilliseconds = PowerTorqueSmoothingMilliseconds;
        _powerTorqueDisplayModel.ShowNegative = PowerTorqueShowNegative;
        _powerTorqueDisplayModel.DriftModeEnabled = PowerTorqueDriftModeEnabled;
        if (changed)
        {
            PublishNativePowerTorque(_powerTorqueDisplayModel.Current, NativePowerTorqueInput.CarOrdinal,
                NativePowerTorqueInput.GameTimestampMilliseconds, NativePowerTorqueInput.ReceivedTimestamp, reset: true);
            _powerTorqueNeedlePlayback.Reset();
            PowerTorqueDisplay = _powerTorqueDisplayModel.Current;
        }
    }
}
