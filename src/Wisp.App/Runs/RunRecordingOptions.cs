namespace Wisp.App.Runs;

public sealed record RunRecordingOptions(TimeSpan? StopAfter = null)
{
    internal bool IsValid => StopAfter is null || StopAfter > TimeSpan.Zero && StopAfter <= TimeSpan.FromMinutes(10);
    internal TimeSpan Limit => StopAfter ?? TimeSpan.FromMinutes(10);
}
