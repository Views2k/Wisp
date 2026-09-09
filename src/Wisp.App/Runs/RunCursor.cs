using System.Globalization;
using Wisp.Core;
using Wisp.Core.Runs;

namespace Wisp.App.Runs;

internal static class RunCursor
{
    internal static string Describe(string label, RecordedRun run, double seconds, RunInterval? bounds = null)
    {
        var rpm = RunPresentation.RecordedValueAt(run.Samples, sample => sample.State.EngineRpm, seconds, bounds);
        var gear = RunPresentation.RecordedValueAt(run.Samples,
            sample => sample.State.Gear == TransmissionGear.Unknown ? null : (double)sample.State.Gear, seconds, bounds);
        if (rpm is null && gear is null)
        {
            return $"{label} · Reading unavailable";
        }

        var rpmText = rpm is { } value ? value.ToString("N0", CultureInfo.CurrentCulture) + " RPM" : "RPM unavailable";
        var gearText = gear switch
        {
            (double)TransmissionGear.Reverse => "reverse",
            (double)TransmissionGear.Neutral => "neutral",
            >= 1 and <= 10 => "gear " + gear.Value.ToString("0", CultureInfo.CurrentCulture),
            _ => "gear unavailable"
        };
        return $"{label} · {rpmText} · {gearText}";
    }
}
