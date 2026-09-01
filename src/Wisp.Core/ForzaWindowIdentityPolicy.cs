namespace Wisp.Core;

public static class ForzaWindowIdentityPolicy
{
    public static ForzaWindowIdentityResult Evaluate(
        IntPtr foregroundWindow,
        int foregroundProcessId,
        int rootProcessId,
        int rootOwnerProcessId,
        IReadOnlySet<int> knownForzaProcessIds,
        IntPtr lastConfirmedForzaWindow,
        bool hasKnownForzaDescendant = false)
    {
        ArgumentNullException.ThrowIfNull(knownForzaProcessIds);

        if (foregroundWindow == IntPtr.Zero)
        {
            var canRetainConfirmedWindow = lastConfirmedForzaWindow != IntPtr.Zero &&
                                           knownForzaProcessIds.Any(processId => processId > 0);
            return new ForzaWindowIdentityResult(
                canRetainConfirmedWindow,
                canRetainConfirmedWindow ? lastConfirmedForzaWindow : IntPtr.Zero);
        }

        var isForzaSurface = hasKnownForzaDescendant ||
                             IsKnownForzaProcess(foregroundProcessId, knownForzaProcessIds) ||
                             IsKnownForzaProcess(rootProcessId, knownForzaProcessIds) ||
                             IsKnownForzaProcess(rootOwnerProcessId, knownForzaProcessIds);

        return new ForzaWindowIdentityResult(
            isForzaSurface,
            isForzaSurface ? foregroundWindow : IntPtr.Zero);
    }

    private static bool IsKnownForzaProcess(
        int processId,
        IReadOnlySet<int> knownForzaProcessIds) =>
        processId > 0 && knownForzaProcessIds.Contains(processId);
}

public readonly record struct ForzaWindowIdentityResult(
    bool IsForzaForeground,
    IntPtr ConfirmedForzaWindow);
