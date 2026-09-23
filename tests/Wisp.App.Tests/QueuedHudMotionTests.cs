using System.Diagnostics;
using System.Reflection;
using System.Windows;
using Wisp.App.NativeRendering;
using Wisp.Core;
using Xunit;

namespace Wisp.App.Tests;

public sealed class QueuedHudMotionTests
{
    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    public void QueuedPowerOrTorqueKeepsItsTimelineAfterANewerFrame(bool torque, int timestampSource)
    {
        var origin = Ticks(1000);
        var now = origin;
        var playback = Playback(typeof(PowerTorqueHudLayer), () => now);
        for (var ms = 0; ms <= 40; ms += 10)
        {
            now = origin + Ticks(ms);
            playback.Update(PowerTorque(torque, ms, now, 0), now);
        }
        AssertNeedle(playback.Build(origin + Ticks(50)), torque ? 700 : 200);

        var published = origin + Ticks(49);
        now = origin + Ticks(52);
        playback.Update(PowerTorque(torque, 49,
            timestampSource == 0 ? published : 0,
            timestampSource == 1 ? published : 0), published);

        AssertNeedle(playback.Build(now), torque ? 680 : 220);
        AssertNeedle(playback.Build(origin + Ticks(85)), torque ? 350 : 550);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void QueuedGForceKeepsItsDotAndTrailOnTheReceivedTimeline(bool hasReceivedTimestamp)
    {
        var origin = Ticks(1000);
        var now = origin;
        var playback = Playback(typeof(GForceHudLayer), () => now);
        for (var ms = 0; ms <= 40; ms += 10)
        {
            now = origin + Ticks(ms);
            playback.Update(GForce(ms, now), now);
        }
        AssertDot(playback.Build(origin + Ticks(50)), .2, .7);
        Assert.Equal(2, Motion(playback).Trail.Count);

        var published = origin + Ticks(49);
        now = origin + Ticks(52);
        playback.Update(GForce(49, hasReceivedTimestamp ? published : 0), published);

        AssertDot(playback.Build(now), .22, .68);
        Assert.Equal(2, Motion(playback).Trail.Count);
        AssertDot(playback.Build(origin + Ticks(85)), .55, .35);
        Assert.Equal(5, Motion(playback).Trail.Count);
    }

    private static HudLayerPlayback Playback(Type layer, Func<long> clock) =>
        (HudLayerPlayback)Create(layer, "Playback", clock);

    private static HudLayerSnapshot PowerTorque(bool torque, int ms, long received, long observed)
    {
        var layer = typeof(PowerTorqueHudLayer);
        const string glyphs = "0123456789PEAK —";
        var glyphCount = glyphs.Length;
        var color = new AnalogHudColor(255, 255, 255);
        var artwork = Create(layer, "Artwork", torque ? 25_000u : 20_000u,
            Array.Empty<AnalogHudTexture>(), new double[glyphCount], new double[glyphCount * glyphCount],
            1d, new AnalogHudPoint(1, 1));
        var options = Create(layer, "Options", 140d, 140d, torque, TorqueUnit.NewtonMeters,
            1000d, false, false, color, color, color, color);
        var display = new PowerTorqueDisplay(true, 100 + ms * 10, 800 - ms * 10, 0, 0);
        var input = new NativePowerTorqueInput(display, 1, (uint)ms, received, observed, 0);
        return (HudLayerSnapshot)Create(layer, "Snapshot", artwork, input, options);
    }

    private static HudLayerSnapshot GForce(int ms, long received)
    {
        var layer = typeof(GForceHudLayer);
        var emptyText = Array.CreateInstance(Nested(layer, "TextLayout"), 0);
        var artwork = Create(layer, "Artwork", 140d, 140d, new Point(70, 70),
            new AnalogHudColor(255, 255, 255), Array.Empty<AnalogHudTexture>(), emptyText);
        var input = new NativeGForceInput(true, .1 + ms * .01, .8 - ms * .01, 1, 1, (uint)ms, received);
        return (HudLayerSnapshot)Create(layer, "Snapshot", artwork, input, Array.Empty<string>(), false, false);
    }

    private static GForceHudMotion Motion(HudLayerPlayback playback) =>
        (GForceHudMotion)playback.GetType().GetField("_motion", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(playback)!;

    private static Type Nested(Type layer, string name) => layer.GetNestedType(name, BindingFlags.NonPublic)!;

    private static object Create(Type layer, string name, params object[] arguments) =>
        Activator.CreateInstance(Nested(layer, name), BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null, args: arguments, culture: null)!;

    private static void AssertNeedle(DirectCompositionDrawCommand[] commands, double expectedValue)
    {
        var needle = Assert.Single(commands, command => command.Shader == DirectCompositionShader.Needle);
        var angle = Math.Atan2(needle.AxisXY, needle.AxisXX) * 180 / Math.PI;
        if (angle < 0) angle += 360;
        var expected = 135 + 270 * expectedValue / 1000;
        Assert.InRange(angle, expected - .001, expected + .001);
    }

    private static void AssertDot(DirectCompositionDrawCommand[] commands, double x, double y)
    {
        var dot = Assert.Single(commands, command => command.TextureId == 60_001);
        Assert.InRange(dot.OriginX, 47 + x * 31 - .0001, 47 + x * 31 + .0001);
        Assert.InRange(dot.OriginY, 47 + y * 31 - .0001, 47 + y * 31 + .0001);
    }

    private static long Ticks(double milliseconds) => (long)(milliseconds * Stopwatch.Frequency / 1000);
}
