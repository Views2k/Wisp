namespace Wisp.App;

public sealed partial class AppController
{
    internal bool TryCompleteFeatureTour()
    {
        if (_disposed || Settings.RequiresSetup) return false;
        var previous = Settings.CompletedFeatureTourId;
        Settings.CompletedFeatureTourId = FeatureTourSession.CurrentTourId;
        if (TrySavePendingSettings()) return true;
        // A failed write must not masquerade as a remembered dismissal.
        Settings.CompletedFeatureTourId = previous;
        return false;
    }

    internal bool TryDismissWhatsNew(string id)
    {
        if (_disposed) return false;
        Settings.DismissedWhatsNewId = id;
        return TrySavePendingSettings();
    }
}
