namespace Wisp.App.Runs;

public sealed record RunRecordingOptions(TimeSpan? StopAfter = null, Wisp.Core.Runs.RunTuneAttachment? TuneAttachment = null)
{
    internal bool IsValid => (StopAfter is null || StopAfter > TimeSpan.Zero && StopAfter <= TimeSpan.FromMinutes(10)) &&
        (TuneAttachment is null || TuneAttachment.IsValid);
    internal TimeSpan Limit => StopAfter ?? TimeSpan.FromMinutes(10);
}
