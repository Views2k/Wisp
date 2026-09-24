using System.Runtime.InteropServices;
using Wisp.App.NativeRendering;
using Xunit;

namespace Wisp.App.Tests;

public sealed class HudCommandBufferTests(ITestOutputHelper output)
{
    [Fact]
    public void BorrowedBufferMatchesOwnedBytesAndNeverOverwritesOwnedSnapshots()
    {
        var scene = new HudScenePlayback();
        scene.Update(SceneCommandBaselineTests.Window(0), 0);
        var owned = scene.Build(0);
        var saved = Bytes(owned);
        var borrowed = scene.BuildReusable(0);
        Assert.Equal(saved, Bytes(borrowed.Commands.AsSpan(0, borrowed.Count)));
        scene.Update(SceneCommandBaselineTests.Window(1), 0);
        scene.BuildReusable(0);
        Assert.Equal(saved, Bytes(owned));
        Assert.NotSame(owned, borrowed.Commands);
    }

    [Fact]
    public void GrowthThenShrinkRemovalAndResetUseOnlyThePopulatedPrefix()
    {
        var snapshot = SceneCommandBaselineTests.Boost(0);
        var layers = Enumerable.Range(0, 20).Select(index =>
            new HudLayerPlacement(index, snapshot, index * 100, index * 20, 1, 0, 0, 1, .5f)).ToArray();
        var scene = new HudScenePlayback();
        scene.Update(new(3000, 1000, true, layers), 0);
        var large = scene.BuildReusable(0);
        Assert.True(large.Count > 256);
        var expected = scene.Build(0);
        Assert.Equal(Bytes(expected), Bytes(large.Commands.AsSpan(0, large.Count)));

        scene.Update(new(3000, 1000, true, [layers[0]]), 0);
        var small = scene.BuildReusable(0);
        Assert.Same(large.Commands, small.Commands);
        Assert.Equal(large.Count / 20, small.Count);
        Assert.Equal(Bytes(expected.AsSpan(0, small.Count)), Bytes(small.Commands.AsSpan(0, small.Count)));
        scene.Update(new(3000, 1000, true, []), 0);
        Assert.Equal(0, scene.BuildReusable(0).Count);
        scene.Reset();
        Assert.Equal(0, scene.BuildReusable(0).Count);
        Assert.Empty(scene.Build(0));
    }

    [Fact]
    public void ReusableSceneRemovesOwnedCommandArrayAllocationsAfterWarmup()
    {
        var scene = new HudScenePlayback();
        scene.Update(SceneCommandBaselineTests.Window(0), 0);
        for (var warmup = 0; warmup < 32; warmup++)
        {
            scene.Build(0);
            scene.BuildReusable(0);
        }
        const int iterations = 512;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < iterations; index++) scene.Build(0);
        var ownedBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < iterations; index++) scene.BuildReusable(0);
        var reusableBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        output.WriteLine($"SCENE_REUSABLE_ALLOC {iterations} owned={ownedBytes} reusable={reusableBytes}");
        Assert.True(reusableBytes < ownedBytes / 4, $"Owned: {ownedBytes}; reusable: {reusableBytes}.");
    }

    private static byte[] Bytes(ReadOnlySpan<DirectCompositionDrawCommand> commands) => MemoryMarshal.AsBytes(commands).ToArray();
}
