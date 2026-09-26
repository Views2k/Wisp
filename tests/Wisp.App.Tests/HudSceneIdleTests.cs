using Wisp.App.NativeRendering;
using Xunit;

namespace Wisp.App.Tests;

public sealed class HudSceneIdleTests
{
    private sealed class Layer(bool idle) : HudLayerSnapshot
    {
        internal bool Changed;
        internal override IReadOnlyList<AnalogHudTexture> Textures { get; } = [];
        internal override HudLayerPlayback CreatePlayback() => new Playback(this, idle);
    }

    private sealed class Playback(Layer layer, bool idle) : HudLayerPlayback
    {
        internal override void Update(HudLayerSnapshot snapshot, long timestamp) { }
        internal override void AppendCommands(List<DirectCompositionDrawCommand> commands, long timestamp) => layer.Changed = false;
        internal override bool CanReuse(long timestamp) => !layer.Changed;
        internal override bool IdleWhenUnchanged => idle;
    }

    private static HudWindowSnapshot Window(HudLayerSnapshot layer, float x = 0) =>
        new(320, 112, true, [new HudLayerPlacement(1, layer, x, 0, 1, 0, 0, 1, 1)]);

    [Fact]
    public void AnUnchangedSceneOfContentDrivenLayersIdlesAfterItIsBuilt()
    {
        var layer = new Layer(idle: true);
        var scene = new HudScenePlayback();
        scene.Update(Window(layer), 0);
        Assert.False(scene.IdleWhenUnchanged(0));
        scene.ConsumeTextureChanges();
        scene.BuildReusable(0);
        Assert.True(scene.IdleWhenUnchanged(0));
        layer.Changed = true;
        Assert.False(scene.IdleWhenUnchanged(0));
        scene.BuildReusable(0);
        Assert.True(scene.IdleWhenUnchanged(0));
        // A moved layer needs drawing again.
        scene.Update(Window(layer, 10), 0);
        Assert.False(scene.IdleWhenUnchanged(0));
    }

    [Fact]
    public void ScenesWithContinuouslyAnimatedLayersNeverIdle()
    {
        var scene = new HudScenePlayback();
        scene.Update(Window(new Layer(idle: false)), 0);
        scene.ConsumeTextureChanges();
        scene.BuildReusable(0);
        Assert.False(scene.IdleWhenUnchanged(0));
    }
}
