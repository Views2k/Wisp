using Wisp.App.Drift;
using Wisp.App.NativeRendering;
using Wisp.Core;
using Xunit;

namespace Wisp.App.Tests;

internal static class DriftGaugeTargetRangeTests
{
    internal static void AssertOnCurrentDispatcher()
    {
        DriftGaugeVisuals.LoadOnUiThread();
        var commands = new DirectCompositionDrawCommand[DriftGaugeVisuals.MaximumCommands];
        var count = DriftGaugeVisuals.Build(commands,
            new DriftGaugePresentation(620, 108, 1, 1, 1, true, 75, 15),
            new DriftAngleReading(90, DriftGuidanceState.OnTarget));
        var targets = commands.Take(count).Where(command => command.TextureId == 1 && command.OriginY == 12 && command.AxisYY == 24)
            .OrderBy(command => command.OriginX).ToArray();
        Assert.Equal(2, targets.Length);
        Assert.Equal(24f, targets[0].OriginX);
        Assert.Equal(596f, targets[1].OriginX + targets[1].AxisXX, 3);
        Assert.Equal(targets[0].AxisXX, targets[1].AxisXX, 3);
    }
}
