using Wisp.Core.Runs;

namespace Wisp.App.Runs;

public sealed partial class LapReviewViewModel
{
    private LapReviewLap? _telemetryContactLap;
    private LapReviewContact[] _telemetryContacts = [];

    private LapReviewContact[]? PreparedTelemetryContacts(LapReviewLap lap) =>
        ReferenceEquals(_telemetryContactLap, lap) ? _telemetryContacts : null;

    private LapReviewContact[] CachedTelemetryContacts(LapReviewLap lap) =>
        PreparedTelemetryContacts(lap) ?? [];

    private void AdoptTelemetryContacts(LapReviewLap lap, LapReviewContact[] contacts)
    {
        if (ReferenceEquals(_telemetryContactLap, lap) && ReferenceEquals(_telemetryContacts, contacts)) return;
        _telemetryContactLap = lap;
        _telemetryContacts = contacts;
        _contactLap = null;
    }

    private void ClearTelemetryContacts()
    {
        _telemetryContactLap = null;
        _telemetryContacts = [];
    }
}
