using Wisp.Core.Runs;

namespace Wisp.App.Runs;

public sealed partial class LapReviewViewModel
{
    private bool _featureAvailable = true;
    private string _featureStatus = "";
    private (LapReviewLap Lap, int Cursor, int Start, int End)? _featureSelection;
    public bool FeatureAvailable => _featureAvailable && !_disposed;
    public string FeatureStatus => _featureStatus;

    internal void SetFeatureAvailable(bool available, string? reason)
    {
        if (_disposed) return;
        _featureStatus = available ? "" : reason ?? "Lap review is temporarily paused.";
        Changed(nameof(FeatureStatus));
        if (_featureAvailable == available) return;
        _featureAvailable = available;
        if (!available)
        {
            if (Lap is { } lap) _featureSelection = (lap, _cursor, _sectionStart, _sectionEnd);
            _loadCancellation?.Cancel();
            _analysisCancellation?.Cancel();
            ClearReview();
            Status = _featureStatus;
        }
        Changed(nameof(FeatureAvailable));
        Changed(nameof(HasLap));
        Changed(nameof(CanPin));
        RaiseCommands();
        if (available) _ = LoadAsync();
    }
}
