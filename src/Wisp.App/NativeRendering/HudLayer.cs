using System.Runtime.InteropServices;

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
    internal virtual void SetNativeSource(INativeNeedleHistorySource? source) { }
    internal virtual bool RefreshNativeHistory(long timestamp) => false;
    internal DirectCompositionDrawCommand[] Build(long timestamp)
    {
        var commands = new List<DirectCompositionDrawCommand>();
        AppendCommands(commands, timestamp);
        return commands.ToArray();
    }
    internal abstract void AppendCommands(List<DirectCompositionDrawCommand> commands, long timestamp);
    internal virtual bool CanReuse(long timestamp) => true;
    internal virtual (string Kind, AnalogHudSample Sample)? NeedleDiagnostic => null;
    internal virtual bool SupportsCompositorNeedle => false;
    internal virtual bool TryCopyCompositorNeedle(long timestamp, Span<CompositorNeedlePoint> points,
        out CompositorNeedleCurve curve, out CompositorNeedleGeometry geometry)
    { curve = default; geometry = default; return false; }
}

internal sealed record HudLayerPlacement(int Id, HudLayerSnapshot Snapshot,
    float OriginX, float OriginY, float AxisXX, float AxisXY, float AxisYX, float AxisYY, float Opacity)
{
    internal bool CompatibleWith(HudLayerPlacement other) => Id == other.Id &&
        OriginX == other.OriginX && OriginY == other.OriginY &&
        AxisXX == other.AxisXX && AxisXY == other.AxisXY &&
        AxisYX == other.AxisYX && AxisYY == other.AxisYY &&
        Opacity == other.Opacity && Equals(Snapshot.CompatibilityKey, other.Snapshot.CompatibilityKey);

    internal void Transform(Span<DirectCompositionDrawCommand> commands)
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

internal sealed record HudWindowSnapshot(int Width, int Height, bool Active, HudLayerPlacement[] Layers,
    NativeGaugeFrame Frame = default, float Opacity = 1, float HostOffsetX = 0, float HostOffsetY = 0)
{
    internal bool CompatibleWith(HudWindowSnapshot other)
    {
        if (!Active || !other.Active || Width != other.Width || Height != other.Height || Layers.Length != other.Layers.Length)
            return false;
        for (var index = 0; index < Layers.Length; index++)
            if (!Layers[index].CompatibleWith(other.Layers[index])) return false;
        return true;
    }
}

internal sealed class HudScenePlayback
{
    private readonly INativeNeedleHistorySource? _nativeSource;
    internal HudScenePlayback(INativeNeedleHistorySource? nativeSource = null) => _nativeSource = nativeSource;
    private readonly Dictionary<int, (HudLayerSnapshot Snapshot, HudLayerPlayback Playback)> _layers = [];
    private readonly List<DirectCompositionDrawCommand> _commands = new(256);
    private DirectCompositionDrawCommand[] _output = new DirectCompositionDrawCommand[256];
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
                state.Playback.SetNativeSource(_nativeSource);
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
    internal bool RefreshNativeHistory(long timestamp)
    {
        var invalidated = false;
        foreach (var layer in _layers.Values) invalidated |= layer.Playback.RefreshNativeHistory(timestamp);
        return invalidated;
    }
    internal bool CanReuse(long timestamp) => _layers.Values.All(layer => layer.Playback.CanReuse(timestamp));
    internal IEnumerable<(string Kind, AnalogHudSample Sample)> NeedleDiagnostics =>
        _layers.Values.Select(layer => layer.Playback.NeedleDiagnostic).Where(sample => sample.HasValue).Select(sample => sample!.Value);
    internal IEnumerable<AnalogHudTexture> Textures => _snapshot is null ? [] :
        _snapshot.Layers.SelectMany(layer => layer.Snapshot.Textures);
    internal bool SupportsCompositorNeedle => _layers.Values.Count(layer => layer.Playback.SupportsCompositorNeedle) == 1;

    internal bool TryCopyCompositorNeedle(long timestamp, Span<CompositorNeedlePoint> points,
        out CompositorNeedleCurve curve, out CompositorNeedleGeometry geometry)
    {
        curve = default;
        geometry = default;
        if (_snapshot is null || !SupportsCompositorNeedle) return false;
        foreach (var layer in _snapshot.Layers)
        {
            if (!_layers[layer.Id].Playback.TryCopyCompositorNeedle(timestamp, points, out curve, out geometry)) continue;
            geometry.Place(layer.OriginX, layer.OriginY, layer.AxisXX, layer.AxisXY,
                layer.AxisYX, layer.AxisYY, layer.Opacity);
            return true;
        }
        return false;
    }
    internal DirectCompositionDrawCommand[] Build(long timestamp)
    {
        var (commands, count) = BuildReusable(timestamp);
        return commands.AsSpan(0, count).ToArray();
    }

    // Render-worker storage: only the populated prefix is valid, until the next build.
    // Call Build when the caller needs an independently owned command snapshot.
    internal (DirectCompositionDrawCommand[] Commands, int Count) BuildReusable(long timestamp)
    {
        _commands.Clear();
        if (_snapshot is null) return (_output, 0);
        foreach (var layer in _snapshot.Layers)
        {
            var start = _commands.Count;
            _layers[layer.Id].Playback.AppendCommands(_commands, timestamp);
            layer.Transform(CollectionsMarshal.AsSpan(_commands)[start..]);
        }
        if (_output.Length < _commands.Count) Array.Resize(ref _output, _commands.Capacity);
        _commands.CopyTo(_output);
        return (_output, _commands.Count);
    }
}
