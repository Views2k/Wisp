namespace Wisp.App;

public static class OverlayPlacementResolver
{
    private const string SpeedMarker = "-SpeedV4-";
    private const string GForceSuffix = "-GForceV2";

    public static OverlayPlacement? FindGForcePlacementForSpeedDisplay(
        IReadOnlyDictionary<string, OverlayPlacement> placements,
        string? speedDisplayKey,
        out string? gForceDisplayKey)
    {
        ArgumentNullException.ThrowIfNull(placements);

        gForceDisplayKey = null;
        if (string.IsNullOrWhiteSpace(speedDisplayKey))
        {
            return null;
        }

        var markerIndex = speedDisplayKey.LastIndexOf(SpeedMarker, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            return null;
        }

        gForceDisplayKey = speedDisplayKey[..markerIndex] + GForceSuffix;
        return placements.TryGetValue(gForceDisplayKey, out var placement)
            ? placement
            : null;
    }
}
