using Wisp.App.DebugLogging;

namespace Wisp.App.Supplementary;

internal static class SupplementaryGameVerification
{
    internal static NativeGameVerification At(HealthContextSnapshot snapshot, DateTimeOffset observedAt)
    {
        // A later attachment must never certify an earlier failure or queued event.
        var sample = snapshot.Samples.LastOrDefault(s => s.TimestampUtc <= observedAt &&
            observedAt - s.TimestampUtc <= TimeSpan.FromSeconds(10));
        return sample?.GameVerification is { IsValid: true } evidence ? evidence : NativeGameVerification.Unavailable;
    }
}
