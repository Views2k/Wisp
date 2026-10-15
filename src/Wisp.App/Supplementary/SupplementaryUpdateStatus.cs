namespace Wisp.App.Supplementary;

internal sealed record SupplementaryUpdateStatus(int SchemaVersion, string State, string SourceVersion,
    string? TargetVersion, int? DownloadPercent, string? Reason, DateTimeOffset ObservedAt)
{
    internal static readonly HashSet<string> States = new(StringComparer.Ordinal)
        { "checking", "current", "available", "waiting-for-game", "waiting-for-work", "downloading", "verified", "installing", "started", "failed", "paused" };
    internal static readonly HashSet<string> Reasons = new(StringComparer.Ordinal)
        { "connection", "timeout", "verification", "storage", "handoff", "cancelled", "version-mismatch", "installer", "unknown" };
    internal bool IsValid => SchemaVersion == 1 && States.Contains(State) && SupplementarySchema.NumericVersion(SourceVersion, true) &&
        (TargetVersion is null || SupplementarySchema.NumericVersion(TargetVersion, true)) && DownloadPercent is null or >= 0 and <= 100 &&
        (Reason is null || Reasons.Contains(Reason)) && ObservedAt.Offset == TimeSpan.Zero;
}

internal static class ApplicationUpdateTelemetry
{
    private static SupplementaryUpdateStatus? _current;
    internal static SupplementaryUpdateStatus? Current => Volatile.Read(ref _current);
    internal static void Record(string state, string? target = null, int? percent = null, string? reason = null, string? source = null)
    {
        var next = new SupplementaryUpdateStatus(1, state, source ?? ApplicationVersionInfo.MachineVersion, target, percent, reason, DateTimeOffset.UtcNow);
        if (!next.IsValid) return;
        var previous = Current;
        if (previous is not null && previous.State == state && previous.TargetVersion == target && previous.DownloadPercent == percent && previous.Reason == reason) return;
        Volatile.Write(ref _current, next);
        SupplementaryObservations.Record("update", "update", state == "started" ? "success" : "attempt",
            state == "downloading" ? "download" : state is "installing" or "started" ? "install-handoff" : "check");
    }
}
