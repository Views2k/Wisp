using System.Media;

namespace Wisp.App.Clips;

public enum ClipShortcutFeedbackKind { Saved, Enabled, Disabled, Failed }

internal sealed class ClipShortcutFeedback(Action<bool>? play = null)
{
    private readonly Action<bool> _play = play ?? PlaySystemSound;

    internal void Play(ClipShortcutFeedbackKind kind, bool enabled)
    {
        if (!enabled) return;
        try { _play(kind == ClipShortcutFeedbackKind.Failed); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Optional feedback must never change the completed clip action.
        }
    }

    private static void PlaySystemSound(bool failed)
    {
        // SystemSound.Play is asynchronous and respects the Windows sound scheme.
        (failed ? SystemSounds.Exclamation : SystemSounds.Asterisk).Play();
    }
}
