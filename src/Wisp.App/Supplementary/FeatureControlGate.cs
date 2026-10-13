using System.Collections.Immutable;

namespace Wisp.App.Supplementary;

internal sealed class FeatureControlGate
{
    private readonly object _sync = new();
    private FeatureControlState _state = new(0, ImmutableDictionary<string, string>.Empty);
    internal FeatureControlState State => Volatile.Read(ref _state);
    internal bool IsAllowed(string feature) => ControlledFeatures.All.Contains(feature) && !State.Disabled.ContainsKey(feature);
    internal string? Reason(string feature) => State.Disabled.GetValueOrDefault(feature);

    internal bool Apply(FeatureControlState state)
    {
        if (state.Revision < 0 || state.Disabled.Keys.Any(key => !ControlledFeatures.All.Contains(key)))
            throw new ArgumentException("Unknown feature control state.", nameof(state));
        lock (_sync)
        {
            if (state.Revision < _state.Revision) return false;
            Volatile.Write(ref _state, state);
            return true;
        }
    }
}
