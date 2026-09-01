using System.Runtime.InteropServices;
using System.Text;

namespace Wisp.App;

internal enum StartupLaunchMode
{
    Interactive,
    Background,
    WaitForForza,
    DisabledCompanion
}

internal static class StartupLaunchPolicy
{
    internal const string BackgroundArgument = "--background";
    internal const string ForzaArgument = "--wait-for-forza";

    internal static bool IsAutomaticInvocation(IEnumerable<string> arguments) =>
        arguments.Any(argument =>
            argument.Equals(BackgroundArgument, StringComparison.OrdinalIgnoreCase) ||
            argument.Equals(ForzaArgument, StringComparison.OrdinalIgnoreCase));

    internal static StartupLaunchMode Evaluate(
        bool backgroundRequested,
        bool companionRequested,
        bool setupWasRequired,
        bool startWithWindows,
        bool startWithForza)
    {
        // Completing the required wizard always leaves an interactive Wisp.
        if (setupWasRequired)
        {
            return StartupLaunchMode.Interactive;
        }

        if ((backgroundRequested || companionRequested) && startWithForza)
        {
            // Honor the opt-in mode even if Windows used an older background
            // command. An explicit manual launch remains interactive.
            return StartupLaunchMode.WaitForForza;
        }

        if (backgroundRequested || companionRequested)
        {
            return startWithWindows
                ? StartupLaunchMode.Background
                : StartupLaunchMode.DisabledCompanion;
        }

        return StartupLaunchMode.Interactive;
    }
}

internal sealed class ForzaStartupLatch
{
    private bool _observedCurrentGame;
    private int _absentObservations;

    internal bool Observe(bool? gameWindowPresent, bool runtimeActive)
    {
        // An incomplete/failed enumeration is not evidence that the game quit.
        if (gameWindowPresent is null)
        {
            return false;
        }

        if (!gameWindowPresent.Value)
        {
            _absentObservations = Math.Min(2, _absentObservations + 1);
            if (_absentObservations == 2)
            {
                _observedCurrentGame = false;
            }
            return false;
        }


        _absentObservations = 0;
        if (_observedCurrentGame)
        {
            return false;
        }

        _observedCurrentGame = true;
        return !runtimeActive;
    }

    internal void SuppressCurrentGame(bool? gameWindowPresent = null)
    {
        _observedCurrentGame = gameWindowPresent != false;
        _absentObservations = 0;
    }

    internal void Reset()
    {
        _observedCurrentGame = false;
        _absentObservations = 0;
    }
}

internal interface IForzaStartupWindowObserver
{
    bool? IsGameWindowPresent();
}

internal sealed class ForzaStartupWindowObserver : IForzaStartupWindowObserver
{
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private const int MaximumWindows = 1024;
    private const int MaximumCaptionLength = 128;

    public bool? IsGameWindowPresent()
    {
        var found = false;
        var truncated = false;
        var visited = 0;
        var caption = new StringBuilder(MaximumCaptionLength);
        var completed = EnumWindows((window, _) =>
        {
            if (++visited > MaximumWindows)
            {
                truncated = true;
                return false;
            }

            if (!IsWindowVisible(window))
            {
                return true;
            }

            caption.Clear();
            if (GetWindowText(window, caption, caption.Capacity) > 0 &&
                MatchesCaption(caption.ToString()))
            {
                found = true;
                return false;
            }
            return true;
        }, IntPtr.Zero);

        return found ? true : completed && !truncated ? false : null;
    }

    // The ordinary top-level caption is only an auto-start hint. It never
    // authorizes telemetry, native reads, HUD ownership, or compatibility.
    internal static bool MatchesCaption(string? caption) =>
        string.Equals(caption?.Trim(), "Forza Horizon 6", StringComparison.OrdinalIgnoreCase);

    private delegate bool EnumWindowCallback(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowCallback callback, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder caption, int maximumCount);
}
