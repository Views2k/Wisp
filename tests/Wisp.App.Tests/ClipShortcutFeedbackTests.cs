using Wisp.App.Clips;
using Xunit;

namespace Wisp.App.Tests;

public sealed class ClipShortcutFeedbackTests
{
    [Fact]
    public void DisabledPreferenceSuppressesEveryOutcomeWithoutPlayingAudio()
    {
        var calls = 0;
        var feedback = new ClipShortcutFeedback(_ => calls++);
        foreach (var kind in Enum.GetValues<ClipShortcutFeedbackKind>()) feedback.Play(kind, false);
        Assert.Equal(0, calls);
        feedback.Play(ClipShortcutFeedbackKind.Saved, true);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(ClipShortcutFeedbackKind.Saved, false)]
    [InlineData(ClipShortcutFeedbackKind.Enabled, false)]
    [InlineData(ClipShortcutFeedbackKind.Disabled, false)]
    [InlineData(ClipShortcutFeedbackKind.Failed, true)]
    public void OutcomesChooseSuccessOrFailureSound(ClipShortcutFeedbackKind kind, bool failure)
    {
        var requests = new List<bool>();
        new ClipShortcutFeedback(requests.Add).Play(kind, true);
        Assert.Equal([failure], requests);
    }

    [Fact]
    public void AudioFailureCannotFailTheCompletedClipAction()
    {
        new ClipShortcutFeedback(_ => throw new InvalidOperationException("No audio device."))
            .Play(ClipShortcutFeedbackKind.Saved, true);
    }
}
