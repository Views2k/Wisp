namespace Wisp.App;

/// <summary>A feature receipt is independent of the patch version that delivered it.</summary>
internal sealed class FeatureTourSession
{
    internal const string CurrentTourId = "wisp-interface-2";
    internal const int StepCount = 4;
    internal int StepIndex { get; private set; }
    internal bool IsOpen { get; private set; }
    internal bool HasPendingReceipt { get; private set; }

    internal static bool ShouldOffer(string? receipt, bool discoveryAllowed, bool requiresSetup, bool displayMode) =>
        discoveryAllowed && !requiresSetup && !displayMode && receipt != CurrentTourId;

    internal void Start() { StepIndex = 0; IsOpen = true; }
    internal bool Next()
    {
        if (!IsOpen || StepIndex == StepCount - 1) return false;
        StepIndex++;
        return true;
    }
    internal void Back() { if (IsOpen && StepIndex > 0) StepIndex--; }
    internal void Close() => IsOpen = false;
    internal bool PersistReceipt(Func<bool> save)
    {
        Close();
        HasPendingReceipt = !save();
        return !HasPendingReceipt;
    }
}
