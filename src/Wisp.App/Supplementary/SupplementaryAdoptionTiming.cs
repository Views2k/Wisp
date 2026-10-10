using Wisp.App.DebugLogging;
using Wisp.Telemetry;

namespace Wisp.App.Supplementary;

internal static class SupplementaryAdoptionTiming
{
    internal static void Record(ToolsPerformanceRecorder recorder, long started, long completed,
        bool adopted, TelemetryPublicationTiming? timing)
    {
        if (started <= 0 || completed < started) return;
        recorder.RecordTicks(ToolsPerformanceMetric.UiUpdateWork, false, completed - started);
        if (!adopted) return;
        if (timing is not { IsOrdered: true } t || t.Published > started)
        {
            recorder.RecordDropped(ToolsPerformanceMetric.TelemetryParseWork, false);
            recorder.RecordDropped(ToolsPerformanceMetric.ParseToUiAdoption, false);
            recorder.RecordDropped(ToolsPerformanceMetric.PublishToUiAdoption, false);
            recorder.RecordDropped(ToolsPerformanceMetric.ReceiveToUiAdoption, false);
            return;
        }
        recorder.RecordTicks(ToolsPerformanceMetric.TelemetryParseWork, false, t.Parsed - t.ParseStarted);
        recorder.RecordTicks(ToolsPerformanceMetric.ParseToUiAdoption, false, completed - t.Parsed);
        recorder.RecordTicks(ToolsPerformanceMetric.PublishToUiAdoption, false, completed - t.Published);
        recorder.RecordTicks(ToolsPerformanceMetric.ReceiveToUiAdoption, false, completed - t.Received);
    }

    internal static void RecordNative(ToolsPerformanceRecorder recorder, long started, long completed, bool adopted, long observed)
    {
        if (started <= 0 || completed < started) return;
        recorder.RecordTicks(ToolsPerformanceMetric.NativeUiUpdateWork, false, completed - started);
        if (!adopted) return;
        if (observed <= 0 || observed > started)
            recorder.RecordDropped(ToolsPerformanceMetric.NativeObservationToUiAdoption, false);
        else recorder.RecordTicks(ToolsPerformanceMetric.NativeObservationToUiAdoption, false, completed - observed);
    }
}
