using System.Diagnostics;

namespace Wisp.App;

public sealed partial class AppController
{
    internal event Action<ConnectionReport>? ConnectionReportChanged;
    private ConnectionReport? _lastConnectionReport;

    internal ConnectionReport GetConnectionReport()
    {
        var now = DateTimeOffset.UtcNow;
        var focus = _forzaFocusService.GetState(now);
        var state = _receiver.Latest;
        var snapshot = _nativeHudProcessService.SnapshotFor(state?.CarOrdinal ?? 0);
        var visibility = EvaluateNativeGameplayVisibility(snapshot, Stopwatch.GetTimestamp());
        var knownWindow = focus.IsForzaForeground && focus.ForegroundWindow != IntPtr.Zero
            || WindowZOrder.IsWindowAvailable(_lastConfirmedForzaWindow);
        return ConnectionExplanation.Describe(new(
            Settings.RequiresSetup, _runtimeSuspended, focus.IsForzaRunning, knownWindow,
            _freshness.GetState(Stopwatch.GetTimestamp()) == Wisp.Core.TelemetryConnectionState.Connected,
            _receiver.IsRunning, _cachedStatistics.ListenerError is not null, state is not null,
            _nativeHudTelemetryActive, _manualOverlayHidden, _overlayVisibleRequested, Settings.OverlayOpacity,
            visibility.Visibility, visibility.Fresh, snapshot.Status, Settings.UdpPort, ViewModel.OverlayHotkeyText));
    }

    private void RefreshOpenConnectionPanel()
    {
        if (ConnectionReportChanged is null) return;
        var report = GetConnectionReport();
        if (report == _lastConnectionReport) return;
        _lastConnectionReport = report;
        ConnectionReportChanged.Invoke(report);
    }
}
