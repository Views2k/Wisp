namespace Wisp.App;

public static class OverlayPlacementResolver
{
    private const string SpeedMarker = "-SpeedV4-";
    private const string GForceSuffix = "-GForceV2";

    public static OverlayPlacement? FindPreferredPlacement(
        IReadOnlyDictionary<string, OverlayPlacement> placements,
        string? lastDisplayKey,
        string currentDisplayKey,
        string variantMarker,
        out string resolvedDisplayKey)
    {
        ArgumentNullException.ThrowIfNull(placements);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentDisplayKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(variantMarker);

        if (!string.IsNullOrWhiteSpace(lastDisplayKey))
        {
            var candidateKey = lastDisplayKey;
            var lastMarkerIndex = lastDisplayKey.LastIndexOf(variantMarker, StringComparison.Ordinal);
            var currentMarkerIndex = currentDisplayKey.LastIndexOf(variantMarker, StringComparison.Ordinal);
            if (lastMarkerIndex >= 0 && currentMarkerIndex >= 0)
            {
                candidateKey = lastDisplayKey[..(lastMarkerIndex + variantMarker.Length)] +
                               currentDisplayKey[(currentMarkerIndex + variantMarker.Length)..];
            }

            if (placements.TryGetValue(candidateKey, out var lastPlacement))
            {
                resolvedDisplayKey = candidateKey;
                return lastPlacement;
            }
        }

        resolvedDisplayKey = currentDisplayKey;
        return placements.TryGetValue(currentDisplayKey, out var currentPlacement)
            ? currentPlacement
            : null;
    }

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
