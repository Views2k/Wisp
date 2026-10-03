using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Wisp.App.Runs;
using Wisp.Core;
using Wisp.Core.Runs;
using Xunit;

namespace Wisp.App.Tests;

public sealed class LapReviewPlotKeyboardTests
{
    [Theory]
    [InlineData(Key.Left)]
    [InlineData(Key.Down)]
    [InlineData(Key.Right)]
    [InlineData(Key.Up)]
    [InlineData(Key.Home)]
    [InlineData(Key.End)]
    public void EmptyLapIgnoresNavigationWithoutChoosingAnInvalidPoint(Key key) => OnSta(() =>
    {
        var plot = new LapReviewPlot { Data = Data(0) };
        var selected = new List<int>();
        // LapReviewView always subscribes; without a subscriber the conditional
        // invocation would skip the failing Math.Clamp argument entirely.
        plot.PointChosen += selected.Add;
        var input = SendKey(plot, key);
        Assert.Empty(selected);
        Assert.False(input.Handled);
    });

    [Fact]
    public void MissingPlotOrLapIgnoresNavigation() => OnSta(() =>
    {
        var plot = new LapReviewPlot();
        var selected = new List<int>();
        plot.PointChosen += selected.Add;
        foreach (var data in new LapReviewPlotData?[] { null, Data(0) with { Lap = null } })
        {
            plot.Data = data;
            foreach (var key in NavigationKeys) Assert.False(SendKey(plot, key).Handled);
        }
        Assert.Empty(selected);
    });

    [Theory]
    [InlineData(Key.Left, 0, 0)]
    [InlineData(Key.Down, 0, 0)]
    [InlineData(Key.Right, 2, 2)]
    [InlineData(Key.Up, 2, 2)]
    [InlineData(Key.Left, 1, 0)]
    [InlineData(Key.Right, 1, 2)]
    [InlineData(Key.Home, 2, 0)]
    [InlineData(Key.End, 0, 2)]
    public void RecordedLapNavigationKeepsExistingFirstLastAndStepBehavior(Key key, int cursor, int expected) => OnSta(() =>
    {
        var plot = new LapReviewPlot { Data = Data(3) with { Cursor = cursor } };
        var selected = new List<int>();
        plot.PointChosen += selected.Add;
        Assert.True(SendKey(plot, key).Handled);
        Assert.Equal(expected, Assert.Single(selected));
    });

    [Fact]
    public void SinglePointLapAlwaysChoosesItsOnlyPoint() => OnSta(() =>
    {
        var plot = new LapReviewPlot { Data = Data(1) };
        var selected = new List<int>();
        plot.PointChosen += selected.Add;
        foreach (var key in NavigationKeys) Assert.True(SendKey(plot, key).Handled);
        Assert.Equal(NavigationKeys.Length, selected.Count);
        Assert.All(selected, index => Assert.Equal(0, index));
    });

    [Fact]
    public void SelectionCanClearPlotDataBeforeTheNextKey() => OnSta(() =>
    {
        var plot = new LapReviewPlot { Data = Data(3) };
        var selected = new List<int>();
        plot.PointChosen += index => { selected.Add(index); plot.Data = null; };
        Assert.True(SendKey(plot, Key.Right).Handled);
        Assert.False(SendKey(plot, Key.Right).Handled);
        Assert.Equal(1, Assert.Single(selected));
    });

    [Fact]
    public void UnavailableChannelValuesDoNotPreventIndexNavigation() => OnSta(() =>
    {
        var data = Data(3);
        var lap = data.Lap! with
        {
            Points = data.Lap!.Points.Select(point => point with
            {
                Sample = point.Sample with { State = point.Sample.State with { GroundSpeedMetersPerSecond = float.NaN } }
            }).ToArray()
        };
        var plot = new LapReviewPlot { Data = data with { Lap = lap } };
        var selected = new List<int>();
        plot.PointChosen += selected.Add;
        Assert.True(SendKey(plot, Key.Right).Handled);
        Assert.Equal(1, Assert.Single(selected));
        Assert.False(SendKey(plot, Key.A).Handled);
        Assert.Single(selected);
    });

    private static readonly Key[] NavigationKeys = [Key.Left, Key.Down, Key.Right, Key.Up, Key.Home, Key.End];
    private static LapReviewPlotData Data(int points) => new(new LapReviewLap
    {
        Points = Enumerable.Range(0, points).Select(index => new LapReviewPoint(index, index, index, index,
            new(index, 0, index), index == 0, new() { State = RunTestData.State() })).ToArray()
    }, null, null, LapReviewChannel.Speed, SpeedUnit.MilesPerHour, 0, 0, Math.Max(0, points - 1));

    private static KeyEventArgs SendKey(LapReviewPlot plot, Key key)
    {
        var input = new KeyEventArgs(Keyboard.PrimaryDevice, new OffscreenSource(plot), Environment.TickCount, key)
        { RoutedEvent = Keyboard.KeyDownEvent };
        plot.RaiseEvent(input);
        Assert.Null(PresentationSource.FromVisual(plot));
        return input;
    }

    private sealed class OffscreenSource(Visual visual) : PresentationSource
    {
        public override Visual RootVisual { get; set; } = visual;
        public override bool IsDisposed => false;
        protected override CompositionTarget GetCompositionTargetCore() => null!;
    }

    private static void OnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { error = exception; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "Lap review keyboard check timed out.");
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
