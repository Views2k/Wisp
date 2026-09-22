using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

namespace Wisp.App;

// Created only for an explicitly armed capture. This is polling, not an input hook.
internal sealed class ShiftCaptureInputMonitor : IDisposable
{
    internal const ushort UpshiftButton = 0x2000; // Default controller B binding.
    internal const ushort DownshiftButton = 0x4000; // Default controller X binding.
    private const ushort WatchedButtons = UpshiftButton | DownshiftButton;
    private readonly Action<string, object, long> _record;
    private readonly Func<bool> _gameIsForeground;
    private readonly IShiftCaptureControllerSource _source;
    private readonly IShiftCaptureInputClock _clock;
    private readonly IShiftCaptureInputDelay _delay;
    private readonly ShiftCapturePollStatistics _pollStatistics;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly object _stopGate = new();
    private readonly Task _worker;
    private Task? _stopTask;
    private string _status = "Checking controller";
    private int _available, _stopRequested;
    private bool _reported, _previousForeground, _previousApiAvailable, _hasBaseline;
    private byte _previousConnectedMask;
    private ushort _previousButtons;
    private long _previousPollStarted, _previousPollCompleted;

    internal ShiftCaptureInputMonitor(Action<string, object, long> record, Func<bool> gameIsForeground)
        : this(record, gameIsForeground, new XInputSource(), new InputClock(), new InputDelay()) { }

    internal ShiftCaptureInputMonitor(Action<string, object, long> record, Func<bool> gameIsForeground,
        IShiftCaptureControllerSource source, IShiftCaptureInputClock clock, IShiftCaptureInputDelay delay)
    {
        _record = record ?? throw new ArgumentNullException(nameof(record));
        _gameIsForeground = gameIsForeground ?? throw new ArgumentNullException(nameof(gameIsForeground));
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _delay = delay ?? throw new ArgumentNullException(nameof(delay));
        _pollStatistics = new(_clock.Frequency);
        _worker = Task.Run(PollAsync);
    }

    internal string Status => Volatile.Read(ref _stopRequested) != 0 ? "Stopped" : Volatile.Read(ref _status);
    internal bool IsDeviceAvailable => Volatile.Read(ref _stopRequested) == 0 && Volatile.Read(ref _available) != 0;
    internal ShiftCapturePollingSnapshot PollingSnapshot => _pollStatistics.Snapshot();

    private async Task PollAsync()
    {
        var cancellation = _cancellation.Token;
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                var foregroundBefore = IsForeground();
                var started = _clock.Timestamp;
                ShiftCaptureControllerState state;
                try { state = _source.Read(); }
                catch { state = default; }
                var completed = _clock.Timestamp;
                // A focus change during the query must not produce a game input edge.
                var foreground = foregroundBefore && IsForeground();
                cancellation.ThrowIfCancellationRequested();
                Observe(state, foreground, started, completed);
                await _delay.WaitAsync(TimeSpan.FromMilliseconds(foreground ? 4 : 100), cancellation)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally
        {
            _hasBaseline = false;
            Volatile.Write(ref _available, 0);
        }
    }

    private bool IsForeground()
    {
        try { return _gameIsForeground(); }
        catch { return false; }
    }

    private void Observe(ShiftCaptureControllerState state, bool foreground, long started, long completed)
    {
        var mask = (byte)(state.ConnectedMask & 0x0F);
        var count = System.Numerics.BitOperations.PopCount((uint)mask);
        var available = state.ApiAvailable && count == 1;
        if (_pollStatistics.Observe(started, completed, foreground, state.ApiAvailable, available) is { } polling)
            Emit("controller_polling", polling, completed);
        var changed = !_reported || foreground != _previousForeground ||
            state.ApiAvailable != _previousApiAvailable || mask != _previousConnectedMask;
        var status = !state.ApiAvailable ? "Controller input unavailable" : count == 0 ? "No controller connected" :
            count > 1 ? "Connect only one controller" : !foreground ? "Waiting for Forza focus" : "Controller ready";
        Volatile.Write(ref _status, status);
        Volatile.Write(ref _available, available ? 1 : 0);
        if (changed)
        {
            _hasBaseline = false;
            if (_reported && (mask != _previousConnectedMask || state.ApiAvailable != _previousApiAvailable))
                Emit("controller_selection_changed", new { connectedControllers = count, apiAvailable = state.ApiAvailable }, completed);
            Emit("controller_status", new
            {
                status,
                apiAvailable = state.ApiAvailable,
                connectedControllers = count,
                gameForeground = foreground,
                initialProbe = !_reported,
                controllerSelectionChanged = _reported && mask != _previousConnectedMask,
                foregroundChanged = _reported && foreground != _previousForeground,
                upshiftButton = "B",
                downshiftButton = "X",
                bindings = "Default Forza controller bindings",
                timestampFrequency = _clock.Frequency
            }, completed);
        }
        _reported = true;
        _previousForeground = foreground;
        _previousApiAvailable = state.ApiAvailable;
        _previousConnectedMask = mask;
        if (!available || !foreground || started <= 0 || completed < started)
        {
            _hasBaseline = false;
            return;
        }

        var slot = System.Numerics.BitOperations.TrailingZeroCount((uint)mask);
        var buttons = (ushort)(state.Buttons(slot) & WatchedButtons);
        if (_hasBaseline && started >= _previousPollCompleted)
        {
            var edges = (ushort)(buttons ^ _previousButtons);
            RecordEdge(UpshiftButton, "B", "upshift");
            RecordEdge(DownshiftButton, "X", "downshift");

            void RecordEdge(ushort button, string name, string action)
            {
                if ((edges & button) == 0) return;
                Emit("controller_button", new ShiftCaptureButtonEvent(
                    name, action, (buttons & button) != 0 ? "pressed" : "released",
                    _previousPollStarted, _previousPollCompleted, started, completed,
                    _previousPollStarted, completed,
                    "Sampled controller state change; not a physical button timestamp"), completed);
            }
        }
        _hasBaseline = true;
        _previousButtons = buttons;
        _previousPollStarted = started;
        _previousPollCompleted = completed;
    }

    private void Emit(string kind, object value, long timestamp)
    {
        if (Volatile.Read(ref _stopRequested) != 0) return;
        try { _record(kind, value, timestamp); }
        catch { /* The capture recorder owns reporting its write failures. */ }
    }

    internal Task StopAsync()
    {
        lock (_stopGate)
        {
            if (_stopTask is not null) return _stopTask;
            Volatile.Write(ref _stopRequested, 1);
            _cancellation.Cancel();
            return _stopTask = FinishStopAsync();
        }
    }

    private async Task FinishStopAsync()
    {
        try { await _worker.ConfigureAwait(false); }
        finally { _cancellation.Dispose(); }
    }

    public void Dispose() => _ = StopAsync();

    private sealed class InputClock : IShiftCaptureInputClock
    {
        public long Timestamp => Stopwatch.GetTimestamp();
        public long Frequency => Stopwatch.Frequency;
    }

    private sealed class InputDelay : IShiftCaptureInputDelay
    {
        public Task WaitAsync(TimeSpan delay, CancellationToken cancellation) => Task.Delay(delay, cancellation);
    }

    private sealed class XInputSource : IShiftCaptureControllerSource
    {
        private int _library; // 0: try1_4, 1: use1_4, 2: use9_1_0, 3: unavailable.
        public ShiftCaptureControllerState Read()
        {
            if (_library == 3) return default;
            byte mask = 0;
            Span<ushort> buttons = stackalloc ushort[4];
            for (uint slot = 0; slot < 4; slot++)
            {
                uint result;
                XInputState state;
                try
                {
                    if (_library is 0 or 1)
                    {
                        try { result = GetState14(slot, out state); _library = 1; }
                        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
                        {
                            _library = 2;
                            result = GetState910(slot, out state);
                        }
                    }
                    else result = GetState910(slot, out state);
                }
                catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
                {
                    _library = 3;
                    return default;
                }
                if (result == 1167) continue; // ERROR_DEVICE_NOT_CONNECTED.
                if (result != 0) return default;
                mask |= (byte)(1 << (int)slot);
                buttons[(int)slot] = (ushort)(state.Gamepad.Buttons & WatchedButtons);
            }
            return new(true, mask, buttons[0], buttons[1], buttons[2], buttons[3]);
        }

        [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
        private static extern uint GetState14(uint index, out XInputState state);
        [DllImport("xinput9_1_0.dll", EntryPoint = "XInputGetState")]
        private static extern uint GetState910(uint index, out XInputState state);
        [StructLayout(LayoutKind.Sequential)]
        private struct XInputState { public uint PacketNumber; public XInputGamepad Gamepad; }
        [StructLayout(LayoutKind.Sequential)]
        private struct XInputGamepad
        {
            public ushort Buttons;
            public byte LeftTrigger, RightTrigger;
            public short LeftX, LeftY, RightX, RightY;
        }
    }
}

internal sealed record ShiftCaptureButtonEvent(
    [property: JsonPropertyName("button")] string Button,
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("edge")] string Edge,
    [property: JsonPropertyName("previousPollStartedQpc")] long PreviousPollStartedQpc,
    [property: JsonPropertyName("previousPollCompletedQpc")] long PreviousPollCompletedQpc,
    [property: JsonPropertyName("pollStartedQpc")] long PollStartedQpc,
    [property: JsonPropertyName("pollCompletedQpc")] long PollCompletedQpc,
    [property: JsonPropertyName("earliestObservedEdgeQpc")] long EarliestObservedEdgeQpc,
    [property: JsonPropertyName("latestObservedEdgeQpc")] long LatestObservedEdgeQpc,
    [property: JsonPropertyName("timestampMeaning")] string TimestampMeaning)
{
    [JsonIgnore]
    public bool Pressed => Edge == "pressed";
}

internal interface IShiftCaptureControllerSource { ShiftCaptureControllerState Read(); }
internal interface IShiftCaptureInputClock { long Timestamp { get; } long Frequency { get; } }
internal interface IShiftCaptureInputDelay { Task WaitAsync(TimeSpan delay, CancellationToken cancellation); }

internal readonly record struct ShiftCaptureControllerState(bool ApiAvailable, byte ConnectedMask,
    ushort Slot0, ushort Slot1, ushort Slot2, ushort Slot3)
{
    internal ushort Buttons(int slot) => slot switch { 0 => Slot0, 1 => Slot1, 2 => Slot2, 3 => Slot3, _ => 0 };
}
