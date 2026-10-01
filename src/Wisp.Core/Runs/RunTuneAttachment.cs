using Wisp.Core.Tunes;

namespace Wisp.Core.Runs;

public enum RunTuneAttachmentKind { CurrentAtStart, SavedMatchedAtStart }

public sealed record RunTuneAttachment(TuneSnapshot Snapshot, string Name, string Description,
    DateTimeOffset AttachedAtUtc, RunTuneAttachmentKind Kind, Guid? SavedTuneId = null,
    bool DrivingContinuityInterrupted = false)
{
    public bool IsValid => Snapshot is not null && TuneSnapshotValidator.TryValidate(Snapshot, out _) && Snapshot.IsComplete &&
        Name is { Length: > 0 and <= 40 } && Name == Name.Trim() && !Name.Any(char.IsControl) &&
        Description is { Length: <= 2000 } && Description == Description.Trim() &&
        !Description.Any(value => char.IsControl(value) && value is not ('\r' or '\n' or '\t')) &&
        AttachedAtUtc != default && AttachedAtUtc.Offset == TimeSpan.Zero && Enum.IsDefined(Kind) &&
        (Kind == RunTuneAttachmentKind.CurrentAtStart ? SavedTuneId is null : SavedTuneId is { } id && id != Guid.Empty);
}
