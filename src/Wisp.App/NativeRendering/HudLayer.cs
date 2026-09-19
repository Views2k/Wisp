namespace Wisp.App.NativeRendering;

// Captured on the window thread. Playback owns no WPF controls or mutable brushes.
internal abstract class HudLayerSnapshot
{
    internal abstract IReadOnlyList<AnalogHudTexture> Textures { get; }
    internal abstract HudLayerPlayback CreatePlayback();
    internal virtual object CompatibilityKey => GetType();
}

internal abstract class HudLayerPlayback
{
    internal abstract void Update(HudLayerSnapshot snapshot, long timestamp);
    internal abstract DirectCompositionDrawCommand[] Build(long timestamp);
    internal virtual bool CanReuse(long timestamp) => true;
    internal virtual (string Kind, AnalogHudSample Sample)? NeedleDiagnostic => null;
}

internal sealed record HudLayerPlacement(int Id, HudLayerSnapshot Snapshot,
    float OriginX, float OriginY, float AxisXX, float AxisXY, float AxisYX, float AxisYY, float Opacity)
{
    internal bool CompatibleWith(HudLayerPlacement other) => Id == other.Id &&
        OriginX == other.OriginX && OriginY == other.OriginY &&
        AxisXX == other.AxisXX && AxisXY == other.AxisXY &&
        AxisYX == other.AxisYX && AxisYY == other.AxisYY &&
        Opacity == other.Opacity && Equals(Snapshot.CompatibilityKey, other.Snapshot.CompatibilityKey);

    internal void Transform(DirectCompositionDrawCommand[] commands)
    {
        for (var i = 0; i < commands.Length; i++)
        {
            ref var command = ref commands[i];
            (command.OriginX, command.OriginY) = (
                OriginX + command.OriginX * AxisXX + command.OriginY * AxisYX,
                OriginY + command.OriginX * AxisXY + command.OriginY * AxisYY);
            (command.AxisXX, command.AxisXY) = (
                command.AxisXX * AxisXX + command.AxisXY * AxisYX,
                command.AxisXX * AxisXY + command.AxisXY * AxisYY);
            (command.AxisYX, command.AxisYY) = (
                command.AxisYX * AxisXX + command.AxisYY * AxisYX,
                command.AxisYX * AxisXY + command.AxisYY * AxisYY);
            command.TintA *= Opacity;
        }
    }
}

internal sealed record HudWindowSnapshot(int Width, int Height, bool Active, HudLayerPlacement[] Layers, NativeGaugeFrame Frame = default, float Opacity = 1)
{
    internal bool CompatibleWith(HudWindowSnapshot other) => Active && other.Active &&
        Width == other.Width && Height == other.Height && Layers.Length == other.Layers.Length &&
        Layers.Zip(other.Layers).All(pair => pair.First.CompatibleWith(pair.Second));
}

internal sealed class HudScenePlayback
{
    private readonly Dictionary<int, (HudLayerSnapshot Snapshot, HudLayerPlayback Playback)> _layers = [];
    private HudWindowSnapshot? _snapshot;
    private bool _texturesChanged;
    internal void Reset() { _layers.Clear(); _snapshot = null; _texturesChanged = true; }
    internal void Update(HudWindowSnapshot snapshot, long timestamp)
    {
        _snapshot = snapshot;
        var retained = new HashSet<int>();
        foreach (var layer in snapshot.Layers)
        {
            retained.Add(layer.Id);
            if (!_layers.TryGetValue(layer.Id, out var state) || state.Snapshot.GetType() != layer.Snapshot.GetType())
            {
                state = (layer.Snapshot, layer.Snapshot.CreatePlayback());
                _texturesChanged = true;
            }
            else if (!ReferenceEquals(state.Snapshot.Textures, layer.Snapshot.Textures)) _texturesChanged = true;
            state.Playback.Update(layer.Snapshot, timestamp);
            _layers[layer.Id] = (layer.Snapshot, state.Playback);
        }
        foreach (var id in _layers.Keys.Where(id => !retained.Contains(id)).ToArray())
        { _layers.Remove(id); _texturesChanged = true; }
    }
    internal bool ConsumeTextureChanges()
    { var changed = _texturesChanged; _texturesChanged = false; return changed; }
    internal bool CanReuse(long timestamp) => _layers.Values.All(layer => layer.Playback.CanReuse(timestamp));
    internal IEnumerable<(string Kind, AnalogHudSample Sample)> NeedleDiagnostics =>
        _layers.Values.Select(layer => layer.Playback.NeedleDiagnostic).Where(sample => sample.HasValue).Select(sample => sample!.Value);
    internal IEnumerable<AnalogHudTexture> Textures => _snapshot is null ? [] :
        _snapshot.Layers.SelectMany(layer => layer.Snapshot.Textures);
    internal DirectCompositionDrawCommand[] Build(long timestamp)
    {
        if (_snapshot is null) return [];
        var commands = new List<DirectCompositionDrawCommand>(256);
        foreach (var layer in _snapshot.Layers)
        {
            var local = _layers[layer.Id].Playback.Build(timestamp);
            layer.Transform(local);
            commands.AddRange(local);
        }
        return commands.ToArray();
    }
}
