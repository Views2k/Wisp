namespace Wisp.App.NativeRendering;

// Bounded automatic recovery from a transient native renderer failure. E_OUTOFMEMORY while the
// game starts comes from Direct3D 11 / DirectComposition allocation pressure as the game claims
// the GPU; the same PC renders normally moments later. Any other HRESULT keeps the established
// unavailable state.
internal sealed class NativeRendererRecovery
{
    internal const int MaximumAttempts = 3;
    internal static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(1);
    private int _attempts;

    internal int Attempts => _attempts;

    internal static bool IsTransient(int hresult) => hresult == NativeRendererException.OutOfMemory;

    // True when the host should create the renderer again after the returned delay instead of
    // retiring it. The delay grows 1 s, 2 s, 4 s so a slow game launch is covered without a spin.
    internal bool TryBeginRetry(int hresult, out TimeSpan delay)
    {
        delay = TimeSpan.Zero;
        if (!IsTransient(hresult) || _attempts >= MaximumAttempts) return false;
        _attempts++;
        delay = InitialDelay * (1 << (_attempts - 1));
        return true;
    }

    // A presented frame proves the renderer works again; the next failure gets a full budget.
    internal void Reset() => _attempts = 0;
}
