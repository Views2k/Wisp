using System.Text.Json;
using System.Threading.Channels;
using Xunit;

namespace Wisp.App.Tests;

public sealed class ShiftCaptureInputMonitorTests
{
    [Fact]
    public async Task InitialProbeReportsUnavailableAndDoesNotInventPresses()
    {
        await using var rig = await Rig.Create(default);
        Assert.False(rig.Monitor.IsDeviceAvailable);
        Assert.Equal("Controller input unavailable", rig.Monitor.Status);
        Assert.Single(rig.Events);
        Assert.Equal("controller_status", rig.Events[0].Kind);
    }

    [Fact]
    public async Task DefaultBindingsReportOnlyBAndXEdgesWithMeasuredBrackets()
    {
        await using var rig = await Rig.Create(Connected(0));
        Assert.Equal(0x2000, ShiftCaptureInputMonitor.UpshiftButton);
        Assert.Equal(0x4000, ShiftCaptureInputMonitor.DownshiftButton);
        await rig.Step(Connected(0x103F)); // Other digital buttons are ignored.
        Assert.Empty(rig.ButtonEvents);
        await rig.Step(Connected(0x303F));
        var press = Assert.Single(rig.ButtonEvents);
        Assert.Equal("B", press.Value.GetProperty("button").GetString());
        Assert.Equal("upshift", press.Value.GetProperty("action").GetString());
        Assert.Equal("pressed", press.Value.GetProperty("edge").GetString());
        Assert.True(press.Value.GetProperty("previousPollStartedQpc").GetInt64() < press.Value.GetProperty("pollCompletedQpc").GetInt64());
        Assert.Equal(press.Timestamp, press.Value.GetProperty("latestObservedEdgeQpc").GetInt64());
        Assert.Contains("not a physical", press.Value.GetProperty("timestampMeaning").GetString());
        await rig.Step(Connected(0x503F));
        Assert.Equal(3, rig.ButtonEvents.Count);
        Assert.Equal("released", rig.ButtonEvents[1].Value.GetProperty("edge").GetString());
        Assert.Equal("X", rig.ButtonEvents[2].Value.GetProperty("button").GetString());
        Assert.Equal("downshift", rig.ButtonEvents[2].Value.GetProperty("action").GetString());
    }

    [Fact]
    public async Task HeldOnStartAndHeldAcrossFocusReturnAreBaselines()
    {
        await using var rig = await Rig.Create(Connected(0x2000));
        Assert.Empty(rig.ButtonEvents);
        await rig.Step(Connected(0x2000), foreground: false);
        Assert.Equal("Waiting for Forza focus", rig.Monitor.Status);
        Assert.True(rig.Monitor.IsDeviceAvailable);
        Assert.Equal(TimeSpan.FromMilliseconds(100), rig.LastDelay);
        await rig.Step(Connected(0x6000), foreground: false);
        await rig.Step(Connected(0x6000), foreground: true);
        Assert.Empty(rig.ButtonEvents);
        Assert.Equal(TimeSpan.FromMilliseconds(4), rig.LastDelay);
        await rig.Step(Connected(0));
        Assert.Equal(2, rig.ButtonEvents.Count);
        Assert.All(rig.ButtonEvents, x => Assert.Equal("released", x.Value.GetProperty("edge").GetString()));
    }

    [Fact]
    public async Task DisconnectReconnectAndAmbiguousDevicesRequireFreshBaseline()
    {
        await using var rig = await Rig.Create(Connected(0));
        await rig.Step(new(true, 0, 0, 0, 0, 0));
        Assert.Equal("No controller connected", rig.Monitor.Status);
        Assert.False(rig.Monitor.IsDeviceAvailable);
        await rig.Step(Connected(0x2000));
        Assert.Empty(rig.ButtonEvents);
        await rig.Step(new(true, 3, 0x2000, 0x4000, 0, 0));
        Assert.Equal("Connect only one controller", rig.Monitor.Status);
        Assert.False(rig.Monitor.IsDeviceAvailable);
        await rig.Step(new(true, 2, 0, 0x4000, 0, 0));
        Assert.True(rig.Monitor.IsDeviceAvailable);
        Assert.Empty(rig.ButtonEvents);
        await rig.Step(new(true, 2, 0, 0, 0, 0));
        Assert.Equal("X", Assert.Single(rig.ButtonEvents).Value.GetProperty("button").GetString());
        Assert.All(rig.Events, e => Assert.DoesNotContain("slot", e.Value.GetRawText(), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SingleConnectedDeviceChangingSlotRebaselinesWithoutFalseEdge()
    {
        await using var rig = await Rig.Create(Connected(0));
        await rig.Step(new(true, 8, 0, 0, 0, 0x2000));
        Assert.Empty(rig.ButtonEvents);
        Assert.Equal(2, rig.Events.Count(e => e.Kind == "controller_status"));
    }

    [Fact]
    public async Task StopCancelsPendingDelayAndPreventsFurtherProbesOrCallbacks()
    {
        await using var rig = await Rig.Create(Connected(0));
        var probes = rig.Source.Probes;
        var events = rig.Events.Count;
        await rig.Monitor.StopAsync();
        await rig.Monitor.StopAsync();
        rig.Monitor.Dispose();
        rig.ReleasePending();
        Assert.Equal(probes, rig.Source.Probes);
        Assert.Equal(events, rig.Events.Count);
        Assert.Equal("Stopped", rig.Monitor.Status);
        Assert.False(rig.Monitor.IsDeviceAvailable);
    }

    [Fact]
    public async Task PollingHealthUsesExistingQueriesAndStopPreservesTheUnreportedPartialInterval()
    {
        await using var rig = await Rig.Create(Connected(0));
        Assert.Equal(1, rig.Monitor.PollingSnapshot.Total.Polls);
        Assert.DoesNotContain(rig.Events, e => e.Kind == "controller_polling");
        rig.AdvanceClock(1_000);
        await rig.Step(Connected(0));
        var report = Assert.Single(rig.Events, e => e.Kind == "controller_polling");
        Assert.Equal(2, report.Value.GetProperty("Polls").GetInt64());
        Assert.Equal(1, report.Value.GetProperty("ApiRead").GetProperty("MeanMilliseconds").GetDouble());
        Assert.Equal(1, report.Value.GetProperty("ForegroundGapsOver50Milliseconds").GetInt64());
        await rig.Step(default, foreground: false);
        var eventCount = rig.Events.Count;
        var probeCount = rig.Source.Probes;
        await rig.Monitor.StopAsync();
        var snapshot = rig.Monitor.PollingSnapshot;
        Assert.Equal(3, snapshot.Total.Polls);
        Assert.Equal(1, snapshot.PendingInterval.Polls);
        Assert.Equal(0, snapshot.PendingInterval.ForegroundPolls);
        Assert.Equal(0, snapshot.PendingInterval.ApiAvailablePolls);
        Assert.Equal(probeCount, rig.Source.Probes);
        Assert.Equal(eventCount, rig.Events.Count);
        rig.ReleasePending();
        Assert.Equal(eventCount, rig.Events.Count);
        Assert.Empty(rig.ButtonEvents);
    }

    private static ShiftCaptureControllerState Connected(ushort buttons) => new(true, 1, buttons, 0, 0, 0);
    private sealed record Recorded(string Kind, JsonElement Value, long Timestamp);
    private sealed class Source : IShiftCaptureControllerSource
    {
        internal ShiftCaptureControllerState State;
        internal int Probes;
        public ShiftCaptureControllerState Read() { Probes++; return State; }
    }
    private sealed class Clock : IShiftCaptureInputClock
    {
        private long _timestamp = 1000;
        public long Timestamp => Interlocked.Increment(ref _timestamp);
        public long Frequency => 1000;
        internal void Advance(long ticks) => Interlocked.Add(ref _timestamp, ticks);
    }
    private sealed class Delay : IShiftCaptureInputDelay
    {
        internal readonly Channel<(TaskCompletionSource Completion, TimeSpan Interval)> Pauses =
            Channel.CreateUnbounded<(TaskCompletionSource, TimeSpan)>();
        public Task WaitAsync(TimeSpan delay, CancellationToken cancellation)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Pauses.Writer.TryWrite((completion, delay));
            return completion.Task.WaitAsync(cancellation);
        }
    }
    private sealed class Rig : IAsyncDisposable
    {
        internal readonly Source Source = new();
        private readonly Clock _clock = new();
        private readonly Delay _delay = new();
        private bool _foreground = true;
        private TaskCompletionSource? _pending;
        internal readonly List<Recorded> Events = [];
        internal ShiftCaptureInputMonitor Monitor = null!;
        internal TimeSpan LastDelay;
        internal List<Recorded> ButtonEvents => Events.Where(e => e.Kind == "controller_button").ToList();

        internal static async Task<Rig> Create(ShiftCaptureControllerState initial)
        {
            var rig = new Rig();
            rig.Source.State = initial;
            rig.Monitor = new((kind, value, timestamp) => rig.Events.Add(new(kind, JsonSerializer.SerializeToElement(value), timestamp)),
                () => rig._foreground, rig.Source, rig._clock, rig._delay);
            await rig.WaitForPause();
            return rig;
        }
        internal async Task Step(ShiftCaptureControllerState state, bool foreground = true)
        {
            Source.State = state;
            _foreground = foreground;
            ReleasePending();
            await WaitForPause();
        }
        internal void ReleasePending() => _pending?.TrySetResult();
        internal void AdvanceClock(long ticks) => _clock.Advance(ticks);
        private async Task WaitForPause()
        {
            var pause = await _delay.Pauses.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            _pending = pause.Completion;
            LastDelay = pause.Interval;
        }
        public async ValueTask DisposeAsync() => await Monitor.StopAsync();
    }
}
